using System.Collections.Generic;
using Colossal.Logging;
using Game.Common;
using Game.Rendering;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using SeenBetterDays.Data;

namespace SeenBetterDays.Rendering
{
    /// <summary>
    /// Weathers a building by darkening the colours the game renders it with, and nothing else.
    ///
    /// The colour a building is drawn with is <see cref="MeshColor"/>, and the game's
    /// MeshColorSystem computes it from scratch - from the prefab's colour variations, or from
    /// the player's own colour in <see cref="CustomMeshColor"/> - whenever the building is marked
    /// Updated or BatchesUpdated, when its renters change, and on load. This renderer runs straight
    /// after that system (see WeatheringColourSystem) and darkens the colour it has just computed,
    /// before the frame is drawn.
    ///
    /// It never writes CustomMeshColor. Until 0.1.4 it did - the weathered colour lived in the
    /// same slot as the player's own - and that slot is saved with the city, with its "off"
    /// switch lost on load. One slip anywhere and a weathered colour became a building's
    /// "original", darkened again on every reload; level 5 buildings were found almost black. Now
    /// the player's colour and the game's are never touched, and since the game always recomputes
    /// from them, a darkening can never be applied on top of another: nothing accumulates, a save
    /// needs no special handling, and without the mod a city looks exactly as it would have.
    ///
    /// Every submesh is darkened on its own, so buildings whose parts differ in colour (a roof
    /// unlike its walls) are weathered too - the old single-slot override could not do that
    /// without repainting the roof.
    /// </summary>
    public sealed class MeshColorOverlayRenderer : IBuildingOverlayRenderer
    {
        private readonly EntityManager m_EntityManager;
        private readonly ILog m_Log;

        private struct Record
        {
            public BuildingVisualProfile m_Profile;

            /// <summary>The first colour this renderer left in MeshColor, to tell whether the game
            /// has rewritten it since.</summary>
            public Color m_Written;
            public bool m_HasWritten;
        }

        private readonly Dictionary<Entity, Record> m_Records = new Dictionary<Entity, Record>();

        /// <summary>Every renderer in the world, for WeatheringColourSystem to drive.</summary>
        private static readonly List<MeshColorOverlayRenderer> s_All = new List<MeshColorOverlayRenderer>();

        public static IReadOnlyList<MeshColorOverlayRenderer> All
        {
            get { return s_All; }
        }

        /// <summary>The building open in the colour panel's Customize tab: shown in its true
        /// colour, so the panel reads and edits that rather than the weathered one.</summary>
        private Entity m_Editing = Entity.Null;

        /// <summary>Round-robin position for the check that catches rewrites no tag announced.</summary>
        private int m_VerifyCursor;
        private readonly List<Entity> m_Scratch = new List<Entity>();

        private int m_HoverReports;
        private const int MaxHoverReports = 60;
        private int m_RewriteReports;
        private const int MaxRewriteReports = 40;

        /// <summary>The building under the cursor this frame, set by WeatheringColourSystem, for
        /// the hover diagnosis.</summary>
        public static Entity HoveredBuilding = Entity.Null;

        private static string Rgb(Color c)
        {
            return c.r.ToString("0.000") + "/" + c.g.ToString("0.000") + "/" + c.b.ToString("0.000");
        }

        /// <summary>Buildings darkened by the last pass, for the performance log.</summary>
        public int LastPassDarkened { get; private set; }

        public MeshColorOverlayRenderer(EntityManager entityManager, ILog log)
        {
            m_EntityManager = entityManager;
            m_Log = log;
            s_All.Add(this);
        }

        /// <summary>
        /// Which parts of the colour weathering may take. Static on purpose: this is one visual
        /// preference for the whole mod, and both the harness and the simulation hold their own
        /// renderer instance - a per-instance setting would let the two disagree about what the
        /// city is supposed to look like.
        /// </summary>
        public static WeatheringResponse Response { get; set; }

        static MeshColorOverlayRenderer()
        {
            // All three levers, chosen by eye against the other two at full intensity. The hue
            // bias is only a few per cent, which is why it was invisible at the intensities the
            // simulation was producing at the time and reads as damp or sooty rather than as
            // paint at the intensities it was judged on.
            Response = WeatheringResponse.Full;
        }

        public string Name
        {
            get { return "RenderedMeshColor"; }
        }

        public bool IsAvailable
        {
            get { return true; }
        }

        public string UnavailableReason
        {
            get { return null; }
        }

        public int TrackedBuildingCount
        {
            get { return m_Records.Count; }
        }

        public int OverlayCount
        {
            get { return m_Records.Count; }
        }

        public bool Has(Entity building)
        {
            return m_Records.ContainsKey(building);
        }

        public void CollectTrackedBuildings(List<Entity> into)
        {
            into.Clear();
            foreach (KeyValuePair<Entity, Record> pair in m_Records)
            {
                into.Add(pair.Key);
            }
        }

        /// <summary>Whether this building has colours to darken.</summary>
        public bool CanWeather(Entity building, out string reason)
        {
            reason = null;
            if (!m_EntityManager.Exists(building)
                || !m_EntityManager.HasBuffer<MeshColor>(building)
                || m_EntityManager.GetBuffer<MeshColor>(building, true).Length == 0)
            {
                reason = "no colour channels";
                return false;
            }

            return true;
        }

        /// <summary>A plain account of one building's colour state, for diagnosis in game.</summary>
        public string Describe(Entity building)
        {
            if (!m_EntityManager.Exists(building))
            {
                return "entity no longer exists";
            }

            int live = m_EntityManager.HasBuffer<MeshColor>(building)
                ? m_EntityManager.GetBuffer<MeshColor>(building, true).Length : -1;
            bool custom = m_EntityManager.HasBuffer<CustomMeshColor>(building)
                          && m_EntityManager.IsComponentEnabled<CustomMeshColor>(building);
            return "rendered colours " + live
                 + ", player or saved custom colour " + (custom ? "yes" : "no")
                 + ", darkened by us " + (Has(building) ? "yes" : "no")
                 + (building == m_Editing ? ", open in the colour panel" : string.Empty);
        }

        /// <summary>
        /// Sets how a building should be weathered. The colour itself is written after the game
        /// has recomputed it this frame - asking for that is all this does.
        /// </summary>
        public bool Apply(Entity building, in BuildingVisualProfile profile, out int placed, out string failureReason)
        {
            placed = 0;
            failureReason = null;

            string reason;
            if (!CanWeather(building, out reason))
            {
                failureReason = reason;
                return false;
            }

            m_Records[building] = new Record { m_Profile = profile };
            RequestRecolour(building);
            placed = 1;
            return true;
        }

        /// <summary>Stops weathering a building; the game recomputes its own colour.</summary>
        public bool Remove(Entity building)
        {
            if (!m_Records.Remove(building))
            {
                return false;
            }

            if (m_EntityManager.Exists(building))
            {
                RequestRecolour(building);
            }

            return true;
        }

        /// <summary>Same as Remove: there is nothing of ours on the building to forget.</summary>
        public void Forget(Entity building)
        {
            Remove(building);
        }

        public int RemoveAll()
        {
            int cleared = m_Records.Count;
            m_Scratch.Clear();
            foreach (KeyValuePair<Entity, Record> pair in m_Records)
            {
                m_Scratch.Add(pair.Key);
            }

            m_Records.Clear();
            for (int i = 0; i < m_Scratch.Count; i++)
            {
                if (m_EntityManager.Exists(m_Scratch[i]))
                {
                    RequestRecolour(m_Scratch[i]);
                }
            }

            m_Editing = Entity.Null;
            return cleared;
        }

        /// <summary>Recomputes every building this renderer weathers, for a change of intensity
        /// or response.</summary>
        public int Reapply()
        {
            m_Scratch.Clear();
            foreach (KeyValuePair<Entity, Record> pair in m_Records)
            {
                m_Scratch.Add(pair.Key);
            }

            for (int i = 0; i < m_Scratch.Count; i++)
            {
                if (m_EntityManager.Exists(m_Scratch[i]))
                {
                    RequestRecolour(m_Scratch[i]);
                }
            }

            return m_Scratch.Count;
        }

        /// <summary>Drops records of buildings that no longer exist, without touching anything.</summary>
        public int PruneOrphans()
        {
            m_Scratch.Clear();
            foreach (KeyValuePair<Entity, Record> pair in m_Records)
            {
                if (!m_EntityManager.Exists(pair.Key) || m_EntityManager.HasComponent<Deleted>(pair.Key))
                {
                    m_Scratch.Add(pair.Key);
                }
            }

            for (int i = 0; i < m_Scratch.Count; i++)
            {
                m_Records.Remove(m_Scratch[i]);
            }

            return m_Scratch.Count;
        }

        /// <summary>
        /// Drops records of buildings from the city loaded before and ends colour editing. Records
        /// of buildings that still exist are kept, in case this is called twice in one city.
        /// </summary>
        public int ForgetPreviousCity()
        {
            m_Editing = Entity.Null;
            return PruneOrphans();
        }

        // ---- Colour editing -------------------------------------------------------------------

        public Entity EditingBuilding
        {
            get { return m_Editing; }
        }

        /// <summary>Shows a building in its true colour while it is open on the Customize tab.</summary>
        public void BeginColourEdit(Entity building)
        {
            EndColourEdit();
            m_Editing = building;
            if (m_EntityManager.Exists(building))
            {
                RequestRecolour(building);
            }
        }

        /// <summary>Weathers the building again, on whatever colour it has now.</summary>
        public void EndColourEdit()
        {
            Entity building = m_Editing;
            m_Editing = Entity.Null;
            if (building != Entity.Null && m_EntityManager.Exists(building) && m_Records.ContainsKey(building))
            {
                RequestRecolour(building);
            }
        }

        // ---- The pass after MeshColorSystem ---------------------------------------------------

        /// <summary>
        /// Darkens the buildings whose colours the game has just recomputed. Called by
        /// WeatheringColourSystem once a frame, straight after MeshColorSystem and before the
        /// frame is drawn.
        ///
        /// A building is darkened when the game recomputed it this frame (Updated, BatchesUpdated,
        /// or a change of renters), and also when a round-robin check finds its colour is no longer
        /// the one we wrote - the game rewrote it by some path no tag announced. Either way the
        /// colour read is the game's own, fresh, so the darkening is applied exactly once to it.
        /// </summary>
        public void DarkenRecoloured(HashSet<Entity> rentersChanged, int verifyBudget)
        {
            LastPassDarkened = 0;
            if (m_Records.Count == 0)
            {
                return;
            }

            m_Scratch.Clear();
            foreach (KeyValuePair<Entity, Record> pair in m_Records)
            {
                m_Scratch.Add(pair.Key);
            }

            int verifyFrom = m_Scratch.Count > 0 ? m_VerifyCursor % m_Scratch.Count : 0;
            int verifyTo = verifyFrom + math.min(verifyBudget, m_Scratch.Count);
            m_VerifyCursor = verifyTo;

            for (int i = 0; i < m_Scratch.Count; i++)
            {
                Entity building = m_Scratch[i];
                if (building == m_Editing || !m_EntityManager.Exists(building)
                    || !m_EntityManager.HasBuffer<MeshColor>(building))
                {
                    continue;
                }

                Record record = m_Records[building];
                bool recoloured = m_EntityManager.HasComponent<BatchesUpdated>(building)
                               || m_EntityManager.HasComponent<Updated>(building)
                               || (rentersChanged != null && rentersChanged.Contains(building));

                if (building == HoveredBuilding && m_HoverReports < MaxHoverReports)
                {
                    m_HoverReports++;
                    DynamicBuffer<MeshColor> seen = m_EntityManager.GetBuffer<MeshColor>(building, true);
                    Color now = seen.Length > 0 ? seen[0].m_ColorSet.m_Channel0 : default;
                    m_Log.Info("Seen Better Days: hovered building " + building.Index
                             + " frame " + UnityEngine.Time.frameCount
                             + " - BatchesUpdated " + m_EntityManager.HasComponent<BatchesUpdated>(building)
                             + ", Updated " + m_EntityManager.HasComponent<Updated>(building)
                             + ", Highlighted " + m_EntityManager.HasComponent<Game.Tools.Highlighted>(building)
                             + ", colour now " + Rgb(now) + ", we last wrote " + (record.m_HasWritten ? Rgb(record.m_Written) : "nothing")
                             + ", will darken " + recoloured + ".");
                }

                bool inVerifyWindow = (i >= verifyFrom && i < verifyTo)
                                   || (verifyTo > m_Scratch.Count && i < verifyTo - m_Scratch.Count);
                if (!recoloured && !(inVerifyWindow && WasRewritten(building, record)))
                {
                    continue;
                }

                if (!recoloured && m_RewriteReports < MaxRewriteReports)
                {
                    // Found by the slow check, not by a tag: something rewrote the colour without
                    // announcing it. Record what the building carried, to find out what.
                    m_RewriteReports++;
                    DynamicBuffer<MeshColor> seen = m_EntityManager.GetBuffer<MeshColor>(building, true);
                    m_Log.Info("Seen Better Days: colour rewritten without a tag on building " + building.Index
                             + " frame " + UnityEngine.Time.frameCount
                             + " - now " + (seen.Length > 0 ? Rgb(seen[0].m_ColorSet.m_Channel0) : "-")
                             + ", we last wrote " + (record.m_HasWritten ? Rgb(record.m_Written) : "nothing")
                             + ", hovered " + (building == HoveredBuilding)
                             + ", Highlighted " + m_EntityManager.HasComponent<Game.Tools.Highlighted>(building)
                             + ", Temp " + m_EntityManager.HasComponent<Game.Tools.Temp>(building) + ".");
                }

                DynamicBuffer<MeshColor> colours = m_EntityManager.GetBuffer<MeshColor>(building);
                if (colours.Length == 0)
                {
                    continue;
                }

                for (int c = 0; c < colours.Length; c++)
                {
                    colours[c] = new MeshColor { m_ColorSet = Weather(colours[c].m_ColorSet, record.m_Profile) };
                }

                record.m_Written = colours[0].m_ColorSet.m_Channel0;
                record.m_HasWritten = true;
                m_Records[building] = record;
                LastPassDarkened++;
            }
        }

        /// <summary>Whether the colour the building shows is no longer the one we left.</summary>
        private bool WasRewritten(Entity building, in Record record)
        {
            if (!record.m_HasWritten)
            {
                return true;
            }

            DynamicBuffer<MeshColor> colours = m_EntityManager.GetBuffer<MeshColor>(building, true);
            if (colours.Length == 0)
            {
                return false;
            }

            Color now = colours[0].m_ColorSet.m_Channel0;
            const float tolerance = 1f / 1024f;
            return math.abs(now.r - record.m_Written.r) > tolerance
                || math.abs(now.g - record.m_Written.g) > tolerance
                || math.abs(now.b - record.m_Written.b) > tolerance;
        }

        /// <summary>
        /// Asks the game to recompute a building's colour this frame. BatchesUpdated is the tag
        /// MeshColorSystem looks for; the darkening is then applied on top by the next pass.
        /// Sub-objects follow, so fittings that take their colour from the building refresh too.
        /// </summary>
        private void RequestRecolour(Entity building)
        {
            if (!m_EntityManager.HasComponent<BatchesUpdated>(building))
            {
                m_EntityManager.AddComponent<BatchesUpdated>(building);
            }

            if (!m_EntityManager.HasBuffer<Game.Objects.SubObject>(building))
            {
                return;
            }

            DynamicBuffer<Game.Objects.SubObject> subObjects =
                m_EntityManager.GetBuffer<Game.Objects.SubObject>(building, true);
            m_Scratch2.Clear();
            for (int i = 0; i < subObjects.Length; i++)
            {
                m_Scratch2.Add(subObjects[i].m_SubObject);
            }

            // Collected first: adding a component is structural and invalidates the buffer handle.
            for (int i = 0; i < m_Scratch2.Count; i++)
            {
                Entity subObject = m_Scratch2[i];
                if (m_EntityManager.Exists(subObject) && !m_EntityManager.HasComponent<BatchesUpdated>(subObject))
                {
                    m_EntityManager.AddComponent<BatchesUpdated>(subObject);
                }
            }
        }

        private readonly List<Entity> m_Scratch2 = new List<Entity>();

        // ---- The colour transform -------------------------------------------------------------

        /// <summary>
        /// How much a weathering value (already scaled by the intensity setting) takes out of a
        /// building's colours: the light factor kept, and the share of saturation removed. Shared
        /// by the renderer and the tooltip, so the tooltip reports exactly what is drawn.
        /// </summary>
        public static void ColourAmounts(float weathering, out float darkness, out float desaturation)
        {
            // Exponent 1.5, not 2. Squared, a typical Aged building (22%) kept 98.8% of its light
            // and read as Maintained; once a healthy city's weathered buildings became almost all
            // Aged, the colour layer had all but vanished. 1.5 separates the first steps and
            // leaves the worst states nearly where they were.
            float colourWeathering = math.pow(math.saturate(weathering), 1.5f);
            darkness = 1f - colourWeathering * 0.25f;
            desaturation = Response == WeatheringResponse.DarknessOnly ? 0f : colourWeathering * 0.35f;
        }

        /// <summary>
        /// Turns a visual profile into a colour transform.
        ///
        /// Three things happen, in order of how much they carry: grime takes the saturation out,
        /// then it takes the light out, and finally the individual families tint what is left -
        /// moss towards green, rust towards ochre. The tints are deliberately slight; they are
        /// there so that two buildings in the same state, weathered by different causes, do not
        /// look stamped from the same die.
        /// </summary>
        private static ColorSet Weather(ColorSet basis, in BuildingVisualProfile profile)
        {
            // The player's intensity setting scales what is drawn and nothing else. A building's
            // state is a fact about the city; this is only how loudly that fact is stated, which
            // is why it is applied here at the last moment rather than folded into the profile.
            float scale = Mod.Settings != null ? Mod.Settings.IntensityScale : 1f;

            float weathering = math.saturate(profile.Weathering * scale);

            // One factor on all three channels: in HSV this lowers V and leaves H and S alone.
            // Colour follows the continuous weathering value rather than the active decal
            // families, and the response accelerates: Aged is only a hint away from Maintained,
            // the larger steps belong between the visibly worse states. At the five diagnostic
            // values, F1/F2/F3/F4/F5 retain about 100/97.4/91.9/84.7/76.9 per cent of their light.
            float darkness;
            float desaturation;
            ColourAmounts(weathering, out darkness, out desaturation);

            // Tint multiplicatively, centred on 1, so it bends the hue without changing how
            // bright the surface is. An added tint dominated a desaturated surface and turned
            // four residential blocks frankly green; multiplying keeps it proportional.
            float3 tint = new float3(1f, 1f, 1f);

            if (Response == WeatheringResponse.Full)
            {
                float moss = math.saturate(profile.Moss);
                float rust = math.saturate(profile.Rust);

                tint = new float3(
                    1f - moss * 0.05f + rust * 0.06f,
                    1f + moss * 0.04f - rust * 0.02f,
                    1f - moss * 0.05f - rust * 0.08f);
            }

            return new ColorSet
            {
                m_Channel0 = Weather(basis.m_Channel0, desaturation, darkness, tint),
                m_Channel1 = Weather(basis.m_Channel1, desaturation, darkness, tint),
                m_Channel2 = Weather(basis.m_Channel2, desaturation, darkness, tint),
            };
        }

        private static Color Weather(Color colour, float desaturation, float darkness, float3 tint)
        {
            float luminance = colour.r * 0.299f + colour.g * 0.587f + colour.b * 0.114f;

            return new Color(
                math.saturate(Mathf.Lerp(colour.r, luminance, desaturation) * darkness * tint.x),
                math.saturate(Mathf.Lerp(colour.g, luminance, desaturation) * darkness * tint.y),
                math.saturate(Mathf.Lerp(colour.b, luminance, desaturation) * darkness * tint.z),
                colour.a);
        }
    }
}

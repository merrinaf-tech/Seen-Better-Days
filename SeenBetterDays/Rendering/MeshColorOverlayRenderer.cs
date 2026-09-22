using System.Collections.Generic;
using Colossal.Logging;
using Game.Common;
using Game.Prefabs;
using Game.Rendering;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using SeenBetterDays.Data;

namespace SeenBetterDays.Rendering
{
    /// <summary>
    /// Weathers a building by overriding its own colours, per instance.
    ///
    /// This is the backend that works. It overrides `MeshColor` through
    /// <see cref="CustomMeshColor"/> - the same mechanism Recolor is built on - so the building's
    /// own material does the drawing. Nothing is added to the world: no entity, no geometry, no
    /// placement, no material clone. A weathered building differs from a clean one by a handful of
    /// colours in a buffer, which is why it is correct at every LOD, in every light, from any
    /// camera angle, without a line of code spent on any of those.
    ///
    /// Its boundary is known and worth stating plainly: it can only touch assets that declare
    /// recolourable channels. In practice that is exactly the set of buildings Recolor can repaint
    /// — confirmed in game — which is a large and well-understood slice rather than an unknown.
    ///
    /// What it cannot express is location: grime everywhere, but not graffiti *here* and a crack
    /// *there*. That remains a job for a close-range decal layer on top, once the decal route is
    /// understood (see docs/RENDERING_POC.md).
    /// </summary>
    public sealed class MeshColorOverlayRenderer : IBuildingOverlayRenderer
    {
        private readonly EntityManager m_EntityManager;
        private readonly ILog m_Log;

        /// <summary>
        /// What this renderer did to a building: the profile it applied, and the exact colour it
        /// wrote.
        ///
        /// The written colour is the half that matters. `CustomMeshColor` has one slot and every
        /// mod that recolours buildings writes to it, so the only way to tell "still ours" from
        /// "the player has repainted this since" is to remember what we left there and look.
        /// </summary>
        private struct Record
        {
            public BuildingVisualProfile m_Profile;
            public ColorSet m_Written;
        }

        private readonly Dictionary<Entity, Record> m_Records = new Dictionary<Entity, Record>();
        private readonly Dictionary<Entity, ColorSet> m_SaveSuspended =
            new Dictionary<Entity, ColorSet>();

        public MeshColorOverlayRenderer(EntityManager entityManager, ILog log)
        {
            m_EntityManager = entityManager;
            m_Log = log;
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
            get { return "PerInstanceMeshColor"; }
        }

        /// <summary>Always: the mechanism is part of the engine, not something we have to find.</summary>
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

        /// <summary>One building is one visual element here - there is nothing to count per mark.</summary>
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

        /// <summary>
        /// Whether this building can be weathered without repainting it, and why not when it
        /// cannot. Reads the snapshot when there is one, so the answer does not change depending
        /// on whether the override happens to be on at the moment it is asked.
        /// </summary>
        public bool CanWeather(Entity building, out string reason)
        {
            reason = null;

            if (!m_EntityManager.Exists(building) || !m_EntityManager.HasBuffer<MeshColor>(building))
            {
                reason = "no colour channels";
                return false;
            }

            if (m_EntityManager.HasBuffer<PristineMeshColor>(building))
            {
                if (AllEntriesEqual(m_EntityManager.GetBuffer<PristineMeshColor>(building, true)))
                {
                    return true;
                }

                reason = "submeshes differ";
                return false;
            }

            DynamicBuffer<MeshColor> live = m_EntityManager.GetBuffer<MeshColor>(building, true);
            if (live.Length == 0)
            {
                reason = "no colour channels";
                return false;
            }

            // An already-painted building is not refused any more: its colour is read as the
            // player's choice and weathered on top. See AdoptForeignColour.
            if (IsOverridden(building))
            {
                return true;
            }

            if (!AllEntriesEqual(live))
            {
                reason = "submeshes differ";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Whether something is *actively* overriding this building's colours.
        ///
        /// Both halves matter. `CustomMeshColor` is an enableable buffer and the game leaves it
        /// present but switched off on ordinary buildings, so asking `HasBuffer` alone answers yes
        /// for almost every building in the city - which is exactly what happened: a guard meant
        /// to protect a handful of already-recoloured buildings refused all 1801 of them, and
        /// nothing was weathered at all. The engine's own code checks both, and so must this.
        /// </summary>
        private bool IsOverridden(Entity building)
        {
            return m_EntityManager.HasBuffer<CustomMeshColor>(building)
                && m_EntityManager.IsComponentEnabled<CustomMeshColor>(building);
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
            int snapshot = m_EntityManager.HasBuffer<PristineMeshColor>(building)
                ? m_EntityManager.GetBuffer<PristineMeshColor>(building, true).Length : -1;

            int subObjects = 0;
            int subObjectsWithColour = 0;
            if (m_EntityManager.HasBuffer<Game.Objects.SubObject>(building))
            {
                DynamicBuffer<Game.Objects.SubObject> subs =
                    m_EntityManager.GetBuffer<Game.Objects.SubObject>(building, true);
                subObjects = subs.Length;
                for (int i = 0; i < subs.Length; i++)
                {
                    if (m_EntityManager.Exists(subs[i].m_SubObject)
                        && m_EntityManager.HasBuffer<MeshColor>(subs[i].m_SubObject))
                    {
                        subObjectsWithColour++;
                    }
                }
            }

            string reason;
            bool can = CanWeather(building, out reason);

            return "MeshColor entries " + live
                 + ", snapshot entries " + snapshot
                 + ", custom colour " + (IsOverridden(building) ? "yes"
                        : m_EntityManager.HasBuffer<CustomMeshColor>(building) ? "present but off" : "no")
                 + ", weathered by us " + (Has(building) ? "yes" : "no")
                 + ", sub-objects " + subObjects + " of which " + subObjectsWithColour + " carry their own colours"
                 + ", weatherable " + (can ? "yes" : "no - " + reason);
        }

        /// <summary>
        /// Weathers one building.
        ///
        /// The shape of this method is dictated by one ECS rule that is easy to forget and
        /// expensive to break: **a structural change invalidates every DynamicBuffer handle in
        /// the world.** `AddBuffer`, `AddComponent` and `RemoveComponent` are all structural, and
        /// a handle taken before one of them and read after it is a read of freed memory. It does
        /// not throw - it crashes the game, and only sometimes, which makes it hard to find.
        ///
        /// So the method is in three strict parts, and the order is not decorative:
        ///
        ///   1. **Read.** Everything needed is copied into plain locals.
        ///   2. **Change.** All the structural work happens together.
        ///   3. **Write.** Fresh handles are taken and used immediately.
        /// </summary>
        public bool Apply(Entity building, in BuildingVisualProfile profile, out int placed, out string failureReason)
        {
            placed = 0;
            failureReason = null;

            if (!m_EntityManager.Exists(building))
            {
                failureReason = "building entity no longer exists";
                return false;
            }

            if (!m_EntityManager.HasBuffer<MeshColor>(building))
            {
                failureReason = "this asset declares no recolourable channels (no MeshColor buffer), "
                              + "so its colours cannot be overridden - the same buildings Recolor cannot repaint";
                return false;
            }

            if (!m_EntityManager.HasComponent<PrefabRef>(building))
            {
                failureReason = "building has no PrefabRef";
                return false;
            }

            Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;

            // Sized per submesh because that is how Recolor writes it, but only entry 0 is ever
            // read: MeshColorSystem.ApplyCustomMeshColors takes dynamicBuffer[0], resizes the
            // building's MeshColor to length 1, and returns.
            int subMeshCount = 1;
            if (m_EntityManager.HasBuffer<SubMesh>(prefab))
            {
                subMeshCount = math.max(1, m_EntityManager.GetBuffer<SubMesh>(prefab, true).Length);
            }

            // ---------------------------------------------------------------- 1. read
            ColorSet basis;
            bool snapshotNeedsWriting;

            if (IsOverridden(building) && !StillOurs(building))
            {
                // Somebody else has painted this building - Recolor, or the game's own variation.
                // That colour is now what "clean" means for it, and the grime goes on top rather
                // than instead of it.
                basis = m_EntityManager.GetBuffer<CustomMeshColor>(building, true)[0].m_ColorSet;
                snapshotNeedsWriting = true;
            }
            else if (m_EntityManager.HasBuffer<PristineMeshColor>(building))
            {
                basis = m_EntityManager.GetBuffer<PristineMeshColor>(building, true)[0].m_ColorSet;
                snapshotNeedsWriting = false;
            }
            else
            {
                DynamicBuffer<MeshColor> meshColors = m_EntityManager.GetBuffer<MeshColor>(building, true);
                if (meshColors.Length == 0)
                {
                    failureReason = "the building's MeshColor buffer is empty";
                    return false;
                }

                // Only asked of a building nobody has overridden yet, which is the only moment the
                // live buffer still says anything. Once an override is on, the engine has collapsed
                // MeshColor to one entry and the question has only one possible answer.
                if (!AllEntriesEqual(meshColors))
                {
                    failureReason = "this building's submeshes carry different colours, and a custom "
                                  + "colour would collapse them into one - it would repaint the roof, "
                                  + "not weather it";
                    return false;
                }

                basis = meshColors[0].m_ColorSet;
                snapshotNeedsWriting = true;
            }

            ColorSet weathered = Weather(basis, profile);

            // ---------------------------------------------------------------- 2. change
            if (!m_EntityManager.HasBuffer<PristineMeshColor>(building))
            {
                m_EntityManager.AddBuffer<PristineMeshColor>(building);
            }

            if (!m_EntityManager.HasBuffer<CustomMeshColor>(building))
            {
                m_EntityManager.AddBuffer<CustomMeshColor>(building);
            }

            Touch(building);

            // ---------------------------------------------------------------- 3. write
            if (snapshotNeedsWriting)
            {
                DynamicBuffer<PristineMeshColor> snapshot =
                    m_EntityManager.GetBuffer<PristineMeshColor>(building);
                snapshot.ResizeUninitialized(subMeshCount);
                for (int i = 0; i < subMeshCount; i++)
                {
                    snapshot[i] = new PristineMeshColor(basis);
                }
            }

            DynamicBuffer<CustomMeshColor> custom = m_EntityManager.GetBuffer<CustomMeshColor>(building);
            custom.ResizeUninitialized(subMeshCount);
            for (int i = 0; i < subMeshCount; i++)
            {
                custom[i] = new CustomMeshColor { m_ColorSet = weathered };
            }

            m_EntityManager.SetComponentEnabled<CustomMeshColor>(building, true);

            // Recorded from the local, never read back out of the buffer - by this point Touch has
            // already made that handle unsafe.
            m_Records[building] = new Record { m_Profile = profile, m_Written = weathered };
            placed = subMeshCount;
            return true;
        }

        /// <summary>
        /// Whether the colour currently on the building is still the one this renderer wrote.
        ///
        /// `CustomMeshColor` holds one colour set and carries no record of who wrote it, so the
        /// question "has anyone else painted this?" cannot be asked directly. It can only be
        /// answered by remembering exactly what we left there and checking whether it is still
        /// there - which is what makes it possible to keep using Recolor, or the game's own colour
        /// variations, while the mod goes on weathering.
        ///
        /// Two limits are inherent rather than unfinished. Recolor's picker reads this same slot,
        /// so on a weathered building it shows the *dirty* colour as the current one; closing that
        /// needs cooperation from Recolor, not more code here. And what the player sees in the city
        /// is always their colour weathered, which on a badly neglected building will not be very
        /// recognisable - that one is the feature as asked for, not a defect.
        /// </summary>
        private bool StillOurs(Entity building)
        {
            Record record;
            if (!m_Records.TryGetValue(building, out record))
            {
                return false;
            }

            return StillOurs(building, record);
        }

        private bool StillOurs(Entity building, Record record)
        {
            if (!m_EntityManager.Exists(building)
                || !m_EntityManager.HasBuffer<CustomMeshColor>(building)
                || !m_EntityManager.IsComponentEnabled<CustomMeshColor>(building))
            {
                return false;
            }

            return MatchesWrittenColour(building, record);
        }

        private bool MatchesWrittenColour(Entity building, Record record)
        {
            if (!m_EntityManager.Exists(building)
                || !m_EntityManager.HasBuffer<CustomMeshColor>(building))
            {
                return false;
            }

            DynamicBuffer<CustomMeshColor> custom = m_EntityManager.GetBuffer<CustomMeshColor>(building, true);
            return custom.Length > 0 && Same(custom[0].m_ColorSet, record.m_Written);
        }

        /// <summary>
        /// Makes this renderer's vanilla colour overrides inactive while the serializer reads the
        /// world. CustomMeshColor is enableable, so this changes an enable bit without moving any
        /// entity to another archetype or invalidating a chunk. Its stored value is replaced with
        /// the pristine colour as well, so the save is harmless even if a future engine version
        /// does not preserve the enable bit. The live MeshColor buffer remains as it was, which
        /// avoids a visible clean/dirty flash while saving.
        /// </summary>
        public int SuspendForSave()
        {
            int disabled = 0;

            foreach (KeyValuePair<Entity, Record> pair in m_Records)
            {
                Entity building = pair.Key;
                if (!m_EntityManager.Exists(building)
                    || !m_EntityManager.HasBuffer<CustomMeshColor>(building)
                    || !m_EntityManager.IsComponentEnabled<CustomMeshColor>(building)
                    || !MatchesWrittenColour(building, pair.Value)
                    || !m_EntityManager.HasBuffer<PristineMeshColor>(building))
                {
                    continue;
                }

                DynamicBuffer<PristineMeshColor> pristine =
                    m_EntityManager.GetBuffer<PristineMeshColor>(building, true);
                if (pristine.Length == 0)
                {
                    continue;
                }

                ColorSet clean = pristine[0].m_ColorSet;
                DynamicBuffer<CustomMeshColor> custom =
                    m_EntityManager.GetBuffer<CustomMeshColor>(building);
                for (int i = 0; i < custom.Length; i++)
                {
                    custom[i] = new CustomMeshColor { m_ColorSet = clean };
                }

                m_EntityManager.SetComponentEnabled<CustomMeshColor>(building, false);
                m_SaveSuspended[building] = clean;
                disabled++;
            }

            return disabled;
        }

        /// <summary>
        /// Reactivates the exact buffers disabled by <see cref="SuspendForSave"/>. No component is
        /// added and no MeshColor rebuild is requested: the live colour never changed. If another
        /// mod altered a buffer while the save was in progress, its value is left alone.
        /// </summary>
        public int ResumeAfterSave()
        {
            if (m_SaveSuspended.Count == 0)
            {
                return 0;
            }

            // This runs once after the writer and its grace period have completed. Synchronizing
            // here keeps the enable-bit changes away from native jobs that may be reading the same
            // chunks, without imposing a barrier during ordinary weathering updates.
            m_EntityManager.CompleteAllTrackedJobs();

            int enabled = 0;
            foreach (KeyValuePair<Entity, ColorSet> suspended in m_SaveSuspended)
            {
                Entity building = suspended.Key;
                Record record;
                if (!m_EntityManager.Exists(building)
                    || !m_Records.TryGetValue(building, out record)
                    || !m_EntityManager.HasBuffer<CustomMeshColor>(building)
                    || m_EntityManager.IsComponentEnabled<CustomMeshColor>(building)
                    || !CustomColourEquals(building, suspended.Value))
                {
                    continue;
                }

                DynamicBuffer<CustomMeshColor> custom =
                    m_EntityManager.GetBuffer<CustomMeshColor>(building);
                for (int i = 0; i < custom.Length; i++)
                {
                    custom[i] = new CustomMeshColor { m_ColorSet = record.m_Written };
                }

                m_EntityManager.SetComponentEnabled<CustomMeshColor>(building, true);
                enabled++;
            }

            m_SaveSuspended.Clear();
            return enabled;
        }

        private bool CustomColourEquals(Entity building, ColorSet expected)
        {
            DynamicBuffer<CustomMeshColor> custom =
                m_EntityManager.GetBuffer<CustomMeshColor>(building, true);
            return custom.Length > 0 && Same(custom[0].m_ColorSet, expected);
        }

        /// <summary>
        /// Whether every entry of the building's MeshColor buffer already holds the same colours.
        ///
        /// This is the test for whether a single-ColorSet override is lossless for this building.
        /// The engine's own <c>ApplyCustomMeshColors</c> writes one ColorSet and truncates
        /// MeshColor to length 1, so for a building whose submeshes differ - the classic beige
        /// block with a blue roof - overriding does not darken the roof, it deletes its colour and
        /// gives it the wall's. That is a repaint, and the brief forbids repainting.
        /// </summary>
        public static bool AllEntriesEqual(DynamicBuffer<PristineMeshColor> pristine)
        {
            for (int i = 1; i < pristine.Length; i++)
            {
                if (!Same(pristine[i].m_ColorSet, pristine[0].m_ColorSet))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The same question asked of a building we have never touched.</summary>
        public static bool AllEntriesEqual(DynamicBuffer<MeshColor> meshColors)
        {
            for (int i = 1; i < meshColors.Length; i++)
            {
                if (!Same(meshColors[i].m_ColorSet, meshColors[0].m_ColorSet))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Same(ColorSet a, ColorSet b)
        {
            return Same(a.m_Channel0, b.m_Channel0)
                && Same(a.m_Channel1, b.m_Channel1)
                && Same(a.m_Channel2, b.m_Channel2);
        }

        private static bool Same(Color a, Color b)
        {
            // Generous by a colour's standards: anything closer than this is a difference no
            // player could see, and treating it as a difference would cost coverage for nothing.
            const float tolerance = 1f / 255f;

            return math.abs(a.r - b.r) <= tolerance
                && math.abs(a.g - b.g) <= tolerance
                && math.abs(a.b - b.b) <= tolerance;
        }


        /// <summary>
        /// Turns a visual profile into a colour transform.
        ///
        /// Three things happen, in order of how much they carry: grime takes the saturation out,
        /// then it takes the light out, and finally the individual families tint what is left -
        /// moss towards green, rust towards ochre, soot-heavy dirt towards cold grey. The tints are
        /// deliberately slight; they are there so that two buildings in the same state, weathered
        /// by different causes, do not look stamped from the same die.
        ///
        /// The seed adds a last few percent of variation, which is what stops a street of identical
        /// prefabs in identical condition from reading as a repeat.
        /// </summary>
        private static ColorSet Weather(ColorSet basis, in BuildingVisualProfile profile)
        {
            // The player's intensity setting scales what is drawn and nothing else. A building's
            // state is a fact about the city; this is only how loudly that fact is stated, which
            // is why it is applied here at the last moment rather than folded into the profile.
            float scale = Mod.Settings != null ? Mod.Settings.IntensityScale : 1f;

            float weathering = math.saturate(profile.Weathering * scale);

            // One factor on all three channels: in HSV this lowers V and leaves H and S alone,
            // which is the whole reason this lever is the default one.
            // Colour follows the continuous weathering value rather than the active decal
            // families. When it followed Dirt and Stain, Aged did not change at all (it has only
            // cracks) and Worn introduced Dirt together with a sudden colour jump.
            //
            // The response accelerates instead of spending the contrast evenly. Aged should be
            // only a hint away from Maintained; the larger steps belong between the visibly worse
            // states. At the five diagnostic values, F1/F2/F3/F4/F5 retain about
            // 100/98.8/94.5/87/77.4 per cent of their original light. This also leaves enough
            // separation in the dark end of the scale to distinguish levels 3, 4 and 5.
            // No random jitter here: the original instance colours already provide variation,
            // while jitter can make adjacent test states appear out of order.
            float colourWeathering = weathering * weathering;
            float darkness = 1f - colourWeathering * 0.25f;

            float desaturation = Response == WeatheringResponse.DarknessOnly
                ? 0f
                : colourWeathering * 0.35f;

            // Tint multiplicatively, centred on 1, so it bends the hue without changing how
            // bright the surface is.
            //
            // The first version *added* a fraction of the luminance to green and red and nothing
            // to blue. On a surface that has just been desaturated to near-grey, an added tint is
            // the only colour left, so it dominates - four residential blocks came out frankly
            // green. Multiplying keeps the shift proportional to what is there, and the factors
            // below are small on purpose: at full moss this is a nine per cent bias, which reads
            // as damp grime rather than as paint.
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

        /// <summary>
        /// Recomputes the colours of every building this renderer is already weathering, from the
        /// profiles it recorded. Used when the response changes under a city that is already
        /// weathered, so the difference can be judged on the same buildings rather than by
        /// memory of how they looked a minute ago.
        /// </summary>
        public int Reapply()
        {
            // Snapshot first: Apply writes back into the same dictionary.
            var work = new List<KeyValuePair<Entity, Record>>(m_Records);

            int done = 0;
            for (int i = 0; i < work.Count; i++)
            {
                int placed;
                string failure;
                if (Apply(work[i].Key, work[i].Value.m_Profile, out placed, out failure))
                {
                    done++;
                }
            }

            return done;
        }

        /// <summary>
        /// Forgets everything this renderer knows about a building and takes its own components
        /// off it, leaving the entity exactly as the game made it.
        ///
        /// Used before the city is written to disk. Nothing of this mod's belongs in a save: the
        /// weathering is a pure function of circumstances the game itself stores, so it can be
        /// recomputed from nothing the moment the city is loaded again.
        /// </summary>
        public void Forget(Entity building)
        {
            Remove(building);

            if (m_EntityManager.Exists(building) && m_EntityManager.HasBuffer<PristineMeshColor>(building))
            {
                m_EntityManager.RemoveComponent<PristineMeshColor>(building);
            }
        }

        public bool Remove(Entity building)
        {
            Record record;
            if (!m_Records.TryGetValue(building, out record))
            {
                return false;
            }

            // CustomMeshColor is shared with recolouring mods and carries no author id. If its
            // value no longer matches what we wrote, another system has taken ownership since our
            // last pass. Forget our record without deleting or replacing that newer colour.
            bool restore = StillOurs(building, record);
            m_Records.Remove(building);

            if (restore)
            {
                RestoreColours(building);
            }

            return true;
        }

        public int RemoveAll()
        {
            int cleared = m_Records.Count;

            foreach (KeyValuePair<Entity, Record> pair in m_Records)
            {
                if (StillOurs(pair.Key, pair.Value))
                {
                    RestoreColours(pair.Key);
                }
            }

            m_Records.Clear();
            return cleared;
        }

        /// <summary>
        /// Puts the building back exactly as it was.
        ///
        /// Dropping the baseline along with the override is deliberate: next time this building is
        /// weathered we want to capture whatever its colours are then. If the player has recoloured
        /// it themselves in the meantime, that is their colour, and theirs is what it returns to.
        /// </summary>
        private void RestoreColours(Entity building)
        {
            if (!m_EntityManager.Exists(building))
            {
                return;
            }

            // MeshColorSystem copies CustomMeshColor into the live MeshColor buffer. Removing the
            // override stops future copies, but it does not put back what the buffer contained
            // before the copy; without this step a building held at Maintained still displays
            // the last weathered colour. Copy the snapshot out before any structural change,
            // then restore it through a fresh buffer handle afterwards.
            ColorSet[] pristineColours = null;
            if (m_EntityManager.HasBuffer<PristineMeshColor>(building))
            {
                DynamicBuffer<PristineMeshColor> pristine =
                    m_EntityManager.GetBuffer<PristineMeshColor>(building, true);

                if (pristine.Length > 0)
                {
                    pristineColours = new ColorSet[pristine.Length];
                    for (int i = 0; i < pristine.Length; i++)
                    {
                        pristineColours[i] = pristine[i].m_ColorSet;
                    }
                }
            }

            if (m_EntityManager.HasBuffer<CustomMeshColor>(building))
            {
                m_EntityManager.RemoveComponent<CustomMeshColor>(building);
            }

            // The snapshot deliberately stays. It is the only record of what this building looked
            // like before we touched it, and dropping it means the next capture happens against a
            // buffer the engine may still have collapsed - which is exactly the race that made
            // roofs come out wrong on some passes and not others.
            //
            // The cost is that a player who recolours this building themselves while the mod is
            // loaded finds our snapshot, not their colour, underneath the weathering. Noticing a
            // player's own recolour is real work and is not in this POC.

            Touch(building);

            if (pristineColours != null && m_EntityManager.HasBuffer<MeshColor>(building))
            {
                DynamicBuffer<MeshColor> live = m_EntityManager.GetBuffer<MeshColor>(building);
                live.ResizeUninitialized(pristineColours.Length);

                for (int i = 0; i < pristineColours.Length; i++)
                {
                    live[i] = new MeshColor { m_ColorSet = pristineColours[i] };
                }
            }
        }

        /// <summary>
        /// Makes MeshColorSystem look at this building again - and at its sub-objects, because
        /// awnings, signs and fittings are separate entities that would otherwise keep the old
        /// colours while the shell changed.
        /// </summary>
        private void Touch(Entity building)
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

            for (int i = 0; i < subObjects.Length; i++)
            {
                Entity subObject = subObjects[i].m_SubObject;
                if (m_EntityManager.Exists(subObject) && !m_EntityManager.HasComponent<BatchesUpdated>(subObject))
                {
                    m_EntityManager.AddComponent<BatchesUpdated>(subObject);
                }
            }
        }

        /// <summary>
        /// Drops buildings that no longer exist. There is no orphan to clean up the way a decal
        /// entity would leave one - the colours live on the building and go with it - so this only
        /// keeps our own index honest.
        /// </summary>
        public int PruneOrphans()
        {
            List<Entity> dead = null;

            foreach (KeyValuePair<Entity, Record> pair in m_Records)
            {
                if (m_EntityManager.Exists(pair.Key) && !m_EntityManager.HasComponent<Deleted>(pair.Key))
                {
                    continue;
                }

                if (dead == null)
                {
                    dead = new List<Entity>();
                }

                dead.Add(pair.Key);
            }

            if (dead == null)
            {
                return 0;
            }

            for (int i = 0; i < dead.Count; i++)
            {
                m_Records.Remove(dead[i]);
            }

            return dead.Count;
        }
    }
}

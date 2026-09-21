using System.Collections.Generic;
using Colossal.Logging;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using SeenBetterDays.Data;
using SeenBetterDays.Geometry;

namespace SeenBetterDays.Rendering
{
    /// <summary>
    /// Approach A: draw weathering with the game's own decal objects.
    ///
    /// For each building we create a small number of ordinary Cities: Skylines II object
    /// entities whose prefab is an existing decal, positioned against the building's facade and
    /// rotated so the decal projects into the wall. Nothing about the building, its prefab, its
    /// meshes or its materials is touched, which is what makes per-instance weathering possible
    /// at all: two buildings sharing a prefab differ only in which extra entities exist beside
    /// them.
    ///
    /// Why entities rather than our own draw calls: they inherit the entire vanilla pipeline -
    /// instanced batching, frustum and distance culling, LOD, shadows, virtual texturing, the
    /// HDRP decal buffer. A city with 40,000 weathered buildings costs ECS entities, which the
    /// game already handles in the hundreds of thousands, not managed objects or per-frame
    /// main-thread work.
    ///
    /// Known deliberate limits in this phase, all documented in docs/RENDERING_POC.md:
    ///   - the bounding box proposes candidate positions, then mesh raycasts find the real wall;
    ///   - a decal's size is fixed by its prefab (object entities carry no scale), so size
    ///     variation needs several prefabs rather than one scaled one;
    ///   - overlays are not saved and not restored on load.
    /// </summary>
    public sealed class DecalObjectOverlayRenderer : IBuildingOverlayRenderer
    {
        /// <summary>Elements per family at full intensity. Kept low on purpose: the point is
        /// to prove the mechanism, and every element is an entity.</summary>
        private const int MaxPerFamily = 8;

        /// <summary>
        /// The facade area a plain count of marks is calibrated against, in square metres: roughly
        /// one modest growable, all four sides.
        ///
        /// Without this a thirty-metre block and a corner shop get the same one or two marks, and
        /// the block reads as clean because the same amount of dirt is spread over ten times the
        /// wall. Weathering is a property of surface, not of buildings.
        /// </summary>
        private const float ReferenceWallArea = 600f;

        /// <summary>Hard cap per building, so a maxed-out profile cannot quietly become twelve
        /// entities per building across a whole city.</summary>
        /// <summary>
        /// Raised from 10 once placement stopped costing a full mesh walk per mark. The old number
        /// was set by what the algorithm could afford, not by what a weathered building should
        /// look like, which is the wrong reason for a number to have a value.
        /// </summary>
        private const int MaxPerBuilding = 24;

        /// <summary>
        /// Local wall samples are shared by every instance of a prefab. Keep the cache bounded by
        /// point count rather than prefab count: a simple house and a complex station differ by
        /// orders of magnitude, while each point has the same memory cost.
        /// </summary>
        private const int MaxCachedSurfacePoints = 120000;

        /// <summary>One candidate can land over a window, an archway or a gap between wings.
        /// Try a second point on each facade before giving up on the mark. This is deliberately
        /// small: the current probe walks mesh triangles on the main thread.</summary>
        private const int PlacementAttemptsPerFacade = 2;

        private sealed class OverlayRecord
        {
            public BuildingVisualProfile Profile;
            public readonly List<Entity> Elements = new List<Entity>(MaxPerBuilding);
        }

        private struct PendingElement
        {
            public Entity Prefab;
            public float3 Position;
            public quaternion Rotation;
            public OverlayFamily Family;
            public int FacadeIndex;
        }

        private readonly EntityManager m_EntityManager;
        private readonly DecalPrefabCatalog m_Catalog;
        private readonly ILog m_Log;

        /// <summary>Matches every live overlay entity. Used both to find the entities a command
        /// buffer just created and to sweep strays.</summary>
        private readonly EntityQuery m_OverlayQuery;

        private readonly Dictionary<Entity, OverlayRecord> m_Records = new Dictionary<Entity, OverlayRecord>();
        private readonly List<PendingElement> m_Pending = new List<PendingElement>(MaxPerBuilding);

        [System.Flags]
        private enum RuntimeInitialization : byte
        {
            None = 0,
            DisableCustomMeshColor = 1,
            SetPseudoRandomSeed = 2,
        }

        /// <summary>
        /// Runtime object archetypes are stable for a prefab. Cache the two pieces of initialisation
        /// that the game's GenerateObjectsSystem performs after creating an object from that
        /// archetype, instead of walking the archetype once for every mark.
        /// </summary>
        private readonly Dictionary<Entity, RuntimeInitialization> m_RuntimeInitialization =
            new Dictionary<Entity, RuntimeInitialization>();

        /// <summary>Which facade new overlays go on. Cyclable at runtime so the tester can see
        /// that the placement really is following the building's rotation.</summary>
        public FacadeSide Side = FacadeSide.Front;

        /// <summary>When set, overrides the catalogue's choice so a specific decal can be tried
        /// by hand. Null means "let the catalogue pick".</summary>
        public DecalPrefabInfo ForcedDecal;

        /// <summary>
        /// Diagnostic escape hatch. Off by default, because a decal whose material does not
        /// declare DecalLayers.Buildings will not paint a facade - it will fall through onto
        /// the terrain behind. Turning it on is how you tell "the placement is wrong" apart
        /// from "the decal simply is not allowed to draw on buildings" on an install that has
        /// no building-capable decals at all.
        /// </summary>
        public bool AllowNonBuildingDecals;

        /// <summary>
        /// Flips which way the decal projects. A CS2 decal is a flat box that projects along its
        /// own Y axis, but the game's code never states the sign, so this is a coin-flip we can
        /// settle by pressing a key instead of by arguing.
        /// </summary>
        public bool InvertProjection;

        /// <summary>
        /// Moves the projector along the facade normal, in metres. Negative goes into the
        /// building.
        ///
        /// This was a fraction of the projector's own depth, which topped out at a quarter of a
        /// metre and could never have found the problem. A building's bounding box is its
        /// *outermost* extent - balconies, canopies, cornices, plant rooms - so the real wall can
        /// sit metres behind the plane we compute from it. A projector half a metre deep, centred
        /// on that plane, then contains no wall at all and paints nothing, which is exactly the
        /// symptom: correct bounds, real batch, passes culling, invisible.
        /// </summary>
        public float NormalOffsetMetres;

        public DecalObjectOverlayRenderer(EntityManager entityManager, DecalPrefabCatalog catalog, EntityQuery overlayQuery, ILog log)
        {
            m_EntityManager = entityManager;
            m_Catalog = catalog;
            m_OverlayQuery = overlayQuery;
            m_Log = log;
        }

        public string Name
        {
            get { return "NativeDecalObjects"; }
        }

        public bool IsAvailable
        {
            get
            {
                return AllowNonBuildingDecals
                    ? m_Catalog.All.Count > 0
                    : m_Catalog.BuildingCapable.Count > 0;
            }
        }

        public string UnavailableReason
        {
            get
            {
                if (IsAvailable)
                {
                    return null;
                }

                if (m_Catalog.All.Count == 0)
                {
                    return "no decal object prefabs are loaded at all";
                }

                return "none of the " + m_Catalog.All.Count
                     + " loaded decals declares DecalLayers.Buildings, so none of them would draw on a facade";
            }
        }

        public int TrackedBuildingCount
        {
            get { return m_Records.Count; }
        }

        public int OverlayCount
        {
            get
            {
                int n = 0;
                foreach (KeyValuePair<Entity, OverlayRecord> pair in m_Records)
                {
                    n += pair.Value.Elements.Count;
                }

                return n;
            }
        }

        public bool Has(Entity building)
        {
            return m_Records.ContainsKey(building);
        }

        public void CollectTrackedBuildings(List<Entity> into)
        {
            into.Clear();
            foreach (KeyValuePair<Entity, OverlayRecord> pair in m_Records)
            {
                into.Add(pair.Key);
            }
        }

        public bool Apply(Entity building, in BuildingVisualProfile profile, out int placed, out string failureReason)
        {
            placed = 0;
            failureReason = null;

            if (!IsAvailable)
            {
                failureReason = UnavailableReason;
                return false;
            }

            if (!TryResolveAllFacades(building, out failureReason))
            {
                return false;
            }

            // Some small growables cannot fit even the smallest approved projector for their
            // active family. Discover that from catalogue metadata before CollectFacadePoints
            // walks the rendered mesh. The proximity system may revisit such a building often;
            // the old order paid for the full mesh scan every time even though placement was
            // geometrically impossible.
            if (ForcedDecal == null
                && !AllowNonBuildingDecals
                && !HasFittingAutomaticDecal(profile))
            {
                failureReason = "no approved active decal fits any facade";
                return false;
            }

            m_Pending.Clear();
            Unity.Mathematics.Random rng = new Unity.Mathematics.Random(profile.Seed == 0u ? 1u : profile.Seed);

            m_LastFamilies.Clear();
            PrepareSurfaceTemplate(building);
            m_SurfaceHits = 0;
            m_SurfaceRecoveries = 0;
            m_SurfaceMisses = 0;
            m_SurfaceSides = 0;
            m_PlannedFacadeMask = 0;
            m_FacadeSequenceStep = 0;
            m_FrontMarkCount = 0;
            m_FrontGraffitiCount = 0;

            // The nominal Front is the side presented to the road. Starting at a random side
            // sounded varied, but on long narrow growables it made the first (and often only)
            // mark just as likely to land on a party wall as on the street facade. Begin at the
            // front, then continue round-robin so additional marks still cover the whole shell.
            m_NextFacade = FindFacade(FacadeSide.Front);

            // At Neglected and Decayed, a player looking from the road must actually be able to
            // read the newly introduced graffiti family. A purely round-robin plan can truthfully
            // contain four graffiti while putting every recognisable one on a flank or the rear.
            // Reserve one low, street-facing example first; PlanAllFamilies accounts for it when
            // sharing out the fixed entity budget.
            PlanRequiredFrontGraffiti(building, profile, ref rng);
            PlanAllFamilies(building, profile, ref rng);
            PlanMinimumMark(building, profile, ref rng);
            PlanMinimumMarkPerFacade(building, profile, ref rng);

            if (m_Pending.Count == 0)
            {
                failureReason = m_SurfaceMisses > 0
                    ? "no sampled position hit the building mesh (" + m_SurfaceMisses + " raycast miss(es)) | "
                      + BuildingSurfaceProbe.DescribeMeshResidency(m_EntityManager, building)
                    : "profile asked for nothing: every family is at zero intensity";
                return false;
            }

            // Only now that we know the plan is non-empty: Apply is the whole statement of how
            // this building should look, so whatever is on it is stale by definition.
            Remove(building);

            OverlayRecord record = new OverlayRecord { Profile = profile };
            SpawnPending(building, record);
            m_Pending.Clear();

            if (record.Elements.Count == 0)
            {
                failureReason = "the command buffer produced no overlay entities";
                return false;
            }

            m_Records[building] = record;
            placed = record.Elements.Count;
            return true;
        }

        private bool HasFittingAutomaticDecal(in BuildingVisualProfile profile)
        {
            for (int familyIndex = 0; familyIndex < s_Families.Length; familyIndex++)
            {
                OverlayFamily family = s_Families[familyIndex];
                if (math.saturate(profile.GetIntensity(family)) <= 0.01f)
                {
                    continue;
                }

                for (int facadeIndex = 0; facadeIndex < m_Facades.Count; facadeIndex++)
                {
                    BuildingFacade facade = m_Facades[facadeIndex];
                    if (m_Catalog.HasAutomaticFamilyThatFits(family, facade.Width, facade.Height))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>All four sides of the building that produced a usable rectangle.</summary>
        private readonly List<BuildingFacade> m_Facades = new List<BuildingFacade>(4);

        /// <summary>How much wall this building has, against <see cref="ReferenceWallArea"/>.
        /// Clamped because the point is proportion, not a tower buried in graffiti.</summary>
        private float m_WallScale = 1f;

        /// <summary>Every prefab-local wall sample for the building being planned. The list itself
        /// is shared by all instances of that prefab; only sampled points are transformed.</summary>
        private List<BuildingSurfaceProbe.SurfacePoint> m_Surface =
            new List<BuildingSurfaceProbe.SurfacePoint>(0);
        private readonly Dictionary<Entity, List<BuildingSurfaceProbe.SurfacePoint>> m_SurfaceTemplates =
            new Dictionary<Entity, List<BuildingSurfaceProbe.SurfacePoint>>();
        private Transform m_SurfaceTransform;
        private int m_CachedSurfacePointCount;
        private bool m_SurfaceTemplateCacheHit;

        private void PrepareSurfaceTemplate(Entity building)
        {
            m_SurfaceTransform = m_EntityManager.GetComponentData<Transform>(building);
            Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;

            List<BuildingSurfaceProbe.SurfacePoint> cached;
            if (m_SurfaceTemplates.TryGetValue(prefab, out cached))
            {
                m_Surface = cached;
                m_SurfaceTemplateCacheHit = true;
                return;
            }

            var collected = new List<BuildingSurfaceProbe.SurfacePoint>(2048);
            BuildingSurfaceProbe.CollectLocalFacadePoints(
                m_EntityManager,
                building,
                collected);

            m_Surface = collected;
            m_SurfaceTemplateCacheHit = false;

            // An empty result often means the game's mesh buffers have not become resident yet.
            // Do not remember it: a later pass must be allowed to try again.
            if (collected.Count == 0)
            {
                return;
            }

            if (m_CachedSurfacePointCount + collected.Count > MaxCachedSurfacePoints)
            {
                m_SurfaceTemplates.Clear();
                m_CachedSurfacePointCount = 0;
            }

            m_SurfaceTemplates[prefab] = collected;
            m_CachedSurfacePointCount += collected.Count;
        }

        /// <summary>Which families the last plan actually used, so "is it even placing graffiti?"
        /// is answered by the log rather than by reading the code.</summary>
        private readonly Dictionary<OverlayFamily, int> m_LastFamilies = new Dictionary<OverlayFamily, int>();

        /// <summary>
        /// Builds every side of the building, not just the one <see cref="Side"/> happens to name.
        ///
        /// `Side` stays for the single-facade diagnostics - the height sweep, the projection
        /// flip - where the point is to vary one thing at a time. A real weathering plan wants all
        /// of them: a building is not dirty only where the camera first looked at it.
        /// </summary>
        private bool TryResolveAllFacades(Entity building, out string failureReason)
        {
            m_Facades.Clear();
            failureReason = null;

            if (!m_EntityManager.Exists(building)
                || !m_EntityManager.HasComponent<Transform>(building)
                || !m_EntityManager.HasComponent<PrefabRef>(building))
            {
                failureReason = "building has no Transform/PrefabRef";
                return false;
            }

            Entity buildingPrefab = m_EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
            if (!m_EntityManager.HasComponent<ObjectGeometryData>(buildingPrefab))
            {
                failureReason = "building prefab has no ObjectGeometryData, so it has no bounds to place against";
                return false;
            }

            Transform transform = m_EntityManager.GetComponentData<Transform>(building);
            ObjectGeometryData geometry = m_EntityManager.GetComponentData<ObjectGeometryData>(buildingPrefab);

            for (int side = 0; side < 4; side++)
            {
                BuildingFacade facade;
                if (BuildingFacade.TryBuild(transform, geometry, (FacadeSide)side, out facade) && facade.IsValid)
                {
                    m_Facades.Add(facade);
                }
            }

            if (m_Facades.Count == 0)
            {
                failureReason = "prefab bounds produced no usable facade on any side";
                return false;
            }

            float area = 0f;
            for (int i = 0; i < m_Facades.Count; i++)
            {
                area += m_Facades[i].Width * m_Facades[i].Height;
            }

            m_WallScale = math.clamp(area / ReferenceWallArea, 0.5f, 4f);
            return true;
        }

        private bool TryResolveFacade(Entity building, out BuildingFacade facade, out string failureReason)
        {
            Transform ignored;
            return TryResolveFacade(building, out facade, out ignored, out failureReason);
        }

        private bool TryResolveFacade(Entity building, out BuildingFacade facade, out Transform transform, out string failureReason)
        {
            facade = default;
            transform = default;
            failureReason = null;

            if (!m_EntityManager.Exists(building))
            {
                failureReason = "building entity no longer exists";
                return false;
            }

            if (!m_EntityManager.HasComponent<Transform>(building)
                || !m_EntityManager.HasComponent<PrefabRef>(building))
            {
                failureReason = "building has no Transform/PrefabRef";
                return false;
            }

            Entity buildingPrefab = m_EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
            if (!m_EntityManager.HasComponent<ObjectGeometryData>(buildingPrefab))
            {
                failureReason = "building prefab has no ObjectGeometryData, so it has no bounds to place against";
                return false;
            }

            transform = m_EntityManager.GetComponentData<Transform>(building);
            ObjectGeometryData geometry = m_EntityManager.GetComponentData<ObjectGeometryData>(buildingPrefab);

            if (!BuildingFacade.TryBuild(transform, geometry, Side, out facade))
            {
                failureReason = "prefab bounds produced no usable " + Side + " facade";
                return false;
            }

            return true;
        }

        /// <summary>
        /// One decal, the given one, dead centre of the facade. No profile, no randomness, no
        /// subtlety - the question this answers is "does a decal render on a wall at all", and
        /// everything that could hide the answer has been taken out.
        /// </summary>
        /// <summary>
        /// A contact sheet, not a single sample: a row of different decals along the bottom of
        /// the facade, at eye level, plus one control decal laid flat on the ground in front of
        /// the building.
        ///
        /// The control is the point of the whole thing. A decal that shows on the ground but not
        /// on the wall means the projection direction is wrong; neither showing means the decal
        /// itself draws nothing we can see; both showing means we are finished arguing.
        ///
        /// The previous single-sample version placed one decal at the centre of the facade,
        /// which on an eighty-metre tower is forty metres up, and it happened to pick a "wet
        /// effect" decal - a roughness change, at night, on a dark wall. It rendered
        /// (<c>passed=1</c>) and was still invisible for two entirely mundane reasons at once.
        /// </summary>
        /// <summary>
        /// The same decal, six times along the facade, each one at a different height.
        ///
        /// This replaces the earlier "five different decals in a row", which had done its job: it
        /// established that the decals being chosen do paint base colour and that they get real
        /// render batches. What it could not answer is *where the wall actually is*. A building's
        /// ObjectGeometryData bounds describe its outermost extent, so the plane we place against
        /// can be well in front of the masonry, and a projector one metre deep sitting on that
        /// plane encloses nothing but air.
        ///
        /// Six depths in one press means one screenshot tells us the answer instead of six rounds
        /// of guessing. Whichever one lands is the offset the real mod needs to derive per prefab.
        /// </summary>
        public bool ApplyHeightSweep(Entity building, DecalPrefabInfo decal, out int placed, out string report, out string failureReason)
        {
            placed = 0;
            report = null;

            if (decal == null)
            {
                failureReason = "no decal to sweep with";
                return false;
            }

            BuildingFacade facade;
            Transform transform;
            if (!TryResolveFacade(building, out facade, out transform, out failureReason))
            {
                return false;
            }

            // Heights above the building's base, not depths into the wall.
            //
            // The measurement that turned this around: a hand-placed decal that works sits 20 m
            // up; ours were all pinned just above the base, and the first thing ever reported was
            // that they landed "on the ground in front of the building, some a little higher".
            // These blocks have a podium and an entrance canopy that stand proud of the brick
            // above, so a projector a metre or two off the ground straddles horizontal surfaces
            // and paints those instead of the facade.
            float[] heights = { 3f, 8f, 13f, 18f, 23f, 28f };

            m_Pending.Clear();
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            for (int i = 0; i < heights.Length; i++)
            {
                float u = math.lerp(0.12f, 0.88f, i / (float)(heights.Length - 1));
                float v = math.saturate(heights[i] / math.max(facade.Height, 0.01f));
                float3 position = PlacementPosition(facade, decal, u, v, 0f);

                m_Pending.Add(new PendingElement
                {
                    Prefab = decal.PrefabEntity,
                    Position = position,
                    Rotation = facade.DecalRotation(InvertProjection),
                    Family = decal.Families,
                    FacadeIndex = -1,
                });

                sb.AppendLine();
                sb.AppendFormat("  height {0,4:0}m above base  at ({1:0.0},{2:0.0},{3:0.0})", heights[i], position.x, position.y, position.z);
            }

            Remove(building);

            OverlayRecord record = new OverlayRecord { Profile = default };
            SpawnPending(building, record);
            m_Pending.Clear();

            if (record.Elements.Count == 0)
            {
                failureReason = "the command buffer produced no overlay entities";
                return false;
            }

            m_Records[building] = record;
            placed = record.Elements.Count;
            report = sb.ToString();
            return true;
        }

        /// <summary>
        /// One decal, flat on the ground, at a world position of the caller's choosing. No
        /// building, no facade, no rotation, no override system.
        ///
        /// This is the floor of the whole investigation, and it is the one thing confirmed to
        /// work in game: if it is visible then entity creation, culling, batching and the
        /// material are all sound, and any remaining problem is specific to projecting onto a
        /// vertical surface. Filed under <see cref="Entity.Null"/> so it lives and dies with the
        /// rest of the overlays without pretending to belong to a building.
        /// </summary>
        public bool ApplyGroundProbe(float3 position, DecalPrefabInfo decal, out string failureReason)
        {
            failureReason = null;

            if (decal == null)
            {
                failureReason = "no decal to probe with";
                return false;
            }

            m_Pending.Clear();
            m_Pending.Add(new PendingElement
            {
                Prefab = decal.PrefabEntity,
                Position = position,
                Rotation = quaternion.identity,
                Family = OverlayFamily.None,
                FacadeIndex = -1,
            });

            Remove(Entity.Null);

            OverlayRecord record = new OverlayRecord { Profile = default };
            SpawnPending(Entity.Null, record);
            m_Pending.Clear();

            if (record.Elements.Count == 0)
            {
                failureReason = "the command buffer produced no probe entity";
                return false;
            }

            m_Records[Entity.Null] = record;
            return true;
        }

        /// <summary>
        /// Places one decal where a ray actually struck the building, oriented by the surface it
        /// struck.
        ///
        /// This is the placement the game itself uses. `Snap.ObjectSurface` - vanilla, merely
        /// exposed by Extra Detailing Tools - sets the position to `controlPoint.m_HitPosition`,
        /// the point where a raycast met the real mesh.
        ///
        /// Everything else in this renderer computes a plane from `ObjectGeometryData.m_Bounds`
        /// instead, which is the building's *outermost* extent: balconies, cornices, canopies,
        /// roof plant. A decal is a projector - a box roughly half a metre deep that paints only
        /// the geometry it contains - so a wall standing behind that outer plane falls outside the
        /// box entirely. That is consistent with every observation from the decal rounds: correct
        /// bounds, a real render batch, culling passed, and nothing on screen.
        ///
        /// So this method exists to settle the question with one keypress rather than another
        /// round of reasoning. If a decal placed at a genuine hit point appears, the bounding-box
        /// plane was the fault and automatic placement becomes a matter of raycasting buildings
        /// programmatically. If it still does not appear, the fault is elsewhere - most likely in
        /// how the entity is created - and this rules out a whole hypothesis for the cost of one
        /// test.
        /// </summary>
        public bool ApplyAtSurface(float3 hitPosition, float3 hitNormal, DecalPrefabInfo decal, out string failureReason)
        {
            failureReason = null;

            if (decal == null)
            {
                failureReason = "no decal selected to place";
                return false;
            }

            float3 up = math.normalizesafe(hitNormal, math.up());
            if (InvertProjection)
            {
                up = -up;
            }

            // Any direction perpendicular to the surface normal will do for the decal's own
            // rotation about it; only the axis it projects along matters here.
            float3 reference = math.abs(math.dot(up, math.up())) > 0.99f ? math.forward() : math.up();
            float3 tangent = math.normalizesafe(math.cross(reference, up), math.forward());

            m_Pending.Clear();
            m_Pending.Add(new PendingElement
            {
                Prefab = decal.PrefabEntity,
                Position = hitPosition + up * NormalOffsetMetres,
                Rotation = quaternion.LookRotationSafe(tangent, up),
                Family = OverlayFamily.None,
                FacadeIndex = -1,
            });

            Remove(Entity.Null);

            OverlayRecord record = new OverlayRecord { Profile = default };
            SpawnPending(Entity.Null, record);
            m_Pending.Clear();

            if (record.Elements.Count == 0)
            {
                failureReason = "the command buffer produced no entity";
                return false;
            }

            m_Records[Entity.Null] = record;
            return true;
        }

        /// <summary>How many of the last plan's marks landed on real geometry, and how many were
        /// skipped because the ray missed. A miss must never place a decal at the bounding box:
        /// that is the path which produced marks floating in front of the building.</summary>
        private int m_SurfaceHits;
        private int m_SurfaceRecoveries;
        private int m_SurfaceMisses;
        private int m_SurfaceSides;
        private int m_NextFacade;
        private int m_FacadeSequenceStep;
        private int m_PlannedFacadeMask;
        private int m_FrontMarkCount;
        private int m_FrontGraffitiCount;

        public string LastPlacementReport
        {
            get
            {
                System.Text.StringBuilder families = new System.Text.StringBuilder();
                foreach (KeyValuePair<OverlayFamily, int> pair in m_LastFamilies)
                {
                    if (families.Length > 0)
                    {
                        families.Append(", ");
                    }

                    families.Append(pair.Value).Append(" ").Append(pair.Key);
                }

                if (families.Length == 0)
                {
                    families.Append("none");
                }

                return families + " | wall scale " + m_WallScale.ToString("0.0") + "x | "
                     + m_SurfaceHits + " raycast hit(s), " + m_SurfaceRecoveries
                     + " nearest-mesh recovery hit(s), " + m_SurfaceMisses
                     + " skipped (no mesh surface), facades=" + DescribeSurfaceSides()
                     + ", covered=" + CountPlannedFacades() + "/" + m_Facades.Count
                     + ", front=" + m_FrontMarkCount + " mark(s) including "
                     + m_FrontGraffitiCount + " graffiti, mesh template="
                     + (m_SurfaceTemplateCacheHit ? "cached" : "new");
            }
        }

        private int CountPlannedFacades()
        {
            int count = 0;
            for (int i = 0; i < m_Facades.Count; i++)
            {
                if ((m_PlannedFacadeMask & (1 << i)) != 0)
                {
                    count++;
                }
            }

            return count;
        }

        private int FindFacade(FacadeSide side)
        {
            for (int i = 0; i < m_Facades.Count; i++)
            {
                if (m_Facades[i].Side == side)
                {
                    return i;
                }
            }

            return 0;
        }

        /// <summary>
        /// Gives the road-facing wall a little more visual weight without abandoning the other
        /// sides. Five successful placements follow Front, Right, Front, Back, Left: 40% on the
        /// facade a player normally sees and 20% on each remaining side. The minimum-facade pass
        /// still repairs any side whose real mesh could not accept its scheduled mark.
        /// </summary>
        private int PreferredFacadeForStep(int step)
        {
            switch (step % 5)
            {
                case 0:
                case 2:
                    return FindFacade(FacadeSide.Front);
                case 1:
                    return FindFacade(FacadeSide.Right);
                case 3:
                    return FindFacade(FacadeSide.Back);
                default:
                    return FindFacade(FacadeSide.Left);
            }
        }

        private string DescribeSurfaceSides()
        {
            if (m_SurfaceSides == 0)
            {
                return "none";
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int side = 0; side < 4; side++)
            {
                if ((m_SurfaceSides & (1 << side)) == 0)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(',');
                }

                sb.Append((FacadeSide)side);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Works out where a mark actually goes, by casting a ray at the building.
        ///
        /// The facade rectangle is still what decides *where on the wall* a mark belongs - it is a
        /// good map of the building's face. What it is not is a good measure of *depth*: it comes
        /// from `ObjectGeometryData.m_Bounds`, the outermost extent, which can stand metres in
        /// front of the wall it is meant to describe. Since a decal projector is a box half a metre
        /// deep, that gap is the whole reason none of them were ever visible.
        ///
        /// So the rectangle proposes a point, a ray finds the surface under it, and the decal goes
        /// where the ray landed - with its rotation taken from the surface rather than from the
        /// box, which also means articulated facades, recesses and corner blocks get marks that lie
        /// on them instead of floating off them.
        ///
        /// When the ray misses - a gap between wings, a point over an archway, or an asset with no
        /// accessible mesh buffers - the mark is skipped. A bounding-box fallback would recreate
        /// the very bug this raycast is meant to solve.
        /// </summary>
        private bool TryResolvePlacement(
            Entity building,
            in BuildingFacade facade,
            float u,
            float v,
            ref Unity.Mathematics.Random rng,
            out float3 position,
            out quaternion rotation)
        {
            position = default;
            rotation = default;
            float3 planePoint = facade.PointAt(u, v);

            // Pick from the wall we already measured, rather than casting a fresh ray for every
            // mark. Same question, asked once per building instead of once per mark.
            if (TryPickCollectedSurface(facade, planePoint, ref rng, out position, out rotation))
            {
                m_SurfaceHits++;
                m_SurfaceSides |= 1 << (int)facade.Side;
                return true;
            }

            // Start well outside the outermost extent and travel inward, so the first thing hit is
            // the nearest real surface facing us.
            const float standOff = 25f;

            float3 origin = planePoint + facade.Normal * standOff;
            float3 hit;
            float3 normal;

            if (building != Entity.Null
                && BuildingSurfaceProbe.TryHit(
                       m_EntityManager, building, origin, -facade.Normal, standOff * 2f, out hit, out normal))
            {
                float3 up = InvertProjection ? -normal : normal;

                // The decal's local forward is the direction its artwork reads as *up*, so on a
                // wall it has to be the world's vertical, flattened onto that wall. Passing a
                // horizontal tangent instead - which is what this did first - is a perfectly valid
                // rotation about the same projection axis, and turns every graffito on its side.
                //
                // `BuildingFacade.DecalRotation` had this right all along: LookRotationSafe(Up,
                // Normal). This is the same statement made against a measured surface instead of a
                // flat rectangle.
                float3 alongSurface = math.up() - up * math.dot(math.up(), up);
                float3 forward = math.normalizesafe(alongSurface, facade.Up);

                position = hit + up * NormalOffsetMetres;
                rotation = quaternion.LookRotationSafe(forward, up);
                m_SurfaceHits++;
                m_SurfaceSides |= 1 << (int)facade.Side;
                return true;
            }

            // A point proposed by the bounding rectangle can lie over a real opening even when
            // the facade has plenty of usable wall. Recover by choosing a nearby triangle from
            // the building mesh itself. This is still an exact surface placement; unlike the old
            // bounding-box fallback it cannot float in front of the building.
            if (building != Entity.Null
                && BuildingSurfaceProbe.TryFindFacadePoint(
                       m_EntityManager, building, planePoint, facade.Normal, out hit, out normal))
            {
                float3 up = InvertProjection ? -normal : normal;
                float3 alongSurface = math.up() - up * math.dot(math.up(), up);
                float3 forward = math.normalizesafe(alongSurface, facade.Up);

                position = hit + up * NormalOffsetMetres;
                rotation = quaternion.LookRotationSafe(forward, up);
                m_SurfaceRecoveries++;
                m_SurfaceSides |= 1 << (int)facade.Side;
                return true;
            }

            m_SurfaceMisses++;
            return false;
        }

        /// <summary>
        /// Chooses a piece of the collected wall that faces the way this facade does, preferring
        /// the one nearest the point the facade rectangle proposed.
        ///
        /// Keeping the rectangle in the loop is deliberate. It is a poor description of *depth* -
        /// that is what made every early decal invisible - but a good one of *where on a building*
        /// a mark belongs, including the bias towards the bottom of a wall. The measured surface
        /// supplies the truth about position; the rectangle still supplies the intent.
        ///
        /// Sampled rather than searched exhaustively: a handful of random candidates, keeping the
        /// closest, gives a mark that lands near the intended spot without walking thousands of
        /// triangles per mark - which is the entire cost saving this is here for.
        /// </summary>
        private bool TryPickCollectedSurface(
            in BuildingFacade facade,
            float3 target,
            ref Unity.Mathematics.Random rng,
            out float3 position,
            out quaternion rotation)
        {
            position = default;
            rotation = default;

            if (m_Surface.Count == 0)
            {
                return false;
            }

            const int samples = 24;

            int best = -1;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < samples; i++)
            {
                int candidate = rng.NextInt(0, m_Surface.Count);
                BuildingSurfaceProbe.SurfacePoint point = m_Surface[candidate];
                float3 worldNormal = math.mul(m_SurfaceTransform.m_Rotation, point.m_Normal);

                // Facing roughly the same way as this side of the building. Loose, so that
                // chamfers, bays and angled wings stay eligible.
                if (math.dot(worldNormal, facade.Normal) < 0.4f)
                {
                    continue;
                }

                float3 worldPosition = m_SurfaceTransform.m_Position
                                     + math.mul(m_SurfaceTransform.m_Rotation, point.m_Position);
                float distance = math.distancesq(worldPosition, target);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            if (best < 0)
            {
                return false;
            }

            BuildingSurfaceProbe.SurfacePoint chosen = m_Surface[best];
            float3 chosenPosition = m_SurfaceTransform.m_Position
                                  + math.mul(m_SurfaceTransform.m_Rotation, chosen.m_Position);
            float3 chosenNormal = math.mul(m_SurfaceTransform.m_Rotation, chosen.m_Normal);

            float3 up = InvertProjection ? -chosenNormal : chosenNormal;
            float3 alongSurface = math.up() - up * math.dot(math.up(), up);
            float3 forward = math.normalizesafe(alongSurface, facade.Up);

            position = chosenPosition + up * NormalOffsetMetres;
            rotation = quaternion.LookRotationSafe(forward, up);
            return true;
        }

        private float3 PlacementPosition(in BuildingFacade facade, DecalPrefabInfo decal, float u, float v)
        {
            return PlacementPosition(facade, decal, u, v, NormalOffsetMetres);
        }

        private float3 PlacementPosition(in BuildingFacade facade, DecalPrefabInfo decal, float u, float v, float offsetMetres)
        {
            return facade.PointAt(u, v) + facade.Normal * offsetMetres;
        }

        /// <summary>
        /// Decides how many marks each family gets, then places them.
        ///
        /// The families used to be planned one after another, each taking what it wanted until the
        /// per-building cap ran out. On a badly weathered tower that is not a budget, it is a
        /// queue: dirt alone asked for fifteen marks against a cap of ten, took all of them, and
        /// every family behind it - including graffiti - got nothing. The building came out
        /// uniformly grubby instead of looking like a place in trouble, and "why are there never
        /// any graffiti?" turned out to be a scheduling question rather than a weighting one.
        ///
        /// The budget is now shared out in proportion, by largest remainder: everyone is scaled
        /// down together, whole marks go to the largest claims, and what is left over goes to the
        /// families with the biggest unmet fractions. A family that asked for a mark and can be
        /// afforded one gets one, which is what keeps rust on the industrial buildings and
        /// graffiti on the commercial ones visible at the top of the scale rather than crowded out
        /// by dirt.
        /// </summary>
        private void PlanRequiredFrontGraffiti(
            Entity building,
            in BuildingVisualProfile profile,
            ref Unity.Mathematics.Random rng)
        {
            if (math.saturate(profile.Graffiti) <= 0.01f
                || ForcedDecal != null
                || AllowNonBuildingDecals
                || !m_Catalog.HasAutomaticFamily(OverlayFamily.Graffiti))
            {
                return;
            }

            int frontIndex = FindFacade(FacadeSide.Front);
            BuildingFacade front = m_Facades[frontIndex];
            if (!m_Catalog.HasAutomaticFamilyThatFits(
                OverlayFamily.Graffiti,
                front.Width,
                front.Height))
            {
                return;
            }

            // A different catalogue pick can have a different footprint. Give the whitelist a few
            // chances to supply something that both fits the facade and finds solid wall between
            // windows, while keeping this one-off visibility guarantee tightly bounded.
            for (int decalAttempt = 0; decalAttempt < 4; decalAttempt++)
            {
                bool familyMatched;
                DecalPrefabInfo decal = m_Catalog.Pick(
                    OverlayFamily.Graffiti,
                    false,
                    front.Width,
                    front.Height,
                    ref rng,
                    out familyMatched);

                if (decal == null)
                {
                    return;
                }

                for (int placementAttempt = 0;
                     placementAttempt < PlacementAttemptsPerFacade;
                     placementAttempt++)
                {
                    if (TryPlanMarkOnFacade(
                        building,
                        OverlayFamily.Graffiti,
                        decal,
                        frontIndex,
                        true,
                        ref rng))
                    {
                        m_NextFacade = (frontIndex + 1) % m_Facades.Count;
                        return;
                    }
                }
            }
        }

        private void PlanAllFamilies(Entity building, in BuildingVisualProfile profile, ref Unity.Mathematics.Random rng)
        {
            float scale = Mod.Settings != null ? Mod.Settings.IntensityScale : 1f;

            float[] wanted = new float[s_Families.Length];
            int[] counts = new int[s_Families.Length];
            float total = 0f;

            for (int i = 0; i < s_Families.Length; i++)
            {
                // The automatic catalogue is source-whitelisted. Do not reserve part of this
                // building's finite budget for a family for which no approved asset is installed.
                if (ForcedDecal == null
                    && !AllowNonBuildingDecals
                    && !m_Catalog.HasAutomaticFamily(s_Families[i]))
                {
                    continue;
                }

                float intensity = math.saturate(profile.GetIntensity(s_Families[i]));
                if (intensity <= 0.01f)
                {
                    continue;
                }

                wanted[i] = intensity * MaxPerFamily * m_WallScale * scale;
                total += wanted[i];
            }

            if (total <= 0f)
            {
                return;
            }

            // Scale the whole request down together when it does not fit, so the *mix* survives
            // even though the quantity cannot.
            float fit = total > MaxPerBuilding ? MaxPerBuilding / total : 1f;

            int spent = 0;
            for (int i = 0; i < wanted.Length; i++)
            {
                counts[i] = (int)math.floor(wanted[i] * fit);
                spent += counts[i];
            }

            // Largest remainder: whatever the flooring left unspent goes to the families that came
            // closest to earning another mark.
            while (spent < MaxPerBuilding)
            {
                int best = -1;
                float bestRemainder = 0.0001f;

                for (int i = 0; i < wanted.Length; i++)
                {
                    if (wanted[i] <= 0f)
                    {
                        continue;
                    }

                    float remainder = wanted[i] * fit - counts[i];
                    if (remainder > bestRemainder)
                    {
                        bestRemainder = remainder;
                        best = i;
                    }
                }

                if (best < 0)
                {
                    break;
                }

                counts[best]++;
                spent++;
            }

            for (int i = 0; i < s_Families.Length; i++)
            {
                int alreadyPlanned;
                m_LastFamilies.TryGetValue(s_Families[i], out alreadyPlanned);
                int remaining = math.max(0, counts[i] - alreadyPlanned);
                if (remaining > 0)
                {
                    PlanMarks(building, s_Families[i], remaining, ref rng);
                }
            }
        }

        private void PlanFamily(
            Entity building,
            OverlayFamily family,
            in BuildingVisualProfile profile,
            ref Unity.Mathematics.Random rng)
        {
            if (m_Pending.Count >= MaxPerBuilding)
            {
                return;
            }

            float intensity = math.saturate(profile.GetIntensity(family));
            if (intensity <= 0.01f)
            {
                return;
            }

            // Intensity buys count here. In the real mod it should mostly buy opacity and
            // coverage instead; count is simply the only knob a fixed decal prefab gives us.
            //
            // Rounded plainly, and deliberately not by chance.
            //
            // This was briefly stochastic - the fraction taken as the probability of one more
            // mark - to rescue the bottom of the scale, which a coarse `round` had been discarding
            // whole. It worked, and it broke something more important: a building at 31% could end
            // up bare while its neighbour at 25% carried two marks. The number in the tooltip and
            // the marks on the wall told different stories, and being able to read a building by
            // looking at it is the entire point of this mod.
            //
            // The variety that dither was providing is already present, one level up: individual
            // buildings differ in their *weathering value* by up to threefold, seeded per
            // building, and that difference is what the tooltip reports. Randomising the mark
            // count on top of it was a second roll of the dice over the first, and only the first
            // one is legible.
            //
            // The bottom of the scale is kept by giving the count more resolution instead, so
            // rounding has something to round.
            float scale = Mod.Settings != null ? Mod.Settings.IntensityScale : 1f;
            float exact = intensity * MaxPerFamily * m_WallScale * scale;
            int count = (int)math.floor(exact + 0.5f);

            PlanMarks(building, family, count, ref rng);
        }

        /// <summary>
        /// Guarantees that a building the mod calls weathered is not pixel-identical to a clean one.
        ///
        /// Marks scale with wall area, which is right - weathering is a property of surface - but
        /// it means a small house needs twice the intensity of a block to earn its first whole
        /// mark, and below that it rounds to nothing and is rejected. Measured: in a whole session
        /// of play not one building at the minimum wall scale was ever detailed.
        ///
        /// So the smallest unit of weathering is one mark, not zero. A building whose tooltip says
        /// "Aged" gets at least something on it; one the mod considers clean still gets nothing,
        /// because the threshold is on intensity rather than on having been asked.
        /// </summary>
        private void PlanMinimumMark(Entity building, in BuildingVisualProfile profile, ref Unity.Mathematics.Random rng)
        {
            if (m_Pending.Count > 0)
            {
                return;
            }

            OverlayFamily strongest = OverlayFamily.None;
            float best = 0.12f;

            foreach (OverlayFamily family in s_Families)
            {
                if (ForcedDecal == null
                    && !AllowNonBuildingDecals
                    && !m_Catalog.HasAutomaticFamily(family))
                {
                    continue;
                }

                float intensity = math.saturate(profile.GetIntensity(family));
                if (intensity > best)
                {
                    best = intensity;
                    strongest = family;
                }
            }

            if (strongest == OverlayFamily.None)
            {
                return;
            }

            PlanMarks(building, strongest, 1, ref rng);
        }

        private static readonly OverlayFamily[] s_Families =
        {
            OverlayFamily.Dirt,
            OverlayFamily.Stain,
            OverlayFamily.Crack,
            OverlayFamily.Moss,
            OverlayFamily.Rust,
            OverlayFamily.Graffiti,
        };

        /// <summary>
        /// Gives every usable side of the building at least one mark.
        ///
        /// The ordinary planner chooses a prefab against the largest facade, then walks around
        /// the building. That spreads marks well when the footprint is roughly square, but on a
        /// long thin building the chosen projector can be wider than either short end. Both ends
        /// are skipped and all the visible weathering accumulates on the long walls.
        ///
        /// This recovery pass works the other way round: for each side still empty it asks the
        /// catalogue for a prefab that fits that side specifically. A side only earns its minimum
        /// when a mesh raycast finds real building geometry, so party walls, gaps between wings
        /// and facade rectangles with no usable surface are not decorated by force.
        /// </summary>
        private void PlanMinimumMarkPerFacade(
            Entity building,
            in BuildingVisualProfile profile,
            ref Unity.Mathematics.Random rng)
        {
            int firstFacade = m_NextFacade;

            for (int offset = 0; offset < m_Facades.Count; offset++)
            {
                int facadeIndex = (firstFacade + offset) % m_Facades.Count;
                if ((m_PlannedFacadeMask & (1 << facadeIndex)) != 0)
                {
                    continue;
                }

                BuildingFacade facade = m_Facades[facadeIndex];
                OverlayFamily tried = OverlayFamily.None;

                // Prefer the strongest active family, but try the others too. The strongest
                // family may have no approved projector small enough for this particular side.
                for (int familyAttempt = 0; familyAttempt < s_Families.Length; familyAttempt++)
                {
                    OverlayFamily strongest = OverlayFamily.None;
                    float best = 0.12f;

                    for (int i = 0; i < s_Families.Length; i++)
                    {
                        OverlayFamily candidate = s_Families[i];
                        if ((tried & candidate) != 0
                            || (ForcedDecal == null
                                && !AllowNonBuildingDecals
                                && !m_Catalog.HasAutomaticFamily(candidate)))
                        {
                            continue;
                        }

                        float intensity = math.saturate(profile.GetIntensity(candidate));
                        if (intensity > best)
                        {
                            best = intensity;
                            strongest = candidate;
                        }
                    }

                    if (strongest == OverlayFamily.None)
                    {
                        break;
                    }

                    tried |= strongest;

                    bool familyMatched;
                    DecalPrefabInfo decal = ForcedDecal;
                    if (decal == null)
                    {
                        decal = m_Catalog.Pick(
                            strongest,
                            AllowNonBuildingDecals,
                            facade.Width,
                            facade.Height,
                            ref rng,
                            out familyMatched);
                    }

                    if (decal == null)
                    {
                        continue;
                    }

                    bool planned = false;
                    for (int placementAttempt = 0;
                         placementAttempt < PlacementAttemptsPerFacade;
                         placementAttempt++)
                    {
                        if (TryPlanMarkOnFacade(
                            building,
                            strongest,
                            decal,
                            facadeIndex,
                            ref rng))
                        {
                            planned = true;
                            break;
                        }
                    }

                    if (!planned)
                    {
                        continue;
                    }

                    // Normally the proportional planner leaves room because four sides are far
                    // below the cap. If it did spend the full budget on fewer sides, replace one
                    // duplicate rather than exceeding the entity limit.
                    TrimDuplicateToBuildingBudget();
                    m_NextFacade = (facadeIndex + 1) % m_Facades.Count;
                    break;
                }
            }
        }

        private void PlanMarks(
            Entity building,
            OverlayFamily family,
            int count,
            ref Unity.Mathematics.Random rng)
        {
            for (int i = 0; i < count && m_Pending.Count < MaxPerBuilding; i++)
            {
                // Choose the facade first and only then choose a projector that fits it. The old
                // order picked against the building's largest wall, so a perfectly valid 8 m mark
                // selected for a 30 m side was retried on a 5 m street facade, rejected, and then
                // placed back on a long side. This made the round-robin look area-weighted even
                // though it was not. Per-facade fitting gives short fronts the same opportunity as
                // long flanks; real-mesh placement can still reject a doorway or party wall.
                int startFacade = PreferredFacadeForStep(m_FacadeSequenceStep);
                bool planned = false;

                for (int facadeOffset = 0; facadeOffset < m_Facades.Count; facadeOffset++)
                {
                    int facadeIndex = (startFacade + facadeOffset) % m_Facades.Count;
                    BuildingFacade facade = m_Facades[facadeIndex];

                    bool familyMatched;
                    DecalPrefabInfo decal = ForcedDecal;
                    if (decal == null)
                    {
                        decal = m_Catalog.Pick(
                            family,
                            AllowNonBuildingDecals,
                            facade.Width,
                            facade.Height,
                            ref rng,
                            out familyMatched);
                    }

                    if (decal == null)
                    {
                        continue;
                    }

                    for (int placementAttempt = 0;
                         placementAttempt < PlacementAttemptsPerFacade;
                         placementAttempt++)
                    {
                        if (TryPlanMarkOnFacade(building, family, decal, facadeIndex, ref rng))
                        {
                            m_NextFacade = (facadeIndex + 1) % m_Facades.Count;
                            m_FacadeSequenceStep++;
                            planned = true;
                            break;
                        }
                    }

                    if (planned)
                    {
                        break;
                    }
                }

                if (!planned)
                {
                    // The misses are already counted by TryResolvePlacement. Continue planning
                    // other families: another decal size and another set of samples may succeed.
                    continue;
                }
            }
        }

        private bool TryPlanMarkOnFacade(
            Entity building,
            OverlayFamily family,
            DecalPrefabInfo decal,
            int facadeIndex,
            ref Unity.Mathematics.Random rng)
        {
            return TryPlanMarkOnFacade(
                building,
                family,
                decal,
                facadeIndex,
                false,
                ref rng);
        }

        private bool TryPlanMarkOnFacade(
            Entity building,
            OverlayFamily family,
            DecalPrefabInfo decal,
            int facadeIndex,
            bool preferLowerGraffiti,
            ref Unity.Mathematics.Random rng)
        {
            BuildingFacade facade = m_Facades[facadeIndex];

            // A projector cannot be clipped to its owning building. If it overhangs this
            // rectangle it can paint an attached neighbour, which is how a Maintained
            // row house appeared to retain two stains that actually belonged next door.
            if (!AllowNonBuildingDecals
                && (decal.Size.x > facade.Width || decal.Size.z > facade.Height))
            {
                return false;
            }

            // Keep the decal inside the facade rectangle. A projector that overhangs the
            // wall does not merely look wrong - it paints whatever else is behind it.
            float marginU = math.min(0.5f, decal.Size.x * 0.5f / math.max(facade.Width, 0.01f));
            float marginV = math.min(0.5f, decal.Size.z * 0.5f / math.max(facade.Height, 0.01f));
            float u = math.lerp(marginU, 1f - marginU, rng.NextFloat());

            float verticalPosition = preferLowerGraffiti && family == OverlayFamily.Graffiti
                ? math.square(rng.NextFloat()) * 0.32f
                : SampleVerticalPosition(family, ref rng);
            float v = math.lerp(
                marginV,
                1f - marginV,
                verticalPosition);

            float3 position;
            quaternion rotation;
            if (!TryResolvePlacement(building, facade, u, v, ref rng, out position, out rotation))
            {
                return false;
            }

            // The real mesh sample can move away from the rectangle's proposed point. Recheck
            // the final centre so the full decal still lies inside the facade.
            float3 fromCenter = position - facade.Center;
            float halfWidth = facade.Width * 0.5f;
            float halfHeight = facade.Height * 0.5f;
            if (!AllowNonBuildingDecals
                && (math.abs(math.dot(fromCenter, facade.Tangent)) + decal.Size.x * 0.5f > halfWidth
                    || math.abs(math.dot(fromCenter, facade.Up)) + decal.Size.z * 0.5f > halfHeight))
            {
                return false;
            }

            m_Pending.Add(new PendingElement
            {
                Prefab = decal.PrefabEntity,
                Position = position,
                Rotation = rotation,
                Family = family,
                FacadeIndex = facadeIndex,
            });

            int already;
            m_LastFamilies.TryGetValue(family, out already);
            m_LastFamilies[family] = already + 1;
            m_PlannedFacadeMask |= 1 << facadeIndex;
            if (facade.Side == FacadeSide.Front)
            {
                m_FrontMarkCount++;
                if (family == OverlayFamily.Graffiti)
                {
                    m_FrontGraffitiCount++;
                }
            }
            return true;
        }

        private void TrimDuplicateToBuildingBudget()
        {
            if (m_Pending.Count <= MaxPerBuilding)
            {
                return;
            }

            int[] perFacade = new int[m_Facades.Count];
            for (int i = 0; i < m_Pending.Count; i++)
            {
                int facadeIndex = m_Pending[i].FacadeIndex;
                if (facadeIndex >= 0 && facadeIndex < perFacade.Length)
                {
                    perFacade[facadeIndex]++;
                }
            }

            // Keep the mark just added. Because it filled an empty side, a previous side must
            // contain a duplicate whenever the old plan had already reached the cap.
            for (int i = m_Pending.Count - 2; i >= 0; i--)
            {
                PendingElement candidate = m_Pending[i];
                if (candidate.FacadeIndex < 0
                    || candidate.FacadeIndex >= perFacade.Length
                    || perFacade[candidate.FacadeIndex] <= 1
                    || (candidate.Family == OverlayFamily.Graffiti
                        && m_Facades[candidate.FacadeIndex].Side == FacadeSide.Front
                        && m_FrontGraffitiCount <= 1))
                {
                    continue;
                }

                m_Pending.RemoveAt(i);

                if (m_Facades[candidate.FacadeIndex].Side == FacadeSide.Front)
                {
                    m_FrontMarkCount--;
                    if (candidate.Family == OverlayFamily.Graffiti)
                    {
                        m_FrontGraffitiCount--;
                    }
                }

                int familyCount;
                if (m_LastFamilies.TryGetValue(candidate.Family, out familyCount))
                {
                    if (familyCount <= 1)
                    {
                        m_LastFamilies.Remove(candidate.Family);
                    }
                    else
                    {
                        m_LastFamilies[candidate.Family] = familyCount - 1;
                    }
                }

                return;
            }
        }

        /// <summary>
        /// Chooses the height of a mark within the usable facade rectangle.
        ///
        /// Ordinary weathering accumulates near the base, so its uniform sample is squared.
        /// Graffiti follows a U-shaped distribution instead: half starts from the bottom edge,
        /// half from the top, and the squared distance makes both edges more likely than the
        /// centre without making the middle impossible.
        /// </summary>
        private static float SampleVerticalPosition(
            OverlayFamily family,
            ref Unity.Mathematics.Random rng)
        {
            float t = rng.NextFloat();

            if (family != OverlayFamily.Graffiti)
            {
                return t * t;
            }

            if (t < 0.5f)
            {
                return 2f * t * t;
            }

            float distanceFromTop = 1f - t;
            return 1f - 2f * distanceFromTop * distanceFromTop;
        }

        /// <summary>
        /// Creates the planned entities.
        ///
        /// This goes through an <see cref="EntityCommandBuffer"/> rather than calling
        /// EntityManager.CreateEntity directly, which also means the new entities' identities
        /// only exist after playback - hence the reconciliation pass at the end, which reads
        /// them back out of the overlay query by the building they tagged themselves with.
        /// </summary>
        private void SpawnPending(Entity building, OverlayRecord record)
        {
            EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            try
            {
                for (int i = 0; i < m_Pending.Count; i++)
                {
                    PendingElement pending = m_Pending[i];
                    ObjectData objectData = m_EntityManager.GetComponentData<ObjectData>(pending.Prefab);
                    RuntimeInitialization initialization = GetRuntimeInitialization(pending.Prefab, objectData);

                    // The archetype the prefab system built for this prefab already carries
                    // Created and Updated, so the object, culling and batching systems pick the
                    // entity up on their own - the same path a placed prop takes.
                    Entity element = commandBuffer.CreateEntity(objectData.m_Archetype);
                    commandBuffer.SetComponent(element, new PrefabRef(pending.Prefab));
                    commandBuffer.SetComponent(element, new Transform(pending.Position, pending.Rotation));

                    // Creating the archetype is only the first half of the game's normal object
                    // creation path. GenerateObjectsSystem also disables the empty, enableable
                    // CustomMeshColor buffer and fills PseudoRandomSeed. Leaving the colour buffer
                    // enabled with length zero can send the Burst rendering jobs down a path that
                    // assumes it has been populated. A captured crash ended in lib_burst_generated
                    // while dereferencing a garbage Entity index immediately after decal churn;
                    // mirror the vanilla initialisation here rather than feeding that invalid
                    // state to the renderer.
                    if ((initialization & RuntimeInitialization.DisableCustomMeshColor) != 0)
                    {
                        commandBuffer.SetComponentEnabled<CustomMeshColor>(element, false);
                    }

                    if ((initialization & RuntimeInitialization.SetPseudoRandomSeed) != 0)
                    {
                        uint mixed = (uint)building.Index * 0x9E3779B9u
                                   ^ (uint)pending.Prefab.Index * 0x85EBCA6Bu
                                   ^ (uint)(i + 1);
                        commandBuffer.SetComponent(element, new PseudoRandomSeed((ushort)(mixed ^ (mixed >> 16))));
                    }

                    commandBuffer.AddComponent(element, new WeatheringOverlay(building, pending.Family));
                }

                commandBuffer.Playback(m_EntityManager);
            }
            finally
            {
                commandBuffer.Dispose();
            }

            CollectElementsFor(building, record);
        }

        private RuntimeInitialization GetRuntimeInitialization(Entity prefab, in ObjectData objectData)
        {
            RuntimeInitialization result;
            if (m_RuntimeInitialization.TryGetValue(prefab, out result))
            {
                return result;
            }

            result = RuntimeInitialization.None;
            TypeIndex customMeshColor = TypeManager.GetTypeIndex<CustomMeshColor>();
            TypeIndex pseudoRandomSeed = TypeManager.GetTypeIndex<PseudoRandomSeed>();
            NativeArray<ComponentType> types = objectData.m_Archetype.GetComponentTypes(Allocator.Temp);
            try
            {
                for (int i = 0; i < types.Length; i++)
                {
                    TypeIndex type = types[i].TypeIndex;
                    if (type == customMeshColor)
                    {
                        result |= RuntimeInitialization.DisableCustomMeshColor;
                    }
                    else if (type == pseudoRandomSeed)
                    {
                        result |= RuntimeInitialization.SetPseudoRandomSeed;
                    }
                }
            }
            finally
            {
                types.Dispose();
            }

            m_RuntimeInitialization[prefab] = result;
            return result;
        }

        /// <summary>
        /// Reads back the overlay entities belonging to one building. Safe because every
        /// overlay tags itself with its building, and because anything previously applied to
        /// this building was marked Deleted first and so is out of the query already.
        /// </summary>
        private void CollectElementsFor(Entity building, OverlayRecord record)
        {
            NativeArray<Entity> live = m_OverlayQuery.ToEntityArray(Allocator.TempJob);
            try
            {
                for (int i = 0; i < live.Length; i++)
                {
                    WeatheringOverlay overlay = m_EntityManager.GetComponentData<WeatheringOverlay>(live[i]);
                    if (overlay.m_Building == building)
                    {
                        record.Elements.Add(live[i]);
                    }
                }
            }
            finally
            {
                live.Dispose();
            }
        }

        /// <summary>
        /// Everything there is to know about one building's overlay entities: which decal each
        /// one uses, how big it is, where it ended up, and - the part that matters when nothing
        /// is visible - what the culling system thinks of it.
        ///
        /// Read this a frame or more after applying. CullingInfo is filled in by the game's own
        /// systems, so straight after the command buffer plays back it is still all zeroes.
        /// </summary>
        public string DescribeElements(Entity building)
        {
            OverlayRecord record;
            if (!m_Records.TryGetValue(building, out record))
            {
                return "no overlay on entity " + building.Index;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(record.Elements.Count).Append(" element(s) on entity ").Append(building.Index)
              .Append(", facade=").Append(Side)
              .Append(", invertProjection=").Append(InvertProjection)
              .Append(", normalOffsetMetres=").Append(NormalOffsetMetres.ToString("0.00"));

            for (int i = 0; i < record.Elements.Count; i++)
            {
                Entity element = record.Elements[i];
                sb.AppendLine();

                if (!m_EntityManager.Exists(element))
                {
                    sb.Append("  [").Append(i).Append("] entity ").Append(element.Index).Append(" NO LONGER EXISTS");
                    continue;
                }

                string decalName = "<unknown>";
                string decalSize = "?";
                if (m_EntityManager.HasComponent<PrefabRef>(element))
                {
                    Entity decalPrefab = m_EntityManager.GetComponentData<PrefabRef>(element).m_Prefab;
                    DecalPrefabInfo info;
                    if (m_Catalog.TryGetByPrefab(decalPrefab, out info))
                    {
                        decalName = info.Name;
                        decalSize = string.Format("{0:0.0}x{1:0.0} deep {2:0.0}", info.Size.x, info.Size.z, info.Size.y);
                    }
                }

                sb.Append("  [").Append(i).Append("] ").Append(decalName).Append(" (").Append(decalSize).Append(")");

                if (m_EntityManager.HasComponent<Transform>(element))
                {
                    Transform t = m_EntityManager.GetComponentData<Transform>(element);
                    sb.AppendFormat(" at ({0:0.0},{1:0.0},{2:0.0})", t.m_Position.x, t.m_Position.y, t.m_Position.z);

                    // The projector axis, in the same form the measurement of a hand-placed decal
                    // prints, so the two can be compared line against line. (0,1,0) is a decal
                    // lying flat; a horizontal vector is one facing a wall.
                    float3 localUp = math.rotate(t.m_Rotation, new float3(0f, 1f, 0f));
                    sb.AppendFormat(" localUp=({0:0.00},{1:0.00},{2:0.00})", localUp.x, localUp.y, localUp.z);
                }

                if (m_EntityManager.HasComponent<Game.Rendering.CullingInfo>(element))
                {
                    Game.Rendering.CullingInfo culling = m_EntityManager.GetComponentData<Game.Rendering.CullingInfo>(element);
                    float3 cullSize = culling.m_Bounds.max - culling.m_Bounds.min;
                    sb.AppendFormat(
                        " | culling: size=({0:0.0},{1:0.0},{2:0.0}) radius={3:0.0} mask={4} minLod={5} passed={6}",
                        cullSize.x, cullSize.y, cullSize.z, culling.m_Radius,
                        culling.m_Mask, culling.m_MinLod, culling.m_PassedCulling);
                }
                else
                {
                    sb.Append(" | NO CullingInfo");
                }

                // Passing culling is not the same as having a batch. If the MeshBatch buffer is
                // empty the batching system never gave this entity anything to draw with, and it
                // will be invisible however correct everything else looks.
                if (m_EntityManager.HasBuffer<Game.Rendering.MeshBatch>(element))
                {
                    DynamicBuffer<Game.Rendering.MeshBatch> batches =
                        m_EntityManager.GetBuffer<Game.Rendering.MeshBatch>(element, true);

                    sb.Append(" | batches=").Append(batches.Length);
                    for (int b = 0; b < batches.Length; b++)
                    {
                        sb.AppendFormat(" [group={0} instance={1} mesh={2}]",
                            batches[b].m_GroupIndex, batches[b].m_InstanceIndex, batches[b].m_MeshIndex);
                    }
                }
                else
                {
                    sb.Append(" | NO MeshBatch buffer");
                }

                // The three components that can silently take an object out of normal rendering.
                sb.Append(" | Placeholder=").Append(m_EntityManager.HasComponent<Game.Objects.Placeholder>(element))
                  .Append(" Overridden=").Append(m_EntityManager.HasComponent<Overridden>(element))
                  .Append(" Marker=").Append(m_EntityManager.HasComponent<Game.Objects.Marker>(element))
                  .Append(" Owner=").Append(m_EntityManager.HasComponent<Game.Common.Owner>(element));
            }

            if (record.Elements.Count > 0 && m_EntityManager.Exists(record.Elements[0]))
            {
                sb.AppendLine();
                sb.Append("  COMPONENTS of [0]: ").Append(DescribeComponents(record.Elements[0]));
            }

            return sb.ToString();
        }

        /// <summary>Every component type on an entity, sorted, to diff ours against a hand-placed
        /// decal that actually paints a wall.</summary>
        private string DescribeComponents(Entity entity)
        {
            NativeArray<ComponentType> types = m_EntityManager.GetComponentTypes(entity, Allocator.Temp);
            try
            {
                List<string> names = new List<string>(types.Length);
                for (int i = 0; i < types.Length; i++)
                {
                    System.Type managed = types[i].GetManagedType();
                    names.Add(managed == null ? types[i].ToString() : managed.FullName);
                }

                names.Sort(System.StringComparer.Ordinal);
                return string.Join(", ", names.ToArray());
            }
            finally
            {
                types.Dispose();
            }
        }

        public bool Remove(Entity building)
        {
            OverlayRecord record;
            if (!m_Records.TryGetValue(building, out record))
            {
                return false;
            }

            for (int i = 0; i < record.Elements.Count; i++)
            {
                DestroyElement(record.Elements[i]);
            }

            m_Records.Remove(building);
            return true;
        }

        /// <summary>
        /// Removes the indexed elements and also recovers live elements whose in-memory record
        /// was lost. This is intentionally reserved for direct player commands: scanning the
        /// overlay query is cheap for one selected building, but doing it for every automatic
        /// detail update would make a city-wide pass quadratic.
        /// </summary>
        public bool RemoveIncludingUntracked(Entity building)
        {
            bool removed = Remove(building);

            // The dictionary is only a fast in-memory index. Saving deliberately clears it,
            // and a reload or an interrupted apply can lose it too, while the ECS entities may
            // still be alive until the game's cleanup systems run. For a direct state change,
            // use the owner stored on WeatheringOverlay as a second source of truth so
            // Maintained/F1 cannot leave old marks behind merely because their record was lost.
            int recovered = 0;
            NativeArray<Entity> live = m_OverlayQuery.ToEntityArray(Allocator.TempJob);
            try
            {
                for (int i = 0; i < live.Length; i++)
                {
                    WeatheringOverlay overlay = m_EntityManager.GetComponentData<WeatheringOverlay>(live[i]);
                    if (overlay.m_Building != building)
                    {
                        continue;
                    }

                    DestroyElement(live[i]);
                    recovered++;
                }
            }
            finally
            {
                live.Dispose();
            }

            if (recovered > 0 && m_Log != null)
            {
                m_Log.Info("Seen Better Days: recovered and removed " + recovered
                         + " untracked decal entit(ies) from building " + building.Index + ".");
            }

            return removed || recovered > 0;
        }

        public int RemoveAll()
        {
            int cleared = m_Records.Count;

            foreach (KeyValuePair<Entity, OverlayRecord> pair in m_Records)
            {
                List<Entity> elements = pair.Value.Elements;
                for (int i = 0; i < elements.Count; i++)
                {
                    DestroyElement(elements[i]);
                }
            }

            m_Records.Clear();
            return cleared;
        }

        /// <summary>
        /// Tagging with <see cref="Deleted"/> rather than destroying outright. That is the
        /// game's own removal path: PrepareCleanUpSystem collects everything carrying Deleted,
        /// the culling system releases the render batch, and CleanUpSystem destroys the entity
        /// at the end of the frame. Destroying it ourselves would leave the batch behind.
        /// </summary>
        private void DestroyElement(Entity element)
        {
            if (!m_EntityManager.Exists(element) || m_EntityManager.HasComponent<Deleted>(element))
            {
                return;
            }

            m_EntityManager.AddComponent<Deleted>(element);
        }

        public int PruneOrphans()
        {
            List<Entity> dead = null;

            foreach (KeyValuePair<Entity, OverlayRecord> pair in m_Records)
            {
                Entity building = pair.Key;

                // Entity.Null is the free-standing ground probe, which belongs to no building.
                if (building == Entity.Null)
                {
                    continue;
                }

                if (m_EntityManager.Exists(building)
                    && !m_EntityManager.HasComponent<Deleted>(building)
                    && !m_EntityManager.HasComponent<UnderConstruction>(building))
                {
                    continue;
                }

                if (dead == null)
                {
                    dead = new List<Entity>();
                }

                dead.Add(building);
            }

            if (dead == null)
            {
                return 0;
            }

            for (int i = 0; i < dead.Count; i++)
            {
                if (m_Log != null)
                {
                    m_Log.Info("Seen Better Days: building " + dead[i].Index
                             + " is gone or under construction, removing its overlay.");
                }

                Remove(dead[i]);
            }

            return dead.Count;
        }

        /// <summary>
        /// Deletes every entity carrying <see cref="WeatheringOverlay"/>, whether or not this
        /// renderer still knows about it. The safety net for when the in-memory index and the
        /// world disagree - a reloaded mod, a failed apply, a save loaded underneath us.
        /// </summary>
        public int SweepStrayOverlays()
        {
            NativeArray<Entity> strays = m_OverlayQuery.ToEntityArray(Allocator.TempJob);
            try
            {
                for (int i = 0; i < strays.Length; i++)
                {
                    DestroyElement(strays[i]);
                }

                m_Records.Clear();
                return strays.Length;
            }
            finally
            {
                strays.Dispose();
            }
        }
    }
}

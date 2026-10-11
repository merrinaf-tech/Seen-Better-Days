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
    /// Known deliberate limits in this phase, all documented in the development notes:
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

        /// <summary>How much larger than a facade a decal may be, in each direction. See
        /// TryPlanMarkOnFacade.</summary>
        private const float MaxOverhang = 2f;

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
            public byte GarbageTier;

            /// <summary>The hand-made design placed, or null for random placement.</summary>
            public SeenBetterDays.Designs.DesignLibrary.Resolved Design;
            public readonly List<Entity> Elements = new List<Entity>(MaxPerBuilding);
        }

        private struct PendingElement
        {
            public Entity Prefab;
            public float3 Position;
            public quaternion Rotation;
            public OverlayFamily Family;
            public int FacadeIndex;

            /// <summary>The decal's larger side, in metres, for the spacing rule.</summary>
            public float Extent;

            /// <summary>A hand-made design's seed for this decal plus one, or 0 to derive one from
            /// the building. Random marks may differ from building to building; a design may not.</summary>
            public int DesignSeedPlusOne;
        }

        /// <summary>
        /// How close two marks on the same facade, or on the lot, may stand: their centres at
        /// least this share of their average size apart, so they may overlap by up to about 60%
        /// but not pile up. Seen on 2026-10-02: a Decayed house carried 24 marks heaped on one
        /// wall. A mark that has no room tries another facade, which spreads them round the
        /// building. 0.6 spread them well but left visibly fewer marks; 0.4 is the middle.
        /// </summary>
        private const float MarkSpacing = 0.4f;

        private bool TooCloseToPlanned(float3 position, float extent, int facadeIndex)
        {
            for (int i = 0; i < m_Pending.Count; i++)
            {
                PendingElement other = m_Pending[i];
                if (other.FacadeIndex != facadeIndex)
                {
                    continue;
                }

                float minimum = MarkSpacing * 0.5f * (extent + other.Extent);
                if (math.distancesq(position, other.Position) < minimum * minimum)
                {
                    return true;
                }
            }

            return false;
        }

        private readonly EntityManager m_EntityManager;
        private readonly DecalPrefabCatalog m_Catalog;
        private readonly ILog m_Log;

        /// <summary>Matches every live overlay entity. Used both to find the entities a command
        /// buffer just created and to sweep strays.</summary>
        private readonly EntityQuery m_OverlayQuery;

        private readonly Dictionary<Entity, OverlayRecord> m_Records = new Dictionary<Entity, OverlayRecord>();
        private readonly List<PendingElement> m_Pending = new List<PendingElement>(MaxPerBuilding);

        /// <summary>The state of the building being planned, for decals with a minimum state
        /// (DecalPrefabInfo.MinState). Set at the start of Apply.</summary>
        private VisualState m_PlanState;

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
            return Apply(building, profile, 0, out placed, out failureReason);
        }

        public bool Apply(Entity building, in BuildingVisualProfile profile, byte garbageTier, out int placed, out string failureReason)
        {
            placed = 0;
            failureReason = null;

            if (!IsAvailable)
            {
                failureReason = UnavailableReason;
                return false;
            }

            long prepareStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            m_RemoveTicks = 0;
            if (!TryResolveAllFacades(building, out failureReason))
            {
                return false;
            }

            // Some small growables cannot fit even the smallest approved projector for their
            // active family. Discover that from catalogue metadata before CollectFacadePoints
            // walks the rendered mesh. The proximity system may revisit such a building often;
            // the old order paid for the full mesh scan every time even though placement was
            // geometrically impossible.
            // When no wall decal fits, the walls are skipped - but not the building: its lot can
            // still take ground decals, which is often all a small house can carry.
            bool automatic = ForcedDecal == null && !AllowNonBuildingDecals;
            bool wallsPossible = !automatic || HasFittingAutomaticDecal(profile) || garbageTier > 0;

            m_Pending.Clear();
            m_PlanState = profile.State;
            Unity.Mathematics.Random rng = new Unity.Mathematics.Random(profile.Seed == 0u ? 1u : profile.Seed);

            m_LastFamilies.Clear();
            if (wallsPossible)
            {
                PrepareSurfaceTemplate(building);
            }

            m_PrepareTicks = System.Diagnostics.Stopwatch.GetTimestamp() - prepareStarted;
            m_SurfaceHits = 0;
            m_SurfaceRecoveries = 0;
            m_SurfaceMisses = 0;
            m_SurfaceSides = 0;
            m_Raycasts = 0;
            m_RaycastTicks = 0;
            m_GroundSetupTicks = 0;
            m_PickTicks = 0;
            m_WallPlanTicks = 0;
            m_GroundPlanTicks = 0;
            m_TriangleSearches = 0;
            m_TriangleTicks = 0;
            m_SpawnTicks = 0;
            m_PlannedFacadeMask = 0;
            m_FacadeSequenceStep = 0;
            m_FrontMarkCount = 0;
            m_FrontGraffitiCount = 0;

            // Specific litter gets its own small, increasing budget. Put it on the lot and at
            // arm's reach on the road-facing wall before ordinary weathering shares the remainder.
            if (automatic && garbageTier > 0)
            {
                PlanGround(building, profile, ref rng, garbageTier, true, MaxPerBuilding);
                PlanGarbageWalls(building, garbageTier, MaxPerBuilding, ref rng);
            }

            int garbageGroundMarks = m_Pending.Count;

            // The nominal Front is the side presented to the road. Starting at a random side
            // sounded varied, but on long narrow growables it made the first (and often only)
            // mark just as likely to land on a party wall as on the street facade. Begin at the
            // front, then continue round-robin so additional marks still cover the whole shell.
            m_NextFacade = FindFacade(FacadeSide.Front);

            // Aged has only cracks available. Reserve one on the road-facing wall before the
            // round-robin spends its small budget on the long sides; later states have additional
            // families that already give the recovery pass several ways to fill the front.
            long wallsStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            if (wallsPossible)
            {
                PlanWalls(building, profile, ref rng);
            }

            int wallMarks = m_Pending.Count - garbageGroundMarks;
            long groundStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            m_WallPlanTicks = groundStarted - wallsStarted;
            if (automatic)
            {
                PlanGround(building, profile, ref rng, 0, false, MaxPerBuilding);
            }

            m_GroundPlanTicks = System.Diagnostics.Stopwatch.GetTimestamp() - groundStarted;

            LastGroundMarks = garbageGroundMarks + m_Pending.Count - garbageGroundMarks - wallMarks;
            LastWallMarks = wallMarks;

            if (m_Pending.Count == 0)
            {
                failureReason = !wallsPossible && automatic
                    ? "no approved active decal fits any facade, and none fits the lot"
                    : m_SurfaceMisses > 0
                        ? "no sampled position hit the building mesh (" + m_SurfaceMisses + " raycast miss(es)) | "
                          + BuildingSurfaceProbe.DescribeMeshResidency(m_EntityManager, building)
                        : profile.MaxIntensity <= 0.01f
                            ? "profile asked for nothing: every family is at zero intensity"
                            : "no automatic mark fit a sampled facade";
                return false;
            }

            return SpawnPlan(building, profile, garbageTier, out placed, out failureReason);
        }

        /// <summary>The wall half of the plan: everything Apply did before the lot had a share.</summary>
        private void PlanWalls(Entity building, in BuildingVisualProfile profile, ref Unity.Mathematics.Random rng)
        {
            PlanRequiredFrontAgedCrack(building, profile, ref rng);

            // From Worn on (small tags only until Neglected, see DecalPrefabInfo.MinState), a player
            // looking from the road must actually be able to
            // read the newly introduced graffiti family. A purely round-robin plan can truthfully
            // contain four graffiti while putting every recognisable one on a flank or the rear.
            // Reserve one low, street-facing example first; PlanAllFamilies accounts for it when
            // sharing out the fixed entity budget.
            PlanRequiredFrontGraffiti(building, profile, ref rng);
            PlanAllFamilies(building, profile, ref rng);
            PlanMinimumMark(building, profile, ref rng);
            PlanMinimumMarkPerFacade(building, profile, ref rng);
            PlanStateMinimum(building, profile, ref rng);
        }

        /// <summary>Replaces whatever the building had with the planned marks.</summary>
        private bool SpawnPlan(Entity building, in BuildingVisualProfile profile, byte garbageTier, out int placed, out string failureReason)
        {
            placed = 0;
            failureReason = null;

            // Only now that we know the plan is non-empty: Apply is the whole statement of how
            // this building should look, so whatever is on it is stale by definition.
            long removeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            Remove(building);
            m_RemoveTicks = System.Diagnostics.Stopwatch.GetTimestamp() - removeStarted;

            OverlayRecord record = new OverlayRecord { Profile = profile, GarbageTier = garbageTier };
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

        /// <summary>
        /// Places a hand-made design: its decals at their recorded positions, relative to the
        /// building, with no wall survey and no search. Replaces whatever the building had.
        /// </summary>
        public bool ApplyDesign(
            Entity building,
            in BuildingVisualProfile profile,
            SeenBetterDays.Designs.DesignLibrary.Resolved design,
            out int placed,
            out string failureReason)
        {
            return ApplyDesign(building, profile, design, 0, out placed, out failureReason);
        }

        public bool ApplyDesign(
            Entity building,
            in BuildingVisualProfile profile,
            SeenBetterDays.Designs.DesignLibrary.Resolved design,
            byte garbageTier,
            out int placed,
            out string failureReason)
        {
            placed = 0;
            failureReason = null;
            m_SpawnTicks = 0;
            m_SurfaceTemplateCacheHit = true;
            m_SurfaceHits = 0;
            m_Raycasts = 0;
            m_RaycastTicks = 0;
            m_GroundSetupTicks = 0;
            m_PickTicks = 0;
            m_WallPlanTicks = 0;
            m_GroundPlanTicks = 0;
            m_TriangleSearches = 0;
            m_TriangleTicks = 0;
            m_SurfaceMisses = 0;
            LastDesign = design.Name;

            if (!m_EntityManager.HasComponent<Transform>(building))
            {
                failureReason = "building has no transform";
                return false;
            }

            Transform transform = m_EntityManager.GetComponentData<Transform>(building);
            m_Pending.Clear();
            for (int i = 0; i < design.Prefabs.Length; i++)
            {
                m_Pending.Add(new PendingElement
                {
                    Prefab = design.Prefabs[i],
                    Position = transform.m_Position + math.mul(transform.m_Rotation, design.Positions[i]),
                    Rotation = math.mul(transform.m_Rotation, design.Rotations[i]),
                    Family = design.Families[i],
                    FacadeIndex = 0,
                    DesignSeedPlusOne = design.Seeds[i] + 1,
                });
            }

            if (garbageTier > 0 && TryResolveAllFacades(building, out failureReason))
            {
                PrepareSurfaceTemplate(building);
                m_PlanState = profile.State;
                m_LastFamilies.Clear();
                m_SurfaceHits = 0;
                m_SurfaceMisses = 0;
                m_NextFacade = FindFacade(FacadeSide.Front);
                int garbageBudget = design.Prefabs.Length + 6;
                Unity.Mathematics.Random litterRng = new Unity.Mathematics.Random(
                    (profile.Seed == 0u ? 1u : profile.Seed) ^ (uint)(garbageTier * 0x9E3779B9));
                PlanGround(building, profile, ref litterRng, garbageTier, true, garbageBudget);
                PlanGarbageWalls(building, garbageTier, garbageBudget, ref litterRng);
            }

            // Litter is supplemental: an unavailable facade survey should not make a valid
            // authored design look like a failed placement.
            failureReason = null;

            Remove(building);

            OverlayRecord record = new OverlayRecord { Profile = profile, Design = design, GarbageTier = garbageTier };
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

        /// <summary>
        /// Places a design's decals as ordinary objects that belong to the player - not tagged as
        /// this mod's overlays - so they can be edited with the usual tools and exported again.
        /// Used by design mode to start a new design from an existing one.
        /// </summary>
        public int SpawnAsPlacedObjects(Entity building, SeenBetterDays.Designs.DesignLibrary.Resolved design)
        {
            if (!m_EntityManager.HasComponent<Transform>(building))
            {
                return 0;
            }

            Transform transform = m_EntityManager.GetComponentData<Transform>(building);
            EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            try
            {
                for (int i = 0; i < design.Prefabs.Length; i++)
                {
                    Entity prefab = design.Prefabs[i];
                    ObjectData objectData = m_EntityManager.GetComponentData<ObjectData>(prefab);
                    RuntimeInitialization initialization = GetRuntimeInitialization(prefab, objectData);

                    Entity element = commandBuffer.CreateEntity(objectData.m_Archetype);
                    commandBuffer.SetComponent(element, new PrefabRef(prefab));
                    commandBuffer.SetComponent(element, new Transform(
                        transform.m_Position + math.mul(transform.m_Rotation, design.Positions[i]),
                        math.mul(transform.m_Rotation, design.Rotations[i])));

                    if ((initialization & RuntimeInitialization.DisableCustomMeshColor) != 0)
                    {
                        commandBuffer.SetComponentEnabled<CustomMeshColor>(element, false);
                    }

                    if ((initialization & RuntimeInitialization.SetPseudoRandomSeed) != 0)
                    {
                        // The design's own seed, so the decals look as they did when it was made
                        // and keep that look if it is exported again.
                        commandBuffer.SetComponent(element, new PseudoRandomSeed(design.Seeds[i]));
                    }
                }

                commandBuffer.Playback(m_EntityManager);
            }
            finally
            {
                commandBuffer.Dispose();
            }

            return design.Prefabs.Length;
        }

        /// <summary>The state the decals on this building were planned for.</summary>
        public bool TryGetPlacedState(Entity building, out VisualState state)
        {
            OverlayRecord record;
            if (!m_Records.TryGetValue(building, out record))
            {
                state = VisualState.Maintained;
                return false;
            }

            state = record.Profile.State;
            return true;
        }

        public bool TryGetGarbageTier(Entity building, out byte tier)
        {
            OverlayRecord record;
            if (!m_Records.TryGetValue(building, out record))
            {
                tier = 0;
                return false;
            }

            tier = record.GarbageTier;
            return true;
        }

        /// <summary>Whether the decals on this building are a hand-made design, and whose.</summary>
        public bool TryGetDesign(Entity building, out string author)
        {
            author = null;
            OverlayRecord record;
            if (!m_Records.TryGetValue(building, out record) || record.Design == null)
            {
                return false;
            }

            author = record.Design.Author;
            return true;
        }

        /// <summary>The design placed by the last ApplyDesign, for the log.</summary>
        public string LastDesign { get; private set; }

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
                    if (m_Catalog.HasAutomaticFamilyThatFits(family, facade.Width * MaxOverhang, facade.Height * MaxOverhang))
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

        /// <summary>Prefab-local wall samples grouped by side. Grouping once when the template is
        /// built guarantees that a short or low-triangle facade is sampled from its own geometry
        /// instead of competing with thousands of triangles from the other three sides.</summary>
        private List<BuildingSurfaceProbe.SurfacePoint>[] m_SurfaceBySide;
        private readonly Dictionary<Entity, List<BuildingSurfaceProbe.SurfacePoint>[]> m_SurfaceTemplates =
            new Dictionary<Entity, List<BuildingSurfaceProbe.SurfacePoint>[]>();
        private Transform m_SurfaceTransform;
        private int m_CachedSurfacePointCount;
        private bool m_SurfaceTemplateCacheHit;

        private void PrepareSurfaceTemplate(Entity building)
        {
            m_SurfaceTransform = m_EntityManager.GetComponentData<Transform>(building);
            Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;

            List<BuildingSurfaceProbe.SurfacePoint>[] cached;
            if (m_SurfaceTemplates.TryGetValue(prefab, out cached))
            {
                m_SurfaceBySide = cached;
                m_SurfaceTemplateCacheHit = true;
                return;
            }

            // Only the manual and developer paths get here without a template: the proximity
            // system waits for the budgeted survey below instead. They are one building at a time
            // and on request, so a synchronous walk is acceptable there.
            var collected = new List<BuildingSurfaceProbe.SurfacePoint>(2048);
            BuildingSurfaceProbe.CollectLocalFacadePoints(
                m_EntityManager,
                building,
                collected);

            m_SurfaceBySide = StoreTemplate(prefab, collected);
            m_SurfaceTemplateCacheHit = false;
        }

        /// <summary>
        /// Groups surveyed wall points by side and remembers them for the prefab.
        ///
        /// An empty result often means the game's mesh buffers have not become resident yet, so it
        /// is returned but not remembered: a later pass must be allowed to try again.
        /// </summary>
        private List<BuildingSurfaceProbe.SurfacePoint>[] StoreTemplate(
            Entity prefab, List<BuildingSurfaceProbe.SurfacePoint> collected)
        {
            var partitioned = new List<BuildingSurfaceProbe.SurfacePoint>[4];
            for (int side = 0; side < partitioned.Length; side++)
            {
                partitioned[side] = new List<BuildingSurfaceProbe.SurfacePoint>(
                    math.max(4, collected.Count / 4));
            }

            for (int i = 0; i < collected.Count; i++)
            {
                BuildingSurfaceProbe.SurfacePoint point = collected[i];
                partitioned[(int)point.m_Side].Add(point);
            }

            if (collected.Count == 0)
            {
                return partitioned;
            }

            if (m_CachedSurfacePointCount + collected.Count > MaxCachedSurfacePoints)
            {
                m_SurfaceTemplates.Clear();
                m_CachedSurfacePointCount = 0;
            }

            m_SurfaceTemplates[prefab] = partitioned;
            m_CachedSurfacePointCount += collected.Count;
            return partitioned;
        }

        // ---- Budgeted wall surveys ---------------------------------------------------------------
        //
        // The first time a building type is met, its triangles have to be walked to find its walls.
        // For a detailed prefab that took up to 60 ms on the main thread in one go - the hitch a
        // player saw while moving the camera into a new district. The proximity system now asks
        // for a survey and moves on; FacadeSurveySystem spends a small slice of every frame on the
        // queue, and the building is detailed on a later pass once its type is known.

        // A List used as a queue: Queue<T> is ambiguous between the game's mscorlib and System
        // references, and this holds a few dozen building types at most.
        private readonly List<Entity> m_SurveyQueue = new List<Entity>();
        private readonly HashSet<Entity> m_SurveyQueued = new HashSet<Entity>();
        private BuildingSurfaceProbe.FacadeSurveyor m_Survey;

        /// <summary>Whether this building's type has already been surveyed.</summary>
        public bool HasSurfaceTemplate(Entity building)
        {
            return m_EntityManager.HasComponent<PrefabRef>(building)
                && m_SurfaceTemplates.ContainsKey(m_EntityManager.GetComponentData<PrefabRef>(building).m_Prefab);
        }

        /// <summary>Queues a survey of this building's type, once.</summary>
        public void RequestSurfaceTemplate(Entity building)
        {
            if (!m_EntityManager.HasComponent<PrefabRef>(building))
            {
                return;
            }

            Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
            if (m_SurfaceTemplates.ContainsKey(prefab) || !m_SurveyQueued.Add(prefab))
            {
                return;
            }

            m_SurveyQueue.Add(prefab);
        }

        /// <summary>Building types waiting for, or in the middle of, a survey.</summary>
        public int PendingSurveys
        {
            get { return m_SurveyQueue.Count + (m_Survey != null ? 1 : 0); }
        }

        /// <summary>Works on the survey queue for at most <paramref name="budgetMs"/>.</summary>
        public void AdvanceSurveys(double budgetMs)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            double ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000d;

            while (true)
            {
                double left = budgetMs - (System.Diagnostics.Stopwatch.GetTimestamp() - started) / ticksPerMs;
                if (left <= 0d)
                {
                    return;
                }

                if (m_Survey == null)
                {
                    if (m_SurveyQueue.Count == 0)
                    {
                        return;
                    }

                    Entity next = m_SurveyQueue[0];
                    m_SurveyQueue.RemoveAt(0);
                    if (m_SurfaceTemplates.ContainsKey(next))
                    {
                        m_SurveyQueued.Remove(next);
                        continue;
                    }

                    m_Survey = new BuildingSurfaceProbe.FacadeSurveyor(m_EntityManager, next);
                }

                if (!m_Survey.Step(m_EntityManager, left))
                {
                    return;
                }

                // An empty survey is not remembered, and taking the prefab out of the queued set
                // lets the proximity system ask again once the mesh is resident.
                StoreTemplate(m_Survey.Prefab, m_Survey.Points);
                m_SurveyQueued.Remove(m_Survey.Prefab);
                m_Survey = null;
            }
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
        // ---- Placement cost, for the slow-placement diagnostic ------------------------------------
        //
        // A placement normally picks from the surveyed wall and costs next to nothing. When no
        // surveyed point is near enough it falls back to a ray against the mesh and then to a
        // triangle search, both of which walk triangles on the main thread. These counters say
        // which of them a slow placement spent its time in.

        private int m_Raycasts;
        private long m_RaycastTicks;
        private long m_GroundSetupTicks;

        /// <summary>Where the rest of a placement went: finding the facades and removing the
        /// building's previous marks (a structural change, which waits for running jobs).</summary>
        private long m_PrepareTicks;
        private long m_RemoveTicks;
        private long m_WallPlanTicks;
        private long m_GroundPlanTicks;
        private long m_PickTicks;

        /// <summary>Time spent creating the decal entities, as against finding where they go.
        /// Measured for both random and designed placement, so the two can be compared.</summary>
        private long m_SpawnTicks;

        /// <summary>Milliseconds the last placement spent creating its decal entities.</summary>
        public double LastSpawnMilliseconds
        {
            get { return m_SpawnTicks * 1000d / System.Diagnostics.Stopwatch.Frequency; }
        }
        private int m_TriangleSearches;
        private long m_TriangleTicks;

        private bool TimedTryHit(EntityManager entityManager, Entity building, float3 origin, float3 direction,
            float maxDistance, out float3 point, out float3 normal)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            bool hit = BuildingSurfaceProbe.TryHit(entityManager, building, origin, direction, maxDistance,
                out point, out normal);
            m_RaycastTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            m_Raycasts++;
            return hit;
        }

        private bool TimedTryFindFacadePoint(EntityManager entityManager, Entity building, float3 target,
            float3 facadeNormal, out float3 point, out float3 normal)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            bool found = BuildingSurfaceProbe.TryFindFacadePoint(entityManager, building, target, facadeNormal,
                out point, out normal);
            m_TriangleTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            m_TriangleSearches++;
            return found;
        }

        /// <summary>Where the last Apply spent its effort. Read by DecalDetailSystem when a
        /// placement was slow.</summary>
        public string DescribeLastPlacementCost()
        {
            double ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000d;
            return string.Format(
                "{0} mark(s) from the surveyed wall, {1} ray fallback(s) taking {2:0.0} ms, "
              + "{3} triangle search(es) taking {4:0.0} ms, {5} miss(es), wall survey {6}, "
              + "creating the decals {7:0.0} ms, terrain and lot surfaces {8:0.0} ms, "
              + "facades and wall survey {9:0.0} ms, removing the old marks {10:0.0} ms, "
              + "planning the walls {11:0.0} ms (picking surveyed points {13:0.0} ms), planning the lot {12:0.0} ms",
                m_SurfaceHits, m_Raycasts, m_RaycastTicks / ticksPerMs,
                m_TriangleSearches, m_TriangleTicks / ticksPerMs, m_SurfaceMisses,
                m_SurfaceTemplateCacheHit ? "cached" : "done during this placement",
                m_SpawnTicks / ticksPerMs, m_GroundSetupTicks / ticksPerMs,
                m_PrepareTicks / ticksPerMs, m_RemoveTicks / ticksPerMs,
                m_WallPlanTicks / ticksPerMs, m_GroundPlanTicks / ticksPerMs, m_PickTicks / ticksPerMs);
        }

        private bool TryResolvePlacement(
            Entity building,
            in BuildingFacade facade,
            float u,
            float v,
            ref Unity.Mathematics.Random rng,
            out float3 position,
            out quaternion rotation,
            float maxVerticalError = float.MaxValue)
        {
            bool resolved = ResolvePlacement(building, facade, u, v, maxVerticalError, ref rng, out position, out rotation);

            // The ray and triangle fallbacks look for the nearest surface in any direction; hold
            // them to the same height limit as the surveyed pick.
            if (resolved && maxVerticalError < float.MaxValue
                && math.abs(position.y - facade.PointAt(u, v).y) > maxVerticalError + 0.5f)
            {
                m_SurfaceMisses++;
                return false;
            }

            return resolved;
        }

        private bool ResolvePlacement(
            Entity building,
            in BuildingFacade facade,
            float u,
            float v,
            float maxVerticalError,
            ref Unity.Mathematics.Random rng,
            out float3 position,
            out quaternion rotation)
        {
            position = default;
            rotation = default;
            float3 planePoint = facade.PointAt(u, v);

            // Pick from the wall we already measured, rather than casting a fresh ray for every
            // mark. Same question, asked once per building instead of once per mark.
            long pickStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            bool picked = TryPickCollectedSurface(facade, planePoint, maxVerticalError, ref rng, out position, out rotation);
            m_PickTicks += System.Diagnostics.Stopwatch.GetTimestamp() - pickStarted;
            if (picked)
            {
                m_SurfaceHits++;
                m_SurfaceSides |= 1 << (int)facade.Side;
                return true;
            }

            // The fallbacks below search the building's mesh: a single ray took 7-9 ms on a large
            // building, so even one per placement made a visible stutter. Automatic marks use
            // the surveyed wall only - a mark fewer is invisible, a stutter is not. The test
            // harness's forced placements keep them.
            if (m_SurveyOnly || (ForcedDecal == null && !AllowNonBuildingDecals))
            {
                m_SurfaceMisses++;
                return false;
            }

            // Start well outside the outermost extent and travel inward, so the first thing hit is
            // the nearest real surface facing us.
            const float standOff = 25f;

            float3 origin = planePoint + facade.Normal * standOff;
            float3 hit;
            float3 normal;

            if (building != Entity.Null
                && TimedTryHit(
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
                && TimedTryFindFacadePoint(
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
        /// Searched exhaustively among the surveyed points - thousands of cheap comparisons, not
        /// triangles, so the cost saving of the survey is kept.
        /// </summary>
        private bool TryPickCollectedSurface(
            in BuildingFacade facade,
            float3 target,
            float maxVerticalError,
            ref Unity.Mathematics.Random rng,
            out float3 position,
            out quaternion rotation)
        {
            position = default;
            rotation = default;

            if (m_SurfaceBySide == null)
            {
                return false;
            }

            List<BuildingSurfaceProbe.SurfacePoint> surface =
                m_SurfaceBySide[(int)facade.Side];
            if (surface.Count == 0)
            {
                return false;
            }

            // Every point, not a random handful. The best of 24 random samples was often 10-20 m
            // from the intended spot on a tall facade, which put street-level graffiti half way
            // up a tower. Compared in the prefab's own space, so each point costs a dot product
            // and a distance rather than two rotations: the target and the facade normal are
            // brought into that space once.
            quaternion inverse = math.inverse(m_SurfaceTransform.m_Rotation);
            float3 localTarget = math.mul(inverse, target - m_SurfaceTransform.m_Position);
            float3 localNormal = math.mul(inverse, facade.Normal);

            int best = -1;
            float bestDistance = float.MaxValue;

            for (int candidate = 0; candidate < surface.Count; candidate++)
            {
                BuildingSurfaceProbe.SurfacePoint point = surface[candidate];

                // Facing roughly the same way as this side of the building. Loose, so that
                // chamfers, bays and angled wings stay eligible.
                if (math.dot(point.m_Normal, localNormal) < 0.4f)
                {
                    continue;
                }

                // Graffiti has to stay at the height it was given - within reach of the street
                // or just under the roof. A wall that has no surface near that height gets no
                // graffiti rather than one moved to wherever there is wall.
                if (math.abs(point.m_Position.y - localTarget.y) > maxVerticalError)
                {
                    continue;
                }

                float distance = math.distancesq(point.m_Position, localTarget);
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

            BuildingSurfaceProbe.SurfacePoint chosen = surface[best];
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

        /// <summary>Places an increasing amount of the approved trash decal pack at street level.
        /// The small dedicated budget remains separate from designs and ordinary grime.</summary>
        private void PlanGarbageWalls(
            Entity building,
            int garbageTier,
            int budget,
            ref Unity.Mathematics.Random rng)
        {
            if (garbageTier <= 0 || ForcedDecal != null || AllowNonBuildingDecals || m_Facades.Count == 0)
            {
                return;
            }

            int requested = s_LitterWallsByTier[math.clamp(garbageTier, 0, s_LitterWallsByTier.Length - 1)];
            int front = FindFacade(FacadeSide.Front);

            // Surveyed wall points only. Litter sits at the foot of the wall, where the survey
            // often has no point, and the ray fallback then cost 9 ms a mark on a large tower -
            // frames of 10 and 22 ms were logged. A missing piece is made up for on the ground.
            m_SurveyOnly = true;
            try
            {
                PlanGarbageWallMarks(building, requested, budget, front, ref rng);
            }
            finally
            {
                m_SurveyOnly = false;
            }
        }

        /// <summary>Litter per tier (30%, 50%, 75% of the most a building can hold): on the
        /// lot's ground, and at the foot of its walls - three, six and ten pieces in all.</summary>
        private static readonly int[] s_LitterGroundByTier = { 0, 2, 4, 7 };
        private static readonly int[] s_LitterWallsByTier = { 0, 1, 2, 3 };

        /// <summary>Set while litter is planned: placement then takes surveyed wall points only.</summary>
        private bool m_SurveyOnly;

        private void PlanGarbageWallMarks(
            Entity building,
            int requested,
            int budget,
            int front,
            ref Unity.Mathematics.Random rng)
        {
            for (int i = 0; i < requested && m_Pending.Count < budget; i++)
            {
                bool placed = false;
                for (int sideOffset = 0; sideOffset < m_Facades.Count && !placed; sideOffset++)
                {
                    int facadeIndex = sideOffset == 0
                        ? front
                        : (front + sideOffset) % m_Facades.Count;
                    BuildingFacade facade = m_Facades[facadeIndex];
                    DecalPrefabInfo decal = m_Catalog.PickTrashWall(
                        facade.Width * MaxOverhang, facade.Height * MaxOverhang, ref rng);
                    if (decal != null)
                    {
                        placed = TryPlanMarkOnFacade(
                            building, OverlayFamily.Dirt, decal, facadeIndex, false, true, ref rng);
                    }
                }
            }
        }

        /// <summary>
        /// Reserves one crack on the road-facing wall for Aged buildings. This state has no other
        /// active family to recover the front when a first crack choice misses its usable masonry.
        /// </summary>
        private void PlanRequiredFrontAgedCrack(
            Entity building,
            in BuildingVisualProfile profile,
            ref Unity.Mathematics.Random rng)
        {
            if (profile.State != VisualState.Aged
                || math.saturate(profile.Crack) <= 0.01f
                || ForcedDecal != null
                || AllowNonBuildingDecals
                || !m_Catalog.HasAutomaticFamily(OverlayFamily.Crack))
            {
                return;
            }

            int frontIndex = FindFacade(FacadeSide.Front);
            BuildingFacade front = m_Facades[frontIndex];
            if (!m_Catalog.HasAutomaticFamilyThatFits(
                OverlayFamily.Crack,
                front.Width * MaxOverhang,
                front.Height * MaxOverhang))
            {
                return;
            }

            // "Fits the facade" only checks the projector footprint. On a narrow street wall a
            // particular crack can still land entirely over windows or doors, so retry both the
            // catalogue choice and the sampled position before accepting that the wall is unusable.
            for (int decalAttempt = 0; decalAttempt < 4; decalAttempt++)
            {
                bool familyMatched;
                DecalPrefabInfo decal = m_Catalog.Pick(
                    OverlayFamily.Crack,
                    false,
                    front.Width * MaxOverhang,
                    front.Height * MaxOverhang,
                    ref rng,
                    out familyMatched,
                    profile.State);

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
                        OverlayFamily.Crack,
                        decal,
                        frontIndex,
                        ref rng))
                    {
                        m_NextFacade = (frontIndex + 1) % m_Facades.Count;
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Reserves one visible graffiti projector on the road-facing wall when that family is
        /// active. The proportional planner accounts for this mark when sharing the remaining
        /// budget.
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
                front.Width * MaxOverhang,
                front.Height * MaxOverhang))
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
                    front.Width * MaxOverhang,
                    front.Height * MaxOverhang,
                    ref rng,
                    out familyMatched,
                    profile.State);

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

        /// <summary>
        /// Decides how many marks each family gets, then places them.
        ///
        /// The budget is shared out in proportion, by largest remainder: everyone is scaled down
        /// together, whole marks go to the largest claims, and what is left over goes to the
        /// families with the biggest unmet fractions. A family that asked for a mark and can be
        /// afforded one gets one, which keeps later families from being crowded out by dirt.
        /// </summary>
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

        /// <summary>
        /// Fewest marks a building gets in each state - Maintained, Aged, Worn, Neglected,
        /// Decayed - whatever its size. Counts scale with wall area, so a small house was worth
        /// half a reference building and an Aged one carried a single crack: nothing anyone would
        /// notice. Large buildings already earn more than this and are unaffected.
        /// </summary>
        private static readonly int[] s_MinimumByState = { 0, 4, 6, 8, 10 };

        /// <summary>
        /// Tops the plan up to the state's minimum, from the families the state has, strongest
        /// first and in turn. Stops early when nothing more fits: the minimum never forces a mark
        /// onto a wall that has no room for one. Scaled down with the intensity option below 100%,
        /// so a player who asked for fewer marks still gets fewer.
        /// </summary>
        private void PlanStateMinimum(Entity building, in BuildingVisualProfile profile, ref Unity.Mathematics.Random rng)
        {
            int target = s_MinimumByState[math.clamp((int)profile.State, 0, s_MinimumByState.Length - 1)];
            float scale = Mod.Settings != null ? Mod.Settings.IntensityScale : 1f;
            if (scale < 1f && target > 0)
            {
                target = math.max(1, (int)math.round(target * scale));
            }

            target = math.min(target, MaxPerBuilding);
            if (m_Pending.Count >= target)
            {
                return;
            }

            var families = new List<OverlayFamily>(s_Families.Length);
            var intensities = new List<float>(s_Families.Length);
            foreach (OverlayFamily family in s_Families)
            {
                if (ForcedDecal == null && !AllowNonBuildingDecals && !m_Catalog.HasAutomaticFamily(family))
                {
                    continue;
                }

                float intensity = math.saturate(profile.GetIntensity(family));
                if (intensity <= 0.01f)
                {
                    continue;
                }

                int at = 0;
                while (at < intensities.Count && intensities[at] >= intensity)
                {
                    at++;
                }

                families.Insert(at, family);
                intensities.Insert(at, intensity);
            }

            if (families.Count == 0)
            {
                return;
            }

            int stalls = 0;
            for (int turn = 0; m_Pending.Count < target && stalls < families.Count; turn++)
            {
                int before = m_Pending.Count;
                PlanMarks(building, families[turn % families.Count], 1, ref rng);
                stalls = m_Pending.Count > before ? 0 : stalls + 1;
            }
        }

        // ---- Ground of the lot -----------------------------------------------------------------

        /// <summary>Ground marks a lot gets in each state - Maintained, Aged, Worn, Neglected,
        /// Decayed - for each GroundAreaUnit of free ground, scaled with how much the lot has.
        /// Small cracks are easy to miss, so a lot carries several.</summary>
        private static readonly int[] s_GroundByState = { 0, 3, 4, 5, 6 };
        private const float GroundAreaUnit = 100f;
        private const int MaxGroundMarks = 12;

        /// <summary>Largest ground decal, in metres, whatever the lot: a bigger one reaches the
        /// street or the neighbours.</summary>
        private const float MaxGroundEdge = 8f;

        private Game.Simulation.TerrainSystem m_TerrainSystem;

        /// <summary>Random points tried for each ground mark.</summary>
        private const int GroundAttempts = 16;

        /// <summary>Chance of accepting the less suitable kind of ground for a decal that only
        /// prefers one - so cracks still appear on a lot with little paving, just less often.</summary>
        private const float OtherGroundChance = 0.25f;

        private struct GroundTriangle
        {
            public float2 A;
            public float2 B;
            public float2 C;
        }

        /// <summary>The paved surfaces of the lot being planned, as world-space triangles.</summary>
        private readonly List<GroundTriangle> m_PavedTriangles = new List<GroundTriangle>();

        /// <summary>Whether each surface prefab is paving, by its name, worked out once.</summary>
        private readonly Dictionary<Entity, bool> m_PavedByPrefab = new Dictionary<Entity, bool>();
        private Game.Prefabs.PrefabSystem m_PrefabSystemForSurfaces;

        private static readonly string[] s_PavedWords =
        {
            "concrete", "asphalt", "pavement", "paving", "paved", "tile", "brick", "stone", "cobble",
            "parking", "plaza", "sidewalk", "walkway", "driveway", "patio", "gravel",
        };

        /// <summary>
        /// Collects the building's paved lot surfaces - drives, patios, paths - as triangles.
        /// A lot's surfaces are areas owned by the building, each with a surface prefab whose name
        /// says what it is; ground covered by none is the terrain itself, grass or soil.
        /// </summary>
        private void CollectPavedTriangles(Entity building)
        {
            m_PavedTriangles.Clear();
            if (!m_EntityManager.HasBuffer<Game.Areas.SubArea>(building))
            {
                return;
            }

            if (m_PrefabSystemForSurfaces == null)
            {
                m_PrefabSystemForSurfaces = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<Game.Prefabs.PrefabSystem>();
            }

            DynamicBuffer<Game.Areas.SubArea> subAreas = m_EntityManager.GetBuffer<Game.Areas.SubArea>(building, true);
            for (int i = 0; i < subAreas.Length; i++)
            {
                Entity area = subAreas[i].m_Area;
                if (!m_EntityManager.Exists(area)
                    || !m_EntityManager.HasComponent<Game.Areas.Surface>(area)
                    || !m_EntityManager.HasComponent<PrefabRef>(area)
                    || !m_EntityManager.HasBuffer<Game.Areas.Node>(area)
                    || !m_EntityManager.HasBuffer<Game.Areas.Triangle>(area)
                    || !IsPavedSurface(m_EntityManager.GetComponentData<PrefabRef>(area).m_Prefab))
                {
                    continue;
                }

                DynamicBuffer<Game.Areas.Node> nodes = m_EntityManager.GetBuffer<Game.Areas.Node>(area, true);
                DynamicBuffer<Game.Areas.Triangle> triangles = m_EntityManager.GetBuffer<Game.Areas.Triangle>(area, true);
                for (int t = 0; t < triangles.Length; t++)
                {
                    int3 index = triangles[t].m_Indices;
                    if (math.any(index < 0) || math.any(index >= nodes.Length))
                    {
                        continue;
                    }

                    m_PavedTriangles.Add(new GroundTriangle
                    {
                        A = nodes[index.x].m_Position.xz,
                        B = nodes[index.y].m_Position.xz,
                        C = nodes[index.z].m_Position.xz,
                    });
                }
            }
        }

        private bool IsPaved(float2 point)
        {
            for (int i = 0; i < m_PavedTriangles.Count; i++)
            {
                GroundTriangle t = m_PavedTriangles[i];
                float d1 = Cross(point, t.A, t.B);
                float d2 = Cross(point, t.B, t.C);
                float d3 = Cross(point, t.C, t.A);
                bool hasNegative = d1 < 0f || d2 < 0f || d3 < 0f;
                bool hasPositive = d1 > 0f || d2 > 0f || d3 > 0f;
                if (!(hasNegative && hasPositive))
                {
                    return true;
                }
            }

            return false;
        }

        private static float Cross(float2 p, float2 a, float2 b)
        {
            return (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
        }

        /// <summary>Whether a surface prefab is paving, by its name. Each new name is written to
        /// the log once with the verdict, so the word list can be checked against the game's.</summary>
        private bool IsPavedSurface(Entity surfacePrefab)
        {
            bool paved;
            if (m_PavedByPrefab.TryGetValue(surfacePrefab, out paved))
            {
                return paved;
            }

            string name = null;
            try
            {
                name = m_PrefabSystemForSurfaces != null ? m_PrefabSystemForSurfaces.GetPrefabName(surfacePrefab) : null;
            }
            catch
            {
                // Unnamed: treated as unpaved.
            }

            string lower = name != null ? name.ToLowerInvariant() : string.Empty;
            paved = false;
            for (int i = 0; i < s_PavedWords.Length && !paved; i++)
            {
                paved = lower.Contains(s_PavedWords[i]);
            }

            m_PavedByPrefab[surfacePrefab] = paved;
            return paved;
        }

        /// <summary>Wall points lower than this, in metres above the building's base, are left
        /// out of the footprint: fences and hedges, not the house.</summary>
        private const float GroundFootprintHeight = 2.2f;

        /// <summary>The house's extent on its lot, in the prefab's own space, from the surveyed
        /// wall points above fence height. False when the prefab has not been surveyed.</summary>
        private bool TryWallFootprint(Entity prefab, out float2 min, out float2 max)
        {
            min = new float2(float.MaxValue);
            max = new float2(float.MinValue);
            List<BuildingSurfaceProbe.SurfacePoint>[] template;
            if (!m_SurfaceTemplates.TryGetValue(prefab, out template) || template == null)
            {
                return false;
            }

            bool any = false;
            for (int side = 0; side < template.Length; side++)
            {
                List<BuildingSurfaceProbe.SurfacePoint> points = template[side];
                if (points == null)
                {
                    continue;
                }

                for (int i = 0; i < points.Count; i++)
                {
                    float3 p = points[i].m_Position;
                    if (p.y < GroundFootprintHeight)
                    {
                        continue;
                    }

                    min = math.min(min, p.xz);
                    max = math.max(max, p.xz);
                    any = true;
                }
            }

            return any;
        }

        /// <summary>Marks the last Apply put on the walls and on the lot, for the log.</summary>
        public int LastWallMarks { get; private set; }
        public int LastGroundMarks { get; private set; }

        /// <summary>Where the last ground mark went, against the terrain and the building, for
        /// the log while the ground layer is new.</summary>
        public string LastGroundExample { get; private set; }

        /// <summary>
        /// Lays decals flat on the building's lot, where the building does not stand: small
        /// cracks from Aged, rubbish and leaves from Worn, large cracks, damage and repair patches
        /// from Neglected, stains and puddles at Decayed (see DecalPrefabCatalog.ClassifyForGround).
        /// Crack and repair decals that cannot paint walls belong here, and a small house whose
        /// walls take no decal still shows its state on its drive and garden.
        ///
        /// Positions are random over the lot, outside the building's footprint plus half a metre,
        /// at the terrain's height there; each decal turns at random about the vertical.
        /// </summary>
        private void PlanGround(
            Entity building,
            in BuildingVisualProfile profile,
            ref Unity.Mathematics.Random rng,
            int garbageTier,
            bool garbageOnly,
            int budget)
        {
            LastGroundExample = "skipped: nothing asked for at this state, or no ground decals";
            int baseCount = garbageOnly ? 0 : s_GroundByState[math.clamp((int)profile.State, 0, s_GroundByState.Length - 1)];
            int litterCount = garbageOnly && garbageTier > 0
                ? s_LitterGroundByTier[math.clamp(garbageTier, 0, s_LitterGroundByTier.Length - 1)]
                : 0;
            if ((baseCount == 0 && litterCount == 0) || m_Catalog.GroundPoolCount == 0
                || !m_EntityManager.HasComponent<Transform>(building)
                || !m_EntityManager.HasComponent<PrefabRef>(building))
            {
                return;
            }

            Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
            if (!m_EntityManager.HasComponent<BuildingData>(prefab))
            {
                return;
            }

            int2 lot = m_EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize;
            float2 half = new float2(lot.x, lot.y) * 4f;

            // The footprint comes from the surveyed walls, not from ObjectGeometryData: a
            // growable's bounds can cover its whole lot, which left no free ground at all. Only
            // points above GroundFootprintHeight count, so the fences and hedges a lot often has
            // as part of the building's model do not stretch the house to the lot's edges.
            float2 footMin;
            float2 footMax;
            if (!TryWallFootprint(prefab, out footMin, out footMax))
            {
                LastGroundExample = "skipped: no surveyed walls to tell the house from its lot";
                return;
            }

            footMin -= 0.5f;
            footMax += 0.5f;
            float lotArea = 4f * half.x * half.y;
            float footArea = math.max(0f, (math.min(footMax.x, half.x) - math.max(footMin.x, -half.x))
                                         * (math.min(footMax.y, half.y) - math.max(footMin.y, -half.y)));
            float free = lotArea - footArea;
            if (free < 8f)
            {
                LastGroundExample = "skipped: " + free.ToString("0") + " m² of free ground on a "
                                  + (half.x * 2f).ToString("0") + "x" + (half.y * 2f).ToString("0") + " m lot, footprint "
                                  + footMin.x.ToString("0.0") + ".." + footMax.x.ToString("0.0") + " / "
                                  + footMin.y.ToString("0.0") + ".." + footMax.y.ToString("0.0");
                return;
            }

            float intensity = Mod.Settings != null ? Mod.Settings.IntensityScale : 1f;
            float areaScale = math.clamp(free / GroundAreaUnit, 0.5f, 2.5f);
            int requested = garbageOnly
                ? (int)math.round(litterCount * areaScale * math.min(1f, intensity))
                : (int)math.round(baseCount * areaScale * math.min(1f, intensity));
            if (garbageOnly && requested == 0 && intensity > 0f) requested = 1;
            int count = math.min(math.clamp(requested, 1, MaxGroundMarks), math.max(0, budget - m_Pending.Count));
            if (count <= 0)
            {
                return;
            }

            var families = new List<OverlayFamily>(3);
            if (!garbageOnly) families.Add(OverlayFamily.Crack);
            if (profile.State >= VisualState.Worn) families.Add(OverlayFamily.Dirt);
            if (!garbageOnly && profile.State >= VisualState.Decayed) families.Add(OverlayFamily.Stain);

            if (m_TerrainSystem == null)
            {
                m_TerrainSystem = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<Game.Simulation.TerrainSystem>();
            }

            // Without waiting for pending terrain work: GetHeightData(true) completes the terrain
            // jobs first, and a placement that asked for it took 27 ms with every other part
            // accounted for. A height a frame old is just as good for a mark on the ground.
            long groundStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            bool haveTerrain = m_TerrainSystem != null;
            Game.Simulation.TerrainHeightData terrain = haveTerrain ? m_TerrainSystem.GetHeightData(false) : default;
            haveTerrain = haveTerrain && terrain.isCreated;

            Transform transform = m_EntityManager.GetComponentData<Transform>(building);
            float maxEdge = math.min(MaxGroundEdge, math.cmin(half));
            int firstFamily = garbageOnly ? 0 : rng.NextInt(families.Count);
            CollectPavedTriangles(building);
            m_GroundSetupTicks += System.Diagnostics.Stopwatch.GetTimestamp() - groundStarted;

            for (int i = 0; i < count; i++)
            {
                DecalPrefabInfo decal = null;
                OverlayFamily family = OverlayFamily.None;
                if (garbageOnly)
                {
                    family = OverlayFamily.Dirt;
                    decal = m_Catalog.PickTrashGround(maxEdge, ref rng);
                }
                else
                {
                    for (int f = 0; f < families.Count && decal == null; f++)
                    {
                        family = families[(firstFamily + i + f) % families.Count];
                        decal = m_Catalog.PickGround(family, maxEdge, profile.State, ref rng);
                    }
                }

                if (decal == null)
                {
                    LastGroundExample = garbageOnly
                        ? "skipped: G87 Trash Decals ground assets are not installed or do not fit this lot"
                        : "skipped: no ground decal of " + string.Join("/", families)
                                      + " up to " + maxEdge.ToString("0.0") + " m allowed at " + profile.State;
                    return;
                }

                bool placedOne = false;
                for (int attempt = 0; attempt < GroundAttempts && !placedOne; attempt++)
                {
                    float2 local = new float2(rng.NextFloat(-half.x, half.x), rng.NextFloat(-half.y, half.y));
                    if (math.all(local > footMin) && math.all(local < footMax))
                    {
                        continue;
                    }

                    float3 position = transform.m_Position + math.mul(transform.m_Rotation, new float3(local.x, 0f, local.y));
                    float extent = math.max(decal.Size.x, decal.Size.z);
                    if (TooCloseToPlanned(position, extent, -1))
                    {
                        continue;
                    }

                    // Paving or not: patches only on paving, cracks mostly there, leaves mostly on
                    // grass. "Mostly" is a chance to accept the other kind of ground, so a lot
                    // with little of the preferred one still gets its marks.
                    bool paved = IsPaved(position.xz);
                    bool accept;
                    switch (decal.GroundSurface)
                    {
                        case GroundSurfacePreference.PavedOnly:
                            accept = paved;
                            break;
                        case GroundSurfacePreference.PreferPaved:
                            accept = paved || rng.NextFloat() < OtherGroundChance;
                            break;
                        case GroundSurfacePreference.PreferUnpaved:
                            accept = !paved || rng.NextFloat() < OtherGroundChance;
                            break;
                        default:
                            accept = true;
                            break;
                    }

                    if (!accept)
                    {
                        continue;
                    }

                    if (haveTerrain)
                    {
                        position.y = Game.Simulation.TerrainUtils.SampleHeight(ref terrain, position);
                    }

                    LastGroundExample = decal.Name + " " + decal.Size.x.ToString("0.0") + "x" + decal.Size.z.ToString("0.0")
                                      + " at lot (" + local.x.ToString("0.0") + ", " + local.y.ToString("0.0")
                                      + ") of " + (half.x * 2f).ToString("0") + "x" + (half.y * 2f).ToString("0")
                                      + " m, footprint " + footMin.x.ToString("0.0") + ".." + footMax.x.ToString("0.0")
                                      + " / " + footMin.y.ToString("0.0") + ".." + footMax.y.ToString("0.0")
                                      + ", terrain height " + position.y.ToString("0.00")
                                      + " against building " + transform.m_Position.y.ToString("0.00")
                                      + (haveTerrain ? string.Empty : " (no terrain system)")
                                      + ", on " + (paved ? "paving" : "unpaved ground")
                                      + " (" + m_PavedTriangles.Count + " paved triangle(s) on this lot)";

                    m_Pending.Add(new PendingElement
                    {
                        Prefab = decal.PrefabEntity,
                        Position = position,
                        Rotation = quaternion.RotateY(rng.NextFloat(0f, 2f * math.PI)),
                        Family = family,
                        FacadeIndex = -1,
                        Extent = extent,
                    });
                    placedOne = true;
                }

                if (!placedOne)
                {
                    LastGroundExample = "skipped: no suitable point among " + GroundAttempts + " for " + decal.Name
                                      + " (" + decal.GroundSurface + ", " + m_PavedTriangles.Count + " paved triangle(s)); footprint "
                                      + footMin.x.ToString("0.0") + ".." + footMax.x.ToString("0.0") + " / "
                                      + footMin.y.ToString("0.0") + ".." + footMax.y.ToString("0.0")
                                      + " of a " + (half.x * 2f).ToString("0") + "x" + (half.y * 2f).ToString("0") + " m lot";
                }
            }
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
                            facade.Width * MaxOverhang,
                            facade.Height * MaxOverhang,
                            ref rng,
                            out familyMatched,
                            profile.State);
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
                            facade.Width * MaxOverhang,
                            facade.Height * MaxOverhang,
                            ref rng,
                            out familyMatched,
                            m_PlanState);
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
            return TryPlanMarkOnFacade(building, family, decal, facadeIndex,
                                       preferLowerGraffiti, false, ref rng);
        }

        private bool TryPlanMarkOnFacade(
            Entity building,
            OverlayFamily family,
            DecalPrefabInfo decal,
            int facadeIndex,
            bool preferLowerGraffiti,
            bool forceLow,
            ref Unity.Mathematics.Random rng)
        {
            BuildingFacade facade = m_Facades[facadeIndex];

            // A decal may be up to MaxOverhang times the facade: it is only drawn where it meets a
            // surface, so the part past the wall's edge or over a ledge simply does not show, and
            // a stain spilling onto the pavement at the foot of the wall reads as weathering too.
            // Before 2026-10-02 it had to fit inside the facade, and on small houses no wall decal
            // the installed packs offer fitted at all - most were left bare. The known cost: on
            // attached row houses a wide decal can reach the house next door, which is why the
            // limit existed (a Maintained row house once showed two stains from its neighbour).
            if (!AllowNonBuildingDecals
                && (decal.Size.x > facade.Width * MaxOverhang || decal.Size.z > facade.Height * MaxOverhang))
            {
                return false;
            }

            // Keep the decal's centre on the facade, so even an oversized decal always meets the
            // wall: a small one can never end up entirely off it and invisible.
            float marginU = math.min(0.5f, decal.Size.x * 0.5f / math.max(facade.Width, 0.01f));
            float marginV = math.min(0.5f, decal.Size.z * 0.5f / math.max(facade.Height, 0.01f));
            float u = math.lerp(marginU, 1f - marginU, rng.NextFloat());

            float v = family == OverlayFamily.Graffiti || forceLow
                ? GraffitiHeight(facade.Height, decal.Size.z, preferLowerGraffiti || forceLow, marginV, ref rng)
                : math.lerp(marginV, 1f - marginV, SampleVerticalPosition(family, ref rng));

            float3 position;
            quaternion rotation;
            if (!TryResolvePlacement(building, facade, u, v, ref rng, out position, out rotation,
                    family == OverlayFamily.Graffiti || forceLow ? GraffitiVerticalTolerance : float.MaxValue))
            {
                return false;
            }

            // The real mesh sample can move away from the rectangle's proposed point. Recheck that
            // the final centre is still on the facade. A decal that fits keeps the old, stricter
            // rule - wholly inside - so only oversized decals are allowed past the edges.
            float3 fromCenter = position - facade.Center;
            float halfWidth = facade.Width * 0.5f;
            float halfHeight = facade.Height * 0.5f;
            float alongWidth = math.abs(math.dot(fromCenter, facade.Tangent));
            float alongHeight = math.abs(math.dot(fromCenter, facade.Up));
            float allowedU = decal.Size.x <= facade.Width ? halfWidth - decal.Size.x * 0.5f : halfWidth;
            float allowedV = decal.Size.z <= facade.Height ? halfHeight - decal.Size.z * 0.5f : halfHeight;
            if (!AllowNonBuildingDecals && (alongWidth > allowedU || alongHeight > allowedV))
            {
                return false;
            }

            float extent = math.max(decal.Size.x, decal.Size.z);
            if (!AllowNonBuildingDecals && TooCloseToPlanned(position, extent, facadeIndex))
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
                Extent = extent,
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
        /// Where graffiti goes up a wall, in metres rather than as a share of the facade.
        ///
        /// It used to be a fraction of the facade height, biased to the edges - on a 25 m block
        /// "near the bottom" still meant the second or third floor, out of anybody's reach. Real
        /// graffiti is either within arm's reach of the pavement or just under the roofline,
        /// painted from the roof. So: most pieces sit with their lower edge 0.2-1.2 m above the
        /// ground (a large mural starts at the ground), the rest with their upper edge 0.3-1.5 m
        /// below the top. The street-facing example is always at street level. Returns the
        /// vertical position as the facade fraction the placement code works in.
        /// </summary>
        /// <summary>How far, in metres, graffiti may land above or below the height it was given.</summary>
        private const float GraffitiVerticalTolerance = 2f;

        private static float GraffitiHeight(
            float facadeHeight,
            float decalHeight,
            bool streetLevelOnly,
            float marginV,
            ref Unity.Mathematics.Random rng)
        {
            float height = math.max(facadeHeight, 0.01f);
            float half = decalHeight * 0.5f;

            float centre = streetLevelOnly || rng.NextFloat() < 0.7f
                ? half + math.lerp(0.2f, 1.2f, rng.NextFloat())
                : height - half - math.lerp(0.3f, 1.5f, rng.NextFloat());

            return math.clamp(centre / height, marginV, 1f - marginV);
        }

        /// <summary>
        /// Chooses the height of a mark within the usable facade rectangle.
        ///
        /// Ordinary weathering accumulates near the base, so its uniform sample is squared.
        /// Graffiti does not come through here any more - see <see cref="GraffitiHeight"/>.
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
            long spawnStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                SpawnPendingUntimed(building, record);
            }
            finally
            {
                m_SpawnTicks += System.Diagnostics.Stopwatch.GetTimestamp() - spawnStarted;
            }
        }

        private void SpawnPendingUntimed(Entity building, OverlayRecord record)
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
                        ushort seed;
                        if (pending.DesignSeedPlusOne > 0)
                        {
                            seed = (ushort)(pending.DesignSeedPlusOne - 1);
                        }
                        else
                        {
                            uint mixed = (uint)building.Index * 0x9E3779B9u
                                       ^ (uint)pending.Prefab.Index * 0x85EBCA6Bu
                                       ^ (uint)(i + 1);
                            seed = (ushort)(mixed ^ (mixed >> 16));
                        }

                        commandBuffer.SetComponent(element, new PseudoRandomSeed(seed));
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
                    // The building's previous marks are queued for deletion but still alive
                    // until the next frame; they are not part of the new record.
                    WeatheringOverlay overlay = m_EntityManager.GetComponentData<WeatheringOverlay>(live[i]);
                    if (overlay.m_Building == building && !DeferredDeletion.IsPending(live[i]))
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
        /// <summary>
        /// The marks currently on a building, as a total and a per-family breakdown ("Crack 4,
        /// Stain 2"). Read from the live overlay entities, so it is what is drawn, not what was
        /// planned. False when the building has none.
        /// </summary>
        public bool TryCountPlaced(Entity building, out int total, out string families)
        {
            total = 0;
            families = null;

            OverlayRecord record;
            if (!m_Records.TryGetValue(building, out record) || record.Elements.Count == 0)
            {
                return false;
            }

            var counts = new Dictionary<OverlayFamily, int>();
            for (int i = 0; i < record.Elements.Count; i++)
            {
                Entity element = record.Elements[i];
                if (!m_EntityManager.Exists(element) || !m_EntityManager.HasComponent<WeatheringOverlay>(element))
                {
                    continue;
                }

                var family = (OverlayFamily)m_EntityManager.GetComponentData<WeatheringOverlay>(element).m_Family;
                int n;
                counts.TryGetValue(family, out n);
                counts[family] = n + 1;
                total++;
            }

            if (total == 0)
            {
                return false;
            }

            var sb = new System.Text.StringBuilder();
            foreach (var pair in counts)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(pair.Key).Append(' ').Append(pair.Value);
            }

            families = sb.ToString();
            return true;
        }

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

            // The building itself: the game picks a mesh variant per building from its seed, and
            // a design made on one variant can miss a facade that sits elsewhere on another.
            if (m_EntityManager.Exists(building))
            {
                sb.AppendLine();
                sb.Append("  BUILDING ").Append(building.Index);
                if (m_EntityManager.HasComponent<Transform>(building))
                {
                    Transform bt = m_EntityManager.GetComponentData<Transform>(building);
                    sb.AppendFormat(" at ({0:0.0},{1:0.0},{2:0.0}) rotation=({3:0.000},{4:0.000},{5:0.000},{6:0.000})",
                        bt.m_Position.x, bt.m_Position.y, bt.m_Position.z,
                        bt.m_Rotation.value.x, bt.m_Rotation.value.y, bt.m_Rotation.value.z, bt.m_Rotation.value.w);
                }

                if (m_EntityManager.HasComponent<PseudoRandomSeed>(building))
                {
                    sb.Append(" seed=").Append(m_EntityManager.GetComponentData<PseudoRandomSeed>(building).m_Seed);
                }

                if (m_EntityManager.HasBuffer<Game.Rendering.MeshGroup>(building))
                {
                    DynamicBuffer<Game.Rendering.MeshGroup> groups = m_EntityManager.GetBuffer<Game.Rendering.MeshGroup>(building, true);
                    sb.Append(" meshGroups=[");
                    for (int g = 0; g < groups.Length; g++)
                    {
                        if (g > 0) sb.Append(", ");
                        sb.Append(groups[g].m_SubMeshGroup).Append('/').Append(groups[g].m_MeshOffset);
                    }

                    sb.Append(']');
                }
                else
                {
                    sb.Append(" meshGroups=none");
                }

                if (m_EntityManager.HasBuffer<Game.Rendering.MeshBatch>(building))
                {
                    DynamicBuffer<Game.Rendering.MeshBatch> buildingBatches = m_EntityManager.GetBuffer<Game.Rendering.MeshBatch>(building, true);
                    sb.Append(" meshes=[");
                    for (int b = 0; b < buildingBatches.Length; b++)
                    {
                        if (b > 0) sb.Append(", ");
                        sb.Append(buildingBatches[b].m_MeshIndex).Append('@').Append(buildingBatches[b].m_GroupIndex);
                    }

                    sb.Append(']');
                }
            }

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
                    if (overlay.m_Building != building || DeferredDeletion.IsPending(live[i]))
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
        /// at the end of the frame. Destroying it ourselves would leave the batch behind. The tag
        /// goes through the game's first modification barrier, not straight on: see
        /// <see cref="DeferredDeletion"/> for the crash tagging it from here caused.
        /// </summary>
        private void DestroyElement(Entity element)
        {
            DeferredDeletion.Delete(m_EntityManager, element);
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
                // Logged before removing: a native crash followed the demolition of a building
                // carrying a design on 2026-10-03, and this is the last thing the mod does then.
                OverlayRecord record;
                if (m_Log != null && m_Records.TryGetValue(dead[i], out record))
                {
                    m_Log.Info("Seen Better Days: building " + dead[i].Index + " "
                             + (!m_EntityManager.Exists(dead[i]) ? "no longer exists"
                                : m_EntityManager.HasComponent<Deleted>(dead[i]) ? "is being demolished"
                                : "is under construction")
                             + "; removing its " + record.Elements.Count + " decal(s)"
                             + (record.Design != null ? " (hand-made design)" : "") + ".");
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

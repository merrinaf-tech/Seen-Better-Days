using Game;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;
using SeenBetterDays.Data;
using SeenBetterDays.Geometry;
using SeenBetterDays.Rendering;

namespace SeenBetterDays.Systems
{
    /// <summary>
    /// Puts the decal detail layer on the buildings the player is close enough to see it on, and
    /// takes it off again when they leave.
    ///
    /// Being proximity-driven is not a compromise forced by a limitation. `MeshVertex` and
    /// `MeshIndex` are not guaranteed to be resident for a prefab the game is not drawing up close,
    /// and placement here is decided by casting rays at the real mesh - so a distant building has
    /// nothing to cast at, and a mark placed on it would be guesswork. It would also be invisible:
    /// at that distance a projector half a metre deep is sub-pixel. The right time to decide where
    /// a graffito goes is when someone can see the wall.
    ///
    /// Nothing is lost by rebuilding it each time. This mod persists nothing, and the placement is
    /// deterministic in the building's seed, so the same wall gets the same marks in the same
    /// places every time the player comes back to it.
    ///
    /// The colour weathering in <see cref="BuildingWeatheringSystem"/> is the opposite: it is
    /// city-wide and always on, because a tint costs nothing and reads at any distance. Together
    /// they are the two halves the brief asked for - a base that colours the whole city, and a
    /// detail layer that only exists where detail can be seen.
    /// </summary>
    public partial class DecalDetailSystem : GameSystemBase
    {
        /// <summary>Close enough that the marks are worth placing.</summary>
        private const float NearRadius = 200f;

        /// <summary>Far enough to take them away again. The gap between the two is deliberate:
        /// with a single radius a building sitting exactly on it would be built and destroyed on
        /// alternate passes for as long as the player stood still.</summary>
        private const float FarRadius = 280f;

        /// <summary>Buildings looked at per update.</summary>
        private const int ScanPerUpdate = 128;

        /// <summary>
        /// How many expensive placement attempts may run per update. Count attempts rather than
        /// successes: the previous limit allowed an arbitrary number of mesh walks when several
        /// buildings could not accept a decal, which is how a normally cheap pass reached 67 ms.
        /// One attempt every sixteen frames still fills the camera radius quickly without stacking
        /// two complex prefabs into one frame.
        /// </summary>
        private const int PlacementAttemptsPerUpdate = 1;

        /// <summary>
        /// Failed plans are deterministic for a building's weathering value and seed. Remember
        /// the ones for which the renderer found no valid automatic placement, otherwise the
        /// proximity scan retries the exact same work every time its cursor comes round. Keep the
        /// cache bounded so long-running cities that replace many buildings cannot grow it
        /// forever; clearing it is safe because it merely permits another attempt.
        /// </summary>
        private const int MaxRejectedPlacements = 4096;

        /// <summary>
        /// The status line is useful while tuning, but reporting it every 32 passes produced
        /// roughly one line every 1.6 seconds and buried the events that mattered. Keep the same
        /// counters and publish a sample about every thirteen seconds instead; a successful build
        /// is still logged immediately by <see cref="TryBuild"/>.
        /// </summary>
        private const int StatusReportPasses = 256;

        private Game.Rendering.CameraUpdateSystem m_CameraSystem;
        private BuildingOverlayTestSystem m_Harness;
        private EntityQuery m_GrowableQuery;
        private EntityQuery m_UnderConstructionQuery;
        private int m_Cursor;

        /// <summary>
        /// What the last few passes actually did, reported periodically.
        ///
        /// Added because the layer was switched on, quietly detailed twenty-six buildings, and was
        /// reported as doing nothing. A system that works invisibly and a system that is broken
        /// look identical from the outside; the difference has to be something it says.
        /// </summary>
        private int m_Built;
        private int m_PlacementAttempts;
        private int m_Removed;
        private int m_InRange;
        private int m_BelowThreshold;
        private int m_MeshNotReady;
        private int m_NoWeatheringState;
        private int m_Ineligible;
        private int m_ApplyFailed;
        private int m_SuppressedRetries;
        private string m_LastApplyFailure;
        private int m_Passes;
        private int m_TimedPasses;
        private double m_WorkMilliseconds;
        private double m_MaxPassMilliseconds;

        private struct RejectedPlacement
        {
            public uint WeatheringBits;
            public uint Seed;
            public uint IntensityBits;
        }

        private readonly Dictionary<Entity, RejectedPlacement> m_RejectedPlacements =
            new Dictionary<Entity, RejectedPlacement>();

        public new bool Enabled { get; set; }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();

            m_CameraSystem = World.GetOrCreateSystemManaged<Game.Rendering.CameraUpdateSystem>();
            m_Harness = World.GetOrCreateSystemManaged<BuildingOverlayTestSystem>();

            m_GrowableQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            m_UnderConstructionQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<UnderConstruction>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            RequireForUpdate(m_GrowableQuery);
        }

        /// <summary>
        /// Switches the detail layer on with the city.
        ///
        /// Guarded on <c>mode.IsGame()</c> so it stays out of the editor, where placing marks on
        /// an asset being authored would be unwelcome.
        /// </summary>
        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            Enabled = mode.IsGame();
            m_Passes = 0;
            m_RejectedPlacements.Clear();
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 16;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (SaveMutationGate.IsBlocked)
            {
                return;
            }

            DecalObjectOverlayRenderer renderer = m_Harness != null ? m_Harness.DecalRenderer : null;
            if (renderer == null || !renderer.IsAvailable)
            {
                return;
            }

            bool wanted = Enabled
                       && (Mod.Settings == null
                           || (Mod.Settings.EnableWeathering && Mod.Settings.EnableDecalDetail));

            if (!wanted)
            {
                if (renderer.TrackedBuildingCount > 0)
                {
                    renderer.RemoveAll();
                }

                return;
            }

            RemoveConstructionDetails(renderer);

            if (m_CameraSystem == null || m_CameraSystem.activeCameraController == null)
            {
                return;
            }

            float3 eye = m_CameraSystem.activeCameraController.pivot;

            long passStarted = Stopwatch.GetTimestamp();
            NativeArray<Entity> buildings = m_GrowableQuery.ToEntityArray(Allocator.TempJob);
            try
            {
                if (buildings.Length == 0)
                {
                    return;
                }

                int placementAttempts = 0;
                int scanned = math.min(ScanPerUpdate, buildings.Length);

                for (int i = 0; i < scanned; i++)
                {
                    m_Cursor = (m_Cursor + 1) % buildings.Length;
                    Entity building = buildings[m_Cursor];

                    if (!EntityManager.Exists(building) || !EntityManager.HasComponent<Transform>(building))
                    {
                        continue;
                    }

                    float distance = math.distance(
                        EntityManager.GetComponentData<Transform>(building).m_Position, eye);

                    bool has = renderer.Has(building);

                    if (distance > FarRadius)
                    {
                        if (has)
                        {
                            if (renderer.Remove(building))
                            {
                                m_Removed++;
                            }
                        }

                        continue;
                    }

                    if (distance <= NearRadius)
                    {
                        m_InRange++;
                    }

                    if (has || distance > NearRadius
                        || placementAttempts >= PlacementAttemptsPerUpdate)
                    {
                        continue;
                    }

                    bool attemptedPlacement;
                    if (TryBuild(renderer, building, out attemptedPlacement))
                    {
                        m_Built++;
                    }

                    if (attemptedPlacement)
                    {
                        placementAttempts++;
                        m_PlacementAttempts++;
                    }
                }
            }
            finally
            {
                buildings.Dispose();

                double elapsedMilliseconds =
                    (Stopwatch.GetTimestamp() - passStarted) * 1000d / Stopwatch.Frequency;
                m_TimedPasses++;
                m_WorkMilliseconds += elapsedMilliseconds;
                m_MaxPassMilliseconds = math.max(m_MaxPassMilliseconds, elapsedMilliseconds);
            }

            if (++m_Passes >= StatusReportPasses)
            {
                Mod.Log.Info("Seen Better Days: decal detail layer - " + m_Built
                           + " building(s) detailed from " + m_PlacementAttempts
                           + " placement attempt(s), " + m_Removed
                           + " removed after leaving the radius, "
                           + renderer.TrackedBuildingCount + " currently detailed, "
                           + m_InRange + " seen in range, "
                           + m_BelowThreshold + " too lightly weathered to bother, "
                           + m_MeshNotReady + " whose mesh is not loaded yet, "
                           + m_NoWeatheringState + " awaiting a weathering state, "
                           + m_Ineligible + " ineligible, "
                           + m_ApplyFailed + " placement failure(s)"
                           + ", " + m_SuppressedRetries
                           + " unchanged failed plan(s) skipped"
                           + (string.IsNullOrEmpty(m_LastApplyFailure)
                               ? "."
                               : " (last: " + m_LastApplyFailure + ").")
                           + " System work: "
                           + (m_TimedPasses > 0
                               ? (m_WorkMilliseconds / m_TimedPasses).ToString("0.000")
                               : "0.000")
                           + " ms average, " + m_MaxPassMilliseconds.ToString("0.000")
                           + " ms slowest pass.");

                m_Passes = 0;
                m_Built = 0;
                m_PlacementAttempts = 0;
                m_Removed = 0;
                m_InRange = 0;
                m_BelowThreshold = 0;
                m_MeshNotReady = 0;
                m_NoWeatheringState = 0;
                m_Ineligible = 0;
                m_ApplyFailed = 0;
                m_SuppressedRetries = 0;
                m_LastApplyFailure = null;
                m_TimedPasses = 0;
                m_WorkMilliseconds = 0d;
                m_MaxPassMilliseconds = 0d;
            }
        }

        /// <summary>
        /// A building can enter construction after it already received close-range marks, most
        /// notably during an upgrade. Sweep the normally tiny construction set every pass so
        /// those marks disappear without waiting for the round-robin cursor to find the building.
        /// </summary>
        private void RemoveConstructionDetails(DecalObjectOverlayRenderer renderer)
        {
            NativeArray<Entity> buildings = m_UnderConstructionQuery.ToEntityArray(Allocator.TempJob);
            try
            {
                for (int i = 0; i < buildings.Length; i++)
                {
                    // Also scan the component marker. A save/reload or interrupted apply can
                    // lose the dictionary record while the overlay entity is still alive.
                    if (renderer.RemoveIncludingUntracked(buildings[i]))
                    {
                        m_Removed++;
                    }
                }
            }
            finally
            {
                buildings.Dispose();
            }
        }

        /// <summary>
        /// Rebuilds one building's marks immediately, wherever it is.
        ///
        /// Used when the player sets a state by hand. Waiting for the round-robin to reach it
        /// would show the old state's marks against the new state's colour for a few seconds,
        /// which during a side-by-side comparison is not a delay - it is a wrong answer.
        /// </summary>
        public bool RebuildNow(Entity building, out string report)
        {
            report = null;

            DecalObjectOverlayRenderer renderer = m_Harness != null ? m_Harness.DecalRenderer : null;
            if (renderer == null || !renderer.IsAvailable)
            {
                report = "the decal catalogue is not available";
                return false;
            }

            renderer.RemoveIncludingUntracked(building);

            // Maintained is deliberately a decal-free state. Say that before looking for mesh
            // geometry: a distant Maintained building needs no mesh and will never "pick up"
            // marks merely because the camera moves closer.
            if (EntityManager.HasComponent<WeatheringState>(building)
                && BuildingVisualProfile.StateFor(
                    EntityManager.GetComponentData<WeatheringState>(building).m_Weathering)
                    == VisualState.Maintained)
            {
                report = "nothing to place at Maintained";
                return false;
            }

            if (!BuildingSurfaceProbe.HasMeshGeometry(EntityManager, building))
            {
                report = "the building's mesh is not loaded, so there is nothing to place marks on "
                       + "- move closer and it will pick them up";
                return false;
            }

            if (!TryBuild(renderer, building))
            {
                report = "nothing to place at this state";
                return false;
            }

            report = renderer.LastPlacementReport;
            return true;
        }

        private bool TryBuild(DecalObjectOverlayRenderer renderer, Entity building)
        {
            bool ignored;
            return TryBuild(renderer, building, out ignored);
        }

        private bool TryBuild(
            DecalObjectOverlayRenderer renderer,
            Entity building,
            out bool attemptedPlacement)
        {
            attemptedPlacement = false;

            if (!EntityManager.HasComponent<WeatheringState>(building))
            {
                m_NoWeatheringState++;
                return false;
            }

            WeatheringState state = EntityManager.GetComponentData<WeatheringState>(building);
            // Maintained has every decal family hard-gated to zero. Treating 8-12% as eligible
            // made the renderer correctly decline the empty profile, but the status report then
            // called that expected result a placement failure on every scan.
            if (BuildingVisualProfile.StateFor(state.m_Weathering) == VisualState.Maintained)
            {
                m_BelowThreshold++;
                return false;
            }

            string reason;
            Entity prefab;
            int level;
            BuildingCategory category = BuildingClassifier.Classify(
                EntityManager, building, out prefab, out level, out reason);

            if (!category.IsEligible())
            {
                m_Ineligible++;
                return false;
            }

            // Checked before attempting, not discovered by failing. A building whose mesh is not
            // loaded yet is not a failure to report - it is simply not ready, and it will be on a
            // later pass once the game has drawn it.
            if (!BuildingSurfaceProbe.HasMeshGeometry(EntityManager, building))
            {
                m_MeshNotReady++;
                return false;
            }

            BuildingVisualProfile profile =
                BuildingVisualProfile.FromWeathering(category, state.m_Weathering, state.m_Seed);

            // The renderer and its random sequence are deterministic for this input. If an
            // unchanged profile already proved that it cannot produce a valid automatic mark,
            // rerunning facade and mesh placement can only produce the same result. A changed
            // weathering value invalidates the entry immediately and gets a fresh attempt.
            RejectedPlacement rejected;
            uint weatheringBits = math.asuint(profile.Weathering);
            uint intensityBits = math.asuint(
                Mod.Settings != null ? Mod.Settings.IntensityScale : 1f);
            bool automaticPlacement = renderer.ForcedDecal == null
                                   && !renderer.AllowNonBuildingDecals;
            if (automaticPlacement
                && m_RejectedPlacements.TryGetValue(building, out rejected))
            {
                if (rejected.WeatheringBits == weatheringBits
                    && rejected.Seed == profile.Seed
                    && rejected.IntensityBits == intensityBits)
                {
                    m_SuppressedRetries++;
                    return false;
                }

                m_RejectedPlacements.Remove(building);
            }

            int placed;
            string failure;
            attemptedPlacement = true;
            if (!renderer.Apply(building, profile, out placed, out failure))
            {
                m_ApplyFailed++;
                m_LastApplyFailure = failure;

                // This result means mesh data was available but the deterministic plan could not
                // fit a usable mark on the sampled wall. Residency-related failures are allowed
                // to retry because a later LOD can expose different mesh buffers.
                if (automaticPlacement
                    && (failure == "profile asked for nothing: every family is at zero intensity"
                        || failure == "no automatic mark fit a sampled facade"))
                {
                    if (m_RejectedPlacements.Count >= MaxRejectedPlacements)
                    {
                        m_RejectedPlacements.Clear();
                    }

                    m_RejectedPlacements[building] = new RejectedPlacement
                    {
                        WeatheringBits = weatheringBits,
                        Seed = profile.Seed,
                        IntensityBits = intensityBits,
                    };
                }

                return false;
            }

            m_RejectedPlacements.Remove(building);

            Mod.Log.Info("Seen Better Days: detailed entity " + building.Index + " with " + placed
                       + " mark(s) - " + renderer.LastPlacementReport
                       + " | weathering " + (state.m_Weathering * 100f).ToString("0") + "%");
            return true;
        }

        [Preserve]
        public DecalDetailSystem()
        {
        }
    }
}

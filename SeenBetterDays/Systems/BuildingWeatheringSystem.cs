using Game;
using Game.Buildings;
using Game.City;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Game.Zones;
using System.Diagnostics;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;
using SeenBetterDays.Data;
using SeenBetterDays.Rendering;
using SeenBetterDays.Simulation;

namespace SeenBetterDays.Systems
{
    /// <summary>
    /// Walks the city's growables and keeps each one looking the way its circumstances deserve.
    ///
    /// The work is spread: a slice of buildings per update, round-robin, so a city of forty
    /// thousand growables costs a predictable trickle rather than one enormous pass. A building is
    /// only handed to the renderer when its weathering has moved enough to be worth redrawing,
    /// which in a settled city is almost never.
    ///
    /// Deliberately still crude in two ways, both of which are fine at this stage and neither of
    /// which is hidden: the scan is a managed main-thread loop rather than a Burst job, and it
    /// visits every growable rather than only the ones whose circumstances changed. The honest
    /// version is change-driven - react to BuildingCondition and Abandoned changing - and that is
    /// the next optimisation, not a rewrite.
    /// </summary>
    public partial class BuildingWeatheringSystem : GameSystemBase
    {
        /// <summary>Buildings examined per update. Small on purpose; the whole city comes round
        /// in a few seconds of game time and nothing here is urgent.</summary>
        private const int BuildingsPerUpdate = 256;

        /// <summary>
        /// Initial catch-up budget. Loading a large city used to recolour every building in one
        /// update; the 13,824-growable performance test therefore concentrated 12,968 renderer
        /// writes and their structural changes into a single frame. A bounded batch keeps that
        /// work measurable and lets the game render between slices.
        /// </summary>
        private const int CatchUpBuildingsPerUpdate = 512;

        /// <summary>How much the weathering must move before it is worth rewriting colours.
        /// Below this the change would not be visible and the redraw would be waste.</summary>
        private const float RedrawThreshold = 0.02f;

        private Game.Simulation.CitySystem m_CitySystem;
        private Game.Serialization.SaveGameSystem m_SaveGameSystem;
        private MeshColorOverlayRenderer m_Renderer;
        private EntityQuery m_GrowableQuery;
        private EntityQuery m_UnderConstructionQuery;
        private int m_Cursor;

        /// <summary>
        /// What a street is worth in this city, recomputed whenever the round-robin wraps.
        ///
        /// The **median** is the one that matters. Low and high are kept only for the census, and
        /// deliberately not used to normalise anything: they are single streets, and letting two
        /// buildings out of eighteen hundred set the scale for the rest is what put 1634 of them
        /// into the same state.
        /// </summary>
        private float m_LandValueLow;
        private float m_LandValueHigh;
        private float m_LandValueMedian;
        private float m_LandValueFloor;
        private float m_LandValueHighWater;
        private bool m_RangeKnown;

        /// <summary>Scratch for the median. Reused so a sweep does not allocate.</summary>
        private float[] m_LandValueScratch;

        private bool m_Enabled;

        /// <summary>Set false to leave the city alone - the harness hotkeys use this so manual
        /// experiments are not immediately overwritten by the simulation.</summary>
        public new bool Enabled
        {
            get { return m_Enabled; }
            set
            {
                if (value && !m_Enabled)
                {
                    m_FullSweepPending = true;
                }

                m_Enabled = value;
            }
        }

        /// <summary>Set when the system is switched on, so a bounded catch-up begins on the next
        /// update instead of waiting for the ordinary round-robin to revisit the whole city.</summary>
        private bool m_FullSweepPending;
        private int m_CatchUpRemaining;
        private int m_CatchUpProcessed;
        private int m_CatchUpBatches;
        private double m_CatchUpWorkMilliseconds;
        private double m_CatchUpMaxBatchMilliseconds;
        private int m_ResetCooldownUpdates;
        private bool m_LegacyCleanupPending;

        /// <summary>
        /// Buildings whose weathering the player has set by hand, which the simulation leaves
        /// alone until released.
        ///
        /// A development aid, and one with a real purpose: the only way to judge whether the five
        /// states read differently is to put them side by side, and a settled city never offers
        /// that - it was measured with 1273 buildings Maintained, 863 Aged and 5 Worn. Waiting for
        /// a city to produce a Decayed building on its own is not a test, it is a hope.
        ///
        /// Belongs behind an advanced option, off by default, when the settings page exists.
        /// </summary>
        private readonly HashSet<Entity> m_Pinned = new HashSet<Entity>();

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();

            m_CitySystem = World.GetOrCreateSystemManaged<Game.Simulation.CitySystem>();
            m_SaveGameSystem = World.GetOrCreateSystemManaged<Game.Serialization.SaveGameSystem>();
            m_Renderer = new MeshColorOverlayRenderer(EntityManager, Mod.Log);

            m_GrowableQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<BuildingCondition>(),
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
        /// Reports the state of the city once, as soon as it has finished loading.
        ///
        /// This exists to answer one question without anyone having to remember to ask it: does a
        /// city saved while weathered come back clean? `WeatheringSaveGuardSystem` strips the mod
        /// out at `SystemUpdatePhase.Serialize`, but whether that phase runs before the entity
        /// data is written is a claim about the engine, and the proof of it is the line below
        /// reading "0 already recoloured" on a city that was saved dirty.
        ///
        /// It is also simply the right place for it: a diagnostic that has to be triggered by hand
        /// is one that gets run when someone remembers, which is not the same as when it matters.
        /// </summary>
        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            if (!mode.IsGame())
            {
                return;
            }

            // The land value range has to be measured against the city that just loaded, not the
            // one that was here before it.
            m_RangeKnown = false;
            m_Pinned.Clear();
            m_CatchUpRemaining = 0;
            m_ResetCooldownUpdates = 0;

            // On by default from here. The hotkey came first because both layers were experiments
            // and being able to switch them off was how they were compared; now that they work, a
            // mod that does nothing until someone presses a chord is a test harness, not a mod.
            // The toggle stays for A/B comparison, and belongs in the options page alongside the
            // tooltip switch when that exists.
            Enabled = true;
            // A save made by an older build can contain both WeatheringState and the vanilla
            // CustomMeshColor it produced. The in-memory ownership record does not survive a
            // reload, so applying again would otherwise treat the already dark colour as a new
            // clean baseline and compound it. Repair those marked leftovers before the first
            // sweep. Current saves contain neither because WeatheringSaveGuardSystem strips them.
            m_LegacyCleanupPending = true;
            m_FullSweepPending = false;
            m_Pinned.Clear();

            Mod.Log.Info("Seen Better Days: on load, " + Census());
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            // Weathering moves in weeks, not frames. Anything faster is wasted work the player
            // could never see.
            return 64;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (SaveMutationGate.IsBlocked(m_SaveGameSystem))
            {
                return;
            }

            if (Mod.ConsumeCityAppearanceResetRequest())
            {
                ResetAndRebuildCityAppearance();
                return;
            }

            if (m_LegacyCleanupPending)
            {
                m_LegacyCleanupPending = false;
                int repaired = ClearPersistedWeatheringOnly();
                if (repaired > 0)
                {
                    m_ResetCooldownUpdates = 2;
                    Mod.Log.Warn("Seen Better Days: repaired " + repaired
                               + " persisted weathering override(s) from an older or interrupted "
                               + "session before applying this version. The old colour was not "
                               + "used as the new baseline.");
                }

                m_FullSweepPending = true;
                return;
            }

            // Removing CustomMeshColor asks the game's rendering systems to restore the prefab
            // palette. Give them two weathering updates to do that before capturing a new pristine
            // baseline; applying again in the same pass is exactly how darkening compounds.
            if (m_ResetCooldownUpdates > 0)
            {
                m_ResetCooldownUpdates--;
                if (m_ResetCooldownUpdates == 0)
                {
                    m_FullSweepPending = true;
                }

                return;
            }

            ClearConstructionVisuals();

            // Read live rather than latched at load, so unticking the option takes the weathering
            // off the city while the player is looking at it. A setting that needs a reload to be
            // believed is a setting people do not trust.
            bool wanted = Enabled
                       && (Mod.Settings == null || Mod.Settings.EnableWeathering);

            if (!wanted)
            {
                if (m_Renderer.TrackedBuildingCount > 0)
                {
                    ResetAll();
                }

                return;
            }

            NativeArray<Entity> buildings = m_GrowableQuery.ToEntityArray(Allocator.TempJob);
            try
            {
                if (buildings.Length == 0)
                {
                    return;
                }

                if (!m_RangeKnown)
                {
                    MeasureLandValueRange(buildings);
                }

                if (m_FullSweepPending)
                {
                    m_FullSweepPending = false;
                    m_CatchUpRemaining = buildings.Length;
                    m_CatchUpProcessed = 0;
                    m_CatchUpBatches = 0;
                    m_CatchUpWorkMilliseconds = 0d;
                    m_CatchUpMaxBatchMilliseconds = 0d;

                    Mod.Log.Info("Seen Better Days: starting bounded colour catch-up for "
                               + buildings.Length + " growable building(s), "
                               + CatchUpBuildingsPerUpdate + " per update.");
                }

                bool catchingUp = m_CatchUpRemaining > 0;
                int count = catchingUp
                    ? math.min(CatchUpBuildingsPerUpdate, math.min(m_CatchUpRemaining, buildings.Length))
                    : math.min(BuildingsPerUpdate, buildings.Length);

                long batchStarted = Stopwatch.GetTimestamp();
                for (int i = 0; i < count; i++)
                {
                    int previous = m_Cursor;
                    m_Cursor = (m_Cursor + 1) % buildings.Length;

                    // One sweep of the city per measurement: land values drift as the city grows,
                    // and a range measured once at load would slowly stop describing it.
                    if (m_Cursor < previous)
                    {
                        MeasureLandValueRange(buildings);
                    }

                    Process(buildings[m_Cursor]);
                }

                if (catchingUp)
                {
                    double elapsedMilliseconds =
                        (Stopwatch.GetTimestamp() - batchStarted) * 1000d / Stopwatch.Frequency;

                    m_CatchUpRemaining -= count;
                    m_CatchUpProcessed += count;
                    m_CatchUpBatches++;
                    m_CatchUpWorkMilliseconds += elapsedMilliseconds;
                    m_CatchUpMaxBatchMilliseconds =
                        math.max(m_CatchUpMaxBatchMilliseconds, elapsedMilliseconds);

                    if (m_CatchUpRemaining <= 0)
                    {
                        Mod.Log.Info("Seen Better Days: colour catch-up completed: "
                                   + m_CatchUpProcessed + " building(s) in "
                                   + m_CatchUpBatches + " batch(es), "
                                   + m_CatchUpWorkMilliseconds.ToString("0.0")
                                   + " ms total system work, slowest batch "
                                   + m_CatchUpMaxBatchMilliseconds.ToString("0.0") + " ms.");
                    }
                }
            }
            finally
            {
                buildings.Dispose();
            }
        }

        private void Process(Entity building)
        {
            if (BuildingClassifier.IsUnderConstruction(EntityManager, building))
            {
                return;
            }

            // Hand-set buildings are the player's statement about how they should look, and the
            // simulation has nothing to add to it.
            if (m_Pinned.Contains(building))
            {
                return;
            }

            string reason;
            Entity prefab;
            int level;
            BuildingCategory category = BuildingClassifier.Classify(
                EntityManager, building, out prefab, out level, out reason);

            if (!category.IsEligible())
            {
                return;
            }

            if (!EntityManager.HasComponent<SpawnableBuildingData>(prefab)
                || !EntityManager.HasComponent<BuildingPropertyData>(prefab))
            {
                return;
            }

            SpawnableBuildingData spawnable = EntityManager.GetComponentData<SpawnableBuildingData>(prefab);
            BuildingPropertyData propertyData = EntityManager.GetComponentData<BuildingPropertyData>(prefab);
            AreaType areaType = EntityManager.GetComponentData<ZoneData>(spawnable.m_ZonePrefab).m_AreaType;

            // Normalising by the game's own abandon threshold is what makes a small block and a
            // tower comparable: both express distress as a fraction of the debt at which the game
            // itself gives up on them.
            // Fetched here, immediately before use, and never held across the call to the
            // renderer. A DynamicBuffer handle does not survive a structural change anywhere in
            // the world, and weathering a building performs several - so a handle taken once in
            // OnUpdate and carried through a thousand buildings is a thousand reads of memory the
            // first building freed.
            DynamicBuffer<CityModifier> cityEffects =
                EntityManager.HasBuffer<CityModifier>(m_CitySystem.City)
                    ? EntityManager.GetBuffer<CityModifier>(m_CitySystem.City, true)
                    : default;

            int levelingCost = BuildingUtils.GetLevelingCost(areaType, propertyData, spawnable.m_Level, cityEffects);
            int abandonCost = BuildingUtils.GetAbandonCost(areaType, propertyData, spawnable.m_Level, levelingCost, cityEffects);

            int condition = EntityManager.GetComponentData<BuildingCondition>(building).m_Condition;
            bool abandoned = EntityManager.HasComponent<Abandoned>(building);

            float efficiency = EfficiencyOf(building);

            float target = WeatheringTarget.Compute(
                condition, abandonCost, spawnable.m_Level, efficiency, abandoned,
                PovertyOf(building), WealthOf(building), SeedFor(building));

            WeatheringState state = EntityManager.HasComponent<WeatheringState>(building)
                ? EntityManager.GetComponentData<WeatheringState>(building)
                : new WeatheringState { m_Weathering = 0f, m_Seed = SeedFor(building) };

            bool firstSight = !EntityManager.HasComponent<WeatheringState>(building);

            // A building we have never seen goes straight to what its circumstances deserve. The
            // ramp exists to animate a *change* in circumstances - a street losing its tenants,
            // a district recovering - and a building that has always stood on a poor street has
            // not just changed: it was already like that before this mod was ever switched on.
            // Ramping from clean on first sight also made the effect nearly impossible to see,
            // because a city takes minutes to creep up to its own steady state.
            float next;
            if (firstSight)
            {
                next = target;
            }
            else
            {
                float step = WeatheringTarget.Step(state.m_Weathering, target, abandoned);
                next = math.clamp(
                    state.m_Weathering + math.clamp(target - state.m_Weathering, -step, step), 0f, 1f);
            }

            // Whether the colours are actually on the building, not whether we remember a value
            // for it. Turning the system off restores colours but leaves the state behind, so
            // asking the state alone means a re-enable finds nothing to do and the city stays
            // clean - which is exactly what happened the first time this ran.
            bool colourMissing = next > 0.005f && !m_Renderer.Has(building);

            bool worthRedrawing = colourMissing
                               || firstSight
                               || math.abs(next - state.m_Weathering) >= RedrawThreshold;

            state.m_Weathering = next;

            if (firstSight)
            {
                EntityManager.AddComponentData(building, state);
            }
            else
            {
                EntityManager.SetComponentData(building, state);
            }

            if (!worthRedrawing)
            {
                return;
            }

            if (next <= 0.005f)
            {
                m_Renderer.Remove(building);
                return;
            }

            BuildingVisualProfile profile = BuildingVisualProfile.FromWeathering(category, next, state.m_Seed);

            int placed;
            string failure;
            m_Renderer.Apply(building, profile, out placed, out failure);
        }

        /// <summary>
        /// Immediately removes both the visible colour and the remembered weathering state from
        /// buildings that have entered construction. The separate query matters for upgrades: a
        /// building may already have been weathered before UnderConstruction was added to it.
        /// Dropping the state makes the completed building a fresh observation rather than a
        /// continuation of its pre-construction appearance.
        /// </summary>
        private void ClearConstructionVisuals()
        {
            NativeArray<Entity> buildings = m_UnderConstructionQuery.ToEntityArray(Allocator.TempJob);
            try
            {
                for (int i = 0; i < buildings.Length; i++)
                {
                    Entity building = buildings[i];
                    m_Pinned.Remove(building);

                    if (m_Renderer.Has(building)
                        || EntityManager.HasBuffer<PristineMeshColor>(building))
                    {
                        m_Renderer.Forget(building);
                    }

                    if (EntityManager.HasComponent<WeatheringState>(building))
                    {
                        EntityManager.RemoveComponent<WeatheringState>(building);
                    }
                }
            }
            finally
            {
                buildings.Dispose();
            }
        }

        /// <summary>
        /// The building's overall efficiency, as the product of its factors.
        ///
        /// Computed here rather than through <c>BuildingUtils.GetEfficiency</c>, whose method group
        /// includes a <c>Span</c> overload that a net48 mod project cannot resolve - the same trap
        /// that makes <c>EntityManager.CreateEntity</c> unreachable. The factors are independent
        /// multipliers (no power, no water, bins uncollected), so multiplying them is the meaning,
        /// not an approximation.
        /// </summary>
        private float EfficiencyOf(Entity building)
        {
            if (!EntityManager.HasBuffer<Efficiency>(building))
            {
                return 1f;
            }

            DynamicBuffer<Efficiency> factors = EntityManager.GetBuffer<Efficiency>(building, true);
            float efficiency = 1f;

            for (int i = 0; i < factors.Length; i++)
            {
                efficiency *= math.max(0f, factors[i].m_Efficiency);
            }

            return math.saturate(efficiency);
        }

        /// <summary>
        /// Reports what the city's circumstances actually are, rather than what they were assumed
        /// to be: how many growables are in distress at all, and how the computed targets spread.
        ///
        /// Worth having before touching a single constant. If almost every building returns a
        /// target near zero then the weathering is not too weak - the *signal* has no spread, and
        /// no amount of tuning the colour response will fix that.
        /// </summary>
        public string Census()
        {
            NativeArray<Entity> buildings = m_GrowableQuery.ToEntityArray(Allocator.TempJob);
            try
            {
                DynamicBuffer<CityModifier> cityEffects =
                    EntityManager.HasBuffer<CityModifier>(m_CitySystem.City)
                        ? EntityManager.GetBuffer<CityModifier>(m_CitySystem.City, true)
                        : default;

                // Before the loop, not after it. Measured afterwards, every target in the census
                // was computed with no wealth term at all and the reported mean described a
                // simulation nobody was running.
                MeasureLandValueRange(buildings);

                int[] bucket = new int[5];
                int eligible = 0;
                int negativeCondition = 0;
                int abandonedCount = 0;
                int poorEfficiency = 0;
                int noColourChannels = 0;
                int submeshesDiffer = 0;
                int alreadyOverridden = 0;
                int untouched = 0;
                int topLevel = 0;
                float targetSum = 0f;
                float worst = 0f;

                for (int i = 0; i < buildings.Length; i++)
                {
                    Entity building = buildings[i];

                    string reason;
                    Entity prefab;
                    int level;
                    BuildingCategory category = BuildingClassifier.Classify(
                        EntityManager, building, out prefab, out level, out reason);

                    if (!category.IsEligible()
                        || !EntityManager.HasComponent<SpawnableBuildingData>(prefab)
                        || !EntityManager.HasComponent<BuildingPropertyData>(prefab))
                    {
                        continue;
                    }

                    SpawnableBuildingData spawnable = EntityManager.GetComponentData<SpawnableBuildingData>(prefab);
                    BuildingPropertyData propertyData = EntityManager.GetComponentData<BuildingPropertyData>(prefab);
                    AreaType areaType = EntityManager.GetComponentData<ZoneData>(spawnable.m_ZonePrefab).m_AreaType;

                    int levelingCost = BuildingUtils.GetLevelingCost(areaType, propertyData, spawnable.m_Level, cityEffects);
                    int abandonCost = BuildingUtils.GetAbandonCost(areaType, propertyData, spawnable.m_Level, levelingCost, cityEffects);
                    int condition = EntityManager.GetComponentData<BuildingCondition>(building).m_Condition;
                    bool abandoned = EntityManager.HasComponent<Abandoned>(building);
                    float efficiency = EfficiencyOf(building);

                    float target = WeatheringTarget.Compute(
                        condition, abandonCost, spawnable.m_Level, efficiency, abandoned,
                PovertyOf(building), WealthOf(building), SeedFor(building));

                    string blocked;
                    if (!m_Renderer.CanWeather(building, out blocked))
                    {
                        if (blocked == "submeshes differ")
                        {
                            submeshesDiffer++;
                        }
                        else if (blocked == "already overridden by something else")
                        {
                            alreadyOverridden++;
                        }
                        else
                        {
                            noColourChannels++;
                        }
                    }

                    eligible++;
                    targetSum += target;
                    worst = math.max(worst, target);
                    if (target <= 0.005f)
                    {
                        untouched++;
                        // Counted among those left alone, not across the whole city. Reported the
                        // other way round it read "left alone 583 of which 738 are level 5",
                        // which cannot be true of any subset and told the reader nothing.
                        if (spawnable.m_Level >= 5)
                        {
                            topLevel++;
                        }
                    }
                    if (condition < 0) negativeCondition++;
                    if (abandoned) abandonedCount++;
                    if (efficiency < 0.95f) poorEfficiency++;

                    bucket[math.clamp((int)BuildingVisualProfile.StateFor(target), 0, 4)]++;
                }

                if (eligible == 0)
                {
                    return "census: no eligible growables.";
                }

                return string.Format(
                    "census of {0} growables: mean target {1:0.000}, worst {2:0.000}"
                  + " | condition below zero: {3} ({4:0.0}%), abandoned: {5}, efficiency under 95%: {6}"
                  + " | Maintained {7} / Aged {8} / Worn {9} / Neglected {10} / Decayed {11}"
                  + " | left alone {20} of which {21} are level 5"
                  + " | cannot weather: {12} with no colour channels, {13} whose submeshes differ,"
                  + " {14} already recoloured"
                  + " | land value {15:0} to {16:0}, tenth {18:0}, median {17:0}, ninetieth {19:0}",
                    eligible, targetSum / eligible, worst,
                    negativeCondition, 100f * negativeCondition / eligible, abandonedCount, poorEfficiency,
                    bucket[0], bucket[1], bucket[2], bucket[3], bucket[4],
                    noColourChannels, submeshesDiffer, alreadyOverridden,
                    m_LandValueLow, m_LandValueHigh, m_LandValueMedian, m_LandValueFloor,
                    m_LandValueHighWater, untouched, topLevel);
            }
            finally
            {
                buildings.Dispose();
            }
        }

        /// <summary>
        /// How well kept this building is, in words, or false if it is not one this mod speaks for.
        ///
        /// Recomputed from the same inputs as the simulation rather than read from a cache, so
        /// what the tooltip says and what the colour was made from cannot drift apart. A
        /// diagnostic that can disagree with the thing it describes is worse than none.
        /// </summary>
        public bool TryDescribeMaintenance(Entity building, out string text)
        {
            text = null;

            if (!EntityManager.Exists(building) || !EntityManager.HasComponent<PrefabRef>(building))
            {
                return false;
            }

            string reason;
            Entity prefab;
            int level;
            BuildingCategory category = BuildingClassifier.Classify(
                EntityManager, building, out prefab, out level, out reason);

            if (!category.IsEligible()
                || !EntityManager.HasComponent<SpawnableBuildingData>(prefab)
                || !EntityManager.HasComponent<BuildingPropertyData>(prefab))
            {
                return false;
            }

            SpawnableBuildingData spawnable = EntityManager.GetComponentData<SpawnableBuildingData>(prefab);
            BuildingPropertyData propertyData = EntityManager.GetComponentData<BuildingPropertyData>(prefab);
            AreaType areaType = EntityManager.GetComponentData<ZoneData>(spawnable.m_ZonePrefab).m_AreaType;

            DynamicBuffer<CityModifier> cityEffects =
                EntityManager.HasBuffer<CityModifier>(m_CitySystem.City)
                    ? EntityManager.GetBuffer<CityModifier>(m_CitySystem.City, true)
                    : default;

            int levelingCost = BuildingUtils.GetLevelingCost(areaType, propertyData, spawnable.m_Level, cityEffects);
            int abandonCost = BuildingUtils.GetAbandonCost(areaType, propertyData, spawnable.m_Level, levelingCost, cityEffects);

            int condition = EntityManager.GetComponentData<BuildingCondition>(building).m_Condition;
            bool abandoned = EntityManager.HasComponent<Abandoned>(building);
            float efficiency = EfficiencyOf(building);
            float poverty = PovertyOf(building);
            float wealth = WealthOf(building);

            float target = WeatheringTarget.Compute(
                condition, abandonCost, spawnable.m_Level, efficiency, abandoned,
                poverty, wealth, SeedFor(building));

            // What is on the building right now, which during a slow recovery is not the target.
            float shown = EntityManager.HasComponent<WeatheringState>(building)
                ? EntityManager.GetComponentData<WeatheringState>(building).m_Weathering
                : 0f;

            string state = BuildingVisualProfile.StateFor(shown).ToString();

            string street;
            float landValue = LandValueOf(building);
            if (!m_RangeKnown || landValue < 0f)
            {
                street = "street value unknown";
            }
            else if (wealth > 0.05f)
            {
                street = string.Format("well-off street ({0:0} against a median of {1:0})",
                                       landValue, m_LandValueMedian);
            }
            else if (poverty > 0.05f)
            {
                street = string.Format("below-median street ({0:0} against a median of {1:0})",
                                       landValue, m_LandValueMedian);
            }
            else
            {
                street = string.Format("ordinary street ({0:0} against a median of {1:0})",
                                       landValue, m_LandValueMedian);
            }

            string trouble = "";
            if (abandoned)
            {
                trouble = ", abandoned";
            }
            else if (condition < 0)
            {
                trouble = string.Format(", upkeep short by {0} of {1}", -condition, abandonCost);
            }
            else if (efficiency < 0.95f)
            {
                trouble = string.Format(", running at {0:0}% efficiency", efficiency * 100f);
            }

            bool pinned = m_Pinned.Contains(building);
            string hold = pinned ? " (held for testing)" : "";
            string trajectory = "";
            if (pinned)
            {
                trajectory = string.Format("\nnatural state: {0} - {1:0}%",
                    BuildingVisualProfile.StateFor(target), target * 100f);
            }
            else if (math.abs(target - shown) >= 0.02f)
            {
                string direction = target > shown ? "weathering" : "recovering";
                trajectory = string.Format("\n{0} toward {1} - {2:0}%", direction,
                    BuildingVisualProfile.StateFor(target), target * 100f);
            }

            text = string.Format("{0} - {1:0}% weathered{2}\nlevel {3}, {4}{5}{6}",
                                 state, shown * 100f, hold, spawnable.m_Level,
                                 street, trouble, trajectory);
            return true;
        }

        /// <summary>
        /// A building's fixed seed, derived from its entity index.
        ///
        /// Derived rather than stored so that the census can ask for it on a building that has
        /// never been weathered and get the same answer the simulation will - a diagnostic that
        /// reports something other than what will happen is worse than none.
        /// </summary>
        private static uint SeedFor(Entity building)
        {
            return (uint)building.Index * 2654435761u + 1u;
        }

        /// <summary>
        /// The land value of the street this building fronts, or -1 where there is none to read.
        ///
        /// `Game.Net.LandValue` lives on the road edge rather than on the building, which is also
        /// the right meaning: a building is worth what its street is worth, and two identical
        /// blocks on different streets should not look identical.
        /// </summary>
        private float LandValueOf(Entity building)
        {
            if (!EntityManager.HasComponent<Building>(building))
            {
                return -1f;
            }

            Entity road = EntityManager.GetComponentData<Building>(building).m_RoadEdge;
            if (road == Entity.Null || !EntityManager.HasComponent<Game.Net.LandValue>(road))
            {
                return -1f;
            }

            return EntityManager.GetComponentData<Game.Net.LandValue>(road).m_LandValue;
        }

        /// <summary>
        /// Where this building sits in the city's own range of land values: 0 on the best street
        /// in town, 1 on the worst.
        ///
        /// Normalised against the city rather than against a constant on purpose. There is no
        /// absolute number that means "poor" across every map, difficulty and mod set, and the
        /// judgement a player makes is always comparative - this corner looks worse than that one.
        /// A city with no spread in land value gets no weathering from this term at all, which is
        /// the honest answer rather than an invented one.
        /// </summary>
        private float PovertyOf(Entity building)
        {
            if (!m_RangeKnown || m_LandValueFloor <= 1f || m_LandValueMedian <= m_LandValueFloor)
            {
                return 0f;
            }

            float value = LandValueOf(building);
            if (value < 0f)
            {
                return 0f;
            }

            // Two robust statistics, and a logarithm between them.
            //
            // The median is the neutral point: a building on an ordinary street is not weathered,
            // because ordinary is what the city is made of. The tenth percentile is the bottom of
            // the scale. Everything above the median reads 0, which is half the city clean by
            // construction - exactly the distinction asked for, rather than a city uniformly
            // dimmed.
            //
            // The interpolation is logarithmic because land value is not distributed evenly. In a
            // real city it piles up at the bottom with a long tail of expensive streets - measured
            // here as 53 at the minimum, 78 at the median and 600 at the top. A linear map between
            // median and floor spends most of its range on differences too small to mean anything;
            // a ratio-based one treats "half as valuable as ordinary" the same way wherever in the
            // city it happens.
            float span = math.log(m_LandValueMedian) - math.log(m_LandValueFloor);

            // How much genuine inequality there is *below* the median - the only part of the
            // distribution this term reads. A city whose ordinary street is barely better off than
            // its poorest gets almost nothing from wealth, however wide its luxury tail. That is
            // the honest answer: no spread to read, no weathering invented from it.
            float inequality = math.saturate(span / math.log(1.5f));

            float clamped = math.clamp(value, m_LandValueFloor, m_LandValueMedian);
            float position = (math.log(m_LandValueMedian) - math.log(clamped)) / span;

            return math.saturate(position * inequality);
        }

        /// <summary>
        /// How well off this building's street is, against the rest of the city: 0 on a median
        /// street or below, 1 at the ninetieth percentile and above.
        ///
        /// This is the term that carries the effect. Land value in a real city is flat below the
        /// median and spread out above it, so this is the half of the distribution with something
        /// to say - see <see cref="PovertyOf"/> for the half that has not.
        /// </summary>
        private float WealthOf(Entity building)
        {
            if (!m_RangeKnown || m_LandValueMedian <= 1f || m_LandValueHighWater <= m_LandValueMedian)
            {
                return 0f;
            }

            float value = LandValueOf(building);
            if (value < 0f)
            {
                return 0f;
            }

            // Logarithmic for the same reason as below the median: land value is a ratio, and
            // twice as valuable should mean the same thing at either end of the city.
            float span = math.log(m_LandValueHighWater) - math.log(m_LandValueMedian);
            float clamped = math.clamp(value, m_LandValueMedian, m_LandValueHighWater);

            return math.saturate((math.log(clamped) - math.log(m_LandValueMedian)) / span);
        }

        private void MeasureLandValueRange(NativeArray<Entity> buildings)
        {
            if (m_LandValueScratch == null || m_LandValueScratch.Length < buildings.Length)
            {
                m_LandValueScratch = new float[math.max(64, buildings.Length)];
            }

            int found = 0;
            for (int i = 0; i < buildings.Length; i++)
            {
                float value = LandValueOf(buildings[i]);
                if (value >= 0f)
                {
                    m_LandValueScratch[found++] = value;
                }
            }

            if (found == 0)
            {
                m_RangeKnown = false;
                return;
            }

            System.Array.Sort(m_LandValueScratch, 0, found);

            m_LandValueLow = m_LandValueScratch[0];
            m_LandValueHigh = m_LandValueScratch[found - 1];
            m_LandValueMedian = m_LandValueScratch[found / 2];

            // The tenth percentile, not the minimum. One derelict corner should not define what
            // "poor" means for the other seventeen hundred buildings.
            m_LandValueFloor = m_LandValueScratch[found / 10];

            // The ninetieth percentile, for the same reason as the tenth: one exceptional street
            // should not be what every other building is measured against.
            m_LandValueHighWater = m_LandValueScratch[(found * 9) / 10];
            m_RangeKnown = true;
        }

        /// <summary>
        /// Sets a building's weathering by hand and holds it there.
        /// </summary>
        public bool Pin(Entity building, float weathering, out string description)
        {
            description = null;

            string reason;
            Entity prefab;
            int level;
            BuildingCategory category = BuildingClassifier.Classify(
                EntityManager, building, out prefab, out level, out reason);

            if (!category.IsEligible())
            {
                description = "not a growable this mod speaks for: " + reason;
                return false;
            }

            WeatheringState state = EntityManager.HasComponent<WeatheringState>(building)
                ? EntityManager.GetComponentData<WeatheringState>(building)
                : new WeatheringState { m_Seed = SeedFor(building) };

            state.m_Weathering = math.saturate(weathering);

            if (EntityManager.HasComponent<WeatheringState>(building))
            {
                EntityManager.SetComponentData(building, state);
            }
            else
            {
                EntityManager.AddComponentData(building, state);
            }

            m_Pinned.Add(building);

            if (state.m_Weathering <= 0.005f)
            {
                m_Renderer.Remove(building);
                description = BuildingVisualProfile.StateFor(state.m_Weathering) + " (0%)";
                return true;
            }

            BuildingVisualProfile profile =
                BuildingVisualProfile.FromWeathering(category, state.m_Weathering, state.m_Seed);

            int placed;
            string failure;
            if (!m_Renderer.Apply(building, profile, out placed, out failure))
            {
                description = "held at " + (state.m_Weathering * 100f).ToString("0")
                            + "% but the colour could not be applied - " + failure;
                return true;
            }

            description = BuildingVisualProfile.StateFor(state.m_Weathering)
                        + " (" + (state.m_Weathering * 100f).ToString("0") + "%)";
            return true;
        }

        /// <summary>Hands a building back to the simulation.</summary>
        public bool Unpin(Entity building)
        {
            if (!m_Pinned.Remove(building))
            {
                return false;
            }

            // Dropping the remembered value makes the next pass a first sight, which snaps it
            // straight to whatever its circumstances deserve instead of crawling back.
            if (EntityManager.HasComponent<WeatheringState>(building))
            {
                EntityManager.RemoveComponent<WeatheringState>(building);
            }

            m_FullSweepPending = true;
            return true;
        }

        public int PinnedCount
        {
            get { return m_Pinned.Count; }
        }

        /// <summary>A plain account of one building's colour state, for diagnosis in game.</summary>
        public string Describe(Entity building)
        {
            return m_Renderer.Describe(building);
        }

        /// <summary>
        /// Takes every trace of this mod off the city's entities, and arranges to put the look
        /// back on the next update.
        ///
        /// Called immediately before the game writes a save. `CustomMeshColor` is a vanilla
        /// component and **is** saved, so a city saved while weathered came back with the
        /// weathering baked in and no record of what was underneath - 1069 buildings out of 1801
        /// in one test city, permanently stuck and refused by the mod thereafter.
        ///
        /// Stripping rather than storing is the right answer here, not just the cautious one.
        /// The weathering is a pure function of circumstances the game already saves - condition,
        /// land value, level, efficiency - so there is nothing to preserve: a loaded city
        /// recomputes an identical result on first sight. It also means a save made with this mod
        /// installed stays loadable by someone who does not have it.
        /// </summary>
        public int SuspendForSave()
        {
            List<Entity> tracked = new List<Entity>();
            m_Renderer.CollectTrackedBuildings(tracked);

            for (int i = 0; i < tracked.Count; i++)
            {
                Entity building = tracked[i];
                m_Renderer.Forget(building);

                if (EntityManager.HasComponent<WeatheringState>(building))
                {
                    EntityManager.RemoveComponent<WeatheringState>(building);
                }
            }

            // Everything comes back in one pass, and every building is a first sight again - which
            // lands on exactly the value it had, because the target never depended on history.
            m_FullSweepPending = true;
            return tracked.Count;
        }

        /// <summary>Recomputes the colours of every building this system is weathering, without
        /// changing any of their weathering values.</summary>
        public int Reapply()
        {
            return m_Renderer.Reapply();
        }

        /// <summary>
        /// Strips the custom colour from every growable in the city, whoever put it there.
        ///
        /// Needed because `CustomMeshColor` is a vanilla component and is **saved with the city**,
        /// while the snapshot this mod takes of the original colours is not. A city saved while
        /// weathered therefore reloads with the weathering baked in and no way to read what was
        /// underneath - which is how 1069 of 1801 buildings came back from a previous session
        /// already recoloured and could no longer be touched.
        ///
        /// Blunt on purpose, and the bluntness is the risk: this cannot tell our leftovers from a
        /// colour the player chose with Recolor, so it removes both. It is a repair tool, not part
        /// of normal running.
        /// </summary>
        public int PurgeAllCustomColours()
        {
            int tracked = m_Renderer.RemoveAll();
            NativeArray<Entity> buildings = m_GrowableQuery.ToEntityArray(Allocator.TempJob);
            try
            {
                int cleared = 0;

                for (int i = 0; i < buildings.Length; i++)
                {
                    Entity building = buildings[i];

                    bool changed = false;

                    if (EntityManager.HasBuffer<Game.Rendering.CustomMeshColor>(building))
                    {
                        EntityManager.RemoveComponent<Game.Rendering.CustomMeshColor>(building);
                        changed = true;
                    }

                    if (EntityManager.HasBuffer<PristineMeshColor>(building))
                    {
                        EntityManager.RemoveComponent<PristineMeshColor>(building);
                        changed = true;
                    }

                    if (EntityManager.HasComponent<WeatheringState>(building))
                    {
                        EntityManager.RemoveComponent<WeatheringState>(building);
                        changed = true;
                    }

                    if (changed && !EntityManager.HasComponent<BatchesUpdated>(building))
                    {
                        EntityManager.AddComponent<BatchesUpdated>(building);
                    }

                    if (changed)
                    {
                        cleared++;
                    }
                }

                m_Pinned.Clear();
                return math.max(cleared, tracked);
            }
            finally
            {
                buildings.Dispose();
            }
        }

        /// <summary>Puts every building this system has touched back to its original colours.</summary>
        public int ResetAll()
        {
            return m_Renderer.RemoveAll();
        }

        /// <summary>
        /// Clears only overrides that still carry this mod's serialised WeatheringState marker.
        /// This runs once after loading and is the migration path from builds that allowed runtime
        /// colours to enter a save. User recolours on unrelated buildings remain untouched.
        /// </summary>
        private int ClearPersistedWeatheringOnly()
        {
            NativeArray<Entity> buildings = m_GrowableQuery.ToEntityArray(Allocator.TempJob);
            try
            {
                int cleared = 0;

                for (int i = 0; i < buildings.Length; i++)
                {
                    Entity building = buildings[i];
                    if (!EntityManager.HasComponent<WeatheringState>(building))
                    {
                        continue;
                    }

                    if (EntityManager.HasBuffer<Game.Rendering.CustomMeshColor>(building))
                    {
                        EntityManager.RemoveComponent<Game.Rendering.CustomMeshColor>(building);
                    }

                    if (EntityManager.HasBuffer<PristineMeshColor>(building))
                    {
                        EntityManager.RemoveComponent<PristineMeshColor>(building);
                    }

                    EntityManager.RemoveComponent<WeatheringState>(building);

                    if (!EntityManager.HasComponent<BatchesUpdated>(building))
                    {
                        EntityManager.AddComponent<BatchesUpdated>(building);
                    }

                    cleared++;
                }

                m_Pinned.Clear();
                return cleared;
            }
            finally
            {
                buildings.Dispose();
            }
        }

        /// <summary>Performs the explicit options-page rebuild, then schedules a clean bounded
        /// pass after the renderer has restored the palettes we actually owned.</summary>
        private void ResetAndRebuildCityAppearance()
        {
            BuildingOverlayTestSystem overlays =
                World.GetExistingSystemManaged<BuildingOverlayTestSystem>();
            int decals = overlays == null ? 0 : overlays.SuspendDecalsForSave();
            int buildings = ResetAll();

            m_RangeKnown = false;
            m_CatchUpRemaining = 0;
            m_CatchUpProcessed = 0;
            m_CatchUpBatches = 0;
            m_CatchUpWorkMilliseconds = 0d;
            m_CatchUpMaxBatchMilliseconds = 0d;
            m_FullSweepPending = false;
            m_ResetCooldownUpdates = 2;

            Mod.Log.Info("Seen Better Days: rebuild requested - removed owned colours from "
                       + buildings + " growable(s) and removed " + decals
                       + " decal entit(ies). External custom colours were preserved; waiting for "
                       + "the palettes, then applying the current rules once.");
        }

        [Preserve]
        public BuildingWeatheringSystem()
        {
        }
    }
}

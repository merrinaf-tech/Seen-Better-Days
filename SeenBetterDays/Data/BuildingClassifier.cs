using Game.Buildings;
using Game.Objects;
using Game.Prefabs;
using Game.Zones;
using Unity.Entities;

namespace SeenBetterDays.Data
{
    /// <summary>
    /// Decides whether a building entity is one Seen Better Days may weather, and which of the four
    /// growable categories it belongs to.
    ///
    /// The test is structural rather than name-based, which is what lets it work on custom
    /// growables from asset packs and community assets without knowing anything about them:
    /// a growable is exactly a building whose prefab carries <see cref="SpawnableBuildingData"/>
    /// and therefore points at a zone prefab, and its category is that zone's own area type.
    /// Services, signature buildings, unique buildings and everything plopped rather than grown
    /// simply do not have that component, or carry one of the explicit exclusions below.
    /// </summary>
    public static class BuildingClassifier
    {
        public static BuildingCategory Classify(
            EntityManager entityManager,
            Entity building,
            out Entity prefabEntity,
            out int level,
            out string reason)
        {
            prefabEntity = Entity.Null;
            level = 0;
            reason = null;

            if (building == Entity.Null || !entityManager.Exists(building))
            {
                reason = "entity does not exist";
                return BuildingCategory.NotABuilding;
            }

            if (!entityManager.HasComponent<Building>(building))
            {
                reason = "entity is not a building";
                return BuildingCategory.NotABuilding;
            }

            if (IsUnderConstruction(entityManager, building))
            {
                reason = "building is under construction";
                return BuildingCategory.Excluded;
            }

            if (!entityManager.HasComponent<PrefabRef>(building))
            {
                reason = "building has no PrefabRef";
                return BuildingCategory.NotABuilding;
            }

            prefabEntity = entityManager.GetComponentData<PrefabRef>(building).m_Prefab;

            if (!entityManager.HasComponent<SpawnableBuildingData>(prefabEntity))
            {
                reason = "not a growable (prefab has no SpawnableBuildingData)";
                return BuildingCategory.Excluded;
            }

            if (entityManager.HasComponent<SignatureBuildingData>(prefabEntity))
            {
                reason = "signature building";
                return BuildingCategory.Excluded;
            }

            if (entityManager.HasComponent<UniqueObjectData>(prefabEntity))
            {
                reason = "unique building";
                return BuildingCategory.Excluded;
            }

            if (entityManager.HasComponent<ServiceObjectData>(prefabEntity))
            {
                reason = "service building";
                return BuildingCategory.Excluded;
            }

            SpawnableBuildingData spawnable = entityManager.GetComponentData<SpawnableBuildingData>(prefabEntity);
            level = spawnable.m_Level;

            if (spawnable.m_ZonePrefab == Entity.Null
                || !entityManager.HasComponent<ZoneData>(spawnable.m_ZonePrefab))
            {
                reason = "growable whose zone prefab could not be resolved";
                return BuildingCategory.Excluded;
            }

            ZoneData zone = entityManager.GetComponentData<ZoneData>(spawnable.m_ZonePrefab);

            switch (zone.m_AreaType)
            {
                case AreaType.Residential:
                    return BuildingCategory.Residential;
                case AreaType.Commercial:
                    return BuildingCategory.Commercial;
                case AreaType.Industrial:
                    // Offices are industrial zones flagged as office - the game's own test.
                    return zone.IsOffice() ? BuildingCategory.Office : BuildingCategory.Industrial;
                default:
                    reason = "zone has no area type";
                    return BuildingCategory.Excluded;
            }
        }

        /// <summary>
        /// Construction is a state of the building entity, not its prefab. Keeping the check here
        /// gives the simulation, detail layer, tooltip and manual test controls one definition of
        /// a building that Seen Better Days must leave alone.
        /// </summary>
        public static bool IsUnderConstruction(EntityManager entityManager, Entity building)
        {
            return building != Entity.Null
                && entityManager.Exists(building)
                && entityManager.HasComponent<UnderConstruction>(building);
        }
    }
}

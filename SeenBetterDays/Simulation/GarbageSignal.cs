using Game.Buildings;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace SeenBetterDays.Simulation
{
    /// <summary>Garbage actually stored at a building, measured against the game's own warning
    /// and maximum thresholds. Below the warning it does not add visible litter.</summary>
    public static class GarbageSignal
    {
        public static float Severity(EntityManager entities, Entity building, in GarbageParameterData parameters)
        {
            if (!entities.HasComponent<GarbageProducer>(building))
            {
                return 0f;
            }

            int warning = parameters.m_WarningGarbageLimit;
            int maximum = parameters.m_MaxGarbageAccumulation;
            if (maximum <= warning)
            {
                return 0f;
            }

            int amount = entities.GetComponentData<GarbageProducer>(building).m_Garbage;
            return math.saturate((amount - warning) / (float)(maximum - warning));
        }

        /// <summary>
        /// How much litter to show, in stable steps so a small change does not lay the decals out
        /// afresh: from 30% of the most a building can hold, 50% and 75%. It used to start a
        /// little past the game's own warning, around 55%, where its first two or three pieces
        /// went unnoticed; starting earlier lets the amount grow visibly instead. The weathering
        /// itself still starts at the warning (<see cref="Severity"/>).
        /// </summary>
        public static byte Tier(EntityManager entities, Entity building, in GarbageParameterData parameters)
        {
            int maximum = parameters.m_MaxGarbageAccumulation;
            if (maximum <= 0 || !entities.HasComponent<GarbageProducer>(building))
            {
                return 0;
            }

            float share = entities.GetComponentData<GarbageProducer>(building).m_Garbage / (float)maximum;
            if (share < 0.30f) return 0;
            if (share < 0.50f) return 1;
            if (share < 0.75f) return 2;
            return 3;
        }
    }
}

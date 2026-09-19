using Colossal.UI.Binding;
using Game.Buildings;
using Game.Common;
using Game.Tools;
using Game.UI.Tooltip;
using Unity.Entities;
using UnityEngine.Scripting;
using SeenBetterDays.Systems;

namespace SeenBetterDays.Systems
{
    /// <summary>
    /// Says, under the cursor, how well kept the game thinks a building is.
    ///
    /// This is a diagnostic before it is a feature. The whole mod is an argument that a colour on a
    /// wall should mean something, and there is no way to judge whether it does without seeing the
    /// number the colour came from at the same time as the colour. Guessing from the look alone is
    /// how three rounds were spent tuning a mapping against the wrong half of a distribution.
    ///
    /// It therefore shows the *reasons* alongside the figure - level, what the street is worth
    /// against the city's median, whether upkeep is short - so a colour that looks wrong can be
    /// traced to the input that made it rather than blamed on the colour transform.
    /// </summary>
    public partial class WeatheringTooltipSystem : TooltipSystemBase
    {
        private ToolRaycastSystem m_RaycastSystem;
        private BuildingWeatheringSystem m_Weathering;
        private StringTooltip m_Tooltip;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();

            m_RaycastSystem = World.GetOrCreateSystemManaged<ToolRaycastSystem>();
            m_Weathering = World.GetOrCreateSystemManaged<BuildingWeatheringSystem>();
            m_Tooltip = new StringTooltip { path = "seenBetterDays.maintenance" };
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (Mod.Settings == null || !Mod.Settings.ShowMaintenanceTooltip)
            {
                return;
            }

            RaycastResult result;
            if (!m_RaycastSystem.GetRaycastResult(out result))
            {
                return;
            }

            Entity building = ResolveBuilding(result.m_Owner);
            if (building == Entity.Null)
            {
                return;
            }

            string text;
            if (!m_Weathering.TryDescribeMaintenance(building, out text))
            {
                return;
            }

            m_Tooltip.value = text;
            AddMouseTooltip(m_Tooltip);
        }

        /// <summary>
        /// Walks up from whatever the cursor actually hit to the building that owns it.
        ///
        /// A raycast lands on the nearest thing, which for a building is often a sub-object - an
        /// awning, a sign, a roof fitting - and those are separate entities. Reporting nothing
        /// because the cursor was over a shop sign would make the tooltip useless exactly where a
        /// player would point it.
        /// </summary>
        private Entity ResolveBuilding(Entity hit)
        {
            for (int guard = 0; guard < 8 && hit != Entity.Null; guard++)
            {
                if (EntityManager.HasComponent<Building>(hit) && !EntityManager.HasComponent<Deleted>(hit))
                {
                    return hit;
                }

                if (!EntityManager.HasComponent<Owner>(hit))
                {
                    return Entity.Null;
                }

                hit = EntityManager.GetComponentData<Owner>(hit).m_Owner;
            }

            return Entity.Null;
        }

        [Preserve]
        public WeatheringTooltipSystem()
        {
        }
    }
}

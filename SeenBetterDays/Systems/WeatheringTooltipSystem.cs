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
        private StringTooltip m_CreditTooltip;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();

            m_RaycastSystem = World.GetOrCreateSystemManaged<ToolRaycastSystem>();
            m_Weathering = World.GetOrCreateSystemManaged<BuildingWeatheringSystem>();
            m_CreditTooltip = new StringTooltip { path = "seenBetterDays.designCredit" };
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (Mod.Settings == null)
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

            // Design authors are credited even when the optional building summary is disabled.
            if (!Mod.Settings.ShowMaintenanceTooltip)
            {
                ShowDesignCredit(building);
                return;
            }

            if (!m_Weathering.TryDescribeMaintenance(building, m_Rows))
            {
                return;
            }

            for (int i = 0; i < m_Rows.Count; i++)
            {
                if (i == m_RowTooltips.Count)
                {
                    m_RowTooltips.Add(new StringTooltip { path = "seenBetterDays.maintenance" + i });
                }

                StringTooltip tooltip = m_RowTooltips[i];
                tooltip.icon = m_Rows[i].Icon;
                tooltip.value = m_Rows[i].Text;
                tooltip.color = m_Rows[i].Color;
                AddMouseTooltip(tooltip);
            }
        }

        private readonly System.Collections.Generic.List<MaintenanceRow> m_Rows =
            new System.Collections.Generic.List<MaintenanceRow>();
        private readonly System.Collections.Generic.List<StringTooltip> m_RowTooltips =
            new System.Collections.Generic.List<StringTooltip>();

        /// <summary>
        /// Walks up from whatever the cursor actually hit to the building that owns it.
        ///
        /// A raycast lands on the nearest thing, which for a building is often a sub-object - an
        /// awning, a sign, a roof fitting - and those are separate entities. Reporting nothing
        /// because the cursor was over a shop sign would make the tooltip useless exactly where a
        /// player would point it.
        /// </summary>
        /// <summary>
        /// Credits the designer of a hand-made design on hover, for every player: the people who
        /// make designs should be seen. Only when the design carries a name.
        /// </summary>
        private void ShowDesignCredit(Entity building)
        {
            string author;
            if (m_Weathering == null || !m_Weathering.TryGetDesignAuthor(building, out author))
            {
                return;
            }

            m_CreditTooltip.icon = TooltipIcons.Author;
            m_CreditTooltip.value = "Author: " + (string.IsNullOrEmpty(author) ? "?" : author);
            AddMouseTooltip(m_CreditTooltip);
        }

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

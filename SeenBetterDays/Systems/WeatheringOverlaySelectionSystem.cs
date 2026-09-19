using Game;
using Game.Common;
using Game.Tools;
using Unity.Entities;
using UnityEngine.Scripting;
using SeenBetterDays.Data;

namespace SeenBetterDays.Systems
{
    /// <summary>
    /// Keeps runtime weathering decals out of the player's object-selection workflow.
    ///
    /// The decal entities must remain ordinary renderable objects, so components such as Hidden,
    /// Marker or Placeholder are not suitable selection guards: they also change how the object
    /// is drawn or processed. Instead, after the tools have resolved their raycast, a selection of
    /// one of our tagged overlays is redirected to the building recorded in WeatheringOverlay.
    /// User-placed decals do not carry that component and are therefore left entirely alone.
    /// </summary>
    public partial class WeatheringOverlaySelectionSystem : GameSystemBase
    {
        private ToolSystem m_ToolSystem;
        private EntityQuery m_OverlayQuery;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();

            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_OverlayQuery = GetEntityQuery(ComponentType.ReadOnly<WeatheringOverlay>());
            RequireForUpdate(m_OverlayQuery);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            Entity selected = m_ToolSystem.selected;
            if (selected == Entity.Null
                || !EntityManager.Exists(selected)
                || !EntityManager.HasComponent<WeatheringOverlay>(selected))
            {
                return;
            }

            Entity building = EntityManager.GetComponentData<WeatheringOverlay>(selected).m_Building;
            m_ToolSystem.selected = building != Entity.Null
                                 && EntityManager.Exists(building)
                                 && !EntityManager.HasComponent<Deleted>(building)
                ? building
                : Entity.Null;
        }

        [Preserve]
        public WeatheringOverlaySelectionSystem()
        {
        }
    }
}

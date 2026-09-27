using Colossal.UI.Binding;
using Game;
using Game.UI;
using Game.UI.InGame;
using Unity.Entities;
using UnityEngine.Scripting;

namespace SeenBetterDays.Systems
{
    /// <summary>
    /// Receives, from SeenBetterDays.mjs, whether the building panel's Customize tab is showing.
    ///
    /// The tab is local state of the game's React panel and never reaches C#, so the UI module
    /// wraps the colour section - which is only mounted on that tab - and reports its mount and
    /// unmount here. Nothing else happens in this system: the colour work belongs in the
    /// simulation's phase, in <see cref="ColourEditSystem"/>.
    /// </summary>
    public partial class ColourTabBindingSystem : UISystemBase
    {
        private const string Group = "seenBetterDays";

        public bool CustomizeTabOpen { get; private set; }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            AddBinding(new TriggerBinding<bool>(Group, "colourTabOpen", open => CustomizeTabOpen = open));
        }
    }

    /// <summary>
    /// While a building is open on the Customize tab, shows it in its clean colour so the game's
    /// colour panel reads and edits the real colour rather than the weathered one; when the tab
    /// closes or another building is selected, weathers it again on top of whatever colour it
    /// has then.
    /// </summary>
    public partial class ColourEditSystem : GameSystemBase
    {
        private ColourTabBindingSystem m_Tab;
        private SelectedInfoUISystem m_SelectedInfo;
        private BuildingWeatheringSystem m_Weathering;
        private Game.Serialization.SaveGameSystem m_SaveGameSystem;

        /// <summary>Frames until the panel is asked to read the building again. The clean colour
        /// reaches MeshColor, which is what the panel shows, a frame after it is written.</summary>
        private int m_PanelRefreshFrames;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Tab = World.GetOrCreateSystemManaged<ColourTabBindingSystem>();
            m_SelectedInfo = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            m_Weathering = World.GetOrCreateSystemManaged<BuildingWeatheringSystem>();
            m_SaveGameSystem = World.GetOrCreateSystemManaged<Game.Serialization.SaveGameSystem>();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (SaveMutationGate.IsBlocked(m_SaveGameSystem))
            {
                return;
            }

            Entity target = m_Tab.CustomizeTabOpen ? m_SelectedInfo.selectedEntity : Entity.Null;
            if (target != m_Weathering.ColourEditBuilding)
            {
                m_Weathering.SetColourEdit(Entity.Null);
                if (target != Entity.Null)
                {
                    m_Weathering.SetColourEdit(target);
                    m_PanelRefreshFrames = 2;
                }
            }

            m_Weathering.TickColourEdit();

            if (m_PanelRefreshFrames > 0 && --m_PanelRefreshFrames == 0)
            {
                m_SelectedInfo.RequestUpdate();
            }
        }
    }
}

using Game;
using UnityEngine.Scripting;
using SeenBetterDays.Rendering;

namespace SeenBetterDays.Systems
{
    /// <summary>
    /// Surveys the walls of newly met building types a little at a time.
    ///
    /// The decal layer used to walk a prefab's triangles the moment it first met that building
    /// type, all in one frame - up to 60 ms for a detailed prefab, a visible hitch whenever the
    /// camera reached a new district. It now queues the type and moves on; this system spends at
    /// most <see cref="BudgetMilliseconds"/> of every frame on the queue. A 60 ms walk becomes
    /// about forty frames of 1.5 ms - well under a second - and the decals appear on the next pass
    /// of the proximity scan once the type is known.
    ///
    /// Read-only: it touches prefab mesh buffers and nothing in the world, so it needs no save
    /// guard for correctness. It still steps aside while a save is being written, like every
    /// other system in the mod, so a save never competes with it for the main thread.
    /// </summary>
    public partial class FacadeSurveySystem : GameSystemBase
    {
        /// <summary>Main-thread time per frame the surveys may take.</summary>
        private const double BudgetMilliseconds = 1.5;

        private Game.Serialization.SaveGameSystem m_SaveGameSystem;
        private BuildingOverlayTestSystem m_Harness;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_SaveGameSystem = World.GetOrCreateSystemManaged<Game.Serialization.SaveGameSystem>();
            m_Harness = World.GetOrCreateSystemManaged<BuildingOverlayTestSystem>();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            DecalObjectOverlayRenderer renderer = m_Harness != null ? m_Harness.DecalRenderer : null;
            if (renderer == null || renderer.PendingSurveys == 0 || SaveMutationGate.IsBlocked(m_SaveGameSystem))
            {
                return;
            }

            renderer.AdvanceSurveys(BudgetMilliseconds);
        }

        [Preserve]
        public FacadeSurveySystem()
        {
        }
    }
}

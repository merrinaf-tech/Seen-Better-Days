using Game;
using UnityEngine.Scripting;

namespace SeenBetterDays.Systems
{
    /// <summary>
    /// Keeps this mod out of the player's save file.
    ///
    /// The base weathering is written into <c>Game.Rendering.CustomMeshColor</c>, which is a
    /// vanilla component and is serialised with the city. The detail layer consists of ordinary
    /// object entities, which the serializer would also keep. Left alone, a city saved while
    /// weathered would therefore retain both kinds of runtime changes.
    /// In one test city that left 1069 growables out of 1801 stuck on the previous session's
    /// colours, which the mod then correctly refused to touch - correctly, and uselessly.
    ///
    /// So this system runs at <see cref="SystemUpdatePhase.Serialize"/> and takes everything off
    /// first. What goes to disk is the city as the game built it. Automatic colours are rebuilt
    /// from circumstances the game saves anyway; temporary developer decal samples stay cleared.
    ///
    /// Two properties fall out of that which are worth having on purpose, not by luck:
    ///
    ///   - A save made with Seen Better Days installed opens normally for someone who does not
    ///     have it, because it contains nothing of ours.
    ///   - Uninstalling the mod leaves no damage behind - there is nothing to clean up.
    ///
    /// Whether the phase really runs before the entity data is written is a claim about the
    /// engine, and it is tested rather than assumed: weather a city, save, reload, and ask for a
    /// census. "0 already recoloured" means this works.
    /// </summary>
    public partial class WeatheringSaveGuardSystem : GameSystemBase
    {
        private BuildingWeatheringSystem m_Weathering;
        private BuildingOverlayTestSystem m_OverlayTest;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Weathering = World.GetOrCreateSystemManaged<BuildingWeatheringSystem>();
            m_OverlayTest = World.GetOrCreateSystemManaged<BuildingOverlayTestSystem>();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            int stripped = m_Weathering.SuspendForSave();
            int strippedDecals = m_OverlayTest.SuspendDecalsForSave();

            if (stripped > 0 || strippedDecals > 0)
            {
                Mod.Log.Info("Seen Better Days: took the weathering off " + stripped
                           + " building(s) and removed " + strippedDecals
                           + " temporary decal entit(ies) so the save contains none of it. "
                           + "Automatic colour weathering goes back on after serialization; "
                           + "developer decal samples stay cleared.");
            }
        }

        [Preserve]
        public WeatheringSaveGuardSystem()
        {
        }
    }
}

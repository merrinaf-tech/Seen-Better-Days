using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using SeenBetterDays.Localization;
using SeenBetterDays.Settings;
using SeenBetterDays.Systems;

namespace SeenBetterDays
{
    /// <summary>
    /// Seen Better Days - rendering proof of concept.
    ///
    /// This build does not age anything. It exists to answer one question: can we add visible
    /// weathering to a single growable building instance, dynamically and cheaply, without
    /// touching its textures and without changing any other building that shares its prefab.
    /// The findings, the chosen technique and what is still open are in docs/RENDERING_POC.md;
    /// the test instructions are in README.md.
    ///
    /// Runtime visual changes are deliberately stripped during serialization rather than
    /// persisted, so a city saved with this installed contains nothing of it and still opens for
    /// someone who does not have it.
    /// </summary>
    public class Mod : IMod
    {
        public const string Id = "SeenBetterDays";
        public const string Version = "0.1.0";

        public static readonly ILog Log = LogManager.GetLogger(Id).SetShowsErrorsInUI(false);

        /// <summary>The options page. Never null once OnLoad has run.</summary>
        public static SeenBetterDaysSettings Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info("Seen Better Days " + Version + " loading.");

            Settings = new SeenBetterDaysSettings(this);
            Settings.RegisterInOptionsUI();
            AssetDatabase.global.LoadSettings(Id, Settings, new SeenBetterDaysSettings(this));

            if (GameManager.instance != null && GameManager.instance.localizationManager != null)
            {
                GameManager.instance.localizationManager.AddSource("en-US", new SeenBetterDaysLocale(Settings));
            }

            Log.Info("Settings: weathering=" + Settings.EnableWeathering
                   + ", detail=" + Settings.EnableDecalDetail
                   + ", intensity=" + Settings.Intensity + "%"
                   + ", tooltip=" + Settings.ShowMaintenanceTooltip
                   + ", state keys=" + Settings.EnableStateKeys + ".");

            // Phase matters twice over, and both were learned the hard way in game.
            //
            // Not UIUpdate: inside MainLoop the game runs ModificationSystem (which drives every
            // Modification phase) BEFORE PreRenderSystem (which drives PreCulling), and
            // PrepareCleanUpSystem strips Created/Updated after MainLoop ends. An object entity
            // created during UIUpdate has already missed PreCullingSystem's initialise pass for
            // that frame and loses its Updated tag before the next one, so its CullingInfo stays
            // all zeroes and it is never drawn.
            //
            // And not Modification4B either: Game.Objects.OverrideSystem runs at Modification5
            // and re-hides our decals - they intersect the building on purpose - so anything we
            // do before it gets undone in the same frame. ModificationEnd is after Modification5
            // and still before PreCulling, which is exactly where Anarchy puts its own
            // RemoveOverridenSystem for the same reason.
            updateSystem.UpdateBefore<BuildingOverlayTestSystem>(SystemUpdatePhase.ModificationEnd);

            // The simulation side. Same phase and for the same reason - it writes colour buffers
            // and needs the renderer to pick them up in the frame it writes them.
            updateSystem.UpdateBefore<BuildingWeatheringSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateBefore<DecalDetailSystem>(SystemUpdatePhase.ModificationEnd);

            // Runs as the city is written to disk and takes the weathering off first, so nothing
            // this mod wrote ends up in the save. See WeatheringSaveGuardSystem for why that is
            // the right answer rather than merely the safe one.
            updateSystem.UpdateAt<WeatheringSaveGuardSystem>(SystemUpdatePhase.Serialize);

            // Reports under the cursor what the simulation thinks of a building, so the colour on
            // the wall can be checked against the number it came from.
            updateSystem.UpdateAt<WeatheringTooltipSystem>(SystemUpdatePhase.UITooltip);

            Log.Info("Seen Better Days loaded. Hotkeys (all Ctrl+Alt+...): "
                   + "J colour weathering, P measure hand-placed decals, H place a decal at the cursor's raycast hit, Y bare ground probe, T height sweep, A apply, S apply with a different seed, "
                   + "D remove, X remove all, F cycle facade, K flip projection direction, "
                   + "G cycle normal offset (metres), N cycle decal, B allow non-building decals, "
                   + "C dump catalogue, I describe selection, W toggle the automatic weathering, Q census of the city, 1/2/3 colour response, Z strip all custom colours, U toggle the decal detail layer, F1-F5 hold the selected building at Maintained/Aged/Worn/Neglected/Decayed, F6 release it.");
        }

        public void OnDispose()
        {
            Log.Info("Seen Better Days disposing.");
        }
    }
}

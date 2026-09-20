using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using SeenBetterDays.Localization;
using SeenBetterDays.Settings;
using SeenBetterDays.Systems;
using System.Threading;

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

        private static int s_CityAppearanceResetRequested;

        /// <summary>Queues a city reset for the simulation thread. Settings buttons can also be
        /// pressed in the main menu, so the request stays pending until a playable city exists.</summary>
        public static void RequestCityAppearanceReset()
        {
            Interlocked.Exchange(ref s_CityAppearanceResetRequested, 1);
            Log.Info("Seen Better Days: city appearance reset requested from the options page.");
        }

        internal static bool ConsumeCityAppearanceResetRequest()
        {
            return Interlocked.Exchange(ref s_CityAppearanceResetRequested, 0) != 0;
        }

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

            // Weathering overlays are implementation details, not player-authored props. Let the
            // tool finish its raycast, then redirect an overlay selection to its owning building.
            updateSystem.UpdateAfter<WeatheringOverlaySelectionSystem>(SystemUpdatePhase.ToolUpdate);

            // Use the serializer's preflight wrapper rather than mutating entities from an
            // ordinary Serialize-phase update. The wrapper calls IPreSerialize before the engine
            // builds its entity table, which is the last safe point to remove runtime visuals.
            updateSystem.UpdateAt<Game.Serialization.PreSerialize<WeatheringSaveGuardSystem>>(
                SystemUpdatePhase.Serialize);

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

using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace SeenBetterDays.Settings
{
    /// <summary>
    /// Options page for Seen Better Days.
    ///
    /// Two of these settings exist because the mod was built in front of someone watching it, and
    /// the things that were useful for building it are not the things a player wants switched on.
    /// The maintenance tooltip and the hand-set state keys are development instruments: they were
    /// always on because the alternative was judging colours blind, and they default to off here
    /// because a player who has not asked for a panel under their cursor should not get one.
    ///
    /// Nothing on this page is written into a city save. The weathering is recomputed from the
    /// game's own numbers every time a city loads, so changing any of these takes effect on the
    /// next pass and leaves nothing behind when it is changed back.
    /// </summary>
    [FileLocation("ModsSettings/SeenBetterDays/SeenBetterDays")]
    [SettingsUIGroupOrder(AppearanceGroup, LayersGroup, ResetGroup, DeveloperGroup)]
    [SettingsUIShowGroupName(AppearanceGroup, LayersGroup, ResetGroup, DeveloperGroup)]
    public class SeenBetterDaysSettings : ModSetting
    {
        public const string MainSection = "Main";

        public const string AppearanceGroup = "AppearanceGroup";
        public const string LayersGroup = "LayersGroup";
        public const string ResetGroup = "ResetGroup";
        public const string DeveloperGroup = "DeveloperGroup";

        public const int MinIntensity = 25;
        public const int MaxIntensity = 200;
        public const int DefaultIntensity = 100;

        public SeenBetterDaysSettings(IMod mod)
            : base(mod)
        {
        }

        /// <summary>
        /// The whole mod. Off leaves every building exactly as the game drew it.
        /// </summary>
        [SettingsUISection(MainSection, LayersGroup)]
        public bool EnableWeathering { get; set; } = true;

        /// <summary>
        /// The close-range marks - grime, cracks, moss, graffiti - on buildings near the camera.
        ///
        /// Separate from the colour because the two cost very different things. The colour is a
        /// few numbers in a buffer and is free at any distance; each set of marks is decided by
        /// casting rays at a building's actual mesh, which is why it only happens near the camera
        /// and why someone on a modest machine might want it off.
        /// </summary>
        [SettingsUISection(MainSection, LayersGroup)]
        [SettingsUIDisableByCondition(typeof(SeenBetterDaysSettings), nameof(IsWeatheringDisabled))]
        public bool EnableDecalDetail { get; set; } = true;

        /// <summary>
        /// How strongly weathering shows, as a percentage of the tuned default.
        ///
        /// It scales what is drawn, never what the simulation decides: a building's state is a
        /// fact about the city, and this is only how loudly that fact is stated.
        /// </summary>
        [SettingsUISection(MainSection, AppearanceGroup)]
        [SettingsUISlider(min = MinIntensity, max = MaxIntensity, step = 5, unit = "percentage")]
        [SettingsUIDisableByCondition(typeof(SeenBetterDaysSettings), nameof(IsWeatheringDisabled))]
        public int Intensity { get; set; } = DefaultIntensity;

        /// <summary>
        /// Removes the appearance currently managed by this instance of the mod, then lets the
        /// current rules calculate it again. Colours owned by Recolor or another mod are left in
        /// place because CustomMeshColor has no author id and must never be cleared speculatively.
        /// </summary>
        [SettingsUISection(MainSection, ResetGroup)]
        [SettingsUIButton]
        [SettingsUIConfirmation]
        public bool ResetCityAppearance
        {
            set { Mod.RequestCityAppearanceReset(); }
        }

        /// <summary>
        /// Shows, under the cursor, how well kept the game thinks a building is and why.
        ///
        /// Off by default. It is genuinely useful - it is the only way to check that a colour on a
        /// wall means what it claims to - but it puts an extra panel under the cursor on every
        /// building, and that is a choice to opt into rather than to discover.
        /// </summary>
        [SettingsUISection(MainSection, DeveloperGroup)]
        [SettingsUIAdvanced]
        public bool ShowMaintenanceTooltip { get; set; }

        /// <summary>
        /// Enables Ctrl+Alt+F1 to F5, which hold the selected building at one of the five states,
        /// and Ctrl+Alt+F6, which gives it back to the simulation.
        ///
        /// Off by default, and worth explaining rather than hiding: a settled city does not
        /// produce the five states for inspection. A measured census of one found 1273 buildings
        /// Maintained, 863 Aged, 5 Worn and nothing worse - four of the five states had never
        /// appeared on a real building. These keys put them side by side on one street. They
        /// change nothing permanently; a held building is released with F6 or by reloading.
        /// </summary>
        [SettingsUISection(MainSection, DeveloperGroup)]
        [SettingsUIAdvanced]
        [SettingsUIDisableByCondition(typeof(SeenBetterDaysSettings), nameof(IsWeatheringDisabled))]
        public bool EnableStateKeys { get; set; }

        /// <summary>
        /// The rest of the Ctrl+Alt developer harness: forced profiles, manual decals, catalogue
        /// and census dumps, emergency cleanups. Off by default and behind the advanced switch -
        /// these are tools for tuning the mod, and a stray chord should not repaint a player's
        /// street.
        /// </summary>
        [SettingsUISection(MainSection, DeveloperGroup)]
        [SettingsUIAdvanced]
        public bool EnableDeveloperShortcuts { get; set; }

        /// <summary>
        /// Design mode, for making hand-made decal designs: Ctrl+Alt+M on a selected building
        /// turns it into a blank canvas, and a panel exports the decals placed on it for a chosen
        /// state. Off by default and advanced: it is for people who design, not for playing.
        /// </summary>
        [SettingsUISection(MainSection, DeveloperGroup)]
        [SettingsUIAdvanced]
        public bool EnableDesignTools { get; set; }

        /// <summary>Written into every design exported, and into its folder name, so a design
        /// keeps its author when it is shipped with the mod.</summary>
        [SettingsUISection(MainSection, DeveloperGroup)]
        [SettingsUIAdvanced]
        [SettingsUITextInput]
        [SettingsUIHideByCondition(typeof(SeenBetterDaysSettings), nameof(IsDesignToolsOff))]
        public string DesignerName { get; set; } = string.Empty;

        /// <summary>Set once the designer has been asked for their name at their first export,
        /// whatever they answered, so it is never asked again. The name itself stays editable
        /// above.</summary>
        [SettingsUIHidden]
        public bool DesignerNameAsked { get; set; }

        public bool IsDesignToolsOff()
        {
            return !EnableDesignTools;
        }

        /// <summary>Used by the disable conditions above: everything else is meaningless with the
        /// mod switched off, and a page of live controls that do nothing is a lie.</summary>
        public bool IsWeatheringDisabled()
        {
            return !EnableWeathering;
        }

        /// <summary>The intensity as a multiplier.</summary>
        public float IntensityScale
        {
            get { return Intensity / 100f; }
        }

        public override void SetDefaults()
        {
            EnableWeathering = true;
            EnableDecalDetail = true;
            Intensity = DefaultIntensity;
            ShowMaintenanceTooltip = false;
            EnableStateKeys = false;
            EnableDeveloperShortcuts = false;
            EnableDesignTools = false;
            DesignerName = string.Empty;
            DesignerNameAsked = false;
        }
    }
}

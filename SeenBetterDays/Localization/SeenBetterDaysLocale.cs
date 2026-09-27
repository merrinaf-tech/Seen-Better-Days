using System.Collections.Generic;
using Colossal;
using SeenBetterDays.Settings;

namespace SeenBetterDays.Localization
{
    /// <summary>
    /// English strings for the options page.
    ///
    /// One language only, on purpose. Translating a page whose wording is still moving would mean
    /// translating it twice; the other languages belong with a release, not with a proof of
    /// concept. The ids come from the settings object because only it can produce them.
    /// </summary>
    public class SeenBetterDaysLocale : IDictionarySource
    {
        private readonly SeenBetterDaysSettings m_Settings;

        public SeenBetterDaysLocale(SeenBetterDaysSettings settings)
        {
            m_Settings = settings;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Settings.GetSettingsLocaleID(), "Seen Better Days" },

                { m_Settings.GetOptionGroupLocaleID(SeenBetterDaysSettings.LayersGroup), "What is drawn" },
                { m_Settings.GetOptionGroupLocaleID(SeenBetterDaysSettings.AppearanceGroup), "How strongly" },
                { m_Settings.GetOptionGroupLocaleID(SeenBetterDaysSettings.ResetGroup), "Appearance maintenance" },
                { m_Settings.GetOptionGroupLocaleID(SeenBetterDaysSettings.DeveloperGroup), "Advanced" },

                {
                    m_Settings.GetOptionLabelLocaleID(nameof(SeenBetterDaysSettings.EnableWeathering)),
                    "Weather buildings"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(SeenBetterDaysSettings.EnableWeathering)),
                    "Growable buildings take on the look their circumstances deserve - the street's "
                  + "value, the building's level, unpaid upkeep, failing services. Nothing is "
                  + "written into your save, and turning this off restores every building exactly "
                  + "as the game drew it."
                },

                {
                    m_Settings.GetOptionLabelLocaleID(nameof(SeenBetterDaysSettings.EnableDecalDetail)),
                    "Close-range detail"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(SeenBetterDaysSettings.EnableDecalDetail)),
                    "Adds grime, cracks, moss and graffiti to weathered buildings near the camera. "
                  + "Costs more than the colouring, because where each mark goes is decided by "
                  + "casting rays at the building's actual geometry - so turn it off if you would "
                  + "rather spend the frames elsewhere. The colouring is unaffected."
                },

                {
                    m_Settings.GetOptionLabelLocaleID(nameof(SeenBetterDaysSettings.Intensity)),
                    "Intensity"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(SeenBetterDaysSettings.Intensity)),
                    "How loudly the weathering is stated. This changes only what is drawn, never "
                  + "what the game decides about a building."
                },

                {
                    m_Settings.GetOptionLabelLocaleID(nameof(SeenBetterDaysSettings.ResetCityAppearance)),
                    "Rebuild Seen Better Days appearance"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(SeenBetterDaysSettings.ResetCityAppearance)),
                    "Removes the decals and colours currently managed by Seen Better Days, waits "
                  + "for the original palettes, then applies the current rules again. Colours "
                  + "that have since been replaced by Recolor or another mod are left untouched."
                },

                {
                    m_Settings.GetOptionLabelLocaleID(nameof(SeenBetterDaysSettings.ShowMaintenanceTooltip)),
                    "Show maintenance in the tooltip"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(SeenBetterDaysSettings.ShowMaintenanceTooltip)),
                    "Adds a line under the cursor saying how weathered a building is and why - its "
                  + "level, what its street is worth against the rest of the city, and any trouble "
                  + "it is in. Useful for understanding why a building looks the way it does, and "
                  + "an extra panel on every building you point at."
                },

                {
                    m_Settings.GetOptionLabelLocaleID(nameof(SeenBetterDaysSettings.EnableStateKeys)),
                    "Set a building's state by hand"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(SeenBetterDaysSettings.EnableStateKeys)),
                    "With a building selected, Ctrl+Alt+F1 to F5 hold it at Maintained, Aged, Worn, "
                  + "Neglected or Decayed, and Ctrl+Alt+F6 gives it back to the simulation. A held "
                  + "building stays that way until released or until the city is reloaded, and "
                  + "nothing is saved either way. Meant for seeing the five states side by side, "
                  + "which a healthy city will never show you on its own."
                },

                {
                    m_Settings.GetOptionLabelLocaleID(nameof(SeenBetterDaysSettings.EnableDeveloperShortcuts)),
                    "Developer shortcuts"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(SeenBetterDaysSettings.EnableDeveloperShortcuts)),
                    "Turns on the Ctrl+Alt keys used to develop and tune the mod: forced looks, "
                  + "manual decals, diagnostics written to the log and emergency cleanups. They "
                  + "can repaint buildings on purpose, so leave this off unless you know you "
                  + "need it."
                },
                {
                    m_Settings.GetOptionLabelLocaleID(nameof(SeenBetterDaysSettings.EnableDesignTools)),
                    "Design tools"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(SeenBetterDaysSettings.EnableDesignTools)),
                    "For making hand-made decal designs. Select a building and press Ctrl+Alt+M: it "
                  + "becomes a blank canvas. Decorate it with Anarchy and Extra Detailing Tools, "
                  + "then export it from the panel for the state you choose. Designs are saved in "
                  + "ModsData/SeenBetterDays/Designs with a screenshot, and used in your city "
                  + "straight away."
                },
                {
                    m_Settings.GetOptionLabelLocaleID(nameof(SeenBetterDaysSettings.DesignerName)),
                    "Designer name"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(SeenBetterDaysSettings.DesignerName)),
                    "Your name as it should appear on the designs you export, and on hover over "
                  + "buildings that use them. It is written into each design and its folder name, "
                  + "and stays with the design if it ships with the mod. Leave it empty to stay "
                  + "anonymous."
                },
            };
        }

        public void Unload()
        {
        }
    }
}

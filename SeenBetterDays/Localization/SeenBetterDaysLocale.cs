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
                { m_Settings.GetOptionGroupLocaleID(SeenBetterDaysSettings.ResetGroup), "Reset and repair" },
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
                    "Reset and rebuild the city appearance"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(SeenBetterDaysSettings.ResetCityAppearance)),
                    "Removes all Seen Better Days decals, saved weathering states and custom "
                  + "building colours, waits for the game to restore the original palettes, then "
                  + "applies only this version's rules. Use this if an older build left buildings "
                  + "black or if the result looks cumulative. Because the game stores no author "
                  + "for a custom colour, this also removes colours applied to growables with "
                  + "Recolor or another mod."
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
            };
        }

        public void Unload()
        {
        }
    }
}

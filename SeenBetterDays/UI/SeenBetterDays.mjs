/*!
 * Cities: Skylines II UI Module
 *
 * Id: SeenBetterDays
 * Author: Fabiozsche
 * Version: 0.1.1
 * Dependencies:
 */

// The banner above is read by the game (UIModuleAsset.ParseModuleInfo). Every line is required,
// including an empty Dependencies line: without it the module fails to load.
//
// Hand-written rather than bundled: it is one small wrapper, and the game exposes React and its
// cs2/* modules as window globals, which is all a bundle would resolve them to anyway.
//
// Tells ColourTabBindingSystem when the building panel's Customize tab is showing. The tab is
// local React state in the game's panel, so it is detected from the colour section, which the
// panel only mounts on that tab.

const SECTIONS = "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx";
const COLOUR_SECTION = "Game.UI.InGame.VisualCustomizeSection";

const React = window.React;
const api = window["cs2/api"];

function reportColourTab(open) {
  try {
    api.trigger("seenBetterDays", "colourTabOpen", open);
  } catch (e) {
    // The C# side is missing or not ready; the panel works as it would without the mod.
  }
}

export default function register(moduleRegistry) {
  moduleRegistry.extend(SECTIONS, "selectedInfoSectionComponents", (components) => {
    const Original = components && components[COLOUR_SECTION];
    if (!Original) {
      return components;
    }

    function SeenBetterDaysColourSection(props) {
      React.useEffect(() => {
        reportColourTab(true);
        return () => reportColourTab(false);
      }, []);
      return React.createElement(Original, props);
    }

    return { ...components, [COLOUR_SECTION]: SeenBetterDaysColourSection };
  });
}

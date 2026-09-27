/*!
 * Cities: Skylines II UI Module
 *
 * Id: SeenBetterDays
 * Author: Fabiozsche
 * Version: 0.1.2
 * Dependencies:
 */

// The banner above is read by the game (UIModuleAsset.ParseModuleInfo). Every line is required,
// including an empty Dependencies line: without it the module fails to load.
//
// Hand-written rather than bundled: it is small, and the game exposes React and its cs2/*
// modules as window globals, which is all a bundle would resolve them to anyway.
//
// Two jobs:
//  - tell ColourTabBindingSystem when the building panel's Customize tab is showing. The tab is
//    local React state in the game's panel, so it is detected from the colour section, which the
//    panel only mounts on that tab;
//  - draw the design-mode panel (DesignStudioSystem).

const GROUP = "seenBetterDays";
const SECTIONS = "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx";
const COLOUR_SECTION = "Game.UI.InGame.VisualCustomizeSection";

const React = window.React;
const api = window["cs2/api"];
const h = React.createElement;

function trigger(name, ...args) {
  try {
    api.trigger(GROUP, name, ...args);
  } catch (e) {
    // The C# side is missing or not ready; nothing to do.
  }
}

// ---- Customize tab --------------------------------------------------------------------------

function wrapColourSection(components) {
  const Original = components && components[COLOUR_SECTION];
  if (!Original) {
    return components;
  }

  function SeenBetterDaysColourSection(props) {
    React.useEffect(() => {
      trigger("colourTabOpen", true);
      return () => trigger("colourTabOpen", false);
    }, []);
    return h(Original, props);
  }

  return { ...components, [COLOUR_SECTION]: SeenBetterDaysColourSection };
}

// ---- Design panel ---------------------------------------------------------------------------

const building$ = api.bindValue(GROUP, "designBuilding", "");
const message$ = api.bindValue(GROUP, "designMessage", "");
const capturing$ = api.bindValue(GROUP, "designCapturing", false);
const designs$ = api.bindValue(GROUP, "designList", "[]");
const askName$ = api.bindValue(GROUP, "designAskName", false);
const suggestedName$ = api.bindValue(GROUP, "designSuggestedName", "");

// Visual state numbers, as in SeenBetterDays.Data.VisualState.
const STATES = [
  ["Aged", 1],
  ["Worn", 2],
  ["Neglected", 3],
  ["Decayed", 4],
];

const panelStyle = {
  position: "absolute",
  top: "80rem",
  left: "50%",
  transform: "translateX(-50%)",
  width: "560rem",
  padding: "14rem 16rem",
  backgroundColor: "rgba(24, 33, 51, 0.94)",
  borderRadius: "6rem",
  color: "#ffffff",
  fontSize: "14rem",
  pointerEvents: "auto",
};
const titleStyle = { fontSize: "16rem", fontWeight: "bold", marginBottom: "4rem" };
const mutedStyle = { color: "rgba(255, 255, 255, 0.7)", marginBottom: "10rem" };
// Fixed-width buttons on rows that never wrap: the UI renderer laid wrapped rows over each
// other, and buttons without a width broke their labels onto two lines.
const rowStyle = { display: "flex", flexDirection: "row", marginTop: "8rem" };
const buttonStyle = {
  width: "124rem",
  height: "32rem",
  marginRight: "8rem",
  display: "flex",
  alignItems: "center",
  justifyContent: "center",
  whiteSpace: "nowrap",
  backgroundColor: "rgba(255, 255, 255, 0.12)",
  borderRadius: "4rem",
  cursor: "pointer",
};
const wideButtonStyle = { ...buttonStyle, width: "256rem" };
const messageStyle = { marginTop: "8rem", color: "#b9e0ff" };
const sectionStyle = { marginTop: "12rem" };
const designRowStyle = { display: "flex", flexDirection: "row", alignItems: "center", marginTop: "6rem" };
const designLabelStyle = { width: "388rem", whiteSpace: "nowrap" };
const inputStyle = {
  width: "520rem",
  height: "32rem",
  marginTop: "8rem",
  padding: "0 8rem",
  backgroundColor: "rgba(255, 255, 255, 0.9)",
  color: "#1c2936",
  borderRadius: "4rem",
};

// The game's own text field blocks game shortcuts while it has focus, so typing a name does not
// open the bulldozer. A plain input is the fallback if the module moves.
function gameTextInput() {
  try {
    return window["cs2/modding"].getModule("game-ui/common/input/text/text-input.tsx", "TextInput");
  } catch (e) {
    return null;
  }
}

function Button({ label, onClick, wide }) {
  const [hover, setHover] = React.useState(false);
  const base = wide ? wideButtonStyle : buttonStyle;
  const style = hover ? { ...base, backgroundColor: "rgba(255, 255, 255, 0.24)" } : base;
  return h("div", { style, onClick, onMouseEnter: () => setHover(true), onMouseLeave: () => setHover(false) }, label);
}

function DesignList() {
  const json = api.useValue(designs$);
  let designs = [];
  try {
    designs = JSON.parse(json || "[]");
  } catch (e) {
    designs = [];
  }

  if (designs.length === 0) {
    return h("div", { style: { ...sectionStyle, ...mutedStyle } }, "No designs for this building model yet.");
  }

  return h(
    "div",
    { style: sectionStyle },
    h("div", null, "Start from a design:"),
    designs.map((d) =>
      h(
        "div",
        { key: d.index, style: designRowStyle },
        h(
          "div",
          { style: designLabelStyle },
          d.state + (d.author ? " by " + d.author : "") + " - " + d.decals + " decals" + (d.shipped ? "" : " (yours)")
        ),
        h(Button, { label: "Place", onClick: () => trigger("designLoad", d.index) })
      )
    )
  );
}

// Asked once, at the first export: the name shown on the designs and on hover in game.
function NamePrompt() {
  const suggested = api.useValue(suggestedName$);
  const [name, setName] = React.useState(suggested || "");
  React.useEffect(() => setName(suggested || ""), [suggested]);
  const TextInput = gameTextInput();
  const onChange = (e) => setName(e.target.value);

  return h(
    "div",
    { style: sectionStyle },
    h("div", { style: titleStyle }, "Your designer name"),
    h(
      "div",
      { style: mutedStyle },
      "Shown on your designs and, in game, on hover over the buildings that use them. Asked only once: you can change it later in the options."
    ),
    TextInput
      ? h(TextInput, { value: name, onChange, style: inputStyle })
      : h("input", { type: "text", value: name, onChange, style: inputStyle }),
    h(
      "div",
      { style: rowStyle },
      h(Button, { wide: true, label: "Use this name and export", onClick: () => trigger("designNameChosen", name) }),
      h(Button, { wide: true, label: "Stay anonymous and export", onClick: () => trigger("designNameChosen", "") })
    )
  );
}

function DesignPanel() {
  const building = api.useValue(building$);
  const message = api.useValue(message$);
  const capturing = api.useValue(capturing$);
  const askName = api.useValue(askName$);

  // Hidden while the export's screenshot is taken, so the picture shows the building only.
  if (!building || capturing) {
    return null;
  }

  return h(
    "div",
    { style: panelStyle },
    h("div", { style: titleStyle }, "Seen Better Days - design mode"),
    h("div", { style: mutedStyle }, building),
    askName ? h(NamePrompt) : null,
    h("div", null, "Export as:"),
    h(
      "div",
      { style: rowStyle },
      STATES.map(([name, value]) =>
        h(Button, { key: name, label: name, onClick: () => trigger("designExport", value) })
      )
    ),
    h(
      "div",
      { style: rowStyle },
      h(Button, { wide: true, label: "Remove the decals I placed", onClick: () => trigger("designClearPlaced") }),
      h(Button, { wide: true, label: "Exit design mode", onClick: () => trigger("designExit") })
    ),
    h(
      "div",
      { style: rowStyle },
      h(Button, { wide: true, label: "Open my designs folder", onClick: () => trigger("designOpenFolder") })
    ),
    h(DesignList),
    message ? h("div", { style: messageStyle }, message) : null
  );
}

export default function register(moduleRegistry) {
  moduleRegistry.extend(SECTIONS, "selectedInfoSectionComponents", wrapColourSection);
  moduleRegistry.append("Game", DesignPanel);
}

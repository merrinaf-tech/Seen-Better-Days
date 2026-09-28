/*!
 * Cities: Skylines II UI Module
 *
 * Id: SeenBetterDays
 * Author: Fabiozsche
 * Version: 0.1.4
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

// Docked on the right under the top bar and narrow, so the building stays in view while it is
// decorated; it also folds down to its title. Every row has fixed widths: the UI renderer laid
// wrapping rows over each other, and buttons without a width broke their labels in two.
const INNER = 276; // panel width minus padding, in rem
const panelStyle = {
  position: "absolute",
  top: "70rem",
  right: "12rem",
  width: INNER + 24 + "rem",
  padding: "10rem 12rem",
  backgroundColor: "rgba(24, 33, 51, 0.94)",
  borderRadius: "6rem",
  color: "#ffffff",
  fontSize: "13rem",
  pointerEvents: "auto",
};
const headerStyle = { display: "flex", flexDirection: "row", alignItems: "center" };
const titleStyle = { width: INNER - 30 + "rem", fontSize: "14rem", fontWeight: "bold" };
const mutedStyle = { color: "rgba(255, 255, 255, 0.65)", fontSize: "12rem" };
const buildingStyle = { ...mutedStyle, width: INNER + "rem", overflow: "hidden", whiteSpace: "nowrap", textOverflow: "ellipsis", marginTop: "2rem" };
const rowStyle = { display: "flex", flexDirection: "row", marginTop: "8rem" };
const buttonBase = {
  height: "28rem",
  display: "flex",
  alignItems: "center",
  justifyContent: "center",
  whiteSpace: "nowrap",
  backgroundColor: "rgba(255, 255, 255, 0.12)",
  borderRadius: "4rem",
  cursor: "pointer",
};
const messageStyle = { marginTop: "8rem", color: "#b9e0ff", fontSize: "12rem", width: INNER + "rem" };
const sectionStyle = { marginTop: "10rem" };
const linkStyle = { cursor: "pointer", width: INNER + "rem", whiteSpace: "nowrap" };
const designRowStyle = { display: "flex", flexDirection: "row", alignItems: "center", marginTop: "4rem" };
const inputStyle = {
  width: INNER - 16 + "rem",
  height: "28rem",
  marginTop: "6rem",
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

function Button({ label, onClick, width, marginRight }) {
  const [hover, setHover] = React.useState(false);
  const style = {
    ...buttonBase,
    width: width + "rem",
    marginRight: (marginRight || 0) + "rem",
    backgroundColor: hover ? "rgba(255, 255, 255, 0.24)" : buttonBase.backgroundColor,
  };
  return h("div", { style, onClick, onMouseEnter: () => setHover(true), onMouseLeave: () => setHover(false) }, label);
}

// The state is asked when exporting, not chosen beforehand: a state button sitting in the
// panel read as a setting, not as part of exporting.
function ExportChooser({ onClose }) {
  const half = (INNER - 8) / 2;
  const pick = (value) => {
    trigger("designExport", value);
    onClose();
  };
  return h(
    "div",
    { style: sectionStyle },
    h("div", null, "Export this design for which state?"),
    h(
      "div",
      { style: rowStyle },
      h(Button, { label: "Aged", width: half, marginRight: 8, onClick: () => pick(1) }),
      h(Button, { label: "Worn", width: half, onClick: () => pick(2) })
    ),
    h(
      "div",
      { style: rowStyle },
      h(Button, { label: "Neglected", width: half, marginRight: 8, onClick: () => pick(3) }),
      h(Button, { label: "Decayed", width: half, onClick: () => pick(4) })
    ),
    h("div", { style: rowStyle }, h(Button, { label: "Cancel", width: INNER, onClick: onClose }))
  );
}

function DesignList() {
  const json = api.useValue(designs$);
  const [open, setOpen] = React.useState(false);
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
    h(
      "div",
      { style: linkStyle, onClick: () => setOpen(!open) },
      (open ? "[-] " : "[+] ") + "Start from a design (" + designs.length + ")"
    ),
    open
      ? designs.map((d) =>
          h(
            "div",
            { key: d.index, style: designRowStyle },
            h(
              "div",
              { style: { width: INNER - 64 + "rem", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis", fontSize: "12rem" } },
              d.state + (d.author ? " by " + d.author : "") + " - " + d.decals + (d.shipped ? "" : " (yours)")
            ),
            h(Button, { label: "Place", width: 60, onClick: () => trigger("designLoad", d.index) })
          )
        )
      : null
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
    h("div", { style: { fontWeight: "bold" } }, "Your designer name"),
    h(
      "div",
      { style: { ...mutedStyle, width: INNER + "rem" } },
      "Shown on your designs and on hover in game. Asked only once; you can change it in the options."
    ),
    TextInput
      ? h(TextInput, { value: name, onChange, style: inputStyle })
      : h("input", { type: "text", value: name, onChange, style: inputStyle }),
    h("div", { style: rowStyle }, h(Button, { label: "Use this name and export", width: INNER, onClick: () => trigger("designNameChosen", name) })),
    h("div", { style: rowStyle }, h(Button, { label: "Stay anonymous and export", width: INNER, onClick: () => trigger("designNameChosen", "") }))
  );
}

function DesignPanel() {
  const building = api.useValue(building$);
  const message = api.useValue(message$);
  const capturing = api.useValue(capturing$);
  const askName = api.useValue(askName$);
  const [collapsed, setCollapsed] = React.useState(false);
  const [choosing, setChoosing] = React.useState(false);

  // Hidden while the export's screenshot is taken, so the picture shows the building only.
  if (!building || capturing) {
    return null;
  }

  const header = h(
    "div",
    { style: headerStyle },
    h("div", { style: titleStyle }, "Design mode"),
    h(Button, { label: collapsed ? "+" : "-", width: 28, onClick: () => setCollapsed(!collapsed) })
  );

  if (collapsed) {
    return h("div", { style: panelStyle }, header);
  }

  return h(
    "div",
    { style: panelStyle },
    header,
    h("div", { style: buildingStyle }, building),
    askName ? h(NamePrompt) : null,
    choosing
      ? h(ExportChooser, { onClose: () => setChoosing(false) })
      : h("div", { style: rowStyle }, h(Button, { label: "Export design", width: INNER, onClick: () => setChoosing(true) })),
    h(DesignList),
    h("div", { style: rowStyle }, h(Button, { label: "Open the zips to send", width: INNER, onClick: () => trigger("designOpenFolder") })),
    h("div", { style: rowStyle }, h(Button, { label: "Exit design mode", width: INNER, onClick: () => trigger("designExit") })),
    message ? h("div", { style: messageStyle }, message) : null
  );
}

export default function register(moduleRegistry) {
  moduleRegistry.extend(SECTIONS, "selectedInfoSectionComponents", wrapColourSection);
  moduleRegistry.append("Game", DesignPanel);
}

---
name: OpenUninstaller
description: A precise native Windows application workspace.
colors:
  dark-AppBackground: "#09090B"
  dark-SidebarBackground: "#101012"
  dark-Surface: "#18181B"
  dark-SurfaceAlt: "#202023"
  dark-SurfaceHover: "#27272A"
  dark-Border: "#303034"
  dark-BorderStrong: "#66666F"
  dark-TextPrimary: "#FAFAFA"
  dark-TextSecondary: "#C2C2C9"
  dark-TextMuted: "#A1A1AA"
  dark-Accent: "#FAFAFA"
  dark-AccentHover: "#E4E4E7"
  dark-AccentForeground: "#18181B"
  dark-AccentSoft: "#27272A"
  dark-Danger: "#F87171"
  dark-DangerHover: "#FCA5A5"
  dark-DangerSoft: "#351D21"
  dark-DangerForeground: "#18181B"
  dark-Success: "#34D399"
  dark-Warning: "#FBBF24"
  dark-SelectionBackground: "#29292E"
  dark-FocusRing: "#A1A1AA"
  light-AppBackground: "#F7F7F8"
  light-SidebarBackground: "#FCFCFD"
  light-Surface: "#FFFFFF"
  light-SurfaceAlt: "#F4F4F5"
  light-SurfaceHover: "#EBEBEE"
  light-Border: "#E4E4E7"
  light-BorderStrong: "#A1A1AA"
  light-TextPrimary: "#18181B"
  light-TextSecondary: "#52525B"
  light-TextMuted: "#62626C"
  light-Accent: "#18181B"
  light-AccentHover: "#3F3F46"
  light-AccentForeground: "#FAFAFA"
  light-AccentSoft: "#EAEAED"
  light-Danger: "#B91C1C"
  light-DangerHover: "#991B1B"
  light-DangerSoft: "#FEF2F2"
  light-DangerForeground: "#FFFFFF"
  light-Success: "#047857"
  light-Warning: "#92400E"
  light-SelectionBackground: "#EDEDF0"
  light-FocusRing: "#52525B"
typography:
  title:
    fontFamily: "Segoe UI Variable Text, Segoe UI"
    fontSize: "28px"
    fontWeight: 600
  section:
    fontFamily: "Segoe UI Variable Text, Segoe UI"
    fontSize: "15px"
    fontWeight: 600
  body:
    fontFamily: "Segoe UI Variable Text, Segoe UI"
    fontSize: "13px"
    fontWeight: 400
  label:
    fontFamily: "Segoe UI Variable Text, Segoe UI"
    fontSize: "11px"
    fontWeight: 600
  mono:
    fontFamily: "Cascadia Mono, Consolas"
    fontSize: "12px"
    fontWeight: 400
rounded:
  badge: "4px"
  theme-choice: "6px"
  control: "8px"
  popup: "10px"
  card: "14px"
spacing:
  compact: "4px"
  control-gap: "8px"
  label-gap: "12px"
  group-gap: "16px"
  card-inset: "22px"
  page-inset: "28px"
components:
  button-primary:
    backgroundColor: "{colors.dark-Accent}"
    textColor: "{colors.dark-AccentForeground}"
    rounded: "{rounded.control}"
    padding: "8px 14px"
  button-primary-hover:
    backgroundColor: "{colors.dark-AccentHover}"
  button-secondary:
    backgroundColor: "{colors.dark-Surface}"
    textColor: "{colors.dark-TextPrimary}"
    rounded: "{rounded.control}"
    padding: "8px 14px"
  button-danger:
    backgroundColor: "{colors.dark-DangerSoft}"
    textColor: "{colors.dark-Danger}"
    rounded: "{rounded.control}"
    padding: "8px 14px"
  button-ghost:
    backgroundColor: "transparent"
    textColor: "{colors.dark-TextPrimary}"
    rounded: "{rounded.control}"
    padding: "4px 8px"
  search-input:
    backgroundColor: "{colors.dark-SurfaceAlt}"
    textColor: "{colors.dark-TextPrimary}"
    rounded: "{rounded.control}"
    height: "36px"
    padding: "7px 10px 7px 34px"
  badge:
    backgroundColor: "transparent"
    textColor: "{colors.dark-TextSecondary}"
    rounded: "{rounded.badge}"
    padding: "3px 7px"
  card:
    backgroundColor: "{colors.dark-Surface}"
    rounded: "{rounded.card}"
    padding: "22px"
---
# Design System: OpenUninstaller

## Overview

**Creative North Star: "The Precise Workspace"**

OpenUninstaller uses native Windows WPF (.NET 8) with a restrained, practical visual system. Zinc surfaces, inverse actions and fine seams give installed applications and maintenance decisions a clear hierarchy. Dark is the default; light uses the same roles, geometry and interaction states.

The working content carries the emphasis. Native typography and Windows window behavior keep the interface familiar; quiet navigation and compact controls leave room for long application names, paths and deliberate actions.

**Key Characteristics:**
- Tonal zinc layers with hairline separation.
- Neutral primary actions and restrained semantic color.
- Compact native type, generous working surfaces and persistent selection.
- Shared controls across the shell, maintenance screens and dialogs.

Evidence: `Themes/Dark.xaml`, `Themes/Light.xaml`, `Themes/Controls.xaml`, `MainWindow.xaml`, `Views/ProgramsView.xaml`, `Views/JunkView.xaml`, `Views/SettingsView.xaml` under `src/OpenUninstaller.App/`; identity geometry in `Assets/app.svg`. These files are the implementation authority. Frontmatter uses portable px notation for WPF device-independent units (DIPs), not physical screen pixels. Component tokens describe the default dark theme; each matching `light-` color key is its light-theme counterpart.

## Colors

A near-neutral zinc palette uses contrast and tonal shifts for hierarchy.

### Primary

The inverse primary uses `Accent` on `AccentForeground`: pale against graphite in dark mode, graphite against pale in light mode. `AccentHover` gives a quiet hover change. `AccentSoft` supports navigation selection without making it look like a primary action. The legacy `AccentGradient` resource is a solid alias, not an instruction to introduce gradients.

### Neutral

`AppBackground` is the outer canvas; `SidebarBackground` identifies the rail and context bar. `Surface` carries cards and tables; `SurfaceAlt` carries inset fields and quiet hover states. `SurfaceHover`, `Border` and `BorderStrong` separate interactive states and edges. `TextPrimary`, `TextSecondary` and `TextMuted` establish readable emphasis. `SelectionBackground` persists on the selected table row; `FocusRing` marks keyboard focus.

### Semantic state

`Danger` and `DangerSoft` identify destructive actions, with `DangerHover` for their text state. `Success` identifies positive status, such as an enabled autorun. `Warning` identifies cautions and report-only states. These are semantic roles, not a secondary brand palette.

**The Meaning Rule.** Reserve red, amber and emerald for destructive, warning and positive state; use neutral contrast for ordinary emphasis.

## Typography

**Display and Body Font:** Segoe UI Variable Text, falling back to Segoe UI.
**Technical Font:** Cascadia Mono, falling back to Consolas.
**Icon Font:** Segoe MDL2 Assets for native Windows glyphs; simple geometric paths supplement it.

The hierarchy is compact and practical. Page titles use the frontmatter title role; section headings use section; body copy and controls use body. Caption labels use label; technical paths and commands use mono. Control labels are semibold (13 DIPs), badges semibold (11.5 DIPs), and navigation labels (12.5 DIPs). Use sentence case and preserve native text shaping. No custom global line height or tracking scale is established.

**The Native Type Rule.** Reuse the native font stack and shared text styles; keep technical values in the dedicated mono style.

**The Useful Copy Rule.** Keep action labels, live results, errors and short decision-critical warnings. Omit introductory paragraphs, repeated instructions, workspace badges, taglines and a decorative footer. Put secondary instructions in tooltips rather than persistent prose.

## Layout

The normal window is (1440 × 860 DIPs), with a minimum of (1040 × 620 DIPs). The stable navigation rail is (216 DIPs), the context bar (56 DIPs). Native Windows title-bar controls remain. Main page margins are typically (28 left/right, 26 top, 18 bottom); Settings ends with a (28 DIP) bottom margin and caps its content at (860 DIPs).

Use the observed compact spacing steps for labels and controls, then larger gaps for groups. Action and filter rows wrap. Data-heavy content stretches to fill available space and scrolls rather than forcing the window beyond its bounds.

Programs uses a flexible table with a (320 DIP) minimum, a (16 DIP) splitter gutter, and a (280 DIP) inspector, resizable between (240–420 DIPs). Its (6 DIP) drag target resizes the adjacent columns. Below a Programs view width of (1000 DIPs), the empty inspector and gutter collapse only when no application is selected. A selected inspector remains available. This is a Programs-specific adaptation, not a mobile breakpoint or a universal side-panel rule.

## Elevation & Depth

The shared system uses tonal layering and single-pixel seams rather than drop shadows. Cards sit on the canvas through their surface fill and border; controls use border contrast, focus treatment and persistent selection. Keep this flat depth model across both themes. ComboBox popups use WPF's native fade animation; no custom timing or general motion scale is established.

**The Quiet Surface Rule.** Use surface tone and a fine border to separate working regions; do not add decorative shadows to shared cards or controls.

## Shapes

Controls and navigation have gently curved corners; small badges and checkbox marks use tighter corners, while cards have a more generous radius. The frontmatter records the shared values. Borders and seams are normally (1 DIP); some keyboard-focus treatments increase to (2 DIPs). Grid cells avoid competing outlines. Dropdown entries and scrollbar thumbs use smaller local rounding; do not turn every local radius into a new global token.

The application identity is a white O with a detached lower-right corner on a rounded obsidian tile. `Assets/app.svg` is canonical; `scripts/generate-app-icon.ps1` derives the (1024px) PNG and nine ICO sizes (16, 20, 24, 32, 40, 48, 64, 128, 256px). The shell uses matching WPF geometry with inverse theme brushes. Preserve the O/corner silhouette and asset provenance when regenerating it.

## Components

### Buttons

Compact, restrained and semibold. Base and secondary buttons use a surface fill, fine border, (14 horizontal / 8 vertical DIP) padding and (36 DIP) minimum height. Primary buttons use inverse neutral contrast. Danger buttons use a soft red fill, red text and border at rest. Ghost and icon buttons are transparent until interaction, with reduced padding.

Base hover uses `SurfaceHover` and `BorderStrong`; primary hover uses `AccentHover`. Danger inherits base hover and changes its foreground to `DangerHover`. Keyboard focus changes the border; base buttons increase it to (2 DIPs). Pressed opacity is (0.8) for base and (0.85) for primary; disabled opacity is (0.4). Preserve labels and existing confirmation workflows for destructive commands.

### Inputs / Fields

Inset neutral fields use the shared control radius, (1 DIP) border and (36 DIP) minimum height. Hover strengthens the border; keyboard focus uses `Accent`; disabled fields fade to (0.55). SearchInput includes a drawn magnifier, placeholder from its Tag and a fixed (36 DIP) height. Its default width is (230 DIPs), overridden to (400 DIPs) in Programs. ComboBoxes share the control silhouette and show a bordered, rounded popup.

### Badges

Small transparent, outlined containers with semibold secondary text. Use the badge radius and padding for counts, types and short status labels; semantic text color may convey an actual state. They are information, not substitute buttons.

### Cards / Containers

The shared card uses `Surface`, a (1 DIP) `Border`, the card radius and default card inset. Tables override padding to zero; inspectors and maintenance rows use tighter local padding. Preserve the same tonal frame rather than adding shadow or decorative headers.

### Navigation

A stable rail uses native glyphs alongside labels. Items have (12 DIP) internal padding, quiet hover fill and a persistent `AccentSoft` selected fill with semibold primary text. Keyboard focus adds a visible border. Keep selected and hovered states distinguishable.

### Theme selector

The shell and Settings reuse the same horizontal ListBox segmented control. A `SurfaceAlt` track has (3 DIP) inset and control rounding; choices use (6 DIP) corners and (12 horizontal / 5 vertical DIP) padding. Selected choice uses `Surface`, primary text and semibold weight. Hover uses `SurfaceHover`; keyboard focus adds `FocusRing`.

### Data tables and inspector

Shared DataGrid rows are (60 DIPs), headers (42 DIPs), with (12 DIP) horizontal cell padding. Fine horizontal rules, muted compact headers and full-row selection keep the table legible without cell grids. Row virtualization remains enabled. Long values trim with tooltips; technical inspector values wrap. In Programs, an empty table offers search/filter/refresh guidance, while the inspector explains selection and groups technical detail inside an expander.

### Selection and progress

Checkboxes use a (16 DIP) square with the badge radius, inverse check state and a keyboard-focus border. Progress bars are quiet neutral tracks with the inverse accent; the shared height is (4 DIPs), with compact (3 DIP) page instances. Status must remain readable as text alongside any color.

## Do's and Don'ts

### Do:
- **Do** reuse DynamicResource theme roles and shared WPF styles across screens and dialogs.
- **Do** preserve light/dark parity, visible keyboard focus and persistent selection.
- **Do** let useful data and clear action labels carry the hierarchy.
- **Do** retain native resizing, scrolling, glyphs and existing safety confirmations.
- **Do** regenerate raster icons from the canonical SVG geometry.

### Don't:
- **Don't** use semantic red, amber or emerald as decorative brand accents.
- **Don't** add gradients or decorative shadows to the shared neutral surfaces.
- **Don't** replace the native typography and controls with web runtime dependencies.
- **Don't** apply the Programs empty-inspector collapse rule indiscriminately to other screens.

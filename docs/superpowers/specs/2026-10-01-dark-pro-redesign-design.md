# ReBackup — "Dark Pro" UI Redesign

Date: 2026-10-01
Status: Approved (mockups, variant B "Dark Pro", with the preview changes below)
Mockups: https://claude.ai/artifact/9c6EFC4kT2mRBwCRM1cufm (row B: `B_Plan`, `B_Preview`, `B_Retention`)

## 1. Goal

The whole app gets the dark, dense "Dark Pro" look of mockup row B: dark surfaces, one teal accent, icons on all
command buttons, cards instead of bare grids. Behaviour does not change, except for the two additions in §5.

## 2. Scope

| In | Out |
|---|---|
| Theme (colors, control styles, icons, dark title bar) for every window: main window, settings window, dialogs the app shows itself | Tray menu (system drawn) |
| Main window shell: icon rail, plan list, header with tabs and commands, status footer | Message boxes (`MessageBox.Show`) — system drawn |
| Tabs: **Plan** (General + Schedule merged), **Ignore & Preview**, **Retention**, **History** | The Versions tab (Phase 6 builds it in this style) |
| Preview: "In backup / Total" toggle; treemap stays below the tree | A light theme or a theme switch |

## 3. Theme

Tokens (exact values):

| Token | Value | Use |
|---|---|---|
| `Bg` | `#0F1216` | window ground |
| `Rail` | `#0B0D10` | icon rail, footer, code/pattern editor ground |
| `Surface` | `#14181D` | plan list, side panels, segmented-control ground |
| `Card` | `#171B21` | cards, tables |
| `Raised` | `#1D232B` | buttons, inputs' hover, selected segment |
| `Border` | `#232A33` | card and table borders, dividers |
| `BorderStrong` | `#2A313B` | buttons, inputs |
| `Text` | `#E6EAF0` | main text |
| `TextMuted` | `#9AA4B2` | labels, secondary text, column headers |
| `TextFaint` | `#6B7584` | disabled text, ignored rows, comments |
| `Accent` | `#2BB3A3` | primary buttons, toggles on, selection mark, "in backup" bars |
| `AccentHover` | `#35C6B5` | primary button hover |
| `AccentText` | `#06201D` | text and icons on `Accent` |
| `AccentSoft` | `#163B37` | soft accent fills (chips, schedule run marks) |
| `AccentLight` | `#5FD3C4` | accent text on dark (links, chip text) |
| `Selection` | `#16302D` | selected list/tree row |
| `Danger` | `#F2545B` | remove icons, errors, failed/canceled status, ignored status dot |
| `Warning` | `#E0A800` | partial status, scheduler-paused note |
| `PartialRow` | `#1E1B12` | background of partial rows |
| `IgnoredRow` | `#1F1518` | background of ignored rows |

Fonts: UI `Segoe UI Variable Text, Segoe UI`, 13 px; numbers tabular where possible; code/paths
`Cascadia Mono, Consolas`, 12 px. Icons: `Segoe Fluent Icons, Segoe MDL2 Assets` glyphs.

The title bar is dark (DWM immersive dark mode) on every app window.

## 4. Layout

- **Icon rail** (60 px, `Rail`): logo tile "Re:" (accent), Settings button at the bottom.
- **Plan list** (260 px, `Surface`): header "PLANS" with a New-plan icon button; one card per plan: status dot, name,
  Run-now icon button (accent) or, while active, Cancel (stop icon) with progress bar and progress text; source path
  (mono, muted); last run · next run (muted). The selected card has an accent border. Delete plan sits at the bottom
  of the list.
- **Header** (main area): plan name (20 px, semibold), segmented tab control (icons + labels: Plan, Ignore, Retention,
  History), then right-aligned Revert, Save (enabled only when dirty) and Run now / Cancel.
- **Footer** (34 px, `Rail`): status message left; scheduler status (warning color) and queue status right.
- **Plan tab**: card "Source → Target" (name, source, target with folder-icon browse buttons, the two switches,
  validation errors), card "Last run" (status dot + status, trigger, start; tiles: duration, copied size, files,
  skipped — from the newest history entry; "Never run" otherwise), card "Schedule" (trigger rows with remove icon,
  Add trigger, the next runs as chips, the tray hint).
- **Ignore & Preview tab**: pattern panel left (300 px, `Surface`, mono editor, two switches); right: toolbar
  (Index now/Rescan, Cancel/Stop, progress, status text, pattern note, the size toggle), summary pills (in backup /
  ignored), tree card, treemap card **below** the tree.
- **Retention tab**: card "Rules" with each rule as a tile (period badge, anchor, keep, remove icon) and Add rule; the
  "Full extension" forecast directly below the rules in the same card; card "Versions in the target now" at the
  bottom (table, refresh icon, empty state).
- **History tab**: one table card; status shown with a colored dot.

Toggles are switch-styled check boxes. Command buttons carry an icon left of the label; pure icon buttons have a
tooltip and an automation name.

## 5. Behaviour changes

1. **Size toggle in the preview.** A two-state segmented toggle "In backup | Total", default **In backup**. The
   tree shows ONE set of values: Files, Size, % of parent (bar + text) — in backup (included only) or total. In "In
   backup" mode ignored rows show "—" for size and percent. The treemap follows the toggle (in backup = sized by
   included size, ignored entries hidden; total = sized by total size, ignored greyed), replacing the
   "Hide ignored entries in the treemap" check box. Loading rows keep the "≥" prefix in both modes. The setting is
   per session (not saved).
2. **Plan tab merges General and Schedule.** No other behaviour changes.

## 6. Testing

The App has no automated tests. Each task: build at 0 warnings, the Core suite, and a visual check by the
controller (screenshots of each tab). Acceptance by the user follows.

# "Dark Pro" UI Redesign — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restyle the whole WPF app in the dark "Dark Pro" look of mockup row B, merge General + Schedule into one Plan tab, and add the "In backup / Total" toggle to the preview.

**Architecture:** A theme layer in `src/ReBackup.App/Theme/` (color tokens, implicit control styles, icon glyphs, dark title bar) is merged in `App.xaml`, so every control is dark without per-view work. Then the shell (`MainWindow.xaml`) and each tab view get the new card layouts. View models change only where the spec adds behaviour (size toggle) or a view needs a value it cannot bind today (last-run card).

**Tech Stack:** .NET 9, WPF, CommunityToolkit.Mvvm 8.4.2.

**Spec:** `docs/superpowers/specs/2026-10-01-dark-pro-redesign-design.md` (mockups: https://claude.ai/artifact/9c6EFC4kT2mRBwCRM1cufm, row B)

## Global Constraints

- Colors only through the theme's brush resources (`{StaticResource Brush.<Token>}` / `{DynamicResource …}`), named exactly after the spec §3 tokens: `Brush.Bg`, `Brush.Rail`, `Brush.Surface`, `Brush.Card`, `Brush.Raised`, `Brush.Border`, `Brush.BorderStrong`, `Brush.Text`, `Brush.TextMuted`, `Brush.TextFaint`, `Brush.Accent`, `Brush.AccentHover`, `Brush.AccentText`, `Brush.AccentSoft`, `Brush.AccentLight`, `Brush.Selection`, `Brush.Danger`, `Brush.Warning`, `Brush.PartialRow`, `Brush.IgnoredRow`. No hard-coded colors in views (`Gray`, `Firebrick`, `#FFF6D8`, …) after Task 1. Drawing controls (treemap, retention timeline) take their colors from the same tokens or from a dark palette defined in the theme.
- Every local `Style` whose `TargetType` has a theme style must use `BasedOn="{StaticResource {x:Type <Type>}}"`, otherwise it silently drops the theme.
- Icons: `TextBlock` glyphs in font `Segoe Fluent Icons, Segoe MDL2 Assets`, glyph codes as `Icon.*` string resources: Play `E768`, Stop `E71A`, Add `E710`, Delete `E74D`, OpenFolder `E838`, Save `E74E`, Undo `E7A7`, Refresh `E72C`, Settings `E713`, Clock `E823`, Calendar `E787`, History `E81C`, Shield `EA18`, Hide `ED1A`, Edit `E70F`, Cancel `E711`, ChevronDown `E70D`, Plan `E8F1`.
- Icon-only buttons carry `ToolTip` and `AutomationProperties.Name`.
- Bindings, commands and view-model behaviour stay as they are unless a task says otherwise. Keyboard shortcuts (Ctrl+S) keep working.
- Builds stay at 0 warnings. Verify with `dotnet build src/ReBackup.App --no-incremental -o <temp dir>` (the user's app may be running and lock `bin`) and `dotnet test tests/ReBackup.Core.Tests`. NEVER stop, kill or start `ReBackup.App`; the controller does visual checks.
- Commit messages end with `Co-Authored-By: Claude <model> <noreply@anthropic.com>` naming the authoring model, separated by a blank line (two `-m` arguments).

---

### Task 1: Theme foundation — the whole app dark, layout unchanged

**Files:**
- Create: `src/ReBackup.App/Theme/Colors.xaml`, `src/ReBackup.App/Theme/Controls.xaml`, `src/ReBackup.App/Theme/Icons.xaml`, `src/ReBackup.App/Services/DarkTitleBar.cs`
- Modify: `src/ReBackup.App/App.xaml`, `MainWindow.xaml(.cs)`, `SettingsWindow.xaml(.cs)`, all `Views/*.xaml`, `Controls/TreemapControl.cs`, `Controls/RetentionTimelineControl.cs`

**Interfaces:**
- Produces: brush keys `Brush.<Token>` (spec §3), `Color.<Token>`; font keys `Font.Ui`, `Font.Mono`; icon keys `Icon.<Name>` (Global Constraints); styles with keys `Button.Accent`, `Button.Icon` (square, glyph only), `Button.Danger` (icon in `Brush.Danger`), `CheckBox.Switch`, `Border.Card` (Card background, Border brush, CornerRadius 10, Padding 18), `TextBlock.CardTitle` (12 px semibold uppercase, `TextMuted`, letter-spacing look via FontStretch not required), `TextBlock.Muted`, `TextBlock.Mono`, `TextBlock.Icon` (icon font, 14 px), `RadioButton.Segment` (segmented control item), `ListView.Table` / `GridViewColumnHeader` dark; `DarkTitleBar.Apply(Window)`.

- [ ] **Step 1: Colors and fonts** — `Colors.xaml`: one `Color` and one frozen `SolidColorBrush` per spec §3 token; `FontFamily` resources `Font.Ui` = `Segoe UI Variable Text, Segoe UI`, `Font.Mono` = `Cascadia Mono, Consolas`.
- [ ] **Step 2: Implicit control styles** — `Controls.xaml` with full `ControlTemplate`s (WPF's default Aero templates ignore dark backgrounds on hover/press) for: `Window` (Bg, Text, Font.Ui, 13 px), `Button` (Raised, BorderStrong 1 px, CornerRadius 6, padding 12,0, height 32, hover lighter, pressed darker, disabled TextFaint), `ToggleButton`, `RepeatButton`, `TextBox` (Bg `Bg`, BorderStrong, CornerRadius 6, focus border Accent, caret `Text`, selection Accent), `PasswordBox` not needed, `CheckBox` (dark box with accent check), `CheckBox.Switch` (36×20 pill, Accent when checked, knob `AccentText`), `RadioButton`, `ComboBox` + `ComboBoxItem` (dark popup), `ListBox` + `ListBoxItem` (transparent, hover Raised, selected `Selection`), `ListView` + `ListViewItem` + `GridViewColumnHeader` (Card ground, header TextMuted 11–12 px uppercase, row height ~28, hover Raised, selected `Selection` with 2 px accent mark left), `ScrollViewer`/`ScrollBar` (thin 10 px, thumb BorderStrong, no arrows), `ProgressBar` (6 px rounded, track Border, fill Accent, indeterminate animation), `ToolTip` (Raised, BorderStrong), `ContextMenu` + `MenuItem` + `Separator` (dark), `TabControl` + `TabItem` (fallback dark look; the shell replaces the main tabs in Task 2), `GridSplitter` (transparent, hover Border), `StatusBar`/`StatusBarItem` (Rail), `Expander` (dark header), `DatePicker` not used. Keyed styles listed under Interfaces.
- [ ] **Step 3: Icons** — `Icons.xaml`: `sys:String` resources `Icon.<Name>` with the glyph characters (`&#xE768;` …).
- [ ] **Step 4: Dark title bar** — `DarkTitleBar.Apply(Window)`: on `SourceInitialized`, call `DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref 1, 4)` (P/Invoke `dwmapi.dll`); ignore failure (older Windows). Apply it to `MainWindow` and `SettingsWindow` (constructor), and to any other `Window` the app creates.
- [ ] **Step 5: Merge** — `App.xaml` merges Colors, Icons, Controls (in that order).
- [ ] **Step 6: Clean the views** — in every XAML file: add `BasedOn` to local styles of themed types; replace hard-coded colors with tokens: `Gray` text → `Brush.TextMuted` (ignored names → `Brush.TextFaint`), `Firebrick`/error text → `Brush.Danger`, `DarkOrange` → `Brush.Warning`, row backgrounds `#FFF6D8` → `Brush.PartialRow`, `#FBE3E1` → `Brush.IgnoredRow`, status dots: included `Brush.Accent`, partial `Brush.Warning`, ignored `Brush.Danger`, not scanned `Brush.TextFaint`; progress bar foreground `#2E9E48` → `Brush.Accent`; pattern editor and other code text boxes use `Font.Mono` on `Brush.Rail`.
- [ ] **Step 7: Drawing controls** — `TreemapControl`: background `Bg`, tile borders `Bg` (1 px gap look), a dark-friendly palette (e.g. teal `#2BB3A3`/`#1F8A7E`/`#5FD3C4`, blue `#7FB2FF`/`#4F84D6`, violet `#C9A2FF`/`#9B6FE0`, amber `#F2B84B`/`#D9932B`, rotating per top-level folder with lighter/darker shades by depth), ignored tiles `#2E3743`, selection outline `Text` 2 px. `RetentionTimelineControl`: axis/labels TextMuted, kept marks per rule in teal/blue/violet, deleted in TextFaint. Read the files first and keep their layout logic.
- [ ] **Step 8: Verify and commit** — build 0/0 (temp output), Core tests pass; commit `feat(app): dark theme — tokens, control styles, icons, dark title bar`.

### Task 2: Shell and Plan tab

**Files:**
- Modify: `src/ReBackup.App/MainWindow.xaml`, `src/ReBackup.App/ViewModels/PlanRunViewModel.cs` (last-run values), possibly `MainViewModel.cs` (selected tab only if needed)
- Create: `src/ReBackup.App/Views/PlanView.xaml(.cs)` (General + Schedule)
- Delete: `src/ReBackup.App/Views/ScheduleView.xaml(.cs)` once its content lives in `PlanView` (keep its code-behind logic, if any, in PlanView)

**Interfaces:**
- Consumes: Task 1 styles and tokens.
- Produces: `PlanRunViewModel.LastRun` (the newest `RunHistoryRow` or null) and `HasRun`; `PlanRunViewModel.EtaText` (the remaining-time text alone, e.g. "about 12 min left", "" when unknown) with `ProgressText` no longer containing it; main tabs as a segmented control.

The remaining time is very important to the user and was cut off in the old 320 px plan card ("…about…"). In the plan card it gets its own line below the progress bar, never trimmed: line 1 = files and bytes (may trim), line 2 = `EtaText` in `Brush.AccentLight`, semibold. The footer's queue status also shows it (`Backing up "X" — 34 % · about 12 min left`).

- [ ] **Step 1: Shell** — per spec §4: icon rail (logo tile, Settings button bound to `OpenSettingsCommand`), plan list panel (header "PLANS" + New plan icon button; plan cards with status dot — color from the plan's run state: running/queued Accent, last failed/canceled/full Danger, completed with warnings Warning, otherwise TextFaint —, name, Run-now accent icon button or Cancel stop button + progress bar + progress text while active, source in mono muted, last/next run muted; selected card accent border; Delete plan at the bottom), header (name, segmented tabs Plan/Ignore/Retention/History with icons, Revert, Save, Run now/Cancel for the selected plan), footer (status message; scheduler status in Warning; queue status). Use a `TabControl` restyled as a segmented header, or `RadioButton.Segment`s bound to a selected-tab index with a content switch — the tab content must keep its state when switching (do not recreate views on every switch).
- [ ] **Step 2: Plan tab** — `PlanView` with the three cards of spec §4: "Source → Target" (from the old General tab, browse buttons as folder icon buttons, `CheckBox.Switch` toggles, errors in Danger), "Last run" (status dot + status text, trigger, start; four tiles: duration, copied, files, skipped from `Run.LastRun`; "Never run" when there is none), "Schedule" (everything ScheduleView had: trigger rows with type/time/weekday/day/interval fields and a red delete icon, Add trigger, validation text, next runs as chips, the tray hint, the scheduler-paused note).
- [ ] **Step 3: Verify and commit** — build 0/0, Core tests; commit `feat(app): new shell and Plan tab (general + schedule)`.

### Task 3: Ignore & Preview with the size toggle

**Files:**
- Modify: `src/ReBackup.App/Views/IgnorePreviewView.xaml`, `src/ReBackup.App/ViewModels/PreviewTreeViewModel.cs`, `src/ReBackup.App/ViewModels/PreviewRowViewModel.cs`, `src/ReBackup.App/Controls/TreemapControl.cs` (if its HideIgnored semantics need a rename)

**Interfaces:**
- Produces: `PreviewTreeViewModel.ShowInBackup` (bool, default true; changing it refreshes all rows); row properties `ShownFilesText`, `ShownSizeText`, `ShownPercent`, `ShownPercentText` that follow the mode (In backup: included files/size and % of the parent's included size, "—" for ignored rows; Total: total files/size and % of the parent's total size); loading rows keep the "≥ " prefix.

- [ ] **Step 1: View model** — add the mode and the four shown properties; keep the existing properties (other bindings may use them).
- [ ] **Step 2: Layout** — spec §4: pattern panel left (title "IGNORE PATTERNS", mono editor on Rail, two `CheckBox.Switch`es), right: toolbar (Index now with Refresh icon as accent button, Cancel with Stop icon, indeterminate progress, progress text, pattern note in Warning, then right-aligned "Show" + segmented toggle "In backup | Total"), summary pills (in backup size + files, ignored size + files), tree card (columns Name, Files, Size, % of parent (bar + centered text), Status (dot + text); partial/ignored row backgrounds; loading rows faint with ⏳; placeholder italic), treemap card below the tree with a GridSplitter between them; treemap `HideIgnored` bound to the toggle (In backup = true); remove the old "Hide ignored entries" check box; keep the "The treemap appears after the scan." overlay; keep the row context menu.
- [ ] **Step 3: Verify and commit** — build 0/0, Core tests; commit `feat(app): dark preview with in-backup/total toggle`.

### Task 4: Retention, History, Settings

**Files:**
- Modify: `src/ReBackup.App/Views/RetentionView.xaml`, `src/ReBackup.App/Views/HistoryView.xaml`, `src/ReBackup.App/SettingsWindow.xaml`

- [ ] **Step 1: Retention** — card "RULES" (hint "a version stays if any rule keeps it; the newest one always stays", Add rule button with Add icon; each rule as a tile: period combo, anchor field + hint, keep field, red delete icon button, error text) in a wrapping row of tiles; directly below in the same card a divider and the "Full extension" forecast (assumption controls, summary text, timeline control); card "VERSIONS IN THE TARGET NOW" at the bottom (refresh icon button, error, summary, table, an empty state "No versions yet" when the list is empty).
- [ ] **Step 2: History** — one table card; status column with a colored dot (Completed Accent, CompletedWithWarnings Warning, Full/Error/Canceled Danger); expanded details keep working.
- [ ] **Step 3: Settings window** — cards per group (config folder, default ignore patterns in mono, close-to-tray and start-with-Windows as switches), OK/Cancel at the bottom (OK accent).
- [ ] **Step 4: Verify and commit** — build 0/0, Core tests; commit `feat(app): dark retention, history and settings`.

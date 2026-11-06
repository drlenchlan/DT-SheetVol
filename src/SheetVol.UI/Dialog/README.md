# Dialog

Owner: U2 (HANDOFF.md section 10). XAML and thin code-behind for SVDIALOG, dark and light resource dictionaries.

The window binds to C4's view models in `SheetVol.Core/Presentation` and holds no engine logic
(6.12, 6.14). Nothing in this folder references an Autodesk assembly, so the window loads and
renders in a plain WPF process (see "Smoke check" below). Labels come from `DialogText` through
`{x:Static}`; the few accessible names DialogText does not carry are in `ViewText.cs`.

## File map

| File | What |
|---|---|
| `DialogWindow.xaml` | The window: strips (design note, "No drawing is open"), left panel 328 wide, main area (stale banner, run problems, strip chart, splitter, grid toolbar, segments grid with row details, total row, reconciliation line, empty state), footer |
| `DialogWindow.xaml.cs` | Constructor `DialogWindow(ISheetVolSession, PendingEdits?, PresentationConfig?, DialogTheme, string? modeNote)`; `ApplyTheme`; grid cell editing (Enter commits, Esc reverts, read-only cells refuse edit); total row follows the grid's column widths and horizontal scroll; title-bar close goes through `DialogViewModel.Close` |
| `StripChartPanel.cs` | Draws `StripChartViewModel`: bars proportional to length, fill above / cut below the axis, ticks at every break, station labels (first and last always, interior ones where they fit), selected segment outlined, hollow bars without a result, HCFCD cut strip. Scrolls when bars would be narrower than `MinBarWidth` and centres a new selection (E913) |
| `Themes/DarkTheme.xaml`, `Themes/LightTheme.xaml` | Colour keys only, identical key sets (E908) |
| `Themes/SheetVolStyles.xaml` | Control styles and templates written for SheetVol (no Autodesk template, icon or font): buttons, text boxes, check boxes, combo boxes, scroll bars, tool tips, progress bar, data grid, status chip, field templates |
| `Themes/ThemeDictionaries.cs` | Code-behind classes for the three dictionaries; loaded by type, not by pack URI |
| `Converters.cs` | `Converters` (static value converters), `HeaderTemplateSelector`, `BindingProxy` (column headers and visibility) |
| `FieldBehavior.cs` | `FieldBehavior.CommitOnEnter` (Enter commits, Esc reverts a `TextFieldViewModel`), `GridEdit.Field` (which field an editable column edits) |
| `DialogTheme.cs`, `ViewText.cs` | Theme enum; accessible names not in DialogText |

## Layout against 6.14

| 6.14 | Where |
|---|---|
| Left panel, 328 wide | `Grid.ColumnDefinitions` of the body, first column `Width="328"` |
| Alignment dropdown, pick button, station range | "Alignment" card |
| Offsets, "same on both sides"; HCFCD OHWM next to them | "Boundary offsets" card; OHWM shows with `IsHcfcd` |
| Surfaces card (base, comparison, cut factor, fill factor, add another); HCFCD labels table row / existing / design | "Surfaces to compare" card; labels from `AnalysisNameLabel`, `BaseSurfaceLabel`, `ComparisonSurfaceLabel` |
| Fill by interval: every / from / to, Fill table | "Fill stations by interval" card |
| Draw options | "Draw in the drawing" card, bound to `RunDefinition.Draw` through the view model |
| Report selector | "Report" card at the top. Shown for both presets: it is the only way to reach the HCFCD preset from the dialog. **Compare with the artboards**: 6.14 says the HCFCD panel "gains" it |
| Strip chart, stale banner above it | Main area rows 0 to 2 |
| Grid: columns per preset, editable begin/end, status chips, per-row message with the fix beside it and the offset-fix fields, total row, reconciliation line, comparison selector, area-unit dropdown in the area header | Main area row 4. Standard columns 0–8 and 15; HCFCD columns 0–2 and 9–15 (visibility through `BindingProxy`). Row details carry the message, fix, impact note, field errors and the offset fix |
| Grid toolbar: Add station, Pick in drawing, Remove, Zoom to | Above the grid, right-aligned |
| Footer: auto-regenerate checkbox, Export CSV, Close, Regenerate (primary); progress and Cancel during a regen | Footer; progress bar and Cancel show while `IsBusy` |
| Totals hidden until every row has a result | Total row visibility = `Grid.TotalsVisible` |

## Theme keys

Every brush is reached through `DynamicResource`, so `DialogWindow.ApplyTheme` (the host calls it
when `COLORTHEME` changes) restyles the open window (E908). Both dictionaries define all of these:

| Group | Keys (`SV.Brush.` prefix) |
|---|---|
| Surfaces | `WindowBackground`, `PanelBackground`, `CardBackground`, `CardBorder`, `Separator` |
| Text | `Text`, `TextMuted`, `TextDisabled` |
| Inputs | `ControlBackground`, `ControlBorder`, `ControlHoverBorder`, `ControlReadOnlyBackground`, `FocusBorder`, `Selection`, `ItemHover` |
| Buttons | `ButtonBackground`, `ButtonHover`, `ButtonPressed`, `ButtonBorder`, `Accent`, `AccentHover`, `AccentPressed`, `AccentText` |
| States | `Error`, `ErrorBackground`, `ErrorRowTint`, `Warning`, `WarningBackground`, `WarningBorder`, `InfoBackground`, `InfoBorder` |
| Status chips | `ChipCurrentBackground`, `ChipCurrentText`, `ChipOutOfDateBackground`, `ChipOutOfDateText`, `ChipErrorBackground`, `ChipErrorText` |
| Grid | `GridBackground`, `GridRow`, `GridRowHover`, `GridRowSelected`, `GridLine`, `GridHeaderBackground`, `GridHeaderText`, `GridTotalBackground` |
| Chart | `ChartPlotBackground`, `ChartFill`, `ChartCut`, `ChartAxis`, `ChartLabel`, `ChartSelection`, `ChartCutMark`, `ChartHollow` |
| Scroll bars, tool tips | `ScrollThumb`, `ScrollThumbHover`, `ToolTipBackground`, `ToolTipBorder` |

Keyed styles and templates in `SheetVolStyles.xaml`: `SV.FocusVisual`, `SV.CardHeaderStyle`,
`SV.FieldLabelStyle`, `SV.MutedTextStyle`, `SV.ErrorTextStyle`, `SV.CellTextStyle`, `SV.CardStyle`,
`SV.SubCardStyle`, `SV.WarningBannerStyle`, `SV.InfoStripStyle`, `SV.ErrorStripStyle`,
`SV.FieldHostStyle`, `SV.PrimaryButtonStyle`, `SV.ScrollPageButtonStyle`, `SV.ScrollThumbStyle`,
`SV.LabelledFieldTemplate`, `SV.FieldTemplate`, `SV.CellFieldTemplate`, `SV.StatusChipTemplate`,
`SV.HeaderTemplateSelector`. Implicit styles: Button, TextBox, CheckBox, ComboBox, ComboBoxItem,
ScrollBar, ToolTip, ProgressBar, GridSplitter, DataGrid, DataGridColumnHeader, DataGridCell, DataGridRow.

## Keyboard (E910)

- Tab order follows the layout: left panel top to bottom, grid toolbar, grid, footer. Within the
  grid, Tab moves cell to cell (WPF DataGrid default) and reaches the offset-fix fields in a row's details.
- Grid: arrows move the current cell; typing or F2 starts an edit (typing starts it with the typed
  character); Enter commits and moves down; Esc while editing discards the typed text; Esc on a
  cell that shows an error reverts it to the last valid value.
- Text fields in the left panel and the row details: Enter commits, Esc reverts, leaving the field commits.
- Every focusable control has an accessible name; field errors are exposed as `HelpText`.

## Smoke check (no person needed)

`SheetVol.UI.Host.UiSmoke.RunDesign(folder, log)` builds the window off-screen against the design
fixture, drives it through the artboard states, writes one PNG per state and prints PASS or FAIL per
check. Two ways to run it:

- In Civil 3D: `NETLOAD` the staged `SheetVol.UI.dll`, run `SVDIALOGSMOKE`. PNGs go to
  `%TEMP%\SheetVol\ui-smoke` (`HostConfig.SmokeOutputFolder`).
- Without AutoCAD: a `net10.0-windows` console with `<UseWPF>true</UseWPF>` and an `[STAThread]`
  `Main` that runs `Assembly.LoadFrom(<build output>\SheetVol.UI.dll)` and invokes the internal static
  `SheetVol.UI.Host.UiSmoke.RunDesign(string folder, Action<string> log, bool bindingErrorsFail)` by
  reflection with `true`. MEASURED 2026-11-05: all checks PASS, zero binding errors, no Autodesk
  assembly loaded in the process. Inside Civil 3D the binding trace is process-wide (it also sees
  AutoCAD's ribbon and palettes), so `SVDIALOGSMOKE` reports the count without judging it.

Rule that keeps the second route working: **no assembly-level Autodesk attribute in SheetVol.UI**
(for example `[assembly: CommandClass]`). WPF's resource lookup reads the assembly's attributes and
resolves every attribute type, so one such attribute makes the window fail to load outside AutoCAD
(MEASURED: `FileNotFoundException 'accoremgd'` from `ResourceManager.CommonAssemblyInit`).

## G2 check for Dan (HANDOFF.md section 10, Gate G2)

Goal: open `SVDIALOG` once in Civil 3D, run T2's breaks through it, compare it with `docs/ui/`.

1. On the dev workstation (PowerShell, repo root): build.

   ```powershell
   dotnet build .\SheetVol.slnx -c Debug
   ```

2. On the dev workstation (PowerShell, repo root): stage the UI build output in a trusted folder
   (SECURELOAD refuses the build folder, NOW.md X4). The folder holds `SheetVol.UI.dll`,
   `SheetVol.Core.dll` and `SheetVol.Civil3D.dll` together.

   ```powershell
   $out = (dotnet msbuild .\src\SheetVol.UI\SheetVol.UI.csproj -getProperty:OutDir -p:Configuration=Debug -nologo | Select-Object -Last 1).Trim()
   if (-not [IO.Path]::IsPathRooted($out)) { $out = Join-Path .\src\SheetVol.UI $out }
   $stage = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\SheetVol-dev-SheetVol.bundle\Contents'
   New-Item -ItemType Directory -Force $stage | Out-Null
   Get-ChildItem $out -File | Where-Object Extension -in '.dll', '.pdb', '.json' | Copy-Item -Destination $stage -Force
   ```

3. Until A4 merges there is no engine session, so start Civil 3D with the design fixture switched
   on. In a PowerShell window, set the variable for that process and start Civil 3D from it (or set
   it once with `setx SHEETVOL_FAKE_SESSION 1` and start Civil 3D the usual way):

   ```powershell
   $env:SHEETVOL_FAKE_SESSION = '1'
   & 'C:\Program Files\Autodesk\AutoCAD 2026\acad.exe' /product C3D /language en-US
   ```

   After A4 merges, skip this step: SVDIALOG uses the engine session and reads the drawing.

4. In Civil 3D 2026, with any drawing open (`SVFIXTURE A` builds surfaces EG and FG_A only; `SVFIXTURE S` then adds alignment S
   along +X from 0 to 2000; `SVFIXTURE T` then adds the layers, table style, surface style and anchor block the settings name,
   which a drawing without the LAN template lacks, so Regenerate does not stop on SV-E501): `NETLOAD` `%APPDATA%\Autodesk\ApplicationPlugins\SheetVol-dev-SheetVol.bundle\Contents\SheetVol.UI.dll`,
   then run `SVDIALOG`. In fixture mode a blue strip at the top says the values are canned T2 values.

5. In the SVDIALOG window: T2's breaks are preloaded (`0, 500, 1300, 1800, 2000`, offsets ±100,
   EG vs FG_A). Press Regenerate and compare with the "Volumes current" artboard. Type 350 into an
   offset and compare with "Out of date, one error" (row 2 in Error with its message, the fix and the
   offset fields under it, the banner above the chart, totals hidden). Choose "HCFCD segment table"
   under Report, type an OHWM elevation, press Regenerate and compare with "HCFCD preset".

6. Then the M rows, in the same session (table below). Record what you see in NOW.md.

| Row | What to do in Civil 3D | Pass looks like |
|---|---|---|
| E901 | Open a second drawing, switch between them; close every drawing with the dialog open | The dialog follows the active drawing (fixture mode: pending edits kept per drawing); with none open, a red strip says "No drawing is open." and the controls are disabled |
| E905 | Start `LINE` and leave it waiting; look at Regenerate | Regenerate disabled with "Finish the active command first."; enabled again after Esc |
| E906 | Press Regenerate | Progress bar and Cancel in the footer (fixture mode: about 1.5 s); Cancel shows "Regenerate cancelled. The drawing was not changed." |
| E908 | Options > Display > Color theme, switch Dark/Light with the dialog open | The dialog restyles at once, no reopen |
| E909 | Windows display scaling 125 %, 150 %, 200 %, and a second monitor at a different scale; drag the window across; shrink it to its minimum | No clipped text; the window stops at its minimum size; at the minimum the grid scrolls sideways instead of squeezing columns |
| E910 | Keyboard only: Tab through, edit a station cell, Enter, Esc | Order as in "Keyboard" above; Narrator reads a name for every control |
| E911 | Select a row (fixture mode: in a drawing where model space has room at 0..2000 along X); press Zoom to; close the dialog | The segment's boundary is highlighted; Zoom to frames it; the highlight goes away on close |
| E913 | In the interval card type Every 8, Fill table, Regenerate | 250 rows scroll smoothly; selecting a row far down moves the chart to it |
| E915 | Core Console run of any script | The UI assembly is never loaded (nothing in the engine references it) |

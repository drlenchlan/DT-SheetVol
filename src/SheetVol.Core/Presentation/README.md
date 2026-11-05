# Presentation

Owner: C4 (HANDOFF.md section 10). Dialog view models, plain INotifyPropertyChanged, no WPF or Autodesk types (6.14).

| File | What |
|---|---|
| `DialogViewModel.cs` | The SVDIALOG window: left panel, footer, regen with progress and cancel, export, stale banner, pending edits |
| `SegmentsGridViewModel.cs`, `SegmentRowViewModel.cs`, `TotalRowViewModel.cs` | Grid, rows (with the per-segment offset fix beside a failing row), total row, comparison selector (E916), toolbar |
| `StripChartViewModel.cs` | Strip chart; values are 0..1 fractions the view scales |
| `AnalysisEditorViewModel.cs` | One base/comparison pair in the surfaces card |
| `TextFieldViewModel.cs` | Editable text with an inline error (E903); implements INotifyDataErrorInfo |
| `PendingEdits.cs` | Edits not yet regenerated, keyed by drawing (E907). The host keeps one instance per session |
| `DialogText.cs` | Every user-facing string, for `{x:Static}` binding |
| `PresentationConfig.cs` | Every display format, unit factor and dialog default |
| `BreakList.cs`, `RunComparison.cs`, `DisplayFormatter.cs`, `RowModel.cs` | Internal helpers |

Construct with `new DialogViewModel(session, pendingEdits, config)`. Since contract revision 2 the
dialog reaches the engine and the host only through `ISheetVolSession`: drawing identity and units
(`ActiveDrawing`), station text (`FormatStation`, `StationRange`), tracker status (`GetRunStatus`),
`IsCommandActive` and `ExportAsync` are session members (database side in `Civil3D/Session`, editor
side in `UI/Host`). Every edit calls `ISheetVolSession.Validate` only; volumes come from Regenerate
or from `ISheetVolSession.GetRunStatus`.

What the dialog writes into the run (contract rev 2):

- The three "Draw in the drawing" checkboxes are `RunDefinition.Draw` (D6). A stored run with
  `Draw = null` draws everything; a new run takes `PresentationConfig.DefaultDrawOptions`.
- The per-segment offset fix beside a failing row writes `SegmentOverrides.OffsetLeft` /
  `OffsetRight` on Apply and re-runs `Validate` (D8). A side equal to the run's offset, or blank,
  carries no override. The planner must read these for the fix to change a row's validation.
- The reconciliation line shows the `RegenResult.OutsideLimits` entry of the analysis the
  comparison selector drives (E916).

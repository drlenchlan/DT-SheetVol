# Presentation

Owner: C4 (HANDOFF.md section 10). Dialog view models, plain INotifyPropertyChanged, no WPF or Autodesk types (6.14).

| File | What |
|---|---|
| `DialogViewModel.cs` | The SVDIALOG window: left panel, footer, regen with progress and cancel, stale banner, pending edits |
| `SegmentsGridViewModel.cs`, `SegmentRowViewModel.cs`, `TotalRowViewModel.cs` | Grid, rows, total row, comparison selector (E916), toolbar |
| `StripChartViewModel.cs` | Strip chart; values are 0..1 fractions the view scales |
| `AnalysisEditorViewModel.cs` | One base/comparison pair in the surfaces card |
| `TextFieldViewModel.cs` | Editable text with an inline error (E903); implements INotifyDataErrorInfo |
| `IDialogHost.cs` | Port for what `ISheetVolSession` does not carry. U2 implements it in `SheetVol.UI/Host` |
| `PendingEdits.cs` | Edits not yet regenerated, keyed by drawing (E907). The host keeps one instance per session |
| `DialogText.cs` | Every user-facing string, for `{x:Static}` binding |
| `PresentationConfig.cs` | Every display format, unit factor and dialog default |
| `BreakList.cs`, `RunComparison.cs`, `DisplayFormatter.cs`, `RowModel.cs` | Internal helpers |

Construct with `new DialogViewModel(session, host, pendingEdits, config)`. Every edit calls
`ISheetVolSession.Validate` only; volumes come from Regenerate or from `IDialogHost.GetRunStatus`.

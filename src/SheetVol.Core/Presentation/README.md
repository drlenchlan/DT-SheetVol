# Presentation

Owner: C4 (HANDOFF.md section 10). Dialog view models, plain INotifyPropertyChanged, no WPF or Autodesk types (6.14).

| File | What |
|---|---|
| `DialogViewModel.cs` | The SVDIALOG window: left panel, footer, regen with progress and cancel, export, stale banner, pending edits |
| `SegmentsGridViewModel.cs`, `SegmentRowViewModel.cs`, `TotalRowViewModel.cs` | Grid, rows (with the per-segment offset fix beside a failing row), total row, comparison selector (E916), toolbar |
| `StripChartViewModel.cs` | Strip chart; values are 0..1 fractions the view scales |
| `AnalysisEditorViewModel.cs` | One comparison in the surfaces card: name and source switch, a base/comparison pair with factors or (rev 3) a volume surface shown read-only |
| `VolumeSurfaceOption.cs` | One entry of the volume surface dropdown (rev 3): a `VolumeSurfaceInfo`, selectable unless its sources were erased |
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

## Rev 3

- OHWM field, per-segment OHWM column and their members removed (`DialogViewModel.Ohwm`/`OhwmLabel`, `SegmentRowViewModel.Ohwm`/`OhwmIsOverride`, `SegmentsGridViewModel.OhwmHeader`, `RowModel` OHWM fields, `IGridOwner.EditOhwm`, `RunComparison.EffectiveOhwm`, the OHWM `DialogText` strings, `PresentationConfig.Elevation`).
- Source switch per comparison (X6, E918): `IsPairSource` / `IsVolumeSurfaceSource`, `VolumeSurface` (a dropdown over `DialogViewModel.VolumeSurfaces`, which mirrors `ISheetVolSession.VolumeSurfaces` and is re-read on SourcesChanged; nothing is added to it). Choosing one writes `AnalysisDef.VolumeSurface` and copies its pair into `BaseSurface`/`ComparisonSurface` (the contract's display copy); the stored factors keep the user's values and are never rewritten (rule 1); the surface's own pair and factors show read-only. Switching back clears `VolumeSurface` and restores the pair and factor fields with their last values. An entry with `SourcesIntact == false` is listed, not pickable, with `DialogText.VolumeSurfaceSourcesErased`. The switch on Volume surface with none chosen changes nothing in the run: Regenerate says `BlockedVolumeSurface`, the switch survives SourcesChanged, and it is not kept in pending edits. A chosen surface that is not listed any more, or is listed disabled, blocks Regenerate (`BlockedVolumeSurfaceUnusable`) with the reason in the card. E902 names "no volume surfaces" only while a comparison is switched to Volume surface. `RunComparison.VolumeInputsEqual` compares a volume surface source by name only. HCFCD card labels read table row, OHWM, proposed.

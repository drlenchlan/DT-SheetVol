# WritersModel

Owner: A2 (HANDOFF.md section 10). Boundary polylines, match marks, model-space and master tables, label-bound template tables, impacted-area outline, persistence store.

Core Console safe: AcCoreMgd, AcDbMgd, AecBaseMgd, AeccDbMgd only.

## Entry points

| Type | Does |
|---|---|
| `ModelWriter.Apply(ctx, plan, stored)` | The one call A4 makes in its 6.10 step 5 transaction. Plans every writer read-only, stops with no writes on any error (E105, E501, E601, SV-E541, SV-E534), then creates, updates or erases only what changed. Returns the model writers' owned handles for the record. |
| `RunStore` | 6.9 store: `Read`, `ReadAll`, `Write` (timestamp-only when unchanged, refuses newer schema or malformed records), `Delete`, `ResolveSource` (E803), `MergeOrphans` (E612), `RecordSignature`. |
| `MatchMarkPlanner.Plan` | Match marks at breaks shared by consecutive segments. |
| `ImpactOutlines.Build(segmentId, (analysisId, impact)...)` | Impact outline specs (rev 3, D13): one per ring of each analysis's `ImpactResult.Outlines`, points as given, key (segment, `ImpactOutline`, `{analysis GUID D}/{ring index}`). No rings, no spec. |
| `ModelTablePlacement` | Model-space anchors: per-sheet table above its boundary, master table left of the run. `TablePlacement.InLayout(name)` for a layout's anchor block or `tablePaperPoint`. |
| `TableRenderer` | Owner-agnostic table create and in-place update from a C2 `TableModel` (A3 may reuse it for paper tables). |
| `ModelWritersConfig` | Every layer, text style, text height, gap and template the writers use beyond C2's `ReportConfig`. |
| `SVWRITERSMODEL` (Check/) | Headless evidence run: `tools/test-headless.ps1 -Script tools/scripts/writersModel.scr`. |

## Call order for A4

1. Step 1 (read-only): `RunStore.Read`. If `CanModify` is false, stop (E105 or a malformed record). Resolve every stored `SourceRef` with `RunStore.ResolveSource`: SV-E803 is a warning, SV-E540 fails the run at step 1.
2. Steps 2 to 4 (pure): build the `ModelWritePlan`: C1 boundaries of station segments (manual segments are the user's own polylines and are not passed), `MatchMarkPlanner.Plan`, `ImpactOutlines.Build` per segment with every analysis's impact result for the HcfcdSegment preset only, C2 table models wrapped with `ModelTableSpec.ForSegment` (segments with no `TargetLayout`, A14) and `ModelTableSpec.ForMaster`, and `HcfcdTableSpec` per segment for the HCFCD preset. Pass every list every regen, empty when a feature is off, so stale objects are erased.
3. Step 5, one transaction under the document lock: `ModelWriter.Apply`; abort if it fails. Then A3's paper writers. Then build the `RunRecord`: the run with `KnownSegments` = this applied plan, `Objects` = model result objects plus A3's, repaired sources, `RunStore.MergeOrphans(previous, A3's new orphans, planned keys)`, the fingerprint. `RunStore.Write`; abort if `Refused`. Commit.
4. SVCLEAR: `ModelWriter.Apply(ctx, ModelWritePlan.Empty, read)` erases the run's model objects, then `RunStore.Delete`.
5. SVSTATUS: `RunStore.ReadAll`; orphans are `record.Orphans`.

`ModelWriter.Apply` may throw on an unexpected AutoCAD error during the write phase; A4's command boundary catches it and aborts the transaction (6.13), which undoes every write (T20).

## Codes

SV-E308 erased (segment removed), SV-E501 missing style or layer, SV-E503 table recreated, SV-E504 locked or frozen layer, SV-E505 table resized, SV-E601 layout or anchor missing for a new master table, SV-E802 object recreated, SV-E803 source found by name, SV-E1017 segment table's layout gone, SV-E540 source missing, SV-E541 malformed writer input, SV-E542 bound HCFCD cell covered by a user's merge, SV-E196 check step failed. C2's SV-E105, SV-E533, SV-E534, SV-E1015, SV-E537, SV-E538 pass through.

## Limits

- The impact outline is exactly the rings C5 returns in `ImpactResult.Outlines`; this package draws, never computes or repairs, them. A hole is its own closed polyline (no hatch, no region). Overlapping materials draw overlapping rings, one set per analysis.
- Table cell text is written MTEXT-escaped. `GetTextString(IgnoreMtextFormat)` drops escaped braces, so a cell is compared by plain text or by raw contents. HCFCD material names containing braces cannot be found by C2's label binding.
- Column widths are estimated from text length at creation (factors in config) and never changed afterwards.

## Rev 3

- The HCFCD check (`WritersModelCheckHcfcd`) builds its run with no `OhwmSpec` and its hand-built impacts with empty `Outlines`; the band outline stays until rev3-output.
- rev3-output (D13): the impact outline writer draws each ring of `ImpactResult.Outlines` on `modelWriters.impactOutlineLayer`, one owned polyline per (segment, analysis, ring), updated in place, recreated when erased (E802), erased when its ring or the whole list is gone (E308); an unclosed ring fails the plan with SV-E541 before any write. The full-width band outline is removed, not kept as a fallback (empty `Outlines` draws nothing; stored pre-rev-3 band keys "0", "1" are erased on the next regen). Check rows: `D13-rect`, `D13-tworings`, `D13-hole`, `E811-outline`, `D13-update`, `E802-outline`, `D13-ringgone`, `D13-empty` (twice), `E541-outline` (`WritersModelCheckOutline.cs`); `D13-noband` (twice) in the HCFCD phase.

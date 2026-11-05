# WritersPaper

Owner: A3 (HANDOFF.md section 10). Layout cloning, viewport create/update, clip, paper tables, title attributes (6.7).

## What A4 calls, in this order

```csharp
var paper = new PaperWriter(paperConfig, reportConfig.Placement, new LayoutNamer(layoutNameConfig));

// 1. Outside the regen transaction, before any write. E601 fails here, by name.
var loaded = paper.LoadTemplate();                 // Outcome<PaperTemplate>
if (!loaded.Succeeded) { /* report loaded.Problems, write nothing */ }
using PaperTemplate template = loaded.Value;       // holds the template's side database: keep it for the whole regen

// 2. Views and tables (pure Core).
//    C3: new ViewCalculator(cfg).Compute(alignment, polygons,
//          new ViewRequest(segment, run.Views, template.PlanViewportWidth, template.PlanViewportHeight, overrides))
//    C2: SegmentTableBuilder.Build(reconciled, segment.Id, run.AreaUnit, alignment.FormatStation, reportConfig)
var request = new PaperRunRequest(runId, sheets);  // PaperSheetRequest(segmentId, LayoutNameParts, ViewSpec, TableModel?, lockView, storedLayoutHandle)

// 3. Inside the regen transaction, document locked, before any write: E501, clip layer, E605 name clash.
var problems = paper.Validate(tr, db, template, request);

// 4. Same transaction. Throws PaperWriterException (Code SV-E690 or a row code): abort the transaction.
PaperApplyResult result = paper.Apply(tr, db, template, request);

// 5. A4 commits, persists result.Sheets' handles, lists result.Orphans in SVSTATUS.
//    result.Writes == 0 means the drawing already realized the plan (E811).
```

- `RunDefinition` has no GUID. A4 supplies a stable `RunId` (owner tags carry it).
- `LayoutNameParts` serve both the layout name pattern (C3) and the title-attribute patterns.
- Sheets are in sheet order; numbering and E307 renames follow it. A layout is found by its
  owner tag; the stored handle is only a tie-breaker when a copied layout claims the same segment.
- `PaperWriter.FindOwned(tr, db, ownerTagKey, runId)` lists every paper object of a run (role,
  segment, qualifier, handle, layout, signature) for persistence and SVSTATUS. Read only.
- `PaperTableSignatures.Of(model)` and `ContentSignatures.Of(viewSpec)` are the signatures written
  into the owner tags, if A4 wants them for `ChangeSetComparer`. The writer itself compares the live
  objects with the plan and writes nothing when they match, so A4 does not have to.
- A sheet with `Table = null` gets no table; an owned table already on that layout is left alone.
- `PaperViewMath.ModelToPaper(viewport, point)` is the P12 mapping, for A4's self-test.

## Settings (`PaperConfig`; the table anchor name comes from `ReportConfig.Placement`)

| Key | Default | Note |
|---|---|---|
| `templatePath` | none | DWT, or a DWG holding the layout; read with ReadDwgFile. Empty = E601 |
| `templateLayout` | none | Layout to clone. Empty = E601 |
| `planViewportLayer` | `SV-VPORT` | 6.7; else the largest floating viewport (Info E602) |
| `tablePaperPoint` | null | Fallback table corner when the template has no anchor block |
| `clipLayer` | null | null = the plan viewport's layer |
| `renumberLayouts` | true (ASSUMED) | E307 |
| `titleAttributes` | `STA_FROM={FROM}`, `STA_TO={TO}`, `SHEET_NO={N}` (PLACEHOLDER) | tag to pattern, C3 tokens |
| `sheetNumberFormat` | `0` | {N} in attributes |
| `annotationScaleNameFormat`, `annotationScaleNumberFormat` | `1:{0}`, `0.######` (PLACEHOLDER) | name of an added scale (E605) |
| `tableColumnWidths`, `tableRowHeight` | `[2.5, 1.5]`, `0.3` (PLACEHOLDER) | new tables only |
| `overallViewMargin` | 1.0 | paper-space view of a new layout = cloned extents x margin |
| `ownerTagKey` | `SHEETVOL_OWNER` | extension-dictionary entry of the owner Xrecord |
| `modelTolerance`, `angleTolerance`, `scaleRelativeTolerance`, `paperTolerance`, `bulgeTolerance` | 1e-6, 1e-9, 1e-9, 1e-9, 1e-12 | "already matches" tests |

Table style, layer and text height come with each `TableModel` (C2's `TableStyleSettings`).

## Owner tag

Xrecord under `ownerTagKey` in the extension dictionary of every layout, viewport, clip
polyline, paper table and filled title-block reference. One `DxfCode.Text` per field:
`SV-OWNER-1`, run GUID, segment GUID, role (C3 `OwnedRole`), qualifier, signature, extras
(a table's row keys `Kind|analysis|ordinal`, which let E505 grow or shrink it in place).
A2 tags model-space objects too; the two formats should become one at the A2/A3 merge.

## Measured behaviour this code depends on (2026.2.3 Core Console, 2026-11-05)

1. **Twist sign.** AutoCAD maps with R(+TwistAngle) (P12); C3's `paper = Rotate(model - centre, twist) / scale`
   has the same sign. Nothing is absorbed. TwistAngle reads back in [0, 2 pi): C3's -90 deg is stored as 270 deg.
2. **Plan viewport must be a new Viewport.** A WblockClone of the template's plan viewport was not
   entered by `_.MSPACE` after first activation (number -1, or taken over as number 1 with the
   paper-space view). A new Viewport carrying the clone's properties got number 2 and kept its view.
   Other viewports that are on in the template are replaced the same way; ones that are off stay clones (off).
   Evidence: `tools/scripts/writersPaperNumbering.scr`, cases R1 to R8.
3. **Paper-space view of a new layout.** `Layout.Initialize()` leaves the overall view at a fixed window
   (centre (16.83, 12.00), height 7.857, with or without a paper size). A viewport outside it is not
   entered. The writer fits the overall view to the cloned content.
4. **Clip edits.** A clip assigned to a viewport refits it and AutoCAD shifts ViewCenter (P12); an
   in-place edit of the clip polyline refits it without the shift. The writer sets the clip's box
   and ViewCenter itself. Setting `Height` keeps `ViewHeight` and changes `CustomScale`, so it is set again.
5. **ViewCenter after a clip or a move.** `ViewCenter = (CenterPoint - A) / CustomScale` with
   `A = CenterPoint - CustomScale * ViewCenter` read before the update. `docs/api-findings.md` B.2 rule 4
   prints the sign reversed.
6. `Viewport.TransformBy` does not move the viewport's clip polyline; a user's MOVE selects both.
7. `Viewport.On` is never set (refused in the creating transaction, P12; not needed, case R8).
8. A layout rename inside an aborted transaction is rolled back.
9. Paper tables: only merge anchors are written (P13); text is compared as plain text
   (`GetTextString(IgnoreMtextFormat)`), so cell formatting the user added to an unchanged value stays.
10. Force-open (`GetObject(id, ForWrite, false, true)`) updates objects on locked layers; the layer stays locked (E504, E604).

Not measured: any of this in full Civil 3D (acad.exe). One manual open of `%TEMP%\sheetvol\a3-t18.dwg`,
clicking T18-1 and T18-2, closes it: the band and its clip should fill the plan viewport, the key map
at the right should show, and double-clicking inside the plan viewport should enter it.

## Headless check

```powershell
.\tools\test-headless.ps1 -Script .\tools\scripts\writersPaper.scr
.\tools\test-headless.ps1 -Script .\tools\scripts\writersPaperReopen.scr -Drawing $env:TEMP\sheetvol\a3-t18.dwg
.\tools\test-headless.ps1 -Script .\tools\scripts\writersPaperNumbering.scr
```

`SVWRITERSPAPER <step>`; every evidence line starts with `A3 `, a failed expectation prints `FAIL`,
a probe-step failure prints `SV-E197`. The first script builds FX's template in the open drawing,
adds a title block and two key maps (PREP), saves it as `%TEMP%\sheetvol\a3-template.dwg`, then runs
E601, E501, T18, the P12 checks, E811 and the E-rows, saves `a3-t18.dwg` and reads it back, then
activates the T18 layouts (E614) and runs the acedTrans oracle. The third script needs the template
the first one saved.

Performance (E613): 60 sheets created in one regen in 35 to 130 ms; an unchanged regen of all 60 in
under 30 ms with zero database events. HANDOFF E613 asks for a warning past roughly 50 layouts per
drawing: AutoCAD itself gets slow to open and switch such drawings; split large runs across drawings.

## Codes

SV-E601 template piece missing or ambiguous (before any write), SV-E602 other viewports (Info),
SV-E604 locked viewport (Info), SV-E605 scale added (Info) or name clash (Error), SV-E608 moved
viewport or LockView (Info), SV-E612 orphan layout (Info), SV-E615 title tag missing (Info),
SV-E501 table style or layer missing, SV-E502 table rebuilt in place (Warning), SV-E503 table
recreated (Info), SV-E504 table on a locked layer (Info), SV-E505 table resized (Info),
SV-E802 owned viewport or clip recreated (Info), SV-E690 unexpected writer failure,
SV-E691 paper config invalid, SV-E197 check step failed.

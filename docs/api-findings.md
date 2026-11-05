# SheetVol API findings

Answers to the probes in HANDOFF.md section 5.3, with printed evidence. Each section is
owned by its probe worker (section 10): section A by S1, section B by S2, section C by S3.
A worker edits only its own section. Observed behavior on this host overrides
documentation. Every figure is MEASURED on the dev seat (Civil 3D 2026.2.3, AutoCAD
25.1.179, Core Console) unless it is marked otherwise.

## A. Volumes, alignment, headless (S1)

## B. Layouts, viewports, tables (S2)

Probes P11, P12, P13, P17, P20, P24. Run 2026-11-05 in the Civil 3D 2026 Core Console
against `tools/fixtures/blank-c3d.dwg`. Code: `src/SheetVol.Civil3D/SelfTest/ProbeB*.cs`.
Commands: `SVPROBEB_P11`, `_P12`, `_P12TRANS`, `_P13`, `_P17`, `_P24`, `_SAVE`, `_VERIFY`,
`_REOPEN`, `_P20A`, `_P20B`, `_P20C`.

### B.0 How to rerun

From the worktree or repo root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-headless.ps1 -Script tools\scripts\probeB.scr -BuildDir <repo>\src\SheetVol.Civil3D\bin\Debug\net10.0-windows
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-headless.ps1 -Script $env:TEMP\sheetvol\probeB-reopen.scr -Drawing $env:TEMP\sheetvol\probeB-out.dwg -BuildDir <same>
```

- The first run builds everything, saves `%TEMP%\sheetvol\probeB-out.dwg`, reads it back
  with `ReadDwgFile`, then activates layouts and runs P20 last, so the `UNDO` cannot touch
  the saved evidence. `SVPROBEB_SAVE` also writes `%TEMP%\sheetvol\probeB-reopen.scr`.
- The second run opens the saved file as the document. This is the user's "open the
  drawing, click the tab" case (E614).
- `-BuildDir` is needed in a worktree. The runner's default prefers
  `C:\LAN-DT-build\SheetVol.Civil3D\bin\...` (the main checkout's output) when that
  folder exists. Worktrees under `C:\LAN-DT-build\sheetvol-wt\` build in-tree, so
  without the flag the runner stages the wrong DLL.
- Logs: `%TEMP%\sheetvol\probeB.log` and `%TEMP%\sheetvol\probeB-reopen.log`. Both runs
  exit with code 0 and take about 5 s each.

### B.1 Summary

| Probe | Answer | Key fact |
|---|---|---|
| P11 | Answered | A layout imported headless from a DWT keeps its plot settings and contents through save and reopen. Use `Layout.Initialize()` (see P12) |
| P12 | Answered | `paper = vp.CenterPoint + CustomScale × (R(+TwistAngle)·(w − ViewTarget) − ViewCenter)`. Matches AutoCAD's own `acedTrans` to 0.000000 in all 8 checks. The 5.2 claim holds: `ViewCenter` is in DCS |
| P13 | Answered | A table is created in a non-current layout, one cell is changed in a second transaction, and exactly 1 cell differs. The change survives save and reopen |
| P17 | Answered | `AddContext` adds a missing scale and `Viewport.AnnotationScale` assigns it. Annotation scale and `CustomScale` are independent: set both |
| P20 | Answered | An aborted transaction rolls back layout creation (16 layouts inside, 15 after). One `UNDO` removes all three layouts one command created (18 to 15) |
| P24 | Answered | Finding a row by label with `GetTextString(FormatOption.IgnoreMtextFormat)` works with merged cells, MTEXT-formatted labels and a deleted row. Only the targeted cells change |

### B.2 Rules for A2 and A3 that come out of these probes

1. **Call `Layout.Initialize()` on every layout created headless before adding any
   viewport** (P12, P11). An uninitialized layout has no overall (paper-space) viewport.
   On first activation AutoCAD promotes the first `Viewport` in the layout block to the
   overall viewport (Number 1), re-zooms it and resets its twist. `LAYOUTCREATEVIEWPORT=1`
   then adds a default viewport as well.
2. **Do not rely on `Viewport.On`.** Inside the creating transaction it always throws
   `eNotInPaperspace`. In a second transaction it is accepted, but only on an initialized
   layout. Either way it reads `False` after save and reopen. First activation turns
   every viewport on. With rule 1 followed, headless-built viewports display (E614
   resolved in Core Console).
3. **Apply the view as 6.6 says** (`ViewTarget` = model centre, `ViewCenter` = (0,0),
   `TwistAngle` = −bearing, `CustomScale` = 1/ModelUnitsPerPaperUnit). A model direction
   at bearing β appears on paper at β + TwistAngle.
4. **A non-rectangular clip moves the viewport.** At commit AutoCAD resizes the viewport
   to the clip's bounding box and shifts `ViewCenter` by the paper offset ÷ scale, in
   paper axes, so the view stays fixed on paper. On regen (E608) do not re-apply
   `ViewCenter = (0,0)` to a clipped viewport: that shifts the view off the clip polygon
   (0.6 paper units in P12A). Use `ViewCenter = (A − vp.CenterPoint) / CustomScale`,
   where A is the paper point the model centre must land on (the centre the clip was
   built around).
5. `Viewport.Locked` does not block API edits (E604). Unlocking and relocking is
   harmless but not required.
6. **Tables: write only merge anchors.** `Cell.IsMerged` is true only on the top-left
   cell of a merge. Covered cells report false but still accept text, which is stored
   hidden. Find covered cells from the anchors' `GetMergeRange()`.
7. **Find labels with `GetTextString(FormatOption.IgnoreMtextFormat)`**, never with
   `TextString`, which returns raw MTEXT codes when a label is formatted.
8. **Annotation scale:** look an existing scale up by ratio (`DrawingUnits / PaperUnits`),
   not by name. Add it if absent (E605). Assign `AnnotationScale` *and* set `CustomScale`.
9. **Layout import:** Method S below, with `Initialize()`. A whole-layout clone
   (Method W) keeps the source `TabOrder`, which collides with an existing tab, and needs
   a rename.
10. `PlotPaperSize` is in millimetres whatever `PlotPaperUnits` says. A new layout from
    `CreateLayout` takes its plot device from the user profile; on this seat that is the
    LAN Framework's `C:\LAN-DT\Autodesk\_Firm\Support\Plotting\DWG To PDF.pc3`, with no
    media. `CopyFrom` replaces it with the template's settings.
11. `CVPORT` cannot be set from .NET in a modal command (`eInvalidInput`); `_.MSPACE`
    from a script works. Do not rely on `Viewport.Number` either: one floating viewport
    reported `Number=-1` after activation in both sessions while `On=True` (P17 layout;
    cause not found).

### P11. Import a layout from a DWT headless; plot settings and contents after save and reopen

**Question.** Can a layout be imported from an external DWT in Core Console without
making it current, and do its plot settings and contents survive save and reopen?

**Ran.** `SVPROBEB_P11` builds a template in a side database (`new Database(true, true)`)
and saves it to `%TEMP%\sheetvol\probeB-template.dwt`. The template holds:

- layer `SV-VPORT`;
- block `SV_TABLE_ANCHOR`;
- layout `SV-TEMPLATE`, made with `LayoutManager.CreateLayout` under a swapped
  `WorkingDatabase` and `Layout.Initialize()`d;
- plot settings via `PlotSettingsValidator`: DWG To PDF, ANSI B, monochrome.ctb, 1:1;
- a plan viewport on `SV-VPORT`, an anchor block reference and a text.

The probe then reads the template back with `ReadDwgFile` and imports it into the open
drawing two ways:

- **Method S** (the Autodesk guide's import sample, minus making the layout current):
  `LayoutManager.CreateLayout(name)`, then `Layout.CopyFrom(sourceLayout)`, then
  `Layout.Initialize()`, then `sourceDb.WblockCloneObjects(paper-space entities,
  layout.BlockTableRecordId, map, Ignore, false)`. The source's overall viewport
  (`GetViewports()[0]`) is skipped. `SV-P11-0` omits `Initialize()` as the negative
  control; `SV-P11-1` and `SV-P11-2` are two segments.
- **Method W:** `WblockCloneObjects({sourceLayoutId}, LayoutDictionaryId, ...)`, then
  `LayoutManager.RenameLayout`, then a `TabOrder` fix. Result: `SV-P11-W`.

`SVPROBEB_SAVE` and `SVPROBEB_VERIFY` save, read the file back and diff a snapshot of
every SV-* layout's plot settings and entities. The activation lines come from the
first run; the reopened-document lines come from the second.

**Output (verbatim).**

```text
P11 template: 121 media for 'DWG To PDF.pc3'; picked 'ANSI_B_(11.00_x_17.00_Inches)'
P11 template: PlotStyleMode (true = color-dependent) = True; 'monochrome.ctb' in style sheet list = True (54 sheets)
P11 template: viewports in layout block before Initialize: 0
P11 template: viewports in layout block after Initialize: 1; GetViewports().Count=1
P11 template: plan viewport On=true (side database) threw NotInPaperspace (eNotInPaperspace)
P11 source layout 'SV-TEMPLATE' (reread from disk): device='DWG To PDF.pc3' media='ANSI_B_(11.00_x_17.00_Inches)' styleSheet='monochrome.ctb' rotation=Degrees000 units=Inches type=Layout std=True/StdScale1To1 paper=(279.3999938965, 431.799987793)
P11 current layout before import: 'Model'
P11 S 'SV-P11-1': skipping source overall viewport 85
P11 S 'SV-P11-1': CreateLayout -> name='SV-P11-1' tabOrder=4 plot: device='C:\LAN-DT\Autodesk\_Firm\Support\Plotting\DWG To PDF.pc3' media='' styleSheet='' rotation=Degrees000 units=Inches type=Layout std=True/StdScale1To1 paper=(0, 0)
P11 S 'SV-P11-1': after CopyFrom -> name='SV-P11-1' tabOrder=4 btrUnchanged=True plot: device='DWG To PDF.pc3' media='ANSI_B_(11.00_x_17.00_Inches)' styleSheet='monochrome.ctb' rotation=Degrees000 units=Inches type=Layout std=True/StdScale1To1 paper=(279.3999938965, 431.799987793)
P11 S 'SV-P11-1' Initialize('SV-P11-1'): viewports 0 -> 1; GetViewports().Count=1; CTAB=Model
P11 S 'SV-P11-1': WblockCloneObjects 3 source entities -> 3 primary clones
P11 W: cloned layout name='SV-TEMPLATE' tabOrder=3 btr='*Paper_Space7' plot: device='DWG To PDF.pc3' media='ANSI_B_(11.00_x_17.00_Inches)' styleSheet='monochrome.ctb' rotation=Degrees000 units=Inches type=Layout std=True/StdScale1To1 paper=(279.3999938965, 431.799987793)
P11 W: TabOrder 3 shared with 1 other layout(s); now 6
P11 current layout after import: 'Model' (unchanged=True)
P11 block definitions named like SV_TABLE_ANCHOR: 1 [SV_TABLE_ANCHOR]
VERIFY P11 SV-P11-1 reopened plot equals template: True
VERIFY P11 SV-P11-1   Viewport 6D0 On=False Number=-1 Locked=False layer=0 center=(6, 4.5, 0) w=12 h=9 target=(0, 0, 0) viewCenter=(16.8348750732, 11.9956988258) twist=0 deg customScale=1.1454364758 viewHeight=7.8572668062 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
VERIFY P11 SV-P11-1   Viewport 6D3 On=False Number=-1 Locked=False layer=SV-VPORT center=(8.5, 5.5, 0) w=15 h=8 target=(0, 0, 0) viewCenter=(0, 0) twist=0 deg customScale=0.02 viewHeight=400 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
VERIFY P11 SV-P11-1   BlockReference 6D4 name=SV_TABLE_ANCHOR layer=0 pos=(1, 10, 0)
VERIFY P11 SV-P11-1   DBText 6D5 'SV TEMPLATE TITLE' layer=0 pos=(1, 0.75, 0) h=0.2
VERIFY P11 SV-P11-0 reopened plot equals template: True
VERIFY P11 SV-P11-2 reopened plot equals template: True
VERIFY P11 SV-P11-W reopened plot equals template: True
VERIFY snapshot: live 515 keys, file 515 keys, equal 502, different 13
```

All 13 differing keys are viewport `.on` flags, live `True` against file `False` (see
P12). For example:

```text
VERIFY DIFF differs: SV-P11-1|Viewport#6D0.on: live 'True' file 'False'
```

First activation, uninitialized import (`SV-P11-0`) against initialized imports:

```text
P12T 'SV-P11-0' GetViewports().Count=1
P12T 'SV-P11-0' after activation: Viewport 6C1 On=True Number=1 Locked=False layer=SV-VPORT center=(8.5, 5.5, 0) w=15 h=8 target=(0, 0, 0) viewCenter=(0, 12.4070138151) twist=0 deg customScale=0.0188317699 viewHeight=424.8140276302 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
P12T 'SV-P11-1' after activation: Viewport 6D3 On=True Number=2 Locked=False layer=SV-VPORT center=(8.5, 5.5, 0) w=15 h=8 target=(0, 0, 0) viewCenter=(0, 0) twist=0 deg customScale=0.02 viewHeight=400 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
P12T 'SV-P11-W' after activation: Viewport 6EC On=True Number=2 Locked=False layer=SV-VPORT center=(8.5, 5.5, 0) w=15 h=8 target=(0, 0, 0) viewCenter=(0, 0) twist=0 deg customScale=0.02 viewHeight=400 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
```

**Answer.** Yes. Both methods import the layout without making it current (CTAB stayed
`Model`).

- **Plot settings** equal the template after import and after save + `ReadDwgFile`, for
  all four imports.
- **Contents survive:** the plan viewport on `SV-VPORT` (scale 0.02), the
  `SV_TABLE_ANCHOR` reference at (1, 10) and the text. Apart from the `On` flags, all
  502 snapshot keys match.
- **`CopyFrom`** copies the plot settings only; the layout name and block are unchanged.
- **`DuplicateRecordCloning.Ignore`** reuses the destination's block definition: one
  `SV_TABLE_ANCHOR` after three imports.
- **Without `Initialize()`** the plan viewport is taken over as the overall viewport at
  first activation (`SV-P11-0`: Number=1, scale 0.02 became 0.0188). With it, the plan
  viewport is Number 2 and unchanged.
- **Method W** works because it carries the template's own overall viewport, but it
  keeps the source `TabOrder` (3, already used by `SV-P11-0`) and the source name.

**Recommendation for A3: Method S with `Initialize()`.**

- Method S sets the final name directly and needs no tab-order fix.
- It does not depend on whether the template's layout was ever activated.
- Rejected: Method W, for the `TabOrder` collision and the rename. Also, cloning with
  `Ignore` when the destination already has a layout of the template's name was **not
  tested**; the probe refuses that case instead of running it (ASSUMED risk: a silent
  skip).
- Not covered: the template was built in code. Dan's real DWT (section 13) has not been
  through this path. Its layout will already have an overall viewport, which the
  `GetViewports()[0]` skip rule handles on the code-built template.

### P12. Viewport created and updated without making its layout current; model-to-paper mapping

**Question.** Can `On`, `ViewTarget`, `ViewCenter`, `TwistAngle`, `CustomScale` and a
non-rectangular clip be set on a viewport in a layout that is not current? Where does a
known model point land on paper? Is `ViewCenter` in display coordinates that rotate with
the twist (5.2)?

**Ran.** `SVPROBEB_P12` creates five layouts with `CreateLayout`, never current. Each
gets one 12 × 7 viewport on `SV-VPORT` centred at paper (8.5, 5.5), scale 1/50. Model
point M = (5000, 3000) and M+100E = (5100, 3000) are drawn in model space.

| Layout | Setup |
|---|---|
| `SV-P12A` | The 6.6 form: target M, ViewCenter (0,0), twist 30°. Created locked with a different view, updated in a second transaction, then clipped by a closed polyline with one arc edge |
| `SV-P12B` | Target (0,0,0), ViewCenter = M's WCS numbers, twist 30° |
| `SV-P12B2` | Target (0,0,0), ViewCenter = R(+30°)·M, twist 30° |
| `SV-P12C` | Target M, ViewCenter (0,0), twist 0 (control) |
| `SV-P12N` | As A but with no `Layout.Initialize()` (negative control) |

Predictions use the read-back values under three hypotheses:

- H1: `ViewCenter` in DCS, where DCS = WCS rotated by +twist about the target.
- H1′: as H1 but rotated by −twist.
- H2: `ViewCenter` is an unrotated WCS offset from the target.

**The oracle.** The script makes each layout current, runs `_.MSPACE`, and
`SVPROBEB_P12TRANS` calls `acedTrans` (P/Invoke into accore.dll) from WCS to DCS to
PSDCS. That is AutoCAD's own transform. LISP `(trans (trans p 0 2) 2 3)` is printed
next to it as a cross-check.

**Output (verbatim).** Creation without making the layout current, and the `On` and
lock behavior:

```text
P12 CTAB before=Model LayoutManager.CurrentLayout='Model' TILEMODE=1 LAYOUTCREATEVIEWPORT=1
P12 SV-P12A create: vp.On=true threw NotInPaperspace (eNotInPaperspace)
P12 SV-P12A CTAB after create=Model
P12 SV-P12A second transaction: On=True (setter accepted)
P12 SV-P12A update while Locked: ViewTarget now (5000, 3000, 0) (was (0, 0, 0), changed=True)
P12 SV-P12A before clip: center=(8.5, 5.5, 0) w=12 h=7
P12 SV-P12A after clip: center=(8.5, 5.5, 0) w=12 h=7 clipOn=True clipId==pline=True
P12 SV-P12A readback after update: On=True Number=-1 Locked=True layer=SV-VPORT center=(9.1, 5.5, 0) w=11.2 h=6 target=(5000, 3000, 0) viewCenter=(30, 0) twist=30 deg customScale=0.02 viewHeight=300 dir=(0,0,1) clipOn=True clipId=6FF annoScale=1:1
P12 SV-P12A clip compensation: dPaperCenter=(0.6, 0) dViewCenter=(30, 0) H1 expects (30.000000, 0.000000) match=True | H2 expects (25.980762, -15.000000) match=False
P12 SV-P12B readback after create: On=False Number=-1 Locked=False layer=SV-VPORT center=(8.5, 5.5, 0) w=12 h=7 target=(0, 0, 0) viewCenter=(5000, 3000) twist=30 deg customScale=0.02 viewHeight=350 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
P12 SV-P12B set vs read: target d=0 viewCenter d=0 twist d=0 customScale d=0 viewHeight - h/scale = 0
P12 SV-P12N second transaction: vp.On=true threw NotInPaperspace (eNotInPaperspace)
P12 SV-P12N second transaction: On=False (setter refused)
P12 CTAB at end=Model LayoutManager.CurrentLayout='Model' (unchanged=True)
```

The oracle, first session:

```text
P12T SV-P12A M: WCS (5000, 3000, 0) -> DCS (0, 0, 0) -> paper (8.500000, 5.500000)
P12T SV-P12A M: |obs-H1|=0.000000 |obs-H1'|=0.000000 |obs-H2|=0.310583  H1 match=True H1' match=True H2 match=False
P12T SV-P12A M+100E: WCS (5100, 3000, 0) -> DCS (86.6025403784, 50, 0) -> paper (10.232051, 6.500000)
P12T SV-P12A M+100E: |obs-H1|=0.000000 |obs-H1'|=2.000000 |obs-H2|=0.310583  H1 match=True H1' match=False H2 match=False
(8.5 5.5 0.0)
(10.2321 6.5 0.0) (10.2321 6.5 0.0)
P12T SV-P12B M: WCS (5000, 3000, 0) -> DCS (2830.1270189222, 5098.0762113533, 0) -> paper (-34.897460, 47.461524)
P12T SV-P12B M: |obs-H1|=0.000000 |obs-H1'|=116.619038 |obs-H2|=60.366456  H1 match=True H1' match=False H2 match=False
P12T SV-P12B M+100E: |obs-H1|=0.000000 |obs-H1'|=118.338498 |obs-H2|=60.366456  H1 match=True H1' match=False H2 match=False
(-34.8975 47.4615 0.0)
P12T SV-P12B2 M: WCS (5000, 3000, 0) -> DCS (2830.1270189222, 5098.0762113533, 0) -> paper (8.500000, 5.500000)
P12T SV-P12B2 M: |obs-H1|=0.000000 |obs-H1'|=116.619038 |obs-H2|=60.366456  H1 match=True H1' match=False H2 match=False
P12T SV-P12B2 M+100E: WCS (5100, 3000, 0) -> DCS (2916.7295593006, 5148.0762113533, 0) -> paper (10.232051, 6.500000)
P12T SV-P12C M: WCS (5000, 3000, 0) -> DCS (0, 0, 0) -> paper (8.500000, 5.500000)
P12T SV-P12C M+100E: WCS (5100, 3000, 0) -> DCS (100, 0, 0) -> paper (10.500000, 5.500000)
```

`CVPORT` from .NET, then `_.MSPACE`:

```text
P12T set CVPORT=2 threw InvalidInput (eInvalidInput)
P12T ORACLE INVALID: the probe viewport is not the active model-space viewport
Command: _.MSPACE
P12T CVPORT now 2 (probe viewport Number=2)
```

First activation, initialized (A) against uninitialized (N):

```text
P12T 'SV-P12A' after activation: Viewport 6FA On=True Number=1 Locked=False layer=0 center=(6, 4.5, 0) w=12 h=9 target=(0, 0, 0) viewCenter=(18.5326147115, 11.9956988258) twist=0 deg customScale=1.1454364758 viewHeight=7.8572668062 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
P12T 'SV-P12A' after activation: Viewport 6FD On=True Number=2 Locked=True layer=SV-VPORT center=(9.1, 5.5, 0) w=11.2 h=6 target=(5000, 3000, 0) viewCenter=(30, 0) twist=30 deg customScale=0.02 viewHeight=300 dir=(0,0,1) clipOn=True clipId=6FF annoScale=1:1
P12T 'SV-P12N' after activation: Viewport 728 On=True Number=1 Locked=False layer=SV-VPORT center=(8.5, 5.5, 0) w=12 h=7 target=(5000, 3000, 0) viewCenter=(5.3031494862, 3.5807085413) twist=0 deg customScale=0.7783832089 viewHeight=8.9929997458 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
P12T 'SV-P12N' after activation: Viewport 7DC On=True Number=-1 Locked=False layer=0 center=(5.3031494862, 3.5807085413, 0) w=8.4850391778 h=5.729133666 target=(0, 0, 0) viewCenter=(16.8348750732, 11.9956988258) twist=0 deg customScale=0.729150964 viewHeight=7.8572668062 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
```

Saved file read back with `ReadDwgFile`:

```text
VERIFY file SV-P12A|Viewport#6FD.center = (9.1, 5.5, 0)
VERIFY file SV-P12A|Viewport#6FD.clipEntity = 6FF
VERIFY file SV-P12A|Viewport#6FD.clipOn = True
VERIFY file SV-P12A|Viewport#6FD.customScale = 0.02
VERIFY file SV-P12A|Viewport#6FD.on = False
VERIFY file SV-P12A|Viewport#6FD.twist = 0.5235987756
VERIFY file SV-P12A|Viewport#6FD.viewCenter = (30, 0)
VERIFY file SV-P12A|Polyline#6FF.vertices = closed (3.5, 2.5)b0 (13.5, 2.5)b0.4 (13.5, 8.5)b0 (4.5, 7.5)b0
```

Second session: the saved file opened as the document, before and after its first
activation:

```text
REOPEN document 'C:\Users\DRLench\AppData\Local\Temp\sheetvol\probeB-out.dwg' CTAB=Model
REOPEN SV-P12A Viewport 6FD On=False Number=-1 Locked=True layer=SV-VPORT center=(9.1, 5.5, 0) w=11.2 h=6 target=(5000, 3000, 0) viewCenter=(30, 0) twist=30 deg customScale=0.02 viewHeight=300 dir=(0,0,1) clipOn=True clipId=6FF annoScale=1:1
P12T 'SV-P12A' after activation: Viewport 6FD On=True Number=2 Locked=True layer=SV-VPORT center=(9.1, 5.5, 0) w=11.2 h=6 target=(5000, 3000, 0) viewCenter=(30, 0) twist=30 deg customScale=0.02 viewHeight=300 dir=(0,0,1) clipOn=True clipId=6FF annoScale=1:1
P12T SV-P12A M: WCS (5000, 3000, 0) -> DCS (0, 0, 0) -> paper (8.500000, 5.500000)
P12T SV-P12A M+100E: |obs-H1|=0.000000 |obs-H1'|=2.000000 |obs-H2|=0.310583  H1 match=True H1' match=False H2 match=False
```

**Answer.**

- **Without making the layout current:** yes. `ViewTarget`, `ViewCenter`, `TwistAngle`
  and `CustomScale` read back exactly as set (difference 0), and
  `ViewHeight = Height / CustomScale` exactly. CTAB stayed `Model` throughout.
- **`On`:** refused (`eNotInPaperspace`) inside the creating transaction. In a later
  transaction it is accepted on an initialized layout and still refused on an
  uninitialized one. It is not persisted: the file and the reopened document both read
  `False`. First activation turns every viewport on, in both sessions.
- **E614, resolved in Core Console:** a viewport built headless in an initialized layout
  displays at first activation with its view intact, including from the saved file in a
  fresh session. Without `Initialize()`, activation takes the plan viewport over as the
  overall viewport (twist 30 became 0, scale 0.02 became 0.778) and adds a default
  viewport. Untested in full `acad.exe` (Core Console only).
- **Clip:** `NonRectClipEntityId` and `NonRectClipOn` are accepted on a non-current layout
  and survive save. At commit the viewport is resized to the clip's bounding box,
  including the arc bulge: centre (8.5, 5.5) became (9.1, 5.5) and 12 × 7 became
  11.2 × 6. `ViewCenter` is shifted to (30, 0) = (0.6, 0) ÷ 0.02, so M stays at
  (8.5, 5.5).
- **Mapping:** H1 holds to 0.000000 in all 8 checks (4 layouts × 2 points), and LISP
  `trans` agrees. That gives
  `paper = vp.CenterPoint + CustomScale × (R(+TwistAngle)·(w − ViewTarget) − ViewCenter)`.
  - **The 5.2 claim is confirmed:** `ViewCenter` is in DCS and rotates with the twist.
    Entering M's WCS numbers as `ViewCenter` (`SV-P12B`) put M at paper
    (−34.897, 47.462), off the sheet. Entering R(+twist)·M (`SV-P12B2`) put M at the
    viewport centre.
  - **AutoCAD's own clip compensation also follows H1:** (30, 0), not H2's
    (25.98, −15).
  - **The 6.6 form is correct.** Twist sign: an east-pointing model direction under
    twist 30° appears at +30° on paper (offset (1.732, 1.0)), so `Twist = −bearing`
    reads the chord left to right.

### P13. AutoCAD Table created and updated in a layout headless

**Question.** Can a `Table` be created in a non-current layout headless, have a cell
updated in a second transaction, and survive save and reopen?

**Ran.** `SVPROBEB_P13` creates a 4 × 3 table in an initialized, non-current layout
`SV-P13` in transaction 1. Transaction 2 writes cell (2,1). Transaction 3 diffs every
cell against the snapshot taken before the write. The save and reopen check is the
`VERIFY` snapshot.

**Output (verbatim).**

```text
P13 table style='Standard' size=4x3 merges after SetSize: [0,0..0,2]
P13 row styles: r0='' r1='' r2=''
P13 title row cell (0,0): IsMerged=True GetMergeRange=[0,0..0,2]
P13 title row cell (0,1): IsMerged=False GetMergeRange=[0,0..0,2]
P13 title row cell (0,2): IsMerged=False GetMergeRange=[0,0..0,2]
P13 covered (non-anchor) cells: 2 [(0,1) (0,2)]
P13 created in transaction 1: handle=733 height=1.5333333333 width=6 CTAB=Model
P13 transaction 2 wrote cell (2,1) and committed
P13 transaction 3 readback: 1 cell(s) changed: (2,1) 'P13 R2C1-' -> 'P13 UPDATED-'
P13 tx3 r2: 'P13 R2C0' | 'P13 UPDATED' | 'P13 R2C2'
VERIFY file SV-P13|Table#733.cell[2,0] = 'P13 R2C0'-
VERIFY file SV-P13|Table#733.cell[2,1] = 'P13 UPDATED'-
VERIFY file SV-P13|Table#733.cell[2,2] = 'P13 R2C2'-
```

An earlier run the same day wrote text into every cell before the covered-cell guard
existed. The covered cells kept their own hidden text:

```text
P13 tx1 r0: 'P13 R0C0'[0,0..0,2] | 'P13 R0C1' | 'P13 R0C2'
```

**Answer.** Yes. Exactly one cell changed in the update, and the value survived save and
reopen. Three facts for A2:

- The `Standard` table style merges row 0 across all columns as soon as `SetSize` runs.
- `Cell.IsMerged` is true only on a merge's anchor. Covered cells report false but
  return the full range from `GetMergeRange()`.
- Text written to a covered cell is accepted silently and stored hidden.
- `Cell.Style` returns an empty string, so row style names are not exposed that way.

Not checked headless: whether the table's display block regenerates (no graphics). The
cell contents are checked.

### P17. Annotation scale on a viewport; adding a missing scale

**Question.** Can an annotation scale be assigned to a viewport, and can a scale missing
from the drawing's list be added first?

**Ran.** `SVPROBEB_P17` lists `ACDB_ANNOTATIONSCALES`, assigns the existing 1:50 scale to
viewport vpE, adds a missing 1:37 scale with `ObjectContextCollection.AddContext`, then
assigns it to vpM. Both are in a non-current initialized layout `SV-P17`. It sets
`CustomScale` and `AnnotationScale` in both orders.

**Output (verbatim).**

```text
P17 scale list: 33 scales; CANNOSCALE='1:1'; current context='1:1'
P17 scales: '1:1' 1:1; '1:2' 1:2; '1:4' 1:4; '1:5' 1:5; '1:8' 1:8; '1:10' 1:10; '1:16' 1:16; '1:20' 1:20; '1:30' 1:30; '1:40' 1:40; '1:50' 1:50; '1:100' 1:100; '2:1' 2:1; '4:1' 4:1; '8:1' 8:1; '10:1' 10:1; '100:1' 100:1; '1/128" = 1'-0"' 0.0078125:12; '1/64" = 1'-0"' 0.015625:12; '1/32" = 1'-0"' 0.03125:12; '1/16" = 1'-0"' 0.0625:12; '3/32" = 1'-0"' 0.09375:12; '1/8" = 1'-0"' 0.125:12; '3/16" = 1'-0"' 0.1875:12; '1/4" = 1'-0"' 0.25:12; '3/8" = 1'-0"' 0.375:12; '1/2" = 1'-0"' 0.5:12; '3/4" = 1'-0"' 0.75:12; '1" = 1'-0"' 1:12; '1-1/2" = 1'-0"' 1.5:12; '3" = 1'-0"' 3:12; '6" = 1'-0"' 6:12; '1'-0" = 1'-0"' 12:12
P17 existing scale for the assign test: '1:50' ratio 50
P17 ratio 37 before add: absent; HasContext('1" = 37'')=False
P17 vpE after assign: annoScale='1:50' customScale=0.02
P17 after add: ratio 37 present as '1" = 37'' scale=0.027027027; HasContext=True; list count=34
P17 vpM order 1 (CustomScale 1/20, then annoScale 1:37): annoScale='1" = 37'' customScale=0.05 (1/37=0.027027027)
P17 vpM order 2 (then CustomScale 1/37): annoScale='1" = 37'' customScale=0.027027027
VERIFY file SV-P17|Viewport#756.annoScale = 1:50
VERIFY file SV-P17|Viewport#757.annoScale = 1" = 37'
VERIFY file SV-P17|Viewport#757.customScale = 0.027027027
VERIFY file scales|1" = 37' = 1:37
```

**Answer.** Yes to both.

- `Viewport.AnnotationScale = scale` works on a non-current layout.
- `AddContext(new AnnotationScale { Name, PaperUnits = 1, DrawingUnits = 37 })` grows the
  list from 33 to 34. The assignment and the added scale survive save and reopen.
- Assigning an annotation scale does not change `CustomScale`, and setting `CustomScale`
  does not change the annotation scale. The writer must set both.
- The fixture's list names civil scales `1:50`, not `1" = 50'`, so look scales up by
  ratio, not by name (E605).

### P20. Layout creation, an aborted transaction, and one UNDO

**Question.** Is layout creation rolled back when its transaction is aborted, and by one
`UNDO`?

**Ran.** `SVPROBEB_P20A` aborts three creation paths:

- `LayoutManager.CreateLayout` inside a transaction;
- Method S (CreateLayout + CopyFrom + Initialize + WblockCloneObjects) in one
  transaction;
- the Method W clone.

`SVPROBEB_P20B` commits all three in one command. The script then runs `_.UNDO` `1`, and
`SVPROBEB_P20C` counts again.

**Output (verbatim).**

```text
P20 before: layoutDictionary=15 LayoutManager.LayoutCount=15 paperSpaceBlocks=14
P20 abort-C inside transaction: layoutDictionary=16 exists=True
P20 abort-C after Abort: exists=False id.IsErased=True id.IsValid=True
P20 after abort-C: layoutDictionary=15 LayoutManager.LayoutCount=15 paperSpaceBlocks=14
P11 S 'SV-P20-ABORT-S': aborted
P20 abort-S after Abort: exists=False id.IsErased=True id.IsValid=True
P20 after abort-S: layoutDictionary=15 LayoutManager.LayoutCount=15 paperSpaceBlocks=14
P11 W: aborted
P20 abort-W after Abort: exists(SV-TEMPLATE)=False id.IsErased=True id.IsValid=True
P20 after abort-W: layoutDictionary=15 LayoutManager.LayoutCount=15 paperSpaceBlocks=14
P20 UNDOCTL=61 (bit 1 = undo enabled)
P20 before committed creates: layoutDictionary=15 LayoutManager.LayoutCount=15 paperSpaceBlocks=14
P20 after committed creates (one command): layoutDictionary=18 LayoutManager.LayoutCount=18 paperSpaceBlocks=17
Command: _.UNDO
Current settings: Auto = On, Control = All, Combine = Yes, Layer = Yes
Enter the number of operations to undo or [Auto/Control/BEgin/End/Mark/Back] <1>: 1
SVPROBEB_P20B
P20 after UNDO 1: SV-P20-UNDO-C exists=False
P20 after UNDO 1: SV-P20-UNDO-S exists=False
P20 after UNDO 1: SV-P20-UNDO-W exists=False
P20 after UNDO 1: layoutDictionary=15 LayoutManager.LayoutCount=15 paperSpaceBlocks=14
```

**Answer.** Yes to both.

- **Abort:** the layout dictionary, `LayoutManager.LayoutCount` and the paper-space block
  table all return to their earlier counts (16 inside the transaction, 15 after). The
  layout's ObjectId reads `IsErased=True`. This holds for all three paths, so a regen
  that fails mid-way and aborts leaves no half-built layout.
- **UNDO:** one `UNDO` reverses a whole command's worth of layout creation, 18 to 15.

### P24. HCFCD table with merged cells: find a row by label, update one cell

**Question.** Can the HCFCD layout be built with merged cells, a row found by its label
text, and one cell updated without disturbing the others, in model space and in a
layout?

**Ran.** `SVPROBEB_P24` builds a 9 × 3 stand-in of the HCFCD table from 6.16 and A13
(bound rows 6 to 9, 1-based). It does this twice: in model space and in an initialized,
non-current layout `SV-P24`.

- The row 6 label carries MTEXT bold formatting, as a user's in-place edit would leave
  it.
- The finder reads column 0 with `GetTextString(FormatOption.IgnoreMtextFormat)` and
  matches the start of the text.
- The value column is the label's merge range `RightColumn + 1`.

Each step is its own transaction, with a full cell and merge diff:

1. Create.
2. Update the area cell only.
3. Bind length and material.
4. A user edit: delete the WETLANDS row and type into the centre-coordinates cell.
5. Bind again.

**Output (verbatim, layout copy; the model-space copy printed the same values).**

```text
P24 layout merges after SetSize (table style default): [0,0..0,2]
P24 layout merges after unmerging all: none
P24 layout created: handle=78F owner=*Paper_Space17 9x3 merges: [0,0..0,2] [1,1..1,2] [2,1..2,2] [3,0..3,2] [4,1..4,2] [5,1..5,2] [6,1..6,2] [7,0..7,2]
P24 layout tx1 r5: 'TOTAL IMPACTED LENGTH (BELOW OHWM)' | 'XX LF'[5,1..5,2] | ''
P24 layout tx1 r6: 'TOTAL IMPACTED AREA (BELOW OHWM)' | 'XX AC'[6,1..6,2] | ''
P24 layout tx1 r7: 'TOTAL CUT MATERIAL'[7,0..7,2] | '' | ''
P24 layout tx1 r8: 'SEDIMENT' | 'XX CY' | 'XX CY/LF'
P24 layout row 5 label reads: TextString='{\fArial|b1|i0|c0|p34;TOTAL IMPACTED LENGTH (BELOW OHWM)}' | GetTextString(FormatOptionNone)='{\fArial|b1|i0|c0|p34;TOTAL IMPACTED LENGTH (BELOW OHWM)}' | GetTextString(IgnoreMtextFormat)='TOTAL IMPACTED LENGTH (BELOW OHWM)'
P24 layout merged non-anchor cell (6,2): TextString='' IsMerged=False
P24 layout update-one: 'TOTAL IMPACTED AREA (BELOW OHWM)' found at row 6; wrote 1 value(s) from column 1
P24 layout update-one: 1 cell(s) changed: (6,1) 'XX AC[6,1..6,2]' -> '1.23 AC[6,1..6,2]'
P24 layout bind: 3 cell(s) changed: (5,1) 'XX LF[5,1..5,2]' -> '1,234 LF[5,1..5,2]'; (8,1) 'XX CY-' -> '567 CY-'; (8,2) 'XX CY/LF-' -> '0.46 CY/LF-'
P24 layout user edit: deleted row 4 (WETLANDS); typed into row 1; now 8 rows; merges: [0,0..0,2] [1,1..1,2] [2,1..2,2] [3,0..3,2] [4,1..4,2] [5,1..5,2] [6,0..6,2]
P24 layout rebind: 'TOTAL IMPACTED LENGTH (BELOW OHWM)' found at row 4; wrote 1 value(s) from column 1
P24 layout rebind: 'TOTAL IMPACTED AREA (BELOW OHWM)' found at row 5; wrote 1 value(s) from column 1
P24 layout rebind: 'SEDIMENT' found at row 7; wrote 2 value(s) from column 1
P24 layout rebind: missing labels=0; 3 cell(s) changed: (4,1) '1,234 LF[4,1..4,2]' -> '1,300 LF[4,1..4,2]'; (5,1) '1.23 AC[5,1..5,2]' -> '1.25 AC[5,1..5,2]'; (7,1) '567 CY-' -> '600 CY-'
P24 layout final r1: 'CENTER COORDINATES' | 'N 13,812,345.67  E 3,101,234.56 (TYPED)'[1,1..1,2] | ''
P24 layout final handle=78F (created as same object, never recreated)
VERIFY P24 Model table 75F: 'TOTAL IMPACTED LENGTH (BELOW OHWM)' row 4 -> '1,300 LF'; 'TOTAL IMPACTED AREA (BELOW OHWM)' row 5 -> '1.25 AC'; 'SEDIMENT' row 7 -> '600 CY' / '0.46 CY/LF'
VERIFY P24 SV-P24 table 78F: 'TOTAL IMPACTED LENGTH (BELOW OHWM)' row 4 -> '1,300 LF'; 'TOTAL IMPACTED AREA (BELOW OHWM)' row 5 -> '1.25 AC'; 'SEDIMENT' row 7 -> '600 CY' / '0.46 CY/LF'
VERIFY file *Model|Table#75F.cell[1,1] = 'N 13,812,345.67  E 3,101,234.56 (TYPED)'[1,1..1,2]
```

**Answer.** Yes, in model space and in a layout.

- **One cell:** updating one bound value changed exactly 1 cell.
- **A full bind:** changed exactly the cells it targeted (3).
- **After the user deleted a row:** the labels were found at their shifted rows (4, 5, 7).
  Merges moved with their rows, the typed centre coordinates were kept, and the bind
  changed only its targets. The material average was rewritten with the same text, so 3
  cells changed, not 4.
- **On the reopened file:** the finder works and every value survived.
- **Formatted labels:** `TextString` returns raw MTEXT codes for a formatted label, so
  only `GetTextString(IgnoreMtextFormat)` finds it.
- **Merges:** the default style's title-row merge has to be undone before applying the
  HCFCD merges.
- **Stand-in:** the HCFCD layout used here comes from 6.16 and A13. Dan's real table
  (section 13) is still open.

### Not covered by S2, for the orchestrator

- `tools/test-headless.ps1` picks the main checkout's build when it runs inside a
  worktree unless `-BuildDir` is given (B.0). The runner belongs to the orchestrator.
- The probe commands print `SV-E190` when a probe step fails at the command boundary.
  The code was chosen for the probes; confirm or reassign it.
- A real LAN DWT (section 13) has not been through P11.
- The HCFCD table in P24 is a stand-in; Dan's table is still open.
- E614 is answered in Core Console only. One manual open of `probeB-out.dwg` in full
  Civil 3D would close it for `acad.exe`.
- The `Number=-1` on viewport 756 in `SV-P17` is unexplained.

## C. Frames, events, modeless UI (S3)

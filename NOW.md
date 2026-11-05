# NOW — SheetVol state of record

Rev 1, 2026-11-05. Newer revs supersede older ones. Facts here win over HANDOFF.md on
what is true; HANDOFF.md wins on method. Every figure is MEASURED unless marked otherwise.

## Position

**Wave 0 done, Gate G0 passed, P16 closed in both hosts (evidence below). Wave 1 in
progress since 2026-11-05: FX, S2, C1, C2, C3, C4, C5 dispatched in parallel, each in a
worktree under `C:\LAN-DT-build\sheetvol-wt\<wp>` on branch `wp/<wp>`; S1 follows the FX
merge because it needs Fixture A.**

## Corrections to HANDOFF.md (Dan or measurement overrides the plan)

| # | Handoff said | Now | Source |
|---|---|---|---|
| X1 | Section 2: host requirement Civil 3D 2026.2.2 or later only | Host floor is Civil 3D **2025.2.7 or 2026.2.2 and later**; both run the .NET 10 runtime. The plugin compiles against the AutoCAD 2025 managed DLLs (assembly version 25.0) so one DLL binds on 2025, 2026 and 2027 hosts. Measured: the DLL compiled against 25.0 loaded in the 2026 host and reported `AcDbMgd: 25.1.0.0` | Dan 2026-11-05 ("2025.2.7 updated to .net 10"); P16 run |
| X2 | Section 11: `SheetVol.sln` | The solution is `SheetVol.slnx`; the .NET 10 SDK's `dotnet new sln` emits the XML format | Wave 0 |
| X3 | Section 11: `accoreconsole ... /isolate svtest ...` | `/isolate` dropped; the run with it hung before reading the script. Working form is in `tools/test-headless.ps1`: `/i /s /product C3D /l en-US`, stdin closed, `LAN_DT_BATCH=1` | Wave 0 |
| X4 | Section 5.1: "Custom code must be in a trusted location" | Confirmed the hard way: `NETLOAD` from `C:\LAN-DT-build` fails with "Unable to load ... assembly" under `SECURELOAD=1`. The runner stages the build into `%APPDATA%\Autodesk\ApplicationPlugins\SheetVol-dev.bundle\Contents\` and scripts load it via `(getenv "SHEETVOL_DLL")` | Wave 0 |

## Platform on this seat (MEASURED 2026-11-05)

| Item | Value |
|---|---|
| Civil 3D 2026 | 2026.2.3 Update, 13.8.1873.0, installed 2026-10-30. AcDbMgd/AcCoreMgd/AcMgd 25.1.179.0, AeccDbMgd 13.8.0.1873 |
| Civil 3D 2025 | AcDbMgd 25.0.189.0 (.NET 10 since 2025.2.7). Reference set for the build |
| Civil 3D 2027 | Installed at `C:\Program Files\Autodesk\AutoCAD 2027` (R26.0). Not built against; `C3D2027` configuration exists in the csproj files, untested |
| .NET SDK | 10.0.204; runtime in the 2026 host reports .NET 10.0.14 |
| AecBaseMgd | Lives in `<install>\ACA\`, not `<install>\C3D\` (handoff listed it without a folder) |
| Registry keys | 2025 `R25.0\ACAD-8100:409`, 2026 `R25.1\ACAD-9100:409`, 2027 `R26.0\ACAD-A100:409`; `Directory.Build.props` reads `AcadLocation` from them |
| Stock templates | No Civil 3D NCS `.dwt` on this seat. `tools/fixtures/blank-c3d.dwg` was saved by the 2026 Core Console from `UserDataCache\Template\map2d.dwt` (Autodesk stock, 148 KB). S1 may replace it |

## Gates

### G0 — PASSED 2026-11-05

`dotnet build .\SheetVol.slnx -c Debug`:

```text
    5 Warning(s)
    0 Error(s)
```

The five warnings are one MSB3277: the Autodesk DLLs reference Microsoft.VisualBasic
10.1.0.0 and the SDK ships 10.0.0.0. Benign, left visible on purpose so a real
reference conflict is not hidden later.

`dotnet test .\tests\SheetVol.Core.Tests\SheetVol.Core.Tests.csproj`:

```text
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2
```

**P16, headless half — PASSED.** `tools\test-headless.ps1 -Script tools\scripts\p16.scr`
against the 2026 Core Console printed:

```text
SVHELLO ok. SheetVol.Civil3D 0.1.0.0 tfm=.NETCoreApp,Version=v10.0
Runtime: .NET 10.0.14  Process: C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe
AcDbMgd: 25.1.0.0  AeccDbMgd: 13.8.0.1873
Core Console: True
CivilApplication.ActiveDocument: ok
```

The AECC modules (Base Core, Pipe Network, Roadway, Plan Production, ...) loaded with
`/product C3D`, and `CivilApplication.ActiveDocument` resolved. This answers the
`accoreconsole` part of P16 and the "can AeccDbMgd types be used headless" premise of P9;
S1 still owns the rest of P9 and P10.

**P16, acad.exe half — PASSED (Dan, 2026-11-05).** In full Civil 3D 2026 Dan NETLOADed
the staged DLL and ran `SVHELLO`:

```text
SVHELLO ok. SheetVol.Civil3D 0.1.0.0 tfm=.NETCoreApp,Version=v10.0
Runtime: .NET 10.0.14  Process: C:\Program Files\Autodesk\AutoCAD 2026\acad.exe
AcDbMgd: 25.1.0.0  AeccDbMgd: 13.8.0.1873
Core Console: False
CivilApplication.ActiveDocument: ok
```

P16 is closed: the net10.0-windows assembly loads in both hosts.

`tools\check-contracts.ps1`: `contracts.md matches Contracts.cs`.

### G1, G2, G3 — not started

## Decisions made in Wave 0

- **Contracts** (`src/SheetVol.Core/Contracts.cs`, twin `docs/contracts.md`): section 6.2
  verbatim; `ISheetVolSession` from 6.14; new in Wave 0: `RunDefinition` (holds
  `KnownSegments` so the planner can carry GUIDs forward by the 6.3 identity rules, plus
  per-segment `SegmentOverrides` for A14 target layout, scale override and E608 LockView),
  `ValidationResult` (segments plus boundaries plus problems, so the dialog can show every row
  with its own error), `RegenResult` (per segment-and-analysis rows, outside-limits row,
  partition flag, export paths), `AnalysisDef`, `ManualSegmentDef`, `ViewOptions`, `Problem`
  with the `SV-Exxx` code and a plain-words `Fix`. Enum defaults carry A2, A3, A4 and the
  6.5 area-unit default.
- **Headless runner** `tools/test-headless.ps1` follows the Sheet Set Editor's proven
  `AccoreConsole.RunScript` form. Environment handed to scripts: `SHEETVOL_DLL` (staged DLL,
  forward slashes), `SHEETVOL_REPO` (repo root, forward slashes). A path with spaces cannot
  sit on a script line, so scripts build paths in LISP with `strcat`.
- **Folder ownership** is written into a `README.md` in every `src/` folder.
- **Rejected:** suppressing MSB3277 (hides future real conflicts); seeding
  `blank-c3d.dwg` from a LAN NTX template (firm content into a public repo, and stop
  condition 12.8 territory).

## Coverage table

Filled as workers return. Status: TODO / IN PROGRESS / GREEN / MANUAL-OK / BLOCKED.

| Scope | Rows | Status |
|---|---|---|
| E1xx Platform | E101–E105 | E101 manual: dev seat verified; E105 GREEN (C2); E102–E104 TODO |
| E2xx Geometry | E201–E219 | TODO (C1) |
| E3xx Segments | E301–E312 | TODO (C1) |
| E4xx Volumes | E401–E421 | U rows GREEN (C2: E405c, E408c, E409, E410c, E411c, E413, E414, E418, E420, E421c; c = Core half); H rows TODO (A1, A4) |
| E5xx Tables | E501–E508 | E508 GREEN (C2); E505 Core half GREEN; H rows TODO (A2, A3) |
| E6xx Layouts | E601–E615 | U rows GREEN (C3: E603, E606, E607, E610; E309, E310 too); H/M rows TODO (A3) |
| E7xx Frames | E701–E713 | TODO (L1) |
| E8xx Regen | E801–E812 | Logic-level GREEN (C3: E804–E806, E811); H/M rows TODO (A4, L2) |
| E9xx Dialog | E901–E917 | U rows GREEN (C4: E902, E903, E904, E907, E912u, E914, E916, E917; logic for E901, E905, E906, E911 too); M rows TODO (U2, Dan) |
| E10xx OHWM | E1001–E1021 | TODO (C5, A1, A2) |
| T1–T31 | | T15, T16, T17 GREEN; T14 fingerprint part and T19 logic part GREEN (C3); T11, T23 GREEN (C2); T22 GREEN (C4); rest TODO |
| P1–P24 | | P16 PASSED both hosts; P11, P12, P13, P17, P20, P24 ANSWERED headless (S2, docs/api-findings.md §B); P1–P10, P18, P19, P22, P23 TODO (S1); P14, P15, P21 TODO (S3) |

## Open items for Dan (HANDOFF.md section 13)

| Item | Status |
|---|---|
| Confirm dev seat at 2026.2.2+ | DONE: 2026.2.3 |
| P16 in full Civil 3D (`SVHELLO`) | DONE 2026-11-05 |
| Four dialog artboards as PNG in `docs/ui/` | OPEN, before Wave 2 |
| HCFCD table as it should look in CAD, plus a sample drawing with real surfaces and an OHWM elevation | OPEN, before Wave 2 |
| Confirm A10 (what "below OHWM" means) and A12 (acre decimals) | OPEN, before Wave 2 |
| View-frame fixture drawing plus a data-referencing second drawing | OPEN, during Wave 2 |
| Run `SVPROBEUI` once | OPEN, during Wave 2 |
| LAN sheet template with `SV-VPORT` viewport and table anchor block; table style and layer names | OPEN, before G3 |
| Corrections to section 3 assumptions | none yet |

## Merge log

| When | Branch | Commit | Gate re-run by orchestrator |
|---|---|---|---|
| 2026-11-05 | wp/c3 (C3 views + tracking) | 83d5ec5 | build 0 errors; `dotnet test` 109/109 on main; diff confined to Views/, Tracking/ and their tests |
| 2026-11-05 | wp/s2 (S2 probes B) | eba01b0 | build 0 errors; probeB.scr re-run by orchestrator in the s2 worktree, exit 0, P20 undo evidence seen in the log (18 → 15 layouts); commit confined to ProbeB*.cs, probeB.scr, api-findings.md |
| 2026-11-05 | wp/c2 (C2 volumes + output) | 23990f6 | build 0 errors; `dotnet test` 152/152 in the c2 worktree and on main; commit confined to Volumes/, Output/ and their tests; no TODO/FIXME/MUTATION residue |
| 2026-11-05 | wp/fx (FX fixtures) | 6c69e2b | build 0 errors; fixtureA.scr re-run by orchestrator in the fx worktree: EG 2 tri, FG_A 52 tri, volume surface 68 tri, UnadjustedCut 5400.000000000002 UnadjustedFill 5400; commit confined to SelfTest/Fixture*.cs and tools/scripts/fixture*.scr. fixtureA.dwg left untracked (SVFIXTURE A rebuilds it in 6 s) |
| 2026-11-05 | wp/c4 (C4 presentation) | b2857c3 | build 0 errors; `dotnet test` 71/71 in the c4 worktree, 328/328 on main after merge; commit confined to Presentation/ and its tests; no WPF or Autodesk types (ICommand is BCL) |

## Decisions pending Dan (raised by workers, not yet ruled)

| # | From | Question | Default in code until ruled |
|---|---|---|---|
| D1 | C3 | Should the viewport clip include the `ViewOverlap` strip, or the boundary only? | `ViewMathConfig.ClipIncludesOverlap = true` (overlap visible) |
| D2 | C3 | "Largest scale that fits" in FitStandard: read as the most detailed scale (smallest model units per paper unit). T16 passes either way | most detailed |
| D3 | C3 | Problem message wording lives in code; only number formats are in config. Does the settings-file rule cover message text? | wording in code |
| D4 | C3 | Fingerprint covers more than 6.11 lists (analyses, view options, overrides, preset, area unit, OHWM, displayed end stations, manual polyline geometry); excludes `KnownSegments` and `AutoMode` | as described |
| D5 | C4 | `IDialogHost` (in Presentation/) holds what `ISheetVolSession` lacks: drawing name and unit system, `FormatStation`, alignment station range, `GetRunStatus` (tracker staleness + last `RegenResult`), `IsCommandActive`, `RequestExport`. Either U2 implements it in UI/Host or these move into the contract at the G1 contract review | U2 implements `IDialogHost` |
| D6 | C4 | Draw options (boundary polylines, table at each segment, summary table) have no home in `RunDefinition`; they never reach the engine | dialog-only until the contract review |
| D7 | C4 | Does the cut factor apply to HCFCD row 9? Dialog shows unfactored `ImpactResult.CutVolume`; C2 defaulted `ImpactVolumesUseCutFactor = true`. The two disagree | needs one answer at G1 |
| D8 | C4 | Per-segment offset fix beside a failing row (6.14) has no target: `SegmentOverrides` has no offset field | text only |

## Facts for later workers (from merged packages)

### From C3

- Twist sign convention is paper = Rotate(model − center, twist) / scale; P12 (S2) must confirm AutoCAD's `TwistAngle` sign and A3 absorbs any difference. `ViewSpec.PaperWidth/Height` is the template viewport size fitted to, not the required size (`ViewResult.RequiredPaperWidth/Height`); A3 never resizes the viewport. Display polygon enters through `IDisplayPolygonSource`; A4 adapts C1's boundary builder. Auto-update wiring (L2) must raise `ToolCommandRunning` for dialog-started regens and pass CommandCancelled/CommandFailed as `CommandEnded`. Codes used: SV-E309, E606, E601, E304, E202, E603(Info). Test fakes `C3Alignment`/`C3BandSource` to consolidate with C1's `Fakes/` after C1 merges.

### From S2 (full detail with verbatim output in `docs/api-findings.md` §B)

- P11: clone a layout as `CreateLayout` → `CopyFrom` → `Layout.Initialize()` → `WblockCloneObjects` of the paper-space entities, skipping the source's overall viewport; plot settings survive save and reopen. Do not clone the whole `Layout` object (keeps the source tab order and name).
- P12: `paper = vp.CenterPoint + CustomScale · (R(+TwistAngle)(w − ViewTarget) − ViewCenter)`; matches `acedTrans` in 8 of 8 checks. 5.2's claim confirmed: `ViewCenter` is in display coordinates; section 6.6's `Twist = −bearing` reads the chord left to right. **`Layout.Initialize()` before adding viewports** prevents AutoCAD from converting the plan viewport into the overall viewport on first activation (E614 resolved headless). `On` cannot be set in the creating transaction and is not saved; first activation turns viewports on. A non-rectangular clip resizes the viewport to the clip's bounding box and moves `ViewCenter`, so a regen must not re-apply `ViewCenter = (0,0)` to a clipped viewport (E608). `Locked` does not block API edits (E604).
- P13: table in a non-current layout, cell updated in a second transaction, survives reopen. `Cell.IsMerged` is true only on the merge's top-left cell; text written to a covered cell is stored hidden without error.
- P17: `AddContext` adds a scale; `Viewport.AnnotationScale` assigns it; annotation scale and `CustomScale` are independent, so writers set both; look scales up by ratio, not name.
- P20: aborted transaction rolls back layout creation on all three paths; one `_.UNDO 1` removes all layouts one command made. 6.10's two-transaction fallback is not needed.
- P24: find rows by `Cell.GetTextString(FormatOption.IgnoreMtextFormat)` (raw `TextString` carries MTEXT codes); labels still found after the user deletes a row; works in model space, a layout, and after reopen.
- Open from S2: `SV-E190` chosen as the probe failure code; Dan's real DWT and HCFCD table untested (P11, P24 used code-built stand-ins); E614 and the `On` behaviour unchecked in full Civil 3D (one manual open of `%TEMP%\sheetvol\probeB-out.dwg` closes it); `CVPORT` cannot be set from .NET in Core Console, scripts use `_.MSPACE`; `_.PSPACE` on the Model tab aborts a script; `(layoutlist)` does not exist in Core Console.
- Runner fix after S2's finding: `tools/test-headless.ps1` now asks MSBuild for `OutDir`, so a worktree stages its own in-tree DLL (MEASURED: main → `C:\LAN-DT-build\...`, worktree → `bin\Debug\...`).

### From C2

- Error rows carry NaN in every number with `Status = Error`; C4 must not display numbers for them. Any negative raw value from `IVolumeSource`, even −1e-13, is SV-E432: A1 cleans sign noise. Band polygons passed to the reconciler exclude skip gaps so a gap's volume lands in outside-limits (E303). Text height in config is paper units; model-space writers scale it. `ReconcileResult` carries outside-limits per analysis while `RegenResult.OutsideLimits` is one `RawVolume?`: A4 decides what goes in `RegenResult` or the contract changes before Wave 1 closes (candidate for stop condition 12.6 review at G1).
- **csproj line owed by the orchestrator (done at this merge if it builds):** `Output/Templates/hcfcd-segment-table.json` must be copied to the output; U1's bundle must ship `Templates\`. Config default `hcfcd.templatePath` resolves against the plugin directory.
- Assumed settings listed in `ReportConfig.AssumedSettings`: `TotalIncludesOutsideLimits=false` (A6), `ImpactVolumesUseCutFactor=true`, `VolumeZeroTolerance=0.01` CY or m³, coverage flag below 1 − 1e-6 and error above 1 + 1e-4, acre decimals 2 (A12 open), a net that rounds to 0 shows `0 CY` with no tag, HCFCD rows 6 and 7 take the first material's length and area (SV-E538 when others differ), HCFCD JSON is a placeholder layout (SV-E532 Info on load).
- Not built: the 6.5 cross-check that the datum-method area agrees with the integrator's area (no tolerance given; sits between C5 and A4).

### From FX

- `Fixtures.CreateA/B/D/H(db)`, `CreateAlignmentS/C/K/SP(db)`, `CreateTemplate(db)`, `WriteTemplateDrawing(path)`, `CreateVolume(db, base, comp)`, `CreateFixtureVolumes(db)`; expected values in `FixtureSpec.Expected`; commands `SVFIXTURE <A|B|D|H|S|C|K|SP|TEMPLATE|ALL|VOLUMES|TEMPLATEDWG path>` and `SVFIXTUREREPORT`. Scripts `tools/scripts/fixtureA.scr`, `fixtureAll.scr`, `fixtureReport.scr`, `fixtureTemplate.scr`, `fixtureTemplateCheck.scr`.
- **Whole-surface sign (MEASURED on Fixture D):** `UnadjustedFillVolume` = comparison above base; Net = Fill − Cut; cubic drawing units (feet). Bounded-volume sign (P1) is still S1's.
- Choices where section 8 left room: K = 30° deflection turning right, tangents (0,0)→(500,0)→(759.81,−150); SP = PI (500,0), left 30°, R 500, clothoids 100 each, out-tangent 300; C = tangents meeting at a PI with a free curve. Fixture B's whole-surface fill is 88,000 ft³ for its triangulation (prism 66,000 plus ridge-end fans): T5's per-500-ft 555.556 CY still holds inside ±100 bands away from the ends.
- API behaviour measured (inputs to P9, P12, P23): adding the EG corners with `AddVertices` as an edit left FG_A non-convex (42 triangles, "Point Outside Surface"); as perimeter breaklines it is right. Civil 3D hid 2 of FG_H's 36 triangles on its own (177–178° slivers, +y side only); collinear vertices removed them (E1013, P23). `Viewport.On = true` throws `eNotInPaperspace` unless the layout is current. AutoCAD adopts the first viewport as the overall viewport on first activation (S2's `Layout.Initialize()` finding is the fix; FX worked around it by activating first). Side-database layouts: activation does nothing until the previous layout is restored, then AutoCAD adds a default floating viewport; the written file's plan viewport scale drifted 0.02 → 0.014712 on first activation after reopen (cause not investigated; for T18 use TEMPLATE in an open drawing then SAVEAS). `PointLocation` negative offset = left (+y on S). `blank-c3d.dwg` has no `_No Labels` label set; first label set used.

## Problem-code registry

| Range | Owner | Meaning |
|---|---|---|
| SV-E1xx | FX, probes | SV-E190 probe step failed (S2); SV-E191–E193 fixture build failures (FX) |
| SV-E2xx, E3xx | C1 | planning and geometry rows (E201, E202, E205, …, E304, E305 …) |
| SV-E4xx | C2, A1 | E401, E405, E410, E411, E419 as rows; E430–E441 volumes without a row (E430 partition fail, E431 partition skipped, E432 negative raw, E438 area above boundary, E439 negative outside-limits) |
| SV-E5xx | C2, A2, A3 | E530–E538 output without a row (E532 placeholder template, E538 material length/area differ) |
| SV-E6xx | C3, A3 | E601, E603 (Info), E606, E309 (does not fit) |
| SV-E10xx | C5, A2 | E1015 row binding |

### From C4

- Labels in `DialogText` constants for `{x:Static}`; formats, unit factors, defaults in `PresentationConfig`. Rows show numbers only while GUID, station range, offsets, analysis and OHWM still match the last regen; totals hidden until every row has a result. Grid edits rewrite the break list and go through `Validate`; a run-level error with no `SegmentId` becomes an inline cell error and the last valid break list is kept. **C1 must confirm:** unedited breaks are written back as raw station numbers (ambiguous when station equations exist), and an unparseable or out-of-range station must come back as an Error with no `SegmentId`. Checkbox "Regenerate when..." = `AutoMode != Off`; Add station splits at the midpoint; Remove merges into the left neighbour; offsets shown as distances (left positive means `OffsetLeft = −value`); pending edits keyed by drawing, restored only when the run name matches; edits refused and Close disabled during a regen.

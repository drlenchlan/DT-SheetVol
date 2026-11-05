# NOW — SheetVol state of record

Rev 1, 2026-11-05. Newer revs supersede older ones. Facts here win over HANDOFF.md on
what is true; HANDOFF.md wins on method. Every figure is MEASURED unless marked otherwise.

## Position

**G0 and G1 passed 2026-11-05 (evidence under Gates). Contract revision 2 is merged on main (stop condition 12.6 applies from here). Wave 2 in progress since 2026-11-05: A1, A2, A3, S3 dispatched from main in worktrees `C:/LAN-DT-build/sheetvol-wt/<wp>`; A4, U2 and a revision-2 follow-up (Planner reads per-segment offsets; fingerprints cover Draw and offset overrides) dispatched from main after the merge.**

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
| E2xx Geometry | E201–E219 | U rows GREEN (C1: E201–E211, E214c, E215, E216, E218); H rows TODO (A1, A4: E212, E213, E217); E219 M |
| E3xx Segments | E301–E312 | U rows GREEN (C1: E301–E308 planning level, E311c, E312; E309, E310 by C3); H halves TODO (A2, A3, A4) |
| E4xx Volumes | E401–E421 | U rows GREEN (C2: E405c, E408c, E409, E410c, E411c, E413, E414, E418, E420, E421c; c = Core half); H rows TODO (A1, A4) |
| E5xx Tables | E501–E508 | E508 GREEN (C2); E505 Core half GREEN; H rows TODO (A2, A3) |
| E6xx Layouts | E601–E615 | U rows GREEN (C3: E603, E606, E607, E610; E309, E310 too); H/M rows TODO (A3) |
| E7xx Frames | E701–E713 | TODO (L1) |
| E8xx Regen | E801–E812 | Logic-level GREEN (C3: E804–E806, E811); H/M rows TODO (A4, L2) |
| E9xx Dialog | E901–E917 | U rows GREEN (C4: E902, E903, E904, E907, E912u, E914, E916, E917; logic for E901, E905, E906, E911 too); M rows TODO (U2, Dan) |
| E10xx OHWM | E1001–E1021 | U rows GREEN (C5: E1001–E1011, E1019, E1020); H rows TODO (A1, A2, A4: E1012–E1018, E1021) |
| T1–T31 | | T15, T16, T17 GREEN; T14 fingerprint part and T19 logic part GREEN (C3); T11, T23 GREEN (C2); T22 GREEN (C4); T25–T30 GREEN on generated triangles (C5); T7, T8, T9, T10 Core half GREEN (C1); rest TODO |
| P1–P24 | | ANSWERED with evidence: P1–P5, P7–P13, P16–P20, P22 (docs/api-findings.md §A, §B). PARTLY: P6 (data-shortcut reference needs Dan's fixture pair; steps in §A.9), P23 (reference and corridor surfaces not measurable headless). TODO: P14, P15, P21 (S3, Wave 2) |

## Open items for Dan (HANDOFF.md section 13)

| Item | Status |
|---|---|
| Confirm dev seat at 2026.2.2+ | DONE: 2026.2.3 |
| P16 in full Civil 3D (`SVHELLO`) | DONE 2026-11-05 |
| Four dialog artboards as PNG in `docs/ui/` | OPEN, before Wave 2 |
| HCFCD table as it should look in CAD, plus a sample drawing with real surfaces and an OHWM elevation | OPEN, before Wave 2 |
| Confirm A10 (what "below OHWM" means) and A12 (acre decimals) | OPEN, before Wave 2 |
| View-frame fixtures `frames-src.dwg`, `frames-sheets.dwg`, `frames-ref.dwg` per `docs/dan-probe-steps.md` step 2 (no LAN templates) | OPEN, needed for P14 and L1 |
| Run `SVPROBEUI` once, then the P15 interactive pass, per `docs/dan-probe-steps.md` (about 25 minutes) | OPEN, steps written 2026-11-05 |
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
| 2026-11-05 | wp/c5 (C5 integrator) | a410702 | build 0 errors; `dotnet test` 94/94 in the c5 worktree, 420/420 on main after merge; commit confined to Volumes/Integrator/ and its tests; no Autodesk references, no residue |
| 2026-11-05 | wp/c1 (C1 planning + geometry) | d601c47 | build 0 errors; `dotnet test` 130/130 in the c1 worktree, 548/548 on main after merge; diff confined to Planning/, Geometry/, tests and Fakes/; no residue |
| 2026-11-05 | wp/s1 (S1 probes A) | 7541b16 | build 0 errors; probeA.scr re-run by orchestrator in the s1 worktree, exit 0, 38 s: T1 and T2 PASS on all 8 segments, partition diff 0, band 200/200 CY, outside-limits 0; diff confined to ProbeA*.cs, probeA*.scr, api-findings.md §A, test-headless.ps1 (header + opt-in -ExtraArgs) |
| 2026-11-05 | contract/rev2 + wp/rev2-adopt (revision 2) | 8e09bcb | build 0 errors; `dotnet test` 558/558 on the adoption branch and on main; adoption confined to Presentation/, Output/, Volumes/ and tests; schema 1 JSON loads as schema 2 |
| 2026-11-05 | wp/rev2b (revision 2 follow-up) | fa43108 | build 0 errors; `dotnet test` 586/586 on the branch and on main; Planning/, Geometry/, Tracking/ and tests only; C3Fakes.cs gone, one Fakes/ set |
| 2026-11-05 | wp/a2 (A2 model writers) | 1c765e4 | build 0 errors; writersModel.scr re-run by orchestrator in the a2 worktree, exit 0, 5 s, 55 PASS lines; diff confined to WritersModel/ and writersModel.scr |
| 2026-11-05 | wp/s3 (S3 probes C) | 24ecd5d | build 0 errors; probeC-events.scr re-run by orchestrator in the s3 worktree, exit 0, 5 s, "0 public event(s)" and "Idle one-shot fired 0 time(s)" reproduced; diff confined to ProbeC*.cs, UI/Probe/, probeC*.scr, api-findings.md §C, dan-probe-steps.md |

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
| D9 | C5 | Noise snapping on the impact path only: a depth within a per-triangle forward error bound τ = 64·ε·(|z| + κ·|∇z|·(L + W)) of zero is treated as 0, and a base elevation within τ of OHWM as OHWM. Without it, rotated or differently-gridded surfaces counted 1e-13 noise as cut (T25 area 19,094 instead of 16,000). Band totals never adjusted. Goes beyond the literal 6.15 text; Dan should accept or reject | snapping on |
| D10 | C5 | A triangle source with nothing in the window is SV-E403 Error (no numbers); two surfaces that do not overlap inside the boundary is SV-E405 Warning with zeros. A4 decides whether one E403 fails the whole run: a design surface covering part of the alignment raises it on every segment it does not reach | per-segment error, run continues |
| D11 | C1 | `MinSegmentLength` has no default in the handoff; C1 used 0 (every remainder kept and reported) | 0 |
| D12 | C1 | A break inside the inside-mitre setback of an angle point, and a non-tangent join at a curve, are E205 errors rather than mitres. Overlap test includes adjacent pairs | errors |
| D13 | A2 | The impacted-area outline below OHWM is approximate: `ImpactResult` carries station intervals, not polygons, so the outline is the band between impacted stations at full width. A true outline needs C5 to return piece polygons (contract change, 12.6) | approximate outline |
| D14 | A2 | Missing configured layers (boundary, outline, match) fail the run under the E501 rule rather than being created; a drawing not started from the LAN template fails until they exist | fail, no create-missing switch |
| D15 | rev2b | When the run's own offsets are invalid (run-level E210), a segment with a usable override still builds; the run is invalid anyway | builds |
| D16 | rev2b | Where every neighbour has different offsets the partition check degrades to "each segment counted once" (each band piece is one segment). A stepped union ring per stretch would be stronger | per-piece check |

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
| SV-E2xx, E3xx | C1 | planning and geometry rows (E201, E202, E205, …, E304, E305 …); SV-E300 = spec syntax, break limit, no segments, ViewFrames driver not planned, repeated known ID |
| SV-E4xx | C2, A1 | E401, E405, E410, E411, E419 as rows; E430–E441 volumes without a row (E430 partition fail, E431 partition skipped, E432 negative raw, E438 area above boundary, E439 negative outside-limits) |
| SV-E5xx | C2, A2, A3 | E530–E538 output without a row (E532 placeholder template, E538 material length/area differ) |
| SV-E6xx | C3, A3 | E601, E603 (Info), E606, E309 (does not fit) |
| SV-E10xx | C5, A2 | E1015 row binding |

### From C4

- Labels in `DialogText` constants for `{x:Static}`; formats, unit factors, defaults in `PresentationConfig`. Rows show numbers only while GUID, station range, offsets, analysis and OHWM still match the last regen; totals hidden until every row has a result. Grid edits rewrite the break list and go through `Validate`; a run-level error with no `SegmentId` becomes an inline cell error and the last valid break list is kept. **C1 must confirm:** unedited breaks are written back as raw station numbers (ambiguous when station equations exist), and an unparseable or out-of-range station must come back as an Error with no `SegmentId`. Checkbox "Regenerate when..." = `AutoMode != Off`; Add station splits at the midpoint; Remove merges into the left neighbour; offsets shown as distances (left positive means `OffsetLeft = −value`); pending edits keyed by drawing, restored only when the run name matches; edits refused and Close disabled during a regen.

### From C5

- Entry points and codes in `src/SheetVol.Core/Volumes/Integrator/README.md`. Boundary is ear-clipped once (concave boundaries correct); pieces whose depth changes sign are split at d = 0 before exact linear integration. Station-of-point: closed form on lines and arcs, bracketed Illinois iteration through `PointAt` on spirals (each spiral lookup costs about length/1 ft samples plus 10–20 `PointAt` calls: A1's adapter speed matters). Boundary validation and ear clipping are O(n²) in vertex count: fine per segment, not for a 10,000-vertex whole-alignment polygon.
- Fixture generators are public `ITriangleSource`s (`Integrator/Fixtures/FixtureSurfaces.cs`) with the exact triangulation in XML docs. **FX and C5 must agree on triangulation:** EG_H on FG_H's grid, transition diagonal from (1300, ±10) to (1310, ±16) gives T28 = 588.148 CY; the other diagonal gives 586.667 CY (both inside the handoff band). FX built H independently (12 / 48 triangles, cut 67,860 ft³ whole surface) while C5's whole-band cut is 67,880 ft³ over [0, 2000] ±100: the 20 ft³ difference is to be reconciled at G1 (likely the hidden sliver triangles FX reported, E1013).
- Measured: Fixture H full extent in 1.6 ms (48 + 48 triangles); 1-ft cells (31,856 + 31,856) in 379 ms, same results. T7 on the arc gives the polygon area 62,831.393 (analytic 62,831.853 differs by 7e-6 from tessellation at 0.01 mid-ordinate).
- Search-limit edge detection unverified for a break exactly on a mitred angle point (E205); raises a Warning that the check did not run rather than skipping silently. Fakes `C5Alignment`/`C5Boundaries` to consolidate with C1's.

### From C1

- Entry: `new Planner(PlanningOptions.ForUnitScale(unitsPerFoot)).Validate(run, alignment, manualOutlines?)` → frozen `ValidationResult`; `Plan()` → `PlanResult` (band pieces, overlaps, dropped GUIDs, station list, partition-check applicability and skip reason). Manual segments are not in `ValidationResult.Segments` (a `SegmentDef` needs stations); their boundaries are in `Boundaries` keyed by the manual segment's ID, so consumers match by `SegmentId`, never by position. A1/A4 read the manual polyline and pass it as `manualOutlines`; missing = E311 "erased".
- **ViewFrames runs are not planned in Core** (E300 error). Frame station ranges must be added to `Core/Planning` in Wave 3 (L1 owns only `Civil3D/Frames`): orchestrator to assign.
- `KnownSegments` must be the last successfully applied plan; persist only after a successful regen (an invalid E211 plan saved then restored loses identities).
- Tessellated polygon area is systematically low by about 3e-5 on curves (vertices on the curve); Fixture D through the API will show the same shortfall; T7 came out at 62,829.758 ft² (C5's integrator got 62,831.393 on its own tessellation; both inside 2e-4).
- Settings for A4's config file: unit scale, MidOrdinate 0.01 ft, MitreLimitDeg 150, MinSegmentLength 0, SegmentLabelFormat `{0}`, MaxBreaks 10,000. New code SV-E300 (spec syntax, break limit, no segments, ViewFrames driver, repeated known ID) added to the registry below.
- Fixture readings differ from FX: C1 read K as a 30° LEFT deflection and chose SP = tangent 200, spiral 150, arc R 500 L 200, spiral 150, tangent 200 turning right; FX built K turning RIGHT and SP as PI (500,0) left 30°, R 500, spirals 100. Core tests use C1's fakes, headless tests use FX's drawings, so nothing is wrong yet, but T10 compares the API volume with the Core polygon area on K and SP: A4's self-test must build the Core fake from the FX alignment's actual `AlignSeg`s through A1's adapter, not from the hand-written fake.
- Measured: 464 segments and 3,305 stations planned in 54 ms. Independent Opus review found and C1 fixed: `START..END/2147483647` overflow loop, per-entry break limit, lopsided identity on a trimmed start, throws on repeated known ID.

### From S1 (full detail with verbatim output in `docs/api-findings.md` §A)

- **P1 sign (MEASURED):** `SurfaceVolumeInfo.Cut` is real cut (P1 polygon Cut=2700 Fill=0; P2 polygon Cut=0 Fill=1350). API `Net` = Fill − Cut (P1 Net=−2700); the handoff's A5 displays Net = Cut − Fill, so A1 flips the sign once, in the adapter. On a plain `TinSurface` (not a volume surface) `GetBoundedVolumes` measures against datum 0.
- **P2:** cubic drawing units. Civil drawing unit is Feet while `INSUNITS` is Inches in `blank-c3d.dwg`: A1 reads the Civil 3D drawing-settings unit, never `INSUNITS`.
- **P3:** bounded results are raw; `CutFactor`/`FillFactor` (both settable) only affect `GetVolumeProperties().Adjusted*`. C2's "apply factors once in Core" stands.
- **P4:** partly outside → only data inside counts; wholly outside → 0/0/0, no exception; hide-boundary hole matches analytic to 4e-12; unclosed or 3-point polygon throws `ArgumentException`. **P5:** winding and start vertex do not matter.
- **P7:** sub-entities via `SubEntityCount` and `[j]`; SCS is one entity with 3 sub-entities; two fixed compound arcs are 2 entities; `Clockwise = true` means right turn; directions are azimuths (A1 converts to radians CCW from +X per the contract). Analytic vs `PointLocation` max difference 4.6e-13 across S, C, K, SP. `EntityBefore`/`EntityAfter` throw at the ends; `ReverseCurve` throws; `GreaterThan180` throws on fixed arcs.
- **P8:** `GetStationStringWithEquations` raw → displayed only; no displayed → raw method (C1's `TryParseStation` does it in Core via the equation list). Ambiguity is real: displayed 9+00 maps to raw 900 and raw 1100 with identical strings; a displayed station can fall in a gap (E208 rejection path).
- **P9:** TIN, volume surface and alignment all create headless with the runner's switches; `/product C3D`, prior `NETLOAD AeccDbMgd.dll` and `/ld AecBase.dbx` all unnecessary on this seat (assumed risk: demand-load registration may differ on another seat; rerun `probeA-p9*.scr` there if `CivilDocument` is ever null).
- **P10:** `CivilDocument.GetCivilDocument(db)` and `CivilApplication.ActiveDocument` both work in Core Console; `GetCivilDocument` also works on `ReadDwgFile` side databases without switching `WorkingDatabase`.
- **P18 (important for 6.10 step 3):** `IsOutOfDate` stayed False through every edit; TIN edits apply at once; **the owned volume surface keeps stale totals until `Rebuild()`** (88,000 → 121,000); with `AutoRebuild` it updates only after the command ends. Rule: rebuild the owned volume surface on every regen, never trust `IsOutOfDate` on it. Sign noise of 5.4e-11 appeared in Fill after a hole (C2's SV-E432 threshold must tolerate it: A1 clamps |x| < 1e-6 to 0).
- **P19:** `Autodesk.Civil.PointNotOnEntityException`, "Point Outside Surface.", same outside the hull and inside hidden areas.
- **P22:** `|Net(0) − Net(1)|` equals the polygon area to 5.8e-16 relative (62,829.8115 ft² on C1's tessellation); the datum method for 2D area is confirmed. Off-edge band matches the analytic clipped area to 4e-16. Volumes Dashboard comparison remains Dan's.
- **P23:** `GetTriangles(false)` / `Triangles` = visible; `GetTriangles(true)` adds hidden ones flagged `IsVisible = false`; hide and outer boundaries hide triangles, masks do not; about 1M triangles read in 1.05–1.48 s, about 110 MB. Reference and corridor surfaces: not measurable headless (Dan's fixture pair; stop condition 12.9 not triggered, open).
- `blank-c3d.dwg` has no surface styles. `SVPROBEA_P6` consults the seat's active data-shortcut project read-only and prints only whether one is set.

### G1 — PASSED 2026-11-05, with two carries

Criteria from HANDOFF.md section 10 and what proved them:

1. T21 green: `dotnet test` 548/548 on main (C1 130 + C2 150 + C3 107 + C4 69 + C5 94 − shared smoke tests, re-run by the orchestrator after every merge).
2. T25–T28 green on generated triangles: C5, re-run on main.
3. `docs/api-findings.md` answers P1–P13 and P16–P20 with evidence: all answered except P6, which is as far as headless can go (data-shortcut references need Dan's fixture pair; §A.9 lists the steps). **Carry 1: P6.** Stop condition 12.2 is not triggered: nothing has shown a volume surface cannot be built from references.
4. Fixture A totals 200 CY cut and 200 CY fill after the documented mapping: S1's T1/T2 table, re-run by the orchestrator (band 200.000 / 200.000, outside-limits 0).

**Carry 2: Fixture H reconciliation** (FX whole-surface cut 67,860 ft³ vs C5 integrator 67,880 ft³ over the band; FX reported two sliver triangles hidden by Civil 3D). Resolved at the A1 merge when the integrator runs against `ITriangleSource` from the real surface (T29, E1012, E1013).

Contract review at G1 (stop condition 12.6 applies from here on): see "Contract revision 2" below.

## Contract revision 2 (G1 review, 2026-11-05)

Changes to `Contracts.cs`, each tied to a decision above: `ISheetVolSession` gains `ActiveDrawing`, `FormatStation`, `StationRange`, `GetRunStatus`, `IsCommandActive`, `ExportAsync` (D5: C4's host interface folded into the one door); `UnitSystem`, `DrawingInfo`, `RunStatus` records in `SheetVol.Core` (C2's duplicate `Volumes.UnitSystem` removed); `RunDefinition.Draw : DrawOptions?` and `SchemaVersion = 2` (D6; null = all on; schema 1 JSON loads with Draw null); `SegmentOverrides.OffsetLeft/OffsetRight` (D8); `RegenResult.OutsideLimits : IReadOnlyList<OutsideLimits>` one per analysis (C2's per-analysis `ReconcileResult`; A4 builds the mapping). Follow-ups dispatched: C1's `SegmentPlanner`/`BoundaryBuilder` must use per-segment offset overrides with per-segment E210; C3's `DefinitionSignature` must hash `Draw` and the offset overrides (else a typed SVDEFINE changing only those is not stale, E912). The adoption re-pinned C3's `Fingerprint_is_stable_across_runs` because `SchemaVersion` is hashed and moved 1 → 2; the format itself is unchanged.

### From the revision 2 follow-up (rev2b)

- Per-segment offsets: `SegmentPlanner` looks up `SegmentOverrides` by the GUID after identity carry-over; each side takes the override where set, else the run's. `BoundaryBuilder.Build(alignment, segments, options)` is the per-segment overload A4 must call (the uniform overload is a checked wrapper). Per-segment E210 is an error on that segment only. The arc/spiral step count follows the largest offset on the element; whether a curve join is tangent is judged with the widest segment at the junction (can raise E205 on a neighbour).
- **Cut-line rule for differing widths:** all offset points at one station lie on one cut line (normal on lines, arcs and spirals; mitre bisector at an angle point); at a break the shared cut line runs from P(s, min(La, Lb)) to P(s, max(Ra, Rb)); each ring runs along it between its own two offset points, so rings touch along the common part, overlap nowhere, leave no gap (tested on a straight, a crossing-width case, a curve PC/PT and K's angle point; intersection area below 1e-6).
- **Band when offsets vary:** the union of the segment polygons, split into one `BandPiece` per run of contiguous equal-offset segments meeting on shared cut lines; the partition check compares the sum of segments with the sum of pieces; outside-limits = surface totals minus the sum of pieces.
- Fingerprints: `DefinitionSignature` hashes `Draw` (null written as all-on) and every override field in GUID order. **Format is now `SVFP2`**, so every run stored under SVFP1 compares stale once after the upgrade.
- Fakes: one shared set under `tests/.../Fakes/` (`FakeAlignments.S/C/K/SP`, `Straight`, `SP_C5` with R 400, `FakeBands`, `FakeBandSource`, `AlignmentBuilder.ArcByLength`). C3's and C5's "consolidate fakes" notes are closed.

### From A2 (evidence in `%TEMP%\sheetvol\writersModel.log` after a run; summary in `src/SheetVol.Civil3D/WritersModel/README.md`)

- Plan-then-write: every writer reads live objects, hashes with C3's `ContentSignatures`, lets `ChangeSetComparer` decide; any planning error means nothing is written; an unchanged regen opens nothing for write (E811: 0 appends, 1 modified = the store Xrecord). Every created object is tagged (Xrecord on its extension dictionary: run name, segment GUID, role, qualifier; generated tables also store row keys so analysis blocks insert or remove in place, E505).
- Store (6.9): NOD → `SHEETVOL` → one Xrecord per run; run JSON through C2's chunker plus a state JSON (handles, sources by handle and name, orphans, fingerprint); schema read first (E105 refuses); 1,000 segments and 4,000 handles = 2,334 strings, longest 250, round-trips identical. E809 (save-as) and E810 (copied objects are plain) proven.
- New config record `ModelWritersConfig` (layers `SV-BOUNDARY`, `SV-IMPACT`, `SV-MATCHLINE`, match text style, height 0.1, gap, template; table sizing factors; fallback paper point; tag key; all placeholders per A7; `matchMarks.enabled = true` assumed). Table style, layer and text height come from `ReportConfig`.
- `GetTextString(IgnoreMtextFormat)` drops escaped braces (`\{v2\}` reads as `v2`): generated tables compare raw contents too; C2's label binding reads plain text only, so an HCFCD material name with braces would never be found. Table display (MTEXT rendering; column widths estimated from text length) is not checked headless.
- **A4 call order** (from A2): step 1 `RunStore.Read(tr, db, runName, report)`, stop if `!CanModify`, `RunStore.ResolveSource` per stored `SourceRef` (SV-E803 warning, SV-E540 fails). Steps 2 to 4 build a `ModelWritePlan`: C1 boundaries for station segments; `MatchMarkPlanner.Plan(...)`; HCFCD: `ImpactOutlines.Build` per segment and `HcfcdTableSpec` from `HcfcdTemplate.Load`; `ModelTableSpec.ForSegment(...)` for segments without `TargetLayout`; `ModelTableSpec.ForMaster(...)`; pass every list on every regen, empty when off, so stale objects get erased. Step 5, one transaction: `ModelWriter.Apply(new ModelWriteContext(tr, db, runName, cfg, report, units), plan, read)`, then A3's writers, then `RunRecord(run with KnownSegments = applied plan, A2 + A3 objects, repaired sources, RunStore.MergeOrphans(...), fingerprint)`, then `RunStore.Write` (abort if Refused), then commit. `SVCLEAR` = `Apply(ctx, ModelWritePlan.Empty, read)` then `RunStore.Delete`; `SVSTATUS` = `RunStore.ReadAll` and `record.Orphans`.
- Open: who writes HCFCD tables on a named layout (`HcfcdTableSpec` accepts `TablePlacement.InLayout`; `TableRenderer` works in any space): A4 decides at integration. The runner now stages subfolders (`Templates\`) after A2 found the HCFCD template missing from the stage.

### From S3 (full detail with verbatim output in `docs/api-findings.md` §C; Dan's steps in `docs/dan-probe-steps.md`)

- **P15, headless half (MEASURED):** the Civil 3D .NET API exposes no events at all (run-time reflection over AeccDbMgd 13.8: 1,887 exported types, 0 public events; same offline for AeccDataShortcutMgd and AecBaseMgd). L2 watches `Database.ObjectModified` and friends and filters by class name (`AeccDbAlignment`, `AeccDbSurfaceTin`, ...). One alignment edit fires a burst: the alignment plus 6 label-group modifications, and after `CommandEnded` the draw-order table fires with no command running; a TIN vertex edit fires exactly one `ObjectModified`. Undo must be recognised by the command name `U` (some label-group events during undo carry `IsUndoing = False`). **Read-only commands need `CommandFlags.NoUndoMarker`** or the next `_.U` undoes the read-only command instead of the edit (A4: `SVSTATUS`, `SVEXPORT`, `SVCONFIG`, probes; E804). **`Application.Idle` never fired in a Core Console script** (0 of 26 commands): batch and headless regen must not rely on Idle (L2). `FindVertexAtXY` returns the nearest vertex instead of throwing.
- **P21 (coded, waiting for Dan's one `SVPROBEUI` run):** `SVPROBEUI` in `SheetVol.UI/Probe` exercises four write routes from a modeless button (no lock as the control, `LockDocument`, `ExecuteInCommandContextAsync`, `SendStringToExecute`), colour theme and three change signals, command-active tracking, highlight and transient outline, DPI and window size, compiled XAML from a NETLOADed DLL; logs to `%TEMP%\sheetvol\probeUI.log`. `Application.ShowModelessWindow(Window)` binds to the AcCoreMgd overload inherited by AcMgd's `Application`.
- **P14 (coded, waiting for Dan's drawings):** `SVPROBEFRAMES` enumerates groups, frames, match lines, logs every `Explode` primitive, solves each frame two ways (the 5.2 guess with a holds / does-not-hold verdict, and independently from the alignment direction), compares with the sheet viewport's twist and scale, and logs viewport XData and extension dictionaries to find how Create Sheets marks a Plan viewport. `SVPROBEC_SOLVECHECK` passes on 3 known rectangles. Once `frames-src.dwg`, `frames-sheets.dwg`, `frames-ref.dwg` are in `tools/fixtures/`, P14 runs headless.
- Runner now also stages `SheetVol.UI.dll` into the dev bundle after S3 found it missing (Dan's step 0 copy lines are no longer needed).

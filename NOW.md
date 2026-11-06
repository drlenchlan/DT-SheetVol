# NOW — SheetVol state of record

Rev 1, 2026-11-05. Newer revs supersede older ones. Facts here win over HANDOFF.md on
what is true; HANDOFF.md wins on method. Every figure is MEASURED unless marked otherwise.

## Position

**G0 and G1 passed 2026-11-05 (evidence under Gates). Contract revision 2 is merged on main (stop condition 12.6 applies from here). Wave 2 (A1, A2, A3, A4, S3, U2, rev2b) and the integration pass are merged; G2 headless half passed 2026-11-05, Dan's SVDIALOG half open. 2026-11-06: Dan's first SVDIALOG attempt returned "Unknown command" because S3's `Probe/ProbeUiCommands.cs` carried an assembly-level `CommandClass` in SheetVol.UI (AutoCAD then registers only that class); the U2 merge gate checked only U2's own diff for the attribute. Fixed on main (merge e9aa8d3), verified headless. Next: Wave 3 (L2, U1 can start; L1 waits on Dan's view-frame fixtures).**

## Corrections to HANDOFF.md (Dan or measurement overrides the plan)

| # | Handoff said | Now | Source |
|---|---|---|---|
| X1 | Section 2: host requirement Civil 3D 2026.2.2 or later only | Host floor is Civil 3D **2025.2.7 or 2026.2.2 and later**; both run the .NET 10 runtime. The plugin compiles against the AutoCAD 2025 managed DLLs (assembly version 25.0) so one DLL binds on 2025, 2026 and 2027 hosts. Measured: the DLL compiled against 25.0 loaded in the 2026 host and reported `AcDbMgd: 25.1.0.0` | Dan 2026-11-05 ("2025.2.7 updated to .net 10"); P16 run |
| X2 | Section 11: `SheetVol.sln` | The solution is `SheetVol.slnx`; the .NET 10 SDK's `dotnet new sln` emits the XML format | Wave 0 |
| X3 | Section 11: `accoreconsole ... /isolate svtest ...` | `/isolate` dropped; the run with it hung before reading the script. Working form is in `tools/test-headless.ps1`: `/i /s /product C3D /l en-US`, stdin closed, `LAN_DT_BATCH=1` | Wave 0 |
| X4 | Section 5.1: "Custom code must be in a trusted location" | Confirmed the hard way: `NETLOAD` from `C:\LAN-DT-build` fails with "Unable to load ... assembly" under `SECURELOAD=1`. The runner stages the build into `%APPDATA%\Autodesk\ApplicationPlugins\SheetVol-dev.bundle\Contents\` and scripts load it via `(getenv "SHEETVOL_DLL")` | Wave 0 |
| X5 | A10, A11, 6.15: OHWM is one elevation per run (override per segment); "below OHWM" is the plan area where existing ground is lower than that elevation; cut counted is existing minus design inside that area | **OHWM is a surface in the drawing, not an elevation.** The HCFCD preset is an ordinary analysis with the OHWM surface as base and the proposed surface as comparison: impact cut is where proposed is below OHWM (OHWM minus proposed), impacted area is the plan area of that cut, impacted length its station extent. Existing ground is not part of rows 6 to 9. Consequences: `OhwmSpec` (elevation) is replaced by an OHWM surface name on the analysis (contract revision 3, 12.6, Dan's call recorded here); the 6.15 integrator keeps its triangle overlay but its half-plane clip `base < OHWM` goes; T25 to T30 fixtures become a flat OHWM surface at the stated elevation and their expected numbers are recomputed as OHWM minus design (not existing minus design); E1003 "cut above OHWM" and E1004 "fill below OHWM" become "proposed above OHWM" (not counted, exported for QA); E1006 side-ditch rule falls away. A10 is closed by this row; A12 (acre decimals) still open | Dan 2026-11-06: "OHWM is just another surface. It would be the base surface in the comparison and the proposed surface would generate a cut when below" |
| X6 | 6.14, 6.16: the HCFCD preset adds an OHWM elevation field to the dialog and an OHWM column to the grid | **No OHWM field anywhere in the UI.** An analysis is Base and Comparison, nothing else; the HCFCD preset names which of the run's analyses feeds rows 6 to 9 (the user picks the OHWM surface as that analysis's base). **An existing TIN volume surface is an alternative analysis source:** it already carries base, comparison and factors. Members (S4 MEASURED 2026-11-06, correcting the orchestrator's string scan that guessed `BaseSurfaceId`): `TinVolumeSurface.GetVolumeProperties().BaseSurface` and `.ComparisonSurface` (ObjectId, read-only), `TinVolumeSurface.CutFactor` / `FillFactor` (read/write). S4 results under "From S4" below. Contract revision 3 = X5 + X6 together | Dan 2026-11-06: "OHWM should never be in any dialog. It should just be Base and Comparison; a volume surface that was already created may be valid for use because it already does a base and comparison. Check that theory and add an or vol surface selection to the system" |

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

### G1 — see below. G2 — headless half PASSED, Dan's half OPEN (see below). G3 — not started

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
| E4xx Volumes | E401–E421 | U rows GREEN (C2); H rows with printed evidence (A1): E401–E404, E407, E408, E410, E411, E412, E421; E405 inputs only; E415 OPEN (Dan fixture); E416 M |
| E5xx Tables | E501–E508 | E508 GREEN (C2); E501–E505 with evidence in model space (A2) and paper space (A3); E506, E507 TODO (A4) |
| E6xx Layouts | E601–E615 | U rows GREEN (C3); H rows with evidence (A3): E601, E602, E604, E605, E608, E609, E612, E613, E615; E611 and E614 part M (Dan opens a3-t18.dwg) |
| E7xx Frames | E701–E713 | TODO (L1) |
| E8xx Regen | E801–E812 | Logic-level GREEN (C3: E804–E806, E811); H/M rows TODO (A4, L2) |
| E9xx Dialog | E901–E917 | U rows GREEN (C4: E902, E903, E904, E907, E912u, E914, E916, E917; logic for E901, E905, E906, E911 too); M rows TODO (U2, Dan) |
| E10xx OHWM | E1001–E1021 | U rows GREEN (C5); H with evidence: E1012, E1013 (A1), E1015–E1018 (A2); E1014 M; E1021 OPEN (Dan fixture) |
| T1–T31 | | T15, T16, T17 GREEN; T14 fingerprint part and T19 logic part GREEN (C3); T11, T23 GREEN (C2); T22 GREEN (C4); T25–T30 GREEN on generated triangles (C5); T7, T8, T9, T10 Core half GREEN (C1); rest TODO |
| P1–P24 | | ANSWERED with evidence: P1–P5, P7–P13, P16–P20, P22 (docs/api-findings.md §A, §B). PARTLY: P6 (data-shortcut reference needs Dan's fixture pair; steps in §A.9), P23 (reference and corridor surfaces not measurable headless). TODO: P14, P15, P21 (S3, Wave 2) |

## Open items for Dan (HANDOFF.md section 13)

| Item | Status |
|---|---|
| Confirm dev seat at 2026.2.2+ | DONE: 2026.2.3 |
| P16 in full Civil 3D (`SVHELLO`) | DONE 2026-11-05 |
| Four dialog artboards as PNG in `docs/ui/` | OPEN, before Wave 2 |
| HCFCD table as it should look in CAD, plus a sample drawing with real surfaces and an OHWM elevation | OPEN, before Wave 2 |
| Confirm A10 (what "below OHWM" means) and A12 (acre decimals) | A10 CLOSED 2026-11-06 (X5: OHWM is a surface); A12 OPEN; D1–D21 RULED 2026-11-06 by the orchestrator, Dan may override |
| View-frame fixtures `frames-src.dwg`, `frames-sheets.dwg`, `frames-ref.dwg` per `docs/dan-probe-steps.md` step 2 (no LAN templates) | OPEN, needed for P14 and L1 |
| Run `SVPROBEUI` once, then the P15 interactive pass, per `docs/dan-probe-steps.md` (about 25 minutes) | OPEN, steps written 2026-11-05 |
| LAN sheet template with `SV-VPORT` viewport and table anchor block; table style and layer names | OPEN, before G3 |
| Corrections to section 3 assumptions | none yet |
| G2 interactive: SVDIALOG on the fixture drawing (screenshot 2026-11-06) | PARTIAL PASS: dialog opens, alignment S picked, four 500 ft segments, chart drawn, regen fails cleanly by D14/E501 (six LAN template names absent: layers SV-BOUNDARY, SV-MATCHLINE, SV-VOLUME, SV-TABLE; surface style SV-No Display; table style SV-Volumes). Verified in RegenPipeline: the run transaction is disposed without Commit, so E412's owned surface was rolled back and "Nothing in this run changed" holds. To finish: add the six to the drawing or template, point the settings at existing names (SVCONFIG, SVCONFIG Reload), or Dan says "go fixture T" for an `SVFIXTURE T` option that creates them |
| Dan's call: E443/E444 (owned surface style or layer missing) warn and fall back to Standard and layer 0, while E501 fails the run for a missing writers layer. Make both strict per D14, or keep the owned surface lenient because it is engine-internal | OPEN 2026-11-06 |

Low-priority defects from the G2 screenshot (any Civil3D worker, with its package): E412 and E802 report an owned surface "created" that the failed run's abort then removed (suppress Info lines on a failed run, or say "would be created"); the E501 boundary message prints twice, once inside the E801 summary and once on its own (Dedupe runs after E801 embeds the first error's text).

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
| 2026-11-05 | wp/a1 (A1 adapters) | 8d073ad | build 0 errors; adapters.scr re-run by orchestrator in the a1 worktree, exit 0, 11 s, 85 + 2 PASS, 0 FAIL; Fixture H reconciled at 67,860 ft³ in that run; diff confined to Adapters/ and adapters*.scr |
| 2026-11-05 | wp/a3 (A3 paper writers) | 1d5068a | build 0 errors (gated on the literal line); writersPaper.scr re-run by orchestrator in the a3 worktree and again on main after the merge, exit 0, 75 PASS, every phase "no FAIL lines"; diff confined to WritersPaper/ and writersPaper*.scr |
| 2026-11-05 | wp/u2 (U2 dialog) | 43215c9 | build 0 errors (gated); off-screen smoke outside AutoCAD 28/28 per U2, renders kept in docs/ui/smoke/ and read by the orchestrator against 6.14; diff confined to UI/Dialog and UI/Host; no assembly-level CommandClass in SheetVol.UI |
| 2026-11-05 | wp/a4 (A4 commands + pipeline) | 453ae3a | build 0 errors (gated); selftest.scr re-run by orchestrator in the a4 worktree and on main after the merge: 37 passed, 0 failed on fake bindings; diff confined to Commands/, Session/, SelfTest/SelfTest*.cs, selftest*.scr |
| 2026-11-05 | wp/int (integration) | dab7245 | build 0 errors (gated); selftest-installed 74/0/3, selftest 45/0, selftest-commands clean, probeA and probeB clean, all re-run by the orchestrator on the final commit; Core 586/586 |
| 2026-11-06 | wp/fix-ui-cmdclass (orchestrator, 2 files) | 4fc2bb3 | build 0 errors (gated); Core 586/586 on main; headless script NETLOADed engine + UI DLL then typed SVDIALOG and SVPROBEUI: both accepted (no "Unknown command"; SVPROBEUI then crashed Core Console opening WPF, expected and out of scope); `grep -r "assembly: CommandClass" src/SheetVol.UI` now matches comments only |
| 2026-11-06 | wp/s4 (S4 probe D) | fe767f9 | build 0 errors (gated); probeD.scr re-run by orchestrator in the s4 worktree, exit 0, 6 s, D2 user vs owned diff 0.00E+00 on all four T2 segments, D3 raw 2700/1350 with factors 1.25/0.9 set, D4 5400 after Rebuild and 2700 again after UNDO 1, D5 IsVolumeSurface=False the command after EG erased, D6 seg 2 cut 2700 to 0 with a user outer boundary; diff confined to ProbeD*.cs, probeD.scr, api-findings.md §D |
| 2026-11-06 | wp/fixture-t (`SVFIXTURE T`: the drawing names the settings expect) | d4af6bd | build 0 errors (gated); Core 589/589; fixtureT.scr re-run by the orchestrator in the fxt worktree, exit 0, 9 s: 9 created / 1 present / 1 not set, second T all present, then SVDEFINE + SVANALYSIS + SVREGEN "OK in 0.2 s, 4 segment(s); created 21" with no E501/E443/E444; diff confined to SelfTest/Fixture*.cs, fixtureT.scr, two READMEs; names come from the settings records, no SV- literals in the fixture |
| 2026-11-06 | wp/d-rulings (D1, D7, D10/D18, D20) | 2bb5406 | build 0 errors (gated); Core 589/589 and selftest.scr 45/0 re-run by the orchestrator in the d1 worktree; diff confined to Core Views/, Output/, Presentation/, their tests, and one OutputPlanner.Hcfcd line (zero ImpactResult for an E827 segment); `ClipIncludesOverlap` and `ImpactVolumesUseCutFactor` now default false |

## Decisions D1–D21: RULED 2026-11-06 (Dan may override any row)

Dan 2026-11-06: "D1–D20, you and your subs try to discern the answers; these questions were raised based on prose that the agents were tracking." Ruled by the orchestrator from the HANDOFF.md text, with an independent Opus read of the same sections (reviewer agreed on 16, and on D1, D10, D13, D16, D21 its handoff citations replaced the orchestrator's first reading). "Basis" quotes the handoff line or says silent. "Change" names the code or settings default that moves; "stands" means the current default is confirmed.

| # | Ruling | Basis | Change |
|---|---|---|---|
| D1 | `Clip = Boundary` clips to the segment boundary only. `ViewOverlap` enlarges the fitted view; the overlap is visible with `Clip = None`. What the viewport shows is what was counted | 6.6 "Clip: `None` or `Boundary` (the boundary transformed to paper space, arcs preserved)"; E304 "`ViewOverlap` affects the viewport only, never the volume" | `ViewMathConfig.ClipIncludesOverlap` default true → false (key stays in settings) |
| D2 | FitStandard picks the most detailed scale that fits (smallest model units per paper unit) | 6.6 "largest scale in `StandardScales` that fits"; T16 picks 100 of {20..100} | stands |
| D3 | Diagnostic wording stays in code keyed by its SV-E code. Text that lands in the drawing or the export lives in settings (already: `OutsideLimitsLabel`, `OutOfDateMarker`, `TextTemplate`, HCFCD template JSON) | 6.13 "one clear line with a stable code"; "No style, layer, text height or path literals outside config defaults" | stands |
| D4 | Accept the wider fingerprint. Under rev 3 the OHWM elevation leaves it (the OHWM surface is hashed as an analysis base) and a volume-surface source adds its handle, name and post-rebuild totals. `KnownSegments` (output) and `AutoMode` (behaviour) stay out | 6.11 fingerprint list; E418 | rev 3 |
| D5 | Settled by rev 2: the dialog host members are on `ISheetVolSession` | rev 2 | done |
| D6 | Settled by rev 2: `RunDefinition.Draw` | rev 2 | done |
| D7 | HCFCD rows 6–9 and the average use the unfactored cut. A Section 404 permit and the HCFCD segment table report in-place (bank) cubic yards, acres and linear feet; shrink/swell factors are an earthwork-balance adjustment, not an impact quantity. Domain basis, not handoff: confirm against Dan's HCFCD sample table when it arrives | silent on factors for row 9; contract `CutVolume` "cubic drawing units, unadjusted" | `HcfcdConfig.ImpactVolumesUseCutFactor` default true → false; export labels the value unfactored |
| D8 | Settled by rev 2: per-segment offset overrides; the dialog's fix beside a failing row is the offset field and Apply, not text | rev 2; 6.14 | done (U2) |
| D9 | Depth snapping on the impact path stays on: a depth inside the floating-point error bound is not cut, and counting it is a silently wrong number. τ is a numerical tolerance that governs the analysis, so it stays in code and gets documented in the Integrator README. The OHWM-elevation snap retires with X5. `MinCutDepth` remains the user threshold in settings | 6.13 "no silently wrong numbers"; E1007 | rev 3 removes the OHWM snap; README documents τ |
| D10 | A surface with no triangles inside one segment's window is the E405/E1011 case: zeros, coverage 0 %, flagged, run continues. E403 is a surface with no triangles at all, which fails the run. Under X5 the OHWM surface usually covers only the channel, so empty segments are normal | E405 "Volumes 0, coverage 0%, flagged"; E1011; E403 | rows must render 0 / 0.00 / 0 / N/A, never blank or NaN (check in rev 3) |
| D11 | `MinSegmentLength` defaults to 0: every remainder kept and reported (E302). Lives in settings as `MinSegmentLengthFeet` | silent on the default; E302 | stands |
| D12 | A break inside the mitre setback and a non-tangent join at a curve stay E205 errors naming the station; never guess a mitre. Adjacent-pair overlap test stays, contact along a shared cut line is not overlap (rev2b measured below 1e-6) | 6.4 step 6 "Never compute on an invalid boundary"; step 5 | stands |
| D13 | The drawn impact outline must be the true plan outline of proposed-below-OHWM. A full-width band overstates row 7 on a permit exhibit. Rev 3 adds piece polygons to `ImpactResult`; until then no outline is drawn rather than an approximate one | A2 deliverable "outline of the impacted area"; 6.15 row 7; 6.13 | rev 3 (contract); A2 drops the band outline |
| D14 | Missing configured layers fail the run, one message naming every missing layer. No create-missing switch: it would put non-standard layers in the drawing. Adding them to the LAN template is Dan's (section 13) | E501 "Fail with the missing name; do not fall back silently" | stands |
| D15 | A run-level E210 rejects the run at SVREGEN; building overridden segments for the dialog's live validation is harmless | E210 "Rejected at definition" | stands |
| D16 | Build the stepped union ring (one polygon per contiguous stretch between skips) so the partition check stays a real test when neighbours' offsets differ; the per-piece check compares segments with themselves and cannot fail | 6.5 "Failure is a bug: abort, write nothing" | Wave 3 follow-up for C1's owner (Core/Planning), low priority |
| D17 | `RebuildSources` defaults to true; each rebuild is reported as Info naming the surface. SVREGEN is the one click with no prompts | E404; 6.12 | stands |
| D18 | E827 Warning with the run succeeding, provided the row shows 0 / 0.00 / 0 / N/A with the coverage flag | E1001; E405 | same check as D10 |
| D19 | Area cross-check max(1 ft², 1e-4 × area) on project data, 1e-9 relative on fixtures; mismatch fails the run and prints both numbers; the absolute floor scales with drawing units (E216) | 6.5; 6.15 | stands |
| D20 | Orphan layouts keep their names. The E603 report names the orphan holding the clean name and points at SVPURGE | E612 "never auto-deleted"; E603 | E603 message text |
| D21 | A user volume surface source falls under `RebuildSources` and E404 like any source: rebuild on every regen (IsOutOfDate lags one command, S4) and report Info naming it; when `RebuildSources` is off, fail with E404 naming it (staleness cannot be guaranteed then; documented). Rebuild never writes its factors, base, comparison or boundary. S4's guards run before every read. No new setting, no "ask": the engine has no dialogs (A9) | E404; A9; 6.5 "never edits a volume surface it does not own" | rev 3; the `volumeSurfaceSource.rebuild` proposal is rejected |

**Rule added for rev 3 from the review (user boundary on a volume-surface source, S4 D6):** when the analysis source is a volume surface, the integrator reads that one TIN as its triangle source (a volume surface's elevation is the comparison-minus-base difference, so depth is its negated z; sign to be MEASURED by the rev 3 Civil3D worker) instead of the base and comparison TINs. The user's boundary is then honoured by both the integrator and `GetBoundedVolumes`, and E1012/E828 keep agreeing.

**Work items from the rulings:** (a) D1 and D7 default flips plus their tests and the settings template; (b) D20 E603 message; (c) D10/D18 zero rendering check; (d) D4, D9, D13, D21 and the boundary rule go into contract revision 3; (e) D16 union ring, Wave 3, low.

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

**Carry 2: Fixture H reconciliation. CLOSED at the A1 merge** (see "From A1": one transition cell triangulated differently; Civil 3D 67,860 ft³ is the product number; T29 agrees to 4.7e-15).

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

### From A1 (evidence in `%TEMP%\sheetvol\adapters.log` after a run; entry points in `src/SheetVol.Civil3D/Adapters/README.md`)

- **Fixture H carry CLOSED (MEASURED):** Civil 3D's volume surface, the owned surface's totals, `GetBoundedVolumes` over the band, and C5's integrator on the adapter's triangles all give 67,860 ft³; C5's generated triangles give 67,880. The 20 ft³ is one transition cell (x 1300–1310, |y| 10–16, corner depths 2, 0, 0, 0) where Civil 3D picks the diagonal away from the depth-2 corner on the +y side and C5's generator picks the depth-2 diagonal on both sides. Neither is wrong; the product number is Civil 3D's because the integrator on Civil 3D's own triangles equals the API (T29, 9 of 9 polygons per fixture within 4.7e-15 relative).
- T1/T2 through adapter → C1 planner → `VolumeSourceAdapter` → C2 reconciler: PASS on all 8 segments, partition and band totals 200/200 CY. T10: API bounded volume equals C1's polygon area on K and SP to 5.5e-16 (lines, arcs and spirals alike; the 2e-5 shortfall against the exact band is C1's 0.01 mid-ordinate tessellation). T24: 2D area 62,829.81 SF (rel −3.25e-5 to analytic, PASS at 2e-4); off-edge band coverage 57.7 %, SV-E419. The integrator's plan area matched the datum-method 2D area on every polygon to 4.4e-15: this is the evidence for the unbuilt 6.5 cross-check (C2 note), which A4 can now implement with a 1e-9 relative tolerance.
- E1013: hide boundary on FG_A: visible-only integrator 1574.074 vs API 1574.074; including hidden triangles gives 2700. P23 scale: 999,698 triangles read in 0.71 s; band window kept 142,814; 27 segment-window queries 11.7 ms.
- Sign and units in one place: `Cut` real cut, raw cubic drawing units, |x| < 1e-6 clamped to 0 (saw 5.4e-11 noise), Civil unit Feet used while `INSUNITS` says Inches. Polygon rule: open or 3-point polygon throws `ArgumentException`.
- Lifecycle: owned surfaces tagged (`OwnershipTag` Xrecord); erased → recreated; renamed → new surface, old left in place with a Warning; user's untagged surface of the same name keeps its factors and the owned one gets a `-2` suffix (E412); a stored handle is used only while the run still names the same object (an edited or misspelled name fails rather than silently using the old surface; E803 is a Warning, SV-E213 when the alignment is missing, SV-E445 when found by handle under a new name). E404: `RebuildSources` on → rebuild with Info, off → Error; a source on a locked layer that needs a rebuild fails naming the layer; the owned surface is updated even on a locked layer.
- **New facts:** `IsOutOfDate` is set on a point-group surface only after the command that moved the point ends; a TIN defined by a 3D-polyline breakline did not flag after its vertex moved. Aborting a regen transaction after `EnsureAnalyses` created an owned surface removes it cleanly; aborting after creating an alignment is clean; **creating an alignment, erasing it in the same transaction, then aborting crashed Core Console (0xC0000005)**: A4 never erases an object it created in the same transaction before an abort (create-then-erase is replaced by not creating).
- For A4: the E1012 fail-the-run rule (max(0.5 ft³, 1e-4 × total) on project data) lives in the pipeline, A1 provides both numbers; embed `AdapterConfig` in the settings file; persist `AnalysisBinding.ToStored()` and the alignment's `StoredRef`. New codes SV-E442 (not found), E443/E444 (style/layer missing, both fire on `blank-c3d.dwg`), E445 (renamed source), E446 (bad config). Placeholders (A7): style `SV-No Display`, layer `SV-VOLUME`.
- Open: reference and corridor surfaces (E415, E1021; stop conditions 12.2 and 12.9) still need Dan's fixture pair; the reference branch of E404 has no fixture.

### From A3 (evidence in `%TEMP%\sheetvol\writersPaper.log` after a run; API and measured behaviour in `src/SheetVol.Civil3D/WritersPaper/README.md`)

- T18 PASS in-session and after reopen: 2 sheets in 52 ms; target, twist, scale, 6-vertex clip and 17 table cells all match the plan; model centre lands at (7, 4.5). P12 oracle: `acedTrans` vs C3's math 0.0 at 0° and at −90°, segment start on the left edge. **Twist sign: C3 and AutoCAD agree**, nothing absorbed; AutoCAD stores `TwistAngle` in [0, 2π) so −90° reads back as 270°, compare mod 2π. **Correction to `docs/api-findings.md` B.2 rule 4:** the sign is reversed there; correct is `ViewCenter = (CenterPoint − A) / CustomScale`.
- **E614, corrected:** S2's `Layout.Initialize()` finding is incomplete. A WblockCloned plan viewport is never entered by `_.MSPACE` after first activation (number −1, or taken over as the overall viewport). The writer therefore replaces the clone with a new `Viewport` carrying the template's properties (layer, size, frozen layers, visual style, a copy of the clip, annotation scale, Locked), does the same for other on viewports, and fits the overall view to the cloned extents. `Viewport.On` is never set and never needed. After activation the plan viewport is `number=2`, `_.MSPACE` enters it.
- Measured: an in-place edit of a clip polyline refits the viewport without shifting `ViewCenter` (0.5 paper-unit drift seen), so the writer sets the clip box and `ViewCenter` itself; `Viewport.Height` changes `CustomScale`; `Viewport.TransformBy` does not move the clip; erasing a clipped viewport erases its clip polyline too.
- Evidence rows: E601 (6 cases, nothing written), E602, E604, E605 (scale added by ratio, found by ratio next regen with 0 events), E608 (moved viewport keeps position and view re-applied; off-centre clip keeps `ViewCenter`; LockView kept), E609, E612, E613 (23 → 83 layouts, unchanged regen 7.9 ms), E615, E307 (two-pass rename, rolled back by abort; renumber-off renames nothing), E501–E505 on paper tables, E802 (erased viewport recreated with its clip), E811 (0 writes, 0 events). E611's "which viewport was active" and E614 in acad.exe remain M: open `%TEMP%\sheetvol\a3-t18.dwg`, click T18-1 and T18-2, the band fills the clipped plan viewport, the on key map shows, double-click enters the plan viewport.
- **A4 call order** (from A3): `new PaperWriter(paperConfig, reportConfig.Placement, new LayoutNamer(layoutNameConfig))`; outside the transaction `paper.LoadTemplate()` (SV-E601/E691 → write nothing; `using var template = t.Value` for the whole regen); C3 `ViewCalculator.Compute(..., new ViewRequest(seg, run.Views, template.PlanViewportWidth, template.PlanViewportHeight, overrides))` and C2 `SegmentTableBuilder.Build(...)`; `new PaperRunRequest(runId, sheets)` of `PaperSheetRequest(segmentId, LayoutNameParts, ViewSpec, TableModel?, lockView, storedLayoutHandle)` in sheet order; in the regen transaction `paper.Validate(tr, db, template, request)` (errors → abort) then `paper.Apply(...)` (throws `PaperWriterException` → abort); commit; persist `result.Sheets`, `result.Orphans` → SVSTATUS, `result.Writes == 0` = E811. `PaperWriter.FindOwned(...)` lists owned paper objects.
- Gaps for A4: `RunDefinition` has no GUID, A4 supplies a stable `RunId` (derive from the run name until a contract revision adds one); A2's and A3's owner-tag Xrecords (`SHEETVOL_OWNER`, schema `SV-OWNER-1` in A3) should be unified at integration. Codes: SV-E197 check step, SV-E690 unexpected writer failure, SV-E691 paper config invalid. Not checked: MText title attributes (may rewrite every regen), inherited row formats. Placeholders: `titleAttributes`, table widths and row height, annotation-scale name format; Dan's real DWT untested.

### From U2 (file map and theme keys in `src/SheetVol.UI/Dialog/README.md`; route and A4 rules in `Host/README.md`; renders in `docs/ui/smoke/`)

- Off-screen smoke outside AutoCAD: 28/28, zero binding errors, "Autodesk assemblies loaded: none". **An assembly-level `[assembly: CommandClass]` on SheetVol.UI breaks WPF resource loading** (resolves `accoremgd`, FileNotFoundException): SheetVol.UI never carries one (U1 warned). Command-context route for picks, regen and export: `DocumentCollection.ExecuteInCommandContextAsync` from the dialog's application context with the document locked inside the callback, result through a `TaskCompletionSource`, continuations on the main thread; `SendStringToExecute` rejected (fire-and-forget, echoes, queues). Unverified in AutoCAD: whether the command context survives a non-`CommandAsync` await, whether `ExecuteInCommandContextAsync` waits for an async callback, whether `LockDocument` from the dialog works during a user command (reads fail soft with SV-E901).
- A4 rules from U2: never call `ExecuteInCommandContextAsync` in the engine (the decorator already does); capture Document and Database once per regen; no awaits inside the write transaction; yield between steps and honour the cancel token; raise `SourcesChanged` after external changes. Session registration: UI references Civil3D, so A4's session is returned from `DialogHost.CreateInner` (integration), not registered from the engine. `SHEETVOL_FAKE_SESSION=1` loads the T2 design session. Codes SV-E900 (no engine session), E901 (open/rebind/read/close failure), E908, E909, E911. `DesignSession` duplicates C4's test fake (UI cannot reference the test project). Seven accessible-name strings live in `Dialog/ViewText.cs`.

### From A4 (settings in `Session/SheetVolSettings.cs`, ports in `Session/Ports.cs`, pipeline in `Session/RegenPipeline.cs`)

- Self-test on fake bindings: 37/37 (T1, T2, T12, T13, T14, T19, T20, T23 raw area, T25 via the pipeline; E801, E802, E809, E811, E404, E504, E506, E507, E1012, E213, D10, config round-trip, session members). `selftest-commands.scr` drives every command headless with typed input; E217 evidence: with the UCS rotated 90°, `SVBREAK Pick` at (0, −750) printed station 7+50.00.
- **Transaction lifetime (MEASURED, review-found):** A1's `AlignmentAdapter` refuses `PointAt`/`FormatStation` once its transaction ends; A4 now prepares exports inside the transaction and only writes files after commit; a failure after commit stays a warning with `Succeeded = true`; no geometry outlives its transaction; a `tr.GetObject` wrapper is not disposed at commit, so `IsDisposed` cannot detect the end of life: the guard tracks the transaction itself.
- **Managed `Database` and `Document` wrappers are not stable objects (MEASURED):** a `ConditionalWeakTable` keyed by them lost its entries; caches key by `UnmanagedObject`.
- Settings file `sheetvol.settings.json` beside the DLL (`SVCONFIG Show/Write/Reload`); built-in defaults used when absent; placeholders reported once per session; unknown keys and bad values refused (SV-E534, SV-E820).
- Spec tension: P18 says always `Rebuild()` the owned volume surface; E811 says no writes except the timestamp. T19 counts writer writes only (the surface rebuild is not a write of an owned drawing object).
- Manual segments are reconciled in a second pass outside the partition check (SV-E826 warning) and get no layout or view. Orphan layouts keep their names, so renumbering after a merge gives the next sheet an E603 "(2)" suffix. Codes SV-E820–E829 (E822 unwired bindings, E823 unexpected exception guard, E826 manual segments, E827 integrator E403 downgraded per D10, E828 area cross-check mismatch), SV-E504 relock note, SV-E199 self-test failure. `SVSELFTEST BindFakes` is test-only and refused when real bindings are wired (drop before release).

### G2 — headless half PASSED 2026-11-05; Dan's half OPEN

Installed bindings (`tools/scripts/selftest-installed.scr`, re-run by the orchestrator on the final integration commit): **74 passed, 0 failed, 3 not run** with reasons in `selftest-result.json` (E807 read-only drawing not possible headless; E812 is L2's batch runner, Wave 3; E415 data-shortcut and corridor surfaces need Dan's fixture pair). Green on installed bindings: T1–T20, T23–T31 (T29 worst 5.8e-15 relative over 28 regens), plus E213, E217, E308, E401–E404, E411, E412, E421, E501, E503–E507, E601, E612, E801–E803, E808, E809, D10. Same commit: `selftest.scr` (fakes) 45/0, `selftest-commands.scr` no SV-E8xx errors, `probeA.scr` T1+T2 0 segments outside ±0.01 CY, `probeB.scr` no SV-E190; earlier on the integration branch: `adapters.scr` 85+2 PASS, `writersModel.scr` 55/0, `writersPaper.scr` 77 PASS. Core suite 586/586. **Open for Dan:** open `SVDIALOG` once in Civil 3D, run T2's breaks through it, compare with `docs/ui/smoke/` or the artboards (section 10 G2 last clause).

### From the integration pass (`src/SheetVol.Civil3D/Integration/README.md`, `Ownership/README.md`)

- `EngineStartup` (`IExtensionApplication`, no AcMgd) sets `EngineBindings.Current` and `SelfTestWorlds.Installed` at load. `CivilAdapterFactory` over A1 with a `TransactionLifetime` guard; `CivilWriterFactory` over A2 and A3. **Port reshape (A4's `Ports.cs`, not the contract):** `IModelWriter`/`IPaperWriter` take one whole-run `WriteRequest` per regen because A2 and A3 compare live objects themselves. Step 5 order: model writer, then paper writer last (A3 erases template clones it creates, so nothing that can fail runs after it), then the store. Owner tags unified in `Ownership/OwnerTag.cs` (A3's `SV-OWNER-1` layout plus `RunIds.For(runName)`); A2's record and A3's class deleted. NoUndoMarker on SVSTATUS, SVEXPORT, SVCONFIG and the read-only probes. E1012 and the 6.5 area check: fixtures 1e-9 relative over a 1e-6 ft³ floor, project max(0.5 ft³, 1e-4 × total) / 1e-4 relative or 1 ft². `DisplayPolygonSource` uses C1's per-segment `Build`. Settings file embeds `adapters`, `modelWriters`, `paper`; A4's duplicate keys dropped. Dialog host: `CreateInner` order = fake session env var, registered factory, A4's `SheetVolSession` wrapped by `EditorSession`; `CivilReads` typed.
- **Orchestrator deviation from section 9 "write no feature code after Wave 0":** three fixes applied by the orchestrator after the integration worker was stopped at 853k tokens, each one specified exactly in its report: `ExportFiles.Prepare` formats only finite stations (manual segments carry NaN), `ExportWriters` writes non-finite doubles as JSON null (new `NonFiniteAsNull` converter in Core), the empty test surface renamed `T-EMPTY` (the `SV-` prefix is the owned-surface prefix). Verified by the 74/0/3 run above.
- Follow-ups: collapse `modelWriters.ownerTagKey` and `paper.ownerTagKey` into one setting; `paper.tablePaperPoint` and `modelWriters.tables.tablePaperPoint` are one 6.7 fallback point; E906 needs `RegenerateAsync` to yield between steps (progress and Cancel will not repaint); the status fingerprint reads owned-surface totals without a rebuild and can lag a source edit until the next regen (L2, P18); `SVSELFTEST BindFakes` is test-only. Do not run `p16.scr` from a worktree (it saves over `tools/fixtures/blank-c3d.dwg`).

### From S4 (full detail with verbatim output in `docs/api-findings.md` §D)

- A user TIN volume surface is a valid analysis source: `GetBoundedVolumes` on it equals A1's owned surface exactly on T2's four segments. Base and comparison come from `GetVolumeProperties().BaseSurface` / `.ComparisonSurface` (read-only ObjectIds; the pair cannot be changed through the API). `CutFactor` / `FillFactor` live on `TinVolumeSurface` (read/write); `GetBoundedVolumes` ignores them and returns raw, so Core applies the surface's factors as it does the analysis factors today. Staleness is the owned surface's: `IsOutOfDate` flags only in the command after the edit, numbers stay stale until `Rebuild()`, and that Rebuild is one UNDO step in the user's drawing (D21). An erased base is silent: same command the `CutFactor` getter throws `CivilException('Retrieve attribute failed.')`, next command the object opens as a plain `TinSurface` (`IsVolumeSurface=False`) with stale numbers, zero after Rebuild; `SurfaceStatus.BaseSurfaceNotFound` is never surfaced by any public member. Guard before every read: still a `TinVolumeSurface`, both source ids not erased. A user outer boundary on the volume surface clips bounded volumes too (68 to 44 triangles, seg 2 cut 2700 to 0), so the export must say whether a row came from a pair or from a volume surface. Probe failure code reuses SV-E194 (S1's range is full).

## Orchestration cost and process (2026-11-05)

Subagent tokens through the integration pass: 7,992,120 across 17 Opus workers (C3 275k, S2 384k, C2 333k, FX 341k, C4 368k, C5 384k, C1 409k, S1 389k, rev2 adopt 367k, rev2b 402k, A2 495k, S3 501k, A1 596k, A3 697k, U2 523k, A4 676k, integration 854k). The integration brief was too wide (six items plus a six-script sweep) and was stopped by the orchestrator at 854k. Standing rules from here, written into `.claude/agents/c3d-dev.md`: one deliverable per worker; point at the two READMEs the task touches and the named NOW.md sections, not "NOW.md in full"; no narration between tool calls; return only the requested fields, tables over paragraphs, about 300 words plus evidence lines.

2026-11-06 additions: fix-ui-cmdclass (orchestrator, no worker), S4 probe D 254,736, D-rulings independent reviewer 161,716, D-rulings adoption worker 504,479 (resumed once by message after OutputPlanner.cs was unlocked). Running total 8,913,051 across 19 Opus workers plus one reviewer.

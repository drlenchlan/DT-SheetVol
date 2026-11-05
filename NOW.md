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
| E1xx Platform | E101–E105 | E101 manual: dev seat verified above. Rest TODO |
| E2xx Geometry | E201–E219 | TODO (C1) |
| E3xx Segments | E301–E312 | TODO (C1) |
| E4xx Volumes | E401–E421 | TODO (C2, A1) |
| E5xx Tables | E501–E508 | TODO (C2, A2, A3) |
| E6xx Layouts | E601–E615 | U rows GREEN (C3: E603, E606, E607, E610; E309, E310 too); H/M rows TODO (A3) |
| E7xx Frames | E701–E713 | TODO (L1) |
| E8xx Regen | E801–E812 | Logic-level GREEN (C3: E804–E806, E811); H/M rows TODO (A4, L2) |
| E9xx Dialog | E901–E917 | TODO (C4, U2) |
| E10xx OHWM | E1001–E1021 | TODO (C5, A1, A2) |
| T1–T31 | | T15, T16, T17 GREEN; T14 fingerprint part and T19 logic part GREEN (C3); rest TODO |
| P1–P24 | | P16 PASSED in both hosts; rest TODO (S1, S2, S3) |

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

## Decisions pending Dan (raised by workers, not yet ruled)

| # | From | Question | Default in code until ruled |
|---|---|---|---|
| D1 | C3 | Should the viewport clip include the `ViewOverlap` strip, or the boundary only? | `ViewMathConfig.ClipIncludesOverlap = true` (overlap visible) |
| D2 | C3 | "Largest scale that fits" in FitStandard: read as the most detailed scale (smallest model units per paper unit). T16 passes either way | most detailed |
| D3 | C3 | Problem message wording lives in code; only number formats are in config. Does the settings-file rule cover message text? | wording in code |
| D4 | C3 | Fingerprint covers more than 6.11 lists (analyses, view options, overrides, preset, area unit, OHWM, displayed end stations, manual polyline geometry); excludes `KnownSegments` and `AutoMode` | as described |

## Facts for later workers (from merged packages)

- C3: twist sign convention is paper = Rotate(model − center, twist) / scale; P12 (S2) must confirm AutoCAD's `TwistAngle` sign and A3 absorbs any difference. `ViewSpec.PaperWidth/Height` is the template viewport size fitted to, not the required size (`ViewResult.RequiredPaperWidth/Height`); A3 never resizes the viewport. Display polygon enters through `IDisplayPolygonSource`; A4 adapts C1's boundary builder. Auto-update wiring (L2) must raise `ToolCommandRunning` for dialog-started regens and pass CommandCancelled/CommandFailed as `CommandEnded`. Codes used: SV-E309, E606, E601, E304, E202, E603(Info). Test fakes `C3Alignment`/`C3BandSource` to consolidate with C1's `Fakes/` after C1 merges.

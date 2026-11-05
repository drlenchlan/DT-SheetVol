# SheetVol

Civil 3D sheet-segment tool: station-range boundaries along an alignment drive the
sheet viewports, and exact cut / fill / net tables are generated per boundary from a
base and a comparison surface. The full plan is `HANDOFF.md`; current state is
`NOW.md`; the frozen API between projects is `docs/contracts.md` (twin of
`src/SheetVol.Core/Contracts.cs`).

## Roles

- The main session is the orchestrator (HANDOFF.md section 9). It owns `HANDOFF.md`,
  `NOW.md`, `docs/contracts.md`, `global.json`, every csproj, the solution, and every
  merge. It writes no feature code after Wave 0.
- Workers are `c3d-dev` subagents (`.claude/agents/c3d-dev.md`), one per work package,
  each in its own git worktree, editing only the files its package owns (section 10).
  Every folder under `src/` carries a `README.md` naming its owner.

## Build and test

```powershell
dotnet build .\SheetVol.slnx -c Debug
dotnet test  .\tests\SheetVol.Core.Tests\SheetVol.Core.Tests.csproj
.\tools\test-headless.ps1 -Script .\tools\scripts\selftest.scr
```

The solution is `SheetVol.slnx` (the .NET 10 SDK's XML solution format), not `.sln`.
Build output goes to `C:\LAN-DT-build\<Project>\` when the repo sits inside the LAN
workspace (its root `Directory.Build.props` is chained); elsewhere it is in-tree.

## Platform

- .NET 10 SDK (`global.json`). Core: `net10.0`. Civil3D and UI: `net10.0-windows`, x64.
- Host floor: Civil 3D 2025.2.7 or 2026.2.2 and later, both .NET 10 runtimes. The
  plugin compiles against the AutoCAD 2025 managed DLLs (assembly version 25.0) so one
  DLL binds on 2025, 2026 and 2027 hosts. `C3D2027` configuration compiles against 2027.
- This seat: Civil 3D 2026.2.3 (13.8.1873.0), AutoCAD 25.1.179, Civil 3D 2025.2.7 and
  2027 also installed. Headless runs use the 2026 Core Console.
- `SheetVol.Civil3D` references AcCoreMgd, AcDbMgd, AecBaseMgd, AeccDbMgd only. Never
  AcMgd, never WPF. Use `Autodesk.AutoCAD.ApplicationServices.Core.Application`.
- No `BinaryFormatter`. `System.Text.Json` only.

## Rules every worker follows (HANDOFF.md 6.13)

- All database access inside a transaction; commit explicitly; never dispose objects
  obtained from `tr.GetObject`.
- Catch AutoCAD and Civil exceptions at the command boundary; print one line with a
  stable `SV-Exxx` code; abort the transaction.
- No AcMgd, WPF, WinForms or message boxes outside `SheetVol.UI`.
- No style, layer, text height or path literals outside config defaults.
- No silent fallbacks and no silently wrong numbers. When in doubt, fail the run and say why.
- Geometry in WCS. User picks are transformed from the current UCS.
- Contracts are frozen. A worker that needs a change stops and reports it.

## Headless gotchas (this seat)

- The LAN DT Framework's `acad.lsp` auto-loads in Core Console. Set `LAN_DT_BATCH=1`
  in the worker's environment so its per-open hooks stay quiet (`tools/test-headless.ps1`
  does this). Never touch `SECURELOAD` from a script.
- Core Console stdout is UTF-16LE. Close stdin so a stray prompt reads EOF.
- Scripts: LF line endings, UTF-8 without BOM, forward slashes in paths, command and
  its arguments on separate lines.

## Commit style

Plain declarative messages. No `Co-Authored-By` or AI trailers. Never bypass hooks.

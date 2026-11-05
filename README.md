# SheetVol

Civil 3D sheet-segment tool. Station-range boundaries along an alignment drive the
sheet viewports, and exact cut / fill / net tables are generated for each boundary
from a base and a comparison surface. One command, `SVREGEN`, rebuilds every
derived object in place.

Status: Wave 0 scaffold. Nothing user-facing exists yet. See `NOW.md` for the
current state and `HANDOFF.md` for the full plan.

## Requirements

- Civil 3D 2025.2.7, or 2026.2.2 and later (the .NET 10 host updates). Earlier
  builds run .NET 8 and cannot load the plugin.
- .NET 10 SDK to build.

## Build

```powershell
dotnet build .\SheetVol.slnx -c Debug
dotnet test  .\tests\SheetVol.Core.Tests\SheetVol.Core.Tests.csproj
.\tools\test-headless.ps1 -Script .\tools\scripts\p16.scr
```

## Layout

| Path | What |
|---|---|
| `src/SheetVol.Core` | Planning, geometry, volumes, views, tracking, presentation. No Autodesk references. |
| `src/SheetVol.Civil3D` | The engine: adapters, writers, commands, session, auto-update, self-test. Loads in Core Console. |
| `src/SheetVol.UI` | Modeless dialog, ribbon, editor-side session members. Full Civil 3D only. |
| `tests/SheetVol.Core.Tests` | xunit suite for Core. |
| `tools/` | Build and headless scripts, fixtures. |
| `docs/` | Frozen contracts, API findings, UI reference. |

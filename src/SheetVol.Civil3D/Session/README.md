# Session

Owner: A4 (HANDOFF.md section 10). Database side of ISheetVolSession, the regen pipeline, settings.

- `Ports.cs`: what A1 (`IAdapterFactory`, `IDrawingSources`), A2 and A3 (`IWriterFactory`, `IModelWriter`, `IPaperWriter`, `IPersistenceStore`) implement. Everything an adapter returns is valid only inside the transaction that opened it.
- `EngineBindings.cs`: the factories the commands use. Unwired (SV-E822) until the orchestrator sets `EngineBindings.Current` at integration.
- `RegenPipeline.cs`: `RegenEngine.Regenerate`, HANDOFF.md 6.10 steps 1 to 6, one transaction per run; `ExportFiles`.
- `OutputPlanner.cs`: what a run owns after regen (boundaries, marks, layouts, viewports, tables).
- `SheetVolSettings.cs`: `sheetvol.settings.json` in the plugin directory; `SettingsProvider`.
- `SheetVolSession.cs`: `ISheetVolSession`. Editor members go through `IEditorSide`, which U2 implements in `SheetVol.UI/Host`.
- `RunRecordCodec.cs`: the persisted run record as Xrecord strings (wraps C2's `RunPersistence`).
- `BreakEditor.cs`: SVBREAK's break-list rewrite.

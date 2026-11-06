# Session

Owner: A4 (HANDOFF.md section 10). Database side of ISheetVolSession, the regen pipeline, settings.

- `Ports.cs`: what the adapters (`IAdapterFactory`, `IDrawingSources`), the writers (`IWriterFactory`, `IModelWriter`, `IPaperWriter`) and the store (`IPersistenceStore`) implement, and the planned objects (`OwnedObject` records carrying A2's specs and A3's sheet inputs). Everything an adapter returns is valid only inside the transaction that opened it. Writers are whole-run appliers: `Apply(WriteRequest)` plans read-only, refuses with no writes on any error, then writes only what changed.
- `EngineBindings.cs`: the factories the commands use. `Integration/EngineStartup` installs the real ones at load; unwired (SV-E822) otherwise.
- `RegenPipeline.cs`: `RegenEngine.Regenerate`, HANDOFF.md 6.10 steps 1 to 6, one transaction per run; step 5 runs the model writer, then the paper writer last (nothing that can fail follows it: A1's create-erase-abort crash), then the store; `ExportFiles`.
- `OutputPlanner.cs`: what a run owns after regen (boundaries, A2 match marks and outlines, layouts, viewports, sheet tables, A2 model and HCFCD tables, master table).
- `SheetVolSettings.cs`: `sheetvol.settings.json` in the plugin directory, embedding A1's `adapters`, A2's `modelWriters` and A3's `paper` sections; `SettingsProvider`.
- `SheetVolSession.cs`: `ISheetVolSession`. Editor members go through `IEditorSide`; U2's `EditorSession` decorates this session in `SheetVol.UI/Host`.
- `RunRecordCodec.cs`: the record as one JSON (fake store, record signature) and C3's fingerprint as text for A2's store.
- `PipelineSupport.cs`: display polygon (C1's per-segment `BoundaryBuilder.Build` overload), `TransactionLifetime` and the lifetime-guarded alignment and volume wrappers, memoized volume source, cancel probe.
- `BreakEditor.cs`: SVBREAK's break-list rewrite.

## Rev 3

- `PipelineCodes.OhwmMissing` and the HCFCD no-OHWM refusal removed; step 1 refuses a `VolumeSurface` analysis with SV-E423 until rev3-adapters; `SheetVolSession.VolumeSurfaces` is empty.

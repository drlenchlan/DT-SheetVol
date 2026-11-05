# Integration

Owner: Wave 2 integration (HANDOFF.md section 10). The real bindings behind A4's ports; no
engine logic of its own.

| File | What |
|---|---|
| `EngineStartup.cs` | `IExtensionApplication` (Core Console safe): sets `EngineBindings.Current` to the installed factories and `SelfTestWorlds.Installed` at load |
| `CivilAdapterFactory.cs` | `IAdapterFactory` / `IDrawingSources` over A1: `DrawingUnitsReader`, `AlignmentAdapter.Find`, `AnalysisResolver`, `TriangleSourceAdapter`, `VolumeSourceAdapter`. Adds the `TransactionLifetime` guard (geometry and volume sources throw once their transaction ends) and the manual-segment polyline read |
| `CivilWriterFactory.cs` | `IWriterFactory`: model writer over A2's `ModelWriter.Apply`, paper writer over A3's `PaperWriter` (`LoadTemplate`, `Validate`, `Apply`), store over A2's `RunStore`, C3's fingerprint kept as text through `RunRecordCodec.FingerprintToText` |
| `CivilSelfTestWorld.cs` | The world edits of `SVSELFTEST Installed`: T12 apex, T14 lengthen S, T17 north alignment |

Step 5 order (Session/RegenPipeline.cs): model writer, then paper writer, then the store. The
paper writer replaces the template's cloned viewports by new ones and erases the clones; A1
measured that erasing an object created in the same transaction and then aborting can crash
the host (0xC0000005), so nothing that can fail runs after it.

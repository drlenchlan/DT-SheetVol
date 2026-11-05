# Host

Owner: U2 (HANDOFF.md section 10). Editor side of ISheetVolSession: pick, highlight, zoom; modeless window hosting.

## File map

| File | What |
|---|---|
| `DialogCommands.cs` | `SVDIALOG` (open, or bring to front) and `SVDIALOGSMOKE` (off-screen render and checks) |
| `DialogHost.cs` | The one window per Civil 3D session: session creation, `Application.ShowModelessWindow`, theme follow, command-state refresh, session-lifetime `PendingEdits` (E907) |
| `EditorSession.cs` | Decorator over the database-side session: editor members (highlight, zoom, picks), database members forwarded under a document lock or in command context, `SourcesChanged` on document activation and close, `IsCommandActive` from the document's command state |
| `CommandContext.cs` | The command-context route (below) and the document lock for synchronous reads |
| `CivilReads.cs` | Alignment name and `StationOffset`, typed (the csproj references `AecBaseMgd`) |
| `ThemeWatcher.cs` | `COLORTHEME` read and followed through `Application.SystemVariableChanged` (E908) |
| `SessionFactory.cs` | `ISessionFactory` and `SessionFactories.Register` |
| `DesignSession.cs` | Design-time session loaded with T2 (adapted from C4's test fake); no Autodesk types |
| `UiSmoke.cs` | Off-screen checks and PNG renders; no Autodesk types |
| `HostConfig.cs`, `HostText.cs` | Every host setting and every host string |

## Command-context route

Editor work runs through `DocumentCollection.ExecuteInCommandContextAsync` from the dialog's
application context, with the document locked inside the callback. It returns a `Task`, so a pick's
result comes straight back to the awaiting view model on the main thread; no helper command, no
command-line echo, no string escaping. `SendStringToExecute` with a continuation was rejected: it is
fire-and-forget (the result needs a second channel), echoes, and queues behind whatever is running.
When called from a document context (for example while `SVDIALOG` itself runs), the work runs in
place under the command's lock, because the call cannot be nested.

- The result travels through a `TaskCompletionSource` completed when the work finishes, so it does
  not depend on whether AutoCAD's own Task waits for an asynchronous callback (not verified).
- The work starts with a `DispatcherSynchronizationContext` current, so its awaits continue on the
  main thread; the lock is taken and released there.
- The callback refuses to run (message "The active drawing changed before the request ran") when
  the active document is no longer the one the request was made for.
- Whether AutoCAD keeps the command context across an await that is not `Editor.CommandAsync` is
  **not verified**. After such an await the work is still on the main thread with the document
  locked, which is what database writes from a modeless window need; it may no longer be in command
  context. See the A4 rules below.

The member exists and compiles against the AutoCAD 2025 reference set (MEASURED, this build); the
live behaviour in a modeless window is Dan's M check (E905, E906, E911).

Guards (from an independent review of this folder, 2026-11-05):

- **One piece of editor or engine work at a time.** Picks, Zoom to, Regenerate and Export run
  through `Exclusive`: refused while a command runs or while another piece is in flight (the dialog
  stays live during a pick prompt; a second request would otherwise run inside it). While one runs,
  `IsCommandActive` is true and the dialog's command state is re-queried at start and end.
- **Reads that never throw.** The view model calls `Validate`, `FormatStation` and the object
  lists on paths with no catch (an async-void command continuation, `Refresh`), where an exception
  would end the AutoCAD process. A failure there is one SV-E901 line and a value that says so:
  `Validate` returns an Error problem, `FormatStation` the raw number, the lists their last good
  value for this drawing. `LoadRun`, `GetRunStatus` and `ActiveDrawing` throw, because they are
  reached only from the window's constructor (SVDIALOG catches) and from `SourcesChanged`.
- **`SourcesChanged` is never raised while a regen runs**: held back and raised (inside a catch)
  when it ends, so the view model never takes its own post-regen rebind path, which has no catch.
- **Highlight geometry** is replaced only by a plan the dialog adopts (segments and no run-level
  error); the dialog refuses a candidate with a new run-level error and keeps the previous plan.

| Member | Route |
|---|---|
| `Highlight` | Main thread, no command context: transient graphics (`TransientManager`, `TransientDrawingMode.Highlight`) of the segment's boundary from the last `Validate` result. Opens no database object. Cleared on `Highlight(null)`, on document deactivation and on dispose |
| `ZoomTo` | Command context: `Editor.GetCurrentView` / `SetCurrentView` to the boundary extents times `HostConfig.ZoomMarginFactor`. Refused in paper space |
| `PickAlignmentAsync` | Command context: `GetEntity` filtered to Civil 3D alignments; reads the name in a transaction |
| `PickStationAsync` | Command context: `GetPoint`, UCS to WCS, `Alignment.StationOffset` on the named alignment (raw station). Design session: station = X |
| `RegenerateAsync`, `ExportAsync` | Command context, document locked, then the inner session |
| `Validate`, `LoadRun`, object lists, `FormatStation`, `StationRange`, `GetRunStatus`, `ActiveDrawing` | Main thread under `LockDocument` (application context) or in place (document context) |
| `IsCommandActive` | Inner session's value, or the active document's `CommandInProgress` is not empty |

Highlight, Zoom to and every handler raised from AutoCAD events never throw: a failure is one line on
the command line with its code. Picks throw `InvalidOperationException` with a plain message, which
the view model shows as "Pick did not complete: ...".

## What the engine session must do (A4)

- Do not marshal into the command context and do not call `ExecuteInCommandContextAsync`: the
  decorator already calls `RegenerateAsync` and `ExportAsync` in command context with the document
  locked, and nesting is not allowed. If the session is also used from commands, check
  `DocumentCollection.IsApplicationContext` rather than marshalling unconditionally.
- Do database work on the main thread. For progress and Cancel to show during a long regen (E906),
  `RegenerateAsync` must yield to the message loop between steps (for example `await Task.Yield()`;
  continuations come back to the main thread through the context the decorator installs) and check
  the token between steps.
- Capture the `Document` and `Database` once at the start of `RegenerateAsync` and use them
  throughout; never re-read `MdiActiveDocument` after a yield (the user can switch drawings while
  the dialog's message loop runs).
- Keep the write phase (6.10 step 5, one transaction) synchronous: no await between starting and
  committing the transaction. Yields belong between the read and compute steps 1 to 4, which write
  nothing. This keeps the writes inside whatever command context AutoCAD still holds.
- Honour the cancellation token: the dialog refuses to close while a regen runs (C4's rule), so a
  regen that ignores Cancel leaves a window that cannot be closed until it finishes.
- Raise `SourcesChanged` after external changes (typed `SVREGEN`, tracker staleness). The decorator
  defers and coalesces it onto the dispatcher, so raising it inside an AutoCAD event is safe.
- Editor members (`Highlight`, `ZoomTo`, picks) are never called on the engine session.

## Where the engine session comes from

`DialogHost.CreateInner` (Wave 2 integration), in this order: the design fixture when
`SHEETVOL_FAKE_SESSION=1` (UI work without an engine); a factory registered with
`SessionFactories.Register`; A4's `SheetVol.Civil3D.Session.SheetVolSession` over the installed
bindings, which `SheetVol.Civil3D`'s `EngineStartup` sets when that assembly is NETLOADed or
autoloaded. With none of these, SV-E900 says to NETLOAD `SheetVol.Civil3D.dll`. AutoCAD runs an
assembly's `IExtensionApplication` only when it loads the assembly itself, not when the CLR loads
it as a dependency of this one, so the engine DLL is loaded explicitly (the bundle lists it).

`SheetVolSession.RegenerateAsync` runs the regen synchronously inside the decorator's command
context: it does not yet yield between steps, so progress and Cancel do not repaint during a long
regen (E906, Dan's M check).

## Codes

| Code | Meaning |
|---|---|
| SV-E900 | The engine assembly is not loaded (or not wired) and the design fixture is not switched on; also the design session's "no files are written" export note |
| SV-E901 | SVDIALOG could not open, the dialog could not rebind to or read the active drawing, or releasing it on close did not complete |
| SV-E908 | The dialog could not follow the colour theme |
| SV-E909 | SVDIALOGSMOKE stopped |
| SV-E911 | Highlight or Zoom to did not complete |

## Settings (`HostConfig`)

`HighlightColorIndex` (4), `ZoomMarginFactor` (1.25), `FallbackStationPattern` (`0.##`), `FakeSessionVariable` (`SHEETVOL_FAKE_SESSION`)
and `FakeSessionEnabledValue` (`1`), `SmokeOutputFolder` (`%TEMP%\SheetVol\ui-smoke`),
`DesignRegenSteps` (10) and `DesignRegenStepDelay` (150 ms), `ColorThemeVariable` (`COLORTHEME`) and
`LightThemeValue` (1). The host passes `HostConfig.Default` and `PresentationConfig.Default` until the
engine's config file (A4) carries a UI section.

## Open items

- **Not run in Civil 3D yet:** every editor member and the command-context route compile against
  the AutoCAD 2025 reference set; their live behaviour is the M rows in `Dialog/README.md`.

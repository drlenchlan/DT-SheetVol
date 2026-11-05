# Dan's probe steps: P21, P15, P14

Owner: S3 (HANDOFF.md section 10, Wave 2). Results go into section C of
`docs/api-findings.md`.

Three probes need a person at full Civil 3D 2026 (HANDOFF.md section 13):

| Probe | What it answers | Needs | Time |
|---|---|---|---|
| P21 | How the SheetVol dialog can live in Civil 3D as a modeless WPF window: database work from a button, colour theme, command-active state, highlight, display scaling | A new empty drawing | about 10 min |
| P14 | How view frame geometry reads through the API, and how Create Sheets marks a Plan viewport | The view-frame fixture drawings (part 2) | making the fixtures; the probe itself can run headless |
| P15 | Which events fire for grip edits, match-line moves, alignment edits and reference synchronize | The same fixtures | about 15 min |

Every probe writes a timestamped log under `%TEMP%\sheetvol\`. Nothing is written to a
drawing you keep: P21 runs in a new drawing you discard; P14 only reads; P15 edits the
fixture source, so part 2 backs the fixtures up first.

`<checkout>` below is the name of the folder you build from: `SheetVol` for the main
checkout, `s3` for this worktree.

## 0. Build and stage the DLLs (once per build)

Close Civil 3D first: a DLL it has loaded cannot be overwritten.

In PowerShell, in the root of the checkout you build from:

```powershell
dotnet build .\SheetVol.slnx -c Debug
.\tools\test-headless.ps1 -Script .\tools\scripts\probeC-events.scr
$ui = (dotnet msbuild .\src\SheetVol.UI\SheetVol.UI.csproj -getProperty:OutDir -p:Configuration=Debug -nologo | Select-Object -Last 1).Trim()
if (-not [IO.Path]::IsPathRooted($ui)) { $ui = Join-Path (Resolve-Path .\src\SheetVol.UI) $ui }
$stage = Join-Path $env:APPDATA ("Autodesk\ApplicationPlugins\SheetVol-dev-" + (Split-Path (Get-Location) -Leaf) + ".bundle\Contents")
Copy-Item (Join-Path $ui 'SheetVol.UI.dll'), (Join-Path $ui 'SheetVol.UI.pdb') -Destination $stage -Force
Get-ChildItem $stage -Filter 'SheetVol.*.dll' | Select-Object Name, LastWriteTime
```

The second line stages `SheetVol.Civil3D.dll` and `SheetVol.Core.dll` into the trusted
folder and runs the headless half of P15 (about 10 s, exit 0). The runner does not stage
`SheetVol.UI.dll` (checked 2026-11-05: it copies only the engine project's output), so the
next four lines copy it beside the engine. The last line should list three DLLs with the
same build time: `SheetVol.Civil3D.dll`, `SheetVol.Core.dll`, `SheetVol.UI.dll`.

## 1. P21: the modeless window (`SVPROBEUI`)

In full Civil 3D 2026, in a new drawing from any template:

Keyboard focus matters: after clicking in the probe window, click once in the drawing area
before typing a command or pressing Esc, or the keys go to the window instead.

1. Load the engine, then the UI, by pasting these two lines at the command line. Each
   line names the staging folder `SheetVol-dev-SheetVol.bundle`; if your `<checkout>` is
   not `SheetVol`, change only the part after `SheetVol-dev-`:

   ```text
   (command "_.NETLOAD" (strcat (getenv "APPDATA") "\\Autodesk\\ApplicationPlugins\\SheetVol-dev-SheetVol.bundle\\Contents\\SheetVol.Civil3D.dll"))
   (command "_.NETLOAD" (strcat (getenv "APPDATA") "\\Autodesk\\ApplicationPlugins\\SheetVol-dev-SheetVol.bundle\\Contents\\SheetVol.UI.dll"))
   ```

2. Draw one line or circle anywhere.
3. Type `SVPROBEUI`. The probe window opens. The path of its log is at the top.
4. Section 1 of the window: click **R0**, **R1**, **R2**, **R3**, once each, in that order.
   R0 is the control and is expected to fail. R3 queues a command, so the command line
   prints `R3 SVPROBEUI_WRITE running as a command` a moment later; if nothing prints,
   move the mouse into the drawing area.
5. Section 2: click **Pick entity** and pick the line. If the log pane's `PICK` line does
   not say `picked`, click in the drawing and type `SVPROBEUI_PICK` instead. Click
   **Highlight()** and look at the drawing. Click **Transient outline** and look again.
   Click **Clear**. In section 4, tick each box that is true.
6. Click in the drawing, start `LINE` and stop at its first prompt. The status line should
   say a command is active and the section 1 and 2 buttons should grey out. Look, then
   click in the drawing and press Esc. Then tick the box if they greyed out.
7. Grip-drag the line you drew a short distance and release.
8. Click in the drawing, type `COLORTHEME`, enter `1` if the interface is dark (or `0` if
   it is light), and watch the window. Click in the drawing and set `COLORTHEME` back.
   Then tick the theme boxes that are true.
9. Drag a corner of the window smaller until it stops, then larger. If you have a second
   monitor with a different display scale, drag the window onto it and back. Click **Log
   size and DPI now**. Tick the clipping and resize boxes if true. Leave the window at a
   size different from the one it opened at.
10. Optional: type anything worth knowing in the note box and click **Add note to log**.
11. `QNEW` a second drawing while the window is open, then close that drawing.
12. Click **Pick entity** and pick the line again, then **Highlight()** and **Transient
    outline**. Close the probe window with its X and look at the drawing.
13. Type `SVPROBEUI` again. In the reopened window, tick "After closing the window, the
    highlight and outline were gone" if they were. Its `SIZE on open` line shows whether
    AutoCAD restored the size you left in step 9.
14. Close the probe window. Close the drawing without saving.

**Pass**, in `%TEMP%\sheetvol\probeUI.log`:

- `ShowModelessWindow returned: IsVisible=True` and `XAML: ProbeXamlPanel constructed`
- `OPEN: ... owned by AutoCAD main window=True`, and a `SIZE on open (Loaded)` line with a
  `DpiScale`
- `R0 threw ... eLockViolation` (the control). If R0 wrote instead, that is a finding, not
  a failure
- `R1 result: wrote SHEETVOL_PROBE/P21_R1`, `R2 inside the callback`, `R2 result`,
  `R3 SVPROBEUI_WRITE running as a command` and `R3 result: wrote`
- `PICK picked`, `HIGHLIGHT Entity.Highlight()`, `TRANSIENT ... returned True`, `CLEAR`
- `CMD WillStart 'LINE' active=1` and `CMD Cancelled 'LINE' active=0`; some line naming
  the grip command
- `THEME applied light` (or dark) `because` one of the three theme signals, after at least
  one `EVENT` line; which signal fired is the answer
- `FOLLOW` lines when the second drawing opens and closes
- `CLOSE ... (E911: highlight cleared on close)` from step 12, and `handler(s) removed`
- two `===== P21 SVPROBEUI open =====` sections, each with a `SIZE on open` line
- your `OBSERVED` lines
- no line with `SV-E198`. If one appears, send the log anyway: it is the finding

## 2. The view-frame fixtures (HANDOFF.md section 13)

P14 and P15 both use these. The API cannot create view frames, so they are made by hand
once. Use the same scratch data-shortcut project as the P6 sitting in
`docs/api-findings.md` section A.9 if you do that one too. Keep LAN templates out of these
files: the repo is not the place for firm content.

In full Civil 3D 2026, with the engine DLL loaded as in part 1, step 1:

1. Open `tools\fixtures\blank-c3d.dwg`, `SAVEAS` `tools\fixtures\frames-src.dwg`.
2. `SVFIXTURE` `A`, then `SVFIXTURE` `K`. K is two tangents with a 30° bend at station
   500, so frames on it rotate.
3. Create View Frames on alignment K, plan only, with a sheet size and scale that give at
   least three frames, one of them across the bend. The sheet template: Autodesk's stock
   Plan Production template if this seat has one; otherwise any non-LAN template with one
   layout and one floating viewport whose Viewport Type is set to Plan in Properties
   (FX's `SVFIXTURE` `TEMPLATEDWG` writes a one-layout drawing you can save as `.dwt`).
   Save.
4. Create Sheets from that group with "All layouts in one new drawing", saved as
   `tools\fixtures\frames-sheets.dwg` (the sheet set file can go to `%TEMP%`).
5. In the scratch shortcut project, create data shortcuts for alignment K and the view
   frame group from `frames-src.dwg`. Start a new drawing from `blank-c3d.dwg`, create a
   reference to the view frame group (and alignment K if asked), and save it as
   `tools\fixtures\frames-ref.dwg`.
6. Close all three. Copy them to a backup folder (or commit them): P15 edits and saves
   `frames-src.dwg`.

## 3. P14: view frame geometry (`SVPROBEFRAMES`)

Read only. It can run headless, so once the three fixtures exist any agent can run this
part; it is written out here so you can run it too.

In PowerShell, in the root of the checkout, after part 0:

```powershell
.\tools\test-headless.ps1 -Script .\tools\scripts\probeC-frames.scr -Drawing .\tools\fixtures\frames-src.dwg
.\tools\test-headless.ps1 -Script .\tools\scripts\probeC-frames.scr -Drawing .\tools\fixtures\frames-ref.dwg
.\tools\test-headless.ps1 -Script .\tools\scripts\probeC-frames.scr -Drawing .\tools\fixtures\frames-sheets.dwg
```

All three runs append to one log. To run it in full Civil 3D instead, open each drawing,
load the engine DLL as in part 1, step 1, and type `SVPROBEFRAMES`.

**Pass**, in `%TEMP%\sheetvol\probeFrames.log`:

- `frames-src`: `CivilDocument.GetViewFrameGroupIds(): 1`; per frame its stations,
  `Sheet=`, the explode tree, `SOLVE: rectangle = ...`, a verdict `hypothesis HOLDS` or
  `hypothesis DOES NOT HOLD` (either is an answer), and the independent solution; the
  `E712` line and one `join` line per pair of frames
- `frames-ref`: `IsReferenceObject=True` on the group and frames, then either an explode
  tree or `Explode threw` (either is an answer; a throw on reference frames sends L1 to
  stop condition 12.3)
- `frames-sheets`: every layout with its viewports, their `XData` and extension
  dictionaries, and a `frame '...' (Sheet names this layout)` line comparing the viewport
  twist and scale with the solved frame
- no line with `SV-E198`

## 4. P15: events for grip edits, match lines, alignment edits, synchronize

In full Civil 3D 2026, with `tools\fixtures\frames-src.dwg` open and the engine DLL
loaded as in part 1, step 1:

1. `SVPROBEEVENTS`.
2. Before each action type `SVPROBEMARK` and a short label, so the log reads in sections:
   1. `SVPROBEMARK` `grip PI`: grip-drag the bend PI of alignment K a few feet.
   2. `SVPROBEMARK` `U after grip`: `U`.
   3. `SVPROBEMARK` `layout tools`: change K in Alignment Layout Tools (drag a PI, or edit a
      tangent length in the grid view), close the tools.
   4. `SVPROBEMARK` `frame grip`: select a view frame, drag its slide grip, then its rotate
      grip.
   5. `SVPROBEMARK` `match line grip`: drag a match line grip along the alignment.
   6. `SVPROBEMARK` `surface edit`: optional, edit FG_A (for example raise a point).
3. `SAVE`.
4. Open `tools\fixtures\frames-ref.dwg`. The probe follows the newly active drawing on
   its own. `SVPROBEMARK` `synchronize`, then synchronize the out-of-date reference (the
   notification balloon, or Prospector > Data Shortcuts).
5. `SVPROBEEVENTSOFF`. It prints the totals.
6. Close both drawings without saving, and put the backup fixtures back.

**Pass**, in `%TEMP%\sheetvol\probeEvents.log`: one `MARK` section per action, each
holding `CommandWillStart` and `CommandEnded` (or `CommandCancelled`) lines naming the
command Civil 3D ran, `ObjectModified` lines naming the object classes it touched, and
`Idle (one-shot) after ...` lines; the synchronize section shows which objects a
synchronize modifies and under which command name. No line with `SV-E198`.

## 5. Sending the logs back

Read each log before sending: it holds drawing paths and nothing else about you. Then
either paste or attach the three files (`probeUI.log`, `probeFrames.log`,
`probeEvents.log`) in the orchestrator session, or commit them under `docs/probe-logs/`
and note it in the "Open items for Dan" table in `NOW.md`. The orchestrator records the
result there, and S3 (or whoever picks it up) writes it into section C of
`docs/api-findings.md`. Commit the three fixture drawings under `tools/fixtures/` so P14
can be rerun headless.

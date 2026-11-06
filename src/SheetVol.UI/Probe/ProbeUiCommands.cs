// S3 probe P21 (HANDOFF.md 5.3, 6.14). Full Civil 3D only: SheetVol.UI is never loaded in
// Core Console (E915).
//
// SVPROBEUI         opens the modeless probe window with
//                   Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessWindow(Window)
//                   (declared on Core.Application in AcCoreMgd and inherited by AcMgd's
//                   Application), or brings it forward when it is already open.
// SVPROBEUI_WRITE   the command the window's R3 button queues with SendStringToExecute: the
//                   same Xrecord write as R0-R2, from an ordinary command.
// SVPROBEUI_PICK    fallback pick for the Highlight tests when ExecuteInCommandContextAsync
//                   cannot prompt.
//
// Log: %TEMP%\sheetvol\probeUI.log (ProbeCLog, shared with the engine probes).

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using SheetVol.Civil3D.SelfTest;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

// No [assembly: CommandClass] here: SheetVol.UI must carry none (see Host/DialogCommands.cs).
// One assembly-level CommandClass makes AutoCAD register only that class, which hid SVDIALOG
// (MEASURED 2026-11-06, "Unknown command" in Civil 3D 2026 with the UI DLL loaded).

namespace SheetVol.UI.Probe;

public static class ProbeUiCommands
{
    private static readonly ProbeCLog Log = new(ProbeCSettings.UiLogName);
    private static ProbeUiWindow? _window;

    [CommandMethod("SVPROBEUI", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
    public static void Open()
    {
        var ed = CoreApp.DocumentManager.MdiActiveDocument?.Editor;
        if (_window is not null)
        {
            _window.Activate();
            _window.Log("SVPROBEUI: window already open; brought forward");
            return;
        }
        try
        {
            Log.Write("===== P21 SVPROBEUI open =====");
            foreach (var f in ProbeCLog.HostFacts()) Log.Write(f);
            Log.Write($"SheetVol.UI {typeof(ProbeUiCommands).Assembly.GetName().Version} from {typeof(ProbeUiCommands).Assembly.Location}; AcMgd {typeof(AcadApp).Assembly.GetName().Version}");
            var w = new ProbeUiWindow(Log);
            w.Closed += (_, _) => _window = null;
            _window = w;
            // AcMgd's Application derives from Core.Application (AcCoreMgd); the WPF overloads
            // ShowModelessWindow(Window), (Window|IntPtr owner, Window) and (..., bool
            // persistSizeAndPosition) are declared there. AcMgd's own overloads take a Uri.
            w.Log("calling Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessWindow(window), the one-argument overload inherited from Core.Application");
            AcadApp.ShowModelessWindow(w);
            w.Log($"ShowModelessWindow returned: IsVisible={w.IsVisible} IsActive={w.IsActive}");
            ed?.WriteMessage($"\nSVPROBEUI: probe window open. Log: {Log.FilePath}");
        }
        catch (System.Exception ex)
        {
            string line = $"{ProbeCSettings.FailCode} SVPROBEUI failed: {ProbeCCommands.Describe(ex)}";
            Log.Write(line);
            ed?.WriteMessage("\n" + line);
            // The window subscribed to AutoCAD events in its constructor; a window that was never
            // shown raises no Closed, so remove the handlers here.
            var w = _window;
            _window = null;
            if (w is not null)
            {
                try { w.Shutdown("ShowModelessWindow failed"); } catch (System.Exception e2) { Log.Write($"{ProbeCSettings.FailCode} shutdown after the failure threw {ProbeCCommands.Describe(e2)}"); }
                try { w.Close(); } catch (System.Exception e3) { Log.Write($"Close after the failure threw {ProbeCCommands.Describe(e3)}"); }
            }
        }
    }

    [CommandMethod("SVPROBEUI_WRITE", CommandFlags.Modal)]
    public static void WriteFromCommand()
    {
        var doc = CoreApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return;
        Say($"R3 {ProbeUiSettings.WriteCommand} running as a command: {ProbeUiWork.State(doc)}");
        try { Say("R3 result: " + ProbeUiWork.WriteRecord(doc, "R3")); }
        catch (System.Exception ex) { Say($"{ProbeCSettings.FailCode} R3 threw {ProbeCCommands.Describe(ex)}"); }
    }

    [CommandMethod("SVPROBEUI_PICK", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
    public static void PickFromCommand()
    {
        var doc = CoreApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return;
        try { Say($"PICK ({ProbeUiSettings.PickCommand}) " + ProbeUiWork.Pick(doc.Editor, out _)); }
        catch (System.Exception ex) { Say($"{ProbeCSettings.FailCode} PICK threw {ProbeCCommands.Describe(ex)}"); }
    }

    /// <summary>To the window's log pane when it is open (which also writes the file), else the file.</summary>
    private static void Say(string message)
    {
        if (_window is not null) _window.Log(message);
        else Log.Write(message);
        CoreApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + message);
    }
}

// Wave 0 probe P16: does a net10.0-windows command assembly load and run in both
// acad.exe and accoreconsole.exe on this host? SVHELLO prints the host, the
// runtime and the Civil 3D document state. Orchestrator-owned; workers leave it.

using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;

[assembly: CommandClass(typeof(SheetVol.Civil3D.Commands.HelloCommand))]

namespace SheetVol.Civil3D.Commands;

public static class HelloCommand
{
    [CommandMethod("SVHELLO", CommandFlags.Modal | CommandFlags.NoUndoMarker)]   // read-only (S3: NoUndoMarker on read-only commands, E804)
    public static void Hello()
    {
        var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        void Say(string s) { if (ed is not null) ed.WriteMessage("\n" + s); else Console.WriteLine(s); }

        var asm = typeof(HelloCommand).Assembly;
        Say($"SVHELLO ok. SheetVol.Civil3D {asm.GetName().Version} tfm={asm.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName}");
        Say($"Runtime: {RuntimeInformation.FrameworkDescription}  Process: {Environment.ProcessPath}");
        Say($"AcDbMgd: {typeof(Autodesk.AutoCAD.DatabaseServices.Database).Assembly.GetName().Version}  AeccDbMgd: {typeof(CivilDocument).Assembly.GetName().Version}");
        // Core Console has an Editor too (MEASURED 2026-11-05), so the process name is the test.
        Say($"Core Console: {string.Equals(Path.GetFileName(Environment.ProcessPath), "accoreconsole.exe", StringComparison.OrdinalIgnoreCase)}");

        try
        {
            var civ = CivilApplication.ActiveDocument;
            Say($"CivilApplication.ActiveDocument: {(civ is null ? "null" : "ok")}");
        }
        catch (System.Exception ex)
        {
            Say($"CivilApplication.ActiveDocument threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}

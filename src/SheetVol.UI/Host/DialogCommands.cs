using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

// No [assembly: CommandClass] in SheetVol.UI, on purpose: WPF's resource lookup reads the
// assembly's attributes (NeutralResourcesLanguage), which resolves every attribute type,
// so an assembly-level Autodesk attribute makes the window fail to load in any process
// without AcCoreMgd (MEASURED: FileNotFoundException 'accoremgd' from
// ResourceManager.CommonAssemblyInit in the plain-WPF smoke). AutoCAD scans the public
// types for [CommandMethod] instead.

namespace SheetVol.UI.Host;

/// <summary>SVDIALOG and its off-screen smoke check. Full AutoCAD only; the UI assembly is never loaded in Core Console (E915).</summary>
public static class DialogCommands
{
    /// <summary>Opens the modeless SheetVol dialog (HANDOFF.md 6.12, 6.14), or brings it to the front.</summary>
    [CommandMethod("SVDIALOG", CommandFlags.Modal)]
    public static void OpenDialog()
    {
        try
        {
            DialogHost.Show(HostConfig.Default, Say);
        }
        catch (System.Exception ex)
        {
            Say(string.Format(HostText.OpenFailedFormat, ex.Message));
        }
    }

    /// <summary>
    /// Builds the dialog off-screen against the design fixture, renders the artboard states to
    /// PNG and prints one PASS or FAIL line per check (XAML load, bindings, themes, minimum size,
    /// accessible names, 250-row virtualization). Writes nothing to the drawing.
    /// </summary>
    [CommandMethod("SVDIALOGSMOKE", CommandFlags.Modal)]
    public static void Smoke()
    {
        try
        {
            var folder = Environment.ExpandEnvironmentVariables(HostConfig.Default.SmokeOutputFolder);
            Say(string.Format(HostText.SmokeStartFormat, folder));
            var passed = UiSmoke.RunDesign(folder, Say, bindingErrorsFail: false);
            Say(string.Format(HostText.SmokeResultFormat, passed ? "PASS" : "FAIL"));
        }
        catch (System.Exception ex)
        {
            Say(string.Format(HostText.SmokeFailedFormat, ex.Message));
        }
    }

    private static void Say(string line) => AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + line);
}

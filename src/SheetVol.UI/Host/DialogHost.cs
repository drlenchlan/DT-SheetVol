using SheetVol.Core;
using SheetVol.Core.Presentation;
using SheetVol.UI.Dialog;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace SheetVol.UI.Host;

/// <summary>
/// Owns the one SVDIALOG window of the Civil 3D session (E901): creates the database-side
/// session (registered factory, or the design fixture when allowed), wraps it in the
/// editor-side <see cref="EditorSession"/>, shows the window modeless and resizable through
/// <see cref="AcApp.ShowModelessWindow(System.Windows.Window)"/>, follows the colour theme,
/// and re-queries the command state when the editor's state changes (E905). Pending edits
/// live here for the whole Civil 3D session, so closing and reopening restores them (E907).
/// </summary>
internal static class DialogHost
{
    private static readonly PendingEdits Pending = new();
    private static DialogWindow? _window;
    private static EditorSession? _session;
    private static ThemeWatcher? _theme;

    /// <summary>Shows the window, or brings the open one to the front.</summary>
    /// <param name="config">Host settings.</param>
    /// <param name="say">Writes one line to the command line.</param>
    public static void Show(HostConfig config, Action<string> say)
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        var inner = CreateInner(config, out var modeNote, out var refusal);
        if (inner is null)
        {
            say(refusal!);
            return;
        }

        var session = new EditorSession(inner, config);
        try
        {
            var window = new DialogWindow(session, Pending, PresentationConfig.Default, ThemeWatcher.Read(config), modeNote);
            session.CommandStateChanged += (_, _) => window.ViewModel.RefreshCommandState();
            _theme = new ThemeWatcher(config, window.ApplyTheme);
            _session = session;
            _window = window;
            window.Closed += OnClosed;
            AcApp.ShowModelessWindow(window);
        }
        catch
        {
            _theme?.Dispose();
            _theme = null;
            _session = null;
            _window = null;
            session.Dispose();
            throw;
        }
    }

    private static ISheetVolSession? CreateInner(HostConfig config, out string? modeNote, out string? refusal)
    {
        modeNote = null;
        refusal = null;
        if (SessionFactories.Registered is { } factory) return factory.Create();

        if (string.Equals(Environment.GetEnvironmentVariable(config.FakeSessionVariable), config.FakeSessionEnabledValue, StringComparison.Ordinal))
        {
            modeNote = HostText.DesignModeNote;
            return new DesignSession(
                PresentationConfig.Default.DefaultRunName,
                () => AcApp.DocumentManager.MdiActiveDocument is { } doc ? new DrawingInfo(doc.Name, UnitSystem.Imperial) : null,
                config.DesignRegenSteps,
                config.DesignRegenStepDelay);
        }

        refusal = string.Format(HostText.NoEngineSessionFormat, config.FakeSessionVariable, config.FakeSessionEnabledValue);
        return null;
    }

    /// <summary>Clears the single-instance state first, so SVDIALOG can always open a new window, then disposes; never throws (Window.Closed).</summary>
    private static void OnClosed(object? sender, EventArgs e)
    {
        var theme = _theme;
        var session = _session;
        _theme = null;
        _session = null;
        _window = null;
        DisposeQuietly(theme);
        DisposeQuietly(session);
    }

    private static void DisposeQuietly(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception ex)
        {
            AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + string.Format(HostText.CloseFailedFormat, ex.Message));
        }
    }
}

using Autodesk.AutoCAD.ApplicationServices;
using SheetVol.UI.Dialog;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace SheetVol.UI.Host;

/// <summary>
/// Follows AutoCAD's colour theme (E908): reads <see cref="HostConfig.ColorThemeVariable"/>
/// (COLORTHEME: 1 light, 0 dark) and re-reads it on <see cref="AcApp.SystemVariableChanged"/>,
/// handing the result to the window, which swaps its colour dictionary without reopening.
/// </summary>
internal sealed class ThemeWatcher : IDisposable
{
    private readonly HostConfig _config;
    private readonly Action<DialogTheme> _apply;
    private bool _disposed;

    /// <summary>Starts following the theme; <paramref name="apply"/> runs on every change.</summary>
    public ThemeWatcher(HostConfig config, Action<DialogTheme> apply)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        AcApp.SystemVariableChanged += OnSystemVariableChanged;
    }

    /// <summary>The theme AutoCAD shows now.</summary>
    public static DialogTheme Read(HostConfig config)
        => Convert.ToInt32(AcApp.GetSystemVariable(config.ColorThemeVariable)) == config.LightThemeValue
            ? DialogTheme.Light
            : DialogTheme.Dark;

    /// <summary>Stops following the theme.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AcApp.SystemVariableChanged -= OnSystemVariableChanged;
    }

    private void OnSystemVariableChanged(object? sender, SystemVariableChangedEventArgs e)
    {
        if (_disposed || !string.Equals(e.Name, _config.ColorThemeVariable, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            _apply(Read(_config));
        }
        catch (Exception ex)
        {
            AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + string.Format(HostText.ThemeFailedFormat, ex.Message));
        }
    }
}

namespace SheetVol.UI.Host;

/// <summary>
/// Settings of the dialog host and the editor side of the session (HANDOFF.md 6.13: no
/// style, colour or path literal outside config defaults). Built-in values are defaults;
/// the host passes <see cref="Default"/> until the engine's config file (A4) carries a
/// UI section, at which point the loader builds an instance from it.
/// </summary>
public sealed record HostConfig
{
    /// <summary>Built-in defaults.</summary>
    public static HostConfig Default { get; } = new();

    /// <summary>AutoCAD colour index of the transient boundary highlight drawn for the selected row (E911).</summary>
    public short HighlightColorIndex { get; init; } = 4;

    /// <summary>Zoom to: the view shows the boundary's extents times this factor, so the boundary is not drawn on the window edge.</summary>
    public double ZoomMarginFactor { get; init; } = 1.25;

    /// <summary>Raw station text shown when the drawing cannot format a station (the read failed and was reported).</summary>
    public string FallbackStationPattern { get; init; } = "0.##";

    /// <summary>
    /// Environment variable that lets SVDIALOG open against the design fixture when no engine
    /// session is registered (before A4 merges). Compared with <see cref="FakeSessionEnabledValue"/>.
    /// </summary>
    public string FakeSessionVariable { get; init; } = "SHEETVOL_FAKE_SESSION";

    /// <summary>Value of <see cref="FakeSessionVariable"/> that turns the design fixture on.</summary>
    public string FakeSessionEnabledValue { get; init; } = "1";

    /// <summary>Folder SVDIALOGSMOKE writes its renders to. Environment variables are expanded.</summary>
    public string SmokeOutputFolder { get; init; } = @"%TEMP%\SheetVol\ui-smoke";

    /// <summary>Design fixture: number of progress steps a regen reports, so progress and Cancel can be seen (E906).</summary>
    public int DesignRegenSteps { get; init; } = 10;

    /// <summary>Design fixture: delay per progress step.</summary>
    public TimeSpan DesignRegenStepDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>AutoCAD system variable that holds the colour theme (E908).</summary>
    public string ColorThemeVariable { get; init; } = "COLORTHEME";

    /// <summary>Value of <see cref="ColorThemeVariable"/> for the light theme; any other value is dark.</summary>
    public int LightThemeValue { get; init; } = 1;
}

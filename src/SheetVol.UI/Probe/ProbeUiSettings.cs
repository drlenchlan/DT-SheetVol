// S3 probe P21. Every name and number the SVPROBEUI window uses, in one place. Probe-only
// values: the product dialog's sizes, names and theme brushes belong to U2 and its config.

namespace SheetVol.UI.Probe;

internal static class ProbeUiSettings
{
    public const string Title = "SheetVol probe P21: modeless WPF window";
    public const double Width = 780, Height = 680, MinWidth = 560, MinHeight = 480;

    /// <summary>Named Objects Dictionary entry the four write routes put one Xrecord each into.
    /// Invisible in the drawing; Dan runs the probe in a new drawing and discards it.</summary>
    public const string ProbeDictionary = "SHEETVOL_PROBE";
    public const string EntryPrefix = "P21_";

    /// <summary>The command the R3 route queues with SendStringToExecute, and the pick fallback.</summary>
    public const string WriteCommand = "SVPROBEUI_WRITE";
    public const string PickCommand = "SVPROBEUI_PICK";

    /// <summary>AutoCAD's theme variable: 0 = dark, 1 = light.</summary>
    public const string ColorThemeVariable = "COLORTHEME";

    /// <summary>Transient outline drawn around the picked entity's extents.</summary>
    public const short TransientColorIndex = 1;
    public const double TransientPadFraction = 0.05;
    public const int TransientSubDrawingMode = 128;

    public const int LogPaneMaxLines = 300;
    public const double LogPaneHeight = 130;
    public const int ResizeSettleMs = 500;

    /// <summary>Probe brushes, ARGB, dark then light. Keys are referenced dynamically by the
    /// code-built window and by the XAML panel, so a theme switch restyles both.</summary>
    public static readonly (string Key, uint Dark, uint Light)[] Brushes =
    {
        (BackgroundKey, 0xFF2B2F36, 0xFFF3F3F3),
        (PanelKey, 0xFF353A42, 0xFFFFFFFF),
        (ForegroundKey, 0xFFE6E6E6, 0xFF1E1E1E),
        (BorderKey, 0xFF4A505A, 0xFFC4C4C4),
        (ButtonKey, 0xFF424852, 0xFFE9E9E9),
    };

    // The XAML panel names these keys literally ({DynamicResource SV.Probe.Panel}); keep both in step.
    public const string BackgroundKey = "SV.Probe.Background";
    public const string PanelKey = "SV.Probe.Panel";
    public const string ForegroundKey = "SV.Probe.Foreground";
    public const string BorderKey = "SV.Probe.Border";
    public const string ButtonKey = "SV.Probe.Button";

    /// <summary>What Dan ticks after looking at the screen; each tick is logged.</summary>
    public static readonly string[] Observations =
    {
        "Highlight() was visible in the drawing",
        "Transient outline was visible in the drawing",
        "Highlight and outline disappeared after Clear",
        "After closing the window, the highlight and outline were gone (tick in the reopened window)",
        "Window restyled when COLORTHEME changed (no reopen)",
        "The XAML panel restyled too",
        "Buttons greyed out while a command was running",
        "Some text was clipped or cut off at this display scaling",
        "Window resized freely and stopped at a minimum size",
    };
}

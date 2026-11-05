namespace SheetVol.Core.Tracking;

/// <summary>
/// Settings for the auto-update policy (6.11). Placeholder defaults; the run config supplies
/// the real values. Command names are AutoCAD global command names, compared
/// case-insensitively after the leading <see cref="CommandPrefixCharacters"/> are removed.
/// </summary>
public sealed record TrackingConfig
{
    /// <summary>Commands during which the tracker never fires and whose changes are discarded (E804).</summary>
    public IReadOnlyList<string> UndoCommandNames { get; init; } = ["U", "UNDO", "REDO", "MREDO"];

    /// <summary>
    /// The tool's own commands. Changes made while one runs are the tool's own writes and are
    /// discarded, so a regen never triggers another (E804).
    /// </summary>
    public IReadOnlyList<string> ToolCommandNames { get; init; } =
        ["SVDEFINE", "SVANALYSIS", "SVBREAK", "SVADOPT", "SVREGEN", "SVSTATUS", "SVAUTO",
         "SVEXPORT", "SVPURGE", "SVCLEAR", "SVSELFTEST", "SVDIALOG"];

    /// <summary>Leading characters stripped from a command name before matching ("_UNDO" and ".UNDO" are UNDO).</summary>
    public string CommandPrefixCharacters { get; init; } = "_.";

    /// <summary>
    /// The one-time message when an auto-update fails and the run drops to report-only (E806).
    /// Composite format: {0} run name, {1} failure reason.
    /// </summary>
    public string ReportOnlyMessageFormat { get; init; } =
        "SheetVol: auto-update of run '{0}' failed: {1} The run is report-only until SVREGEN succeeds or SVAUTO is set again.";
}

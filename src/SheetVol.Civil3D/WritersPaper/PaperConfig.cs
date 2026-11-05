// A3 work package (HANDOFF.md section 10, Wave 2). Owner: A3.
//
// Every name, number and format the paper writers use that the analysis does not govern
// (HANDOFF.md 6.13, the settings-file rule). Defaults marked PLACEHOLDER stand until LAN
// names arrive (section 13); defaults marked ASSUMED are judgement calls awaiting Dan.
// The table anchor block name is NOT here: it lives in ReportConfig.Placement (C2) and is
// passed to PaperWriter, so the fact has one home. Table style, layer and text height come
// with each TableModel (C2's TableStyleSettings).

using System.Globalization;
using SheetVol.Core;
using SheetVol.Core.Views;

namespace SheetVol.Civil3D.WritersPaper;

/// <summary>Settings for the paper writers (6.7). Loaded by A4 from the run config.</summary>
public sealed record PaperConfig
{
    /// <summary>
    /// Path of the sheet template (a DWT, or a DWG holding the template layout). Read with
    /// ReadDwgFile, never opened as a document. No default: an empty value fails the run
    /// before any write (E601).
    /// </summary>
    public string TemplatePath { get; init; } = "";

    /// <summary>Name of the layout in the template that every generated layout is cloned from (6.7). No default (E601).</summary>
    public string TemplateLayout { get; init; } = "";

    /// <summary>Layer that marks the plan viewport in the template (6.7). When no floating viewport is on it, the largest floating viewport is the plan viewport.</summary>
    public string PlanViewportLayer { get; init; } = "SV-VPORT";

    /// <summary>
    /// Fallback table top-left corner on paper, paper units, used when the template layout has
    /// no table anchor block reference (6.7). Null: no fallback, and a template without an
    /// anchor fails the run (E601).
    /// </summary>
    public Pt? TablePaperPoint { get; init; }

    /// <summary>Layer for the viewport clip polyline. Null puts the clip on the plan viewport's own layer. A named layer that is missing fails the run (E601).</summary>
    public string? ClipLayer { get; init; }

    /// <summary>
    /// Renumber existing layouts when sheets are inserted or removed (E307). True: every owned
    /// layout is renamed to its pattern name through C3's two-pass plan. False: existing layouts
    /// keep their names; only new layouts take pattern names. ASSUMED default true.
    /// </summary>
    public bool RenumberLayouts { get; init; } = true;

    /// <summary>
    /// Title-block attribute fill (6.7): attribute tag to value pattern. Patterns use the layout
    /// name tokens of C3's LayoutNamer: {RUN}, {N}, {LABEL}, {FROM}, {TO}, {ALIGN}. Tags are
    /// matched case-insensitively. A tag not found on the layout is skipped with a note (E615).
    /// PLACEHOLDER: the three examples from 6.7.
    /// </summary>
    public IReadOnlyDictionary<string, string> TitleAttributes { get; init; } = new Dictionary<string, string>
    {
        ["STA_FROM"] = "{FROM}",
        ["STA_TO"] = "{TO}",
        ["SHEET_NO"] = "{N}",
    };

    /// <summary>Invariant .NET numeric format for {N} in title attributes. Layout names use LayoutNameConfig.SheetNumberFormat.</summary>
    public string SheetNumberFormat { get; init; } = "0";

    /// <summary>
    /// Name given to an annotation scale the writer has to add because no scale of the view's
    /// ratio exists (E605, P17). {0} is model units per paper unit, invariant culture with
    /// <see cref="AnnotationScaleNumberFormat"/>. Existing scales are found by ratio, never by
    /// name. PLACEHOLDER, matching the stock civil list ("1:50").
    /// </summary>
    public string AnnotationScaleNameFormat { get; init; } = "1:{0}";

    /// <summary>Invariant .NET numeric format for the ratio in <see cref="AnnotationScaleNameFormat"/>.</summary>
    public string AnnotationScaleNumberFormat { get; init; } = "0.######";

    /// <summary>Column widths of a new paper table, paper units, left to right; the last width repeats for further columns. PLACEHOLDER.</summary>
    public IReadOnlyList<double> TableColumnWidths { get; init; } = [2.5, 1.5];

    /// <summary>Row height of a new paper table, paper units. AutoCAD grows a row to fit its text. PLACEHOLDER.</summary>
    public double TableRowHeight { get; init; } = 0.3;

    /// <summary>
    /// Paper-space view of a new layout: the extents of the cloned template content times this
    /// factor (1 = exactly the extents, as ZOOM Extents). Without the fit the plan viewport can sit
    /// outside the window Layout.Initialize() leaves and show nothing (E614).
    /// </summary>
    public double OverallViewMargin { get; init; } = 1.0;

    /// <summary>Extension-dictionary entry that holds the owner Xrecord on every object the writers create or fill.</summary>
    public string OwnerTagKey { get; init; } = "SHEETVOL_OWNER";

    /// <summary>A viewport target within this distance of the planned model centre counts as unchanged, drawing units.</summary>
    public double ModelTolerance { get; init; } = 1e-6;

    /// <summary>A twist within this angle of the planned twist counts as unchanged, radians.</summary>
    public double AngleTolerance { get; init; } = 1e-9;

    /// <summary>A CustomScale or annotation scale ratio within this relative difference counts as unchanged.</summary>
    public double ScaleRelativeTolerance { get; init; } = 1e-9;

    /// <summary>A clip vertex within this distance of its planned paper position counts as unchanged, paper units.</summary>
    public double PaperTolerance { get; init; } = 1e-9;

    /// <summary>A clip bulge within this difference of the planned bulge counts as unchanged.</summary>
    public double BulgeTolerance { get; init; } = 1e-12;

    /// <summary>Every problem with the values; empty when usable. Each is an SV-E691 error.</summary>
    public IReadOnlyList<Problem> Validate()
    {
        var p = new List<Problem>();
        void Bad(string key, string why) => p.Add(new Problem(PaperCodes.ConfigInvalid, Severity.Error, $"Paper config {key}: {why}", Fix: $"Correct {key} in the run config."));
        void Text(string key, string? v) { if (string.IsNullOrWhiteSpace(v)) Bad(key, "must not be empty."); }
        void Positive(string key, double v) { if (!double.IsFinite(v) || v <= 0) Bad(key, "must be a number above zero."); }

        Text("planViewportLayer", PlanViewportLayer);
        Text("ownerTagKey", OwnerTagKey);
        if (ClipLayer is not null) Text("clipLayer", ClipLayer);
        if (TablePaperPoint is Pt tp && (!double.IsFinite(tp.X) || !double.IsFinite(tp.Y))) Bad("tablePaperPoint", "must be two finite numbers.");
        Positive("tableRowHeight", TableRowHeight);
        if (!double.IsFinite(OverallViewMargin) || OverallViewMargin < 1) Bad("overallViewMargin", "must be a number of 1 or more.");
        if (TableColumnWidths is null || TableColumnWidths.Count == 0) Bad("tableColumnWidths", "must list at least one width.");
        else for (int i = 0; i < TableColumnWidths.Count; i++) Positive($"tableColumnWidths[{i}]", TableColumnWidths[i]);
        Positive("modelTolerance", ModelTolerance);
        Positive("angleTolerance", AngleTolerance);
        Positive("scaleRelativeTolerance", ScaleRelativeTolerance);
        Positive("paperTolerance", PaperTolerance);
        Positive("bulgeTolerance", BulgeTolerance);

        if (string.IsNullOrWhiteSpace(AnnotationScaleNameFormat) || !AnnotationScaleNameFormat.Contains("{0}", StringComparison.Ordinal))
            Bad("annotationScaleNameFormat", "must contain {0}.");
        else
        {
            try { _ = string.Format(CultureInfo.InvariantCulture, AnnotationScaleNameFormat, 50.0.ToString(AnnotationScaleNumberFormat, CultureInfo.InvariantCulture)); }
            catch (FormatException) { Bad("annotationScaleNameFormat / annotationScaleNumberFormat", "is not a valid format."); }
        }
        try { _ = 1.ToString(SheetNumberFormat, CultureInfo.InvariantCulture); }
        catch (FormatException) { Bad("sheetNumberFormat", "is not a valid numeric format."); }

        if (TitleAttributes is null) Bad("titleAttributes", "must be a map (it may be empty).");
        else
        {
            var sample = new LayoutNameParts("RUN", 1, "1", "0+00", "5+00", "ALIGN");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (tag, pattern) in TitleAttributes)
            {
                if (string.IsNullOrWhiteSpace(tag)) { Bad("titleAttributes", "has an empty tag."); continue; }
                if (!seen.Add(tag)) Bad($"titleAttributes.{tag}", "is listed twice (tags compare case-insensitively).");
                if (pattern is null) { Bad($"titleAttributes.{tag}", "has no pattern."); continue; }
                try { _ = LayoutNamer.FormatPattern(pattern, sample, SheetNumberFormat); }
                catch (FormatException ex) { Bad($"titleAttributes.{tag}", ex.Message); }
            }
        }
        return p;
    }

    /// <summary>Width of table column <paramref name="column"/>; the last configured width repeats.</summary>
    internal double ColumnWidth(int column) => TableColumnWidths[Math.Min(column, TableColumnWidths.Count - 1)];

    /// <summary>The name for an added annotation scale of the given ratio.</summary>
    internal string AnnotationScaleName(double modelUnitsPerPaperUnit)
        => string.Format(CultureInfo.InvariantCulture, AnnotationScaleNameFormat,
            modelUnitsPerPaperUnit.ToString(AnnotationScaleNumberFormat, CultureInfo.InvariantCulture));
}

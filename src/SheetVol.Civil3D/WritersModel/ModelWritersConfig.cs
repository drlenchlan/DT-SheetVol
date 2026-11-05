// A2 model writers (HANDOFF.md section 10, Wave 2). Owner: A2.
//
// Every layer, text style, text height, gap and text template the model writers use that
// C2's ReportConfig does not already hold. Table style, table layer and table text height
// come from ReportConfig (SegmentTable.Style, MasterTable.Style, Hcfcd.Style); the anchor
// block name from ReportConfig.Placement; the persistence dictionary from
// ReportConfig.Persistence. No such literal appears anywhere else in WritersModel
// (HANDOFF.md 6.13). Defaults marked PLACEHOLDER stand until LAN names arrive (A7);
// defaults marked ASSUMED are judgement calls awaiting Dan.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Volumes;

namespace SheetVol.Civil3D.WritersModel;

/// <summary>A point on a layout, paper units.</summary>
/// <param name="X">X, paper units.</param>
/// <param name="Y">Y, paper units.</param>
public sealed record PaperPoint(double X, double Y);

/// <summary>Match-line marks (6.8): a line across the band at a shared break plus the text.</summary>
public sealed record MatchMarkConfig
{
    /// <summary>Draw match-line marks. ASSUMED default true (6.8 calls them optional).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Layer for the line and the text. PLACEHOLDER (A7). A missing layer fails the run (SV-E501).</summary>
    public string Layer { get; init; } = "SV-MATCHLINE";

    /// <summary>Text style of the mark text. A missing style fails the run (SV-E501).</summary>
    public string TextStyle { get; init; } = "Standard";

    /// <summary>Text height in paper units; model-space marks are scaled by the view scale. PLACEHOLDER (A7).</summary>
    public double TextHeight { get; init; } = 0.1;

    /// <summary>Gap between the match line and the text, paper units.</summary>
    public double TextGap { get; init; } = 0.05;

    /// <summary>Mark text: {0} displayed station, {1} the other sheet's label (6.8).</summary>
    public string TextTemplate { get; init; } = "MATCH LINE STA {0} — SEE SHEET {1}";

    /// <summary>
    /// Fraction of the segment length used to sample the direction of travel at a break, so
    /// the text sits on the segment's own side of the line. Must be above 0 and at most 0.5.
    /// </summary>
    public double DirectionSampleFraction { get; init; } = 1e-3;
}

/// <summary>How model-space and layout tables are laid out when they are first created.</summary>
public sealed record TableLayoutConfig
{
    /// <summary>Gap between a segment's boundary and its model-space table, and between the run and the model-space master table, paper units.</summary>
    public double PlacementGap { get; init; } = 0.25;

    /// <summary>Estimated character width as a multiple of text height, for sizing columns at creation.</summary>
    public double CharacterWidthFactor { get; init; } = 0.9;

    /// <summary>Cell margin on each side as a multiple of text height, for sizing columns at creation.</summary>
    public double CellMarginFactor { get; init; } = 0.75;

    /// <summary>Row height as a multiple of text height at creation; AutoCAD grows a row when its text needs more.</summary>
    public double RowHeightFactor { get; init; } = 2.0;

    /// <summary>Narrowest column at creation, as a multiple of text height.</summary>
    public double MinimumColumnWidthFactor { get; init; } = 4.0;

    /// <summary>
    /// Table top-left on a layout when the layout holds no anchor block (6.7). Null means
    /// none: a table to be created on a layout with neither fails with SV-E601.
    /// </summary>
    public PaperPoint? TablePaperPoint { get; init; }
}

/// <summary>
/// The A2 config record. Composed by A4 into the SheetVol settings file beside C2's
/// <see cref="ReportConfig"/>; loads from JSON with <see cref="FromJson"/>, unknown keys rejected.
/// </summary>
public sealed record ModelWritersConfig
{
    /// <summary>Layer of the boundary polylines. PLACEHOLDER (A7). A missing layer fails the run (SV-E501).</summary>
    public string BoundaryLayer { get; init; } = "SV-BOUNDARY";

    /// <summary>Draw the impacted-area outline when the preset is HcfcdSegment (6.15).</summary>
    public bool DrawImpactOutline { get; init; } = true;

    /// <summary>Layer of the impacted-area outline polylines. PLACEHOLDER (A7). A missing layer fails the run (SV-E501).</summary>
    public string ImpactOutlineLayer { get; init; } = "SV-IMPACT";

    /// <summary>Extension-dictionary key of the ownership tag on every object the writers create.</summary>
    public string OwnerTagKey { get; init; } = "SHEETVOL_OWNER";

    /// <summary>Match-line marks.</summary>
    public MatchMarkConfig MatchMarks { get; init; } = new();

    /// <summary>Table layout at creation.</summary>
    public TableLayoutConfig Tables { get; init; } = new();

    /// <summary>The built-in defaults.</summary>
    public static ModelWritersConfig Default { get; } = new();

    /// <summary>Setting paths whose default is a placeholder for a LAN name (A7).</summary>
    public static IReadOnlyList<string> PlaceholderSettings { get; } =
        ["boundaryLayer", "impactOutlineLayer", "matchMarks.layer", "matchMarks.textHeight"];

    /// <summary>Setting paths whose default is an assumption awaiting confirmation.</summary>
    public static IReadOnlyList<string> AssumedSettings { get; } = ["matchMarks.enabled"];

    /// <summary>Every problem with the values; empty when usable. Each is an SV-E534 error.</summary>
    public IReadOnlyList<Problem> Validate()
    {
        var p = new List<Problem>();
        void Bad(string path, string why) => p.Add(new Problem(OutputCodes.ConfigInvalid, Severity.Error, $"Config modelWriters.{path}: {why}"));
        void Text(string path, string? v) { if (string.IsNullOrWhiteSpace(v)) Bad(path, "must not be empty."); }
        void Positive(string path, double v) { if (!double.IsFinite(v) || v <= 0) Bad(path, "must be a number above zero."); }
        void NonNegative(string path, double v) { if (!double.IsFinite(v) || v < 0) Bad(path, "must be a number of zero or more."); }

        Text("boundaryLayer", BoundaryLayer);
        Text("impactOutlineLayer", ImpactOutlineLayer);
        Text("ownerTagKey", OwnerTagKey);

        var m = MatchMarks;
        Text("matchMarks.layer", m.Layer);
        Text("matchMarks.textStyle", m.TextStyle);
        Positive("matchMarks.textHeight", m.TextHeight);
        NonNegative("matchMarks.textGap", m.TextGap);
        if (string.IsNullOrWhiteSpace(m.TextTemplate)) Bad("matchMarks.textTemplate", "must not be empty.");
        else
        {
            try { _ = string.Format(CultureInfo.InvariantCulture, m.TextTemplate, "x", "y"); }
            catch (FormatException) { Bad("matchMarks.textTemplate", "is not a valid format with arguments {0} and {1}."); }
        }
        if (!double.IsFinite(m.DirectionSampleFraction) || m.DirectionSampleFraction <= 0 || m.DirectionSampleFraction > 0.5)
            Bad("matchMarks.directionSampleFraction", "must be above 0 and at most 0.5.");

        var t = Tables;
        NonNegative("tables.placementGap", t.PlacementGap);
        Positive("tables.characterWidthFactor", t.CharacterWidthFactor);
        NonNegative("tables.cellMarginFactor", t.CellMarginFactor);
        Positive("tables.rowHeightFactor", t.RowHeightFactor);
        Positive("tables.minimumColumnWidthFactor", t.MinimumColumnWidthFactor);
        if (t.TablePaperPoint is { } pp && (!double.IsFinite(pp.X) || !double.IsFinite(pp.Y)))
            Bad("tables.tablePaperPoint", "must hold finite x and y.");
        return p;
    }

    /// <summary>Reads the config from JSON (camelCase, comments allowed). Missing keys keep defaults; unknown keys are errors.</summary>
    public static Outcome<ModelWritersConfig> FromJson(string json)
    {
        ModelWritersConfig? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<ModelWritersConfig>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Outcome<ModelWritersConfig>.Fail(new Problem(OutputCodes.ConfigInvalid, Severity.Error, $"Config modelWriters JSON is invalid: {ex.Message}"));
        }
        if (cfg is null)
            return Outcome<ModelWritersConfig>.Fail(new Problem(OutputCodes.ConfigInvalid, Severity.Error, "Config modelWriters JSON is empty."));
        var problems = cfg.Validate();
        return problems.Count > 0 ? Outcome<ModelWritersConfig>.Fail(problems) : Outcome<ModelWritersConfig>.Ok(cfg);
    }

    /// <summary>Writes this config as indented JSON in the shape <see cref="FromJson"/> reads.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
    };
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SheetVol.Core.Volumes;

namespace SheetVol.Core.Output;

/// <summary>Table style, layer and text height for one kind of table. Every default is a PLACEHOLDER until LAN names arrive (A7).</summary>
public sealed record TableStyleSettings
{
    /// <summary>AutoCAD table style name. PLACEHOLDER (A7). A missing style fails the run (E501).</summary>
    public string TableStyle { get; init; } = "SV-Volumes";

    /// <summary>Layer for the table. PLACEHOLDER (A7). A missing layer fails the run (E501).</summary>
    public string Layer { get; init; } = "SV-TABLE";

    /// <summary>Text height in paper units (inches or millimetres). A model-space writer scales it by the view scale. PLACEHOLDER (A7).</summary>
    public double TextHeight { get; init; } = 0.1;
}

/// <summary>Number formats, decimals and fixed words used in table cells (6.8, A12). Exports never use these: they are invariant and unrounded (E508).</summary>
public sealed record FormatConfig
{
    /// <summary>Culture for table cells, so tables look the same on every seat whatever the regional settings (E508).</summary>
    public string TableCultureName { get; init; } = "en-US";

    /// <summary>Volume decimals: whole cubic yards or cubic metres (A12, 6.16 <c>#,##0</c>).</summary>
    public int VolumeDecimals { get; init; } = 0;

    /// <summary>Area decimals in square feet (6.5).</summary>
    public int SquareFeetDecimals { get; init; } = 0;

    /// <summary>Area decimals in acres (6.5, A12 <c>0.00</c>; A12 notes the decimals still need confirming).</summary>
    public int AcresDecimals { get; init; } = 2;

    /// <summary>Area decimals in square metres (6.5).</summary>
    public int SquareMetresDecimals { get; init; } = 0;

    /// <summary>Area decimals in hectares (6.5).</summary>
    public int HectaresDecimals { get; init; } = 2;

    /// <summary>Length decimals: whole feet or metres (A12, 6.16 <c>#,##0</c>).</summary>
    public int LengthDecimals { get; init; } = 0;

    /// <summary>Average (volume per length) decimals (A12, 6.16 <c>0.00</c>).</summary>
    public int AverageDecimals { get; init; } = 2;

    /// <summary>Coverage percent decimals.</summary>
    public int CoverageDecimals { get; init; } = 1;

    /// <summary>Group thousands in table numbers (<c>#,##0</c>).</summary>
    public bool UseThousandsSeparator { get; init; } = true;

    /// <summary>.NET format for cut and fill factors in footers. Shows every significant digit up to six places.</summary>
    public string FactorFormat { get; init; } = "0.0#####";

    /// <summary>Extra decimals beyond the column's display decimals when a footnote shows an unrounded total (E413, E420).</summary>
    public int FootnoteExtraDecimals { get; init; } = 2;

    /// <summary>Text for an average whose impacted length is 0 (6.15, 6.16).</summary>
    public string NotApplicable { get; init; } = "N/A";

    /// <summary>Volume cell: {0} number, {1} unit. Gives <c>100 CY</c> (6.8).</summary>
    public string VolumeCellTemplate { get; init; } = "{0} {1}";

    /// <summary>Net cell with a tag: {0} magnitude, {1} unit, {2} tag. Gives <c>50 CY (CUT)</c> (A5).</summary>
    public string NetCellTemplate { get; init; } = "{0} {1} ({2})";

    /// <summary>Net tag when cut exceeds fill (A5).</summary>
    public string CutTag { get; init; } = "CUT";

    /// <summary>Net tag when fill exceeds cut (A5).</summary>
    public string FillTag { get; init; } = "FILL";

    /// <summary>Appended to a table title when the table no longer matches the drawing (1.2 L2, E418).</summary>
    public string OutOfDateMarker { get; init; } = "(OUT OF DATE)";
}

/// <summary>The per-sheet table (6.8).</summary>
public sealed record SegmentTableConfig
{
    /// <summary>Title: {0} begin station, {1} end station, as formatted by the alignment (station equations honoured).</summary>
    public string TitleTemplate { get; init; } = "EARTHWORK STA {0} TO STA {1}";

    /// <summary>Analysis header row: {0} analysis name, {1} base surface, {2} comparison surface.</summary>
    public string AnalysisHeaderTemplate { get; init; } = "{0}";

    /// <summary>Label of the cut row.</summary>
    public string CutLabel { get; init; } = "CUT";

    /// <summary>Label of the fill row.</summary>
    public string FillLabel { get; init; } = "FILL";

    /// <summary>Label of the net row.</summary>
    public string NetLabel { get; init; } = "NET";

    /// <summary>Label of the optional area row.</summary>
    public string AreaLabel { get; init; } = "AREA";

    /// <summary>Adds an AREA row to each analysis block (6.8, default off).</summary>
    public bool AreaInSegmentTable { get; init; } = false;

    /// <summary>Footer row shown for an analysis whose factors are not both 1 (E410): {0} analysis, {1} cut factor, {2} fill factor.</summary>
    public string FactorFooterTemplate { get; init; } = "{0}: CUT FACTOR {1}, FILL FACTOR {2}";

    /// <summary>Style, layer and text height.</summary>
    public TableStyleSettings Style { get; init; } = new();
}

/// <summary>The master table (6.8).</summary>
public sealed record MasterTableConfig
{
    /// <summary>Title row text.</summary>
    public string Title { get; init; } = "EARTHWORK SUMMARY";

    /// <summary>Analysis header row: {0} analysis name, {1} base surface, {2} comparison surface.</summary>
    public string AnalysisHeaderTemplate { get; init; } = "{0}";

    /// <summary>Column header: sheet label.</summary>
    public string SheetHeader { get; init; } = "SHEET";

    /// <summary>Column header: begin station.</summary>
    public string BeginStationHeader { get; init; } = "BEGIN STA";

    /// <summary>Column header: end station.</summary>
    public string EndStationHeader { get; init; } = "END STA";

    /// <summary>Column header for 2D area; {0} is the area unit label (6.8: the header carries the unit).</summary>
    public string AreaHeaderTemplate { get; init; } = "2D AREA ({0})";

    /// <summary>Column header: cut.</summary>
    public string CutHeader { get; init; } = "CUT";

    /// <summary>Column header: fill.</summary>
    public string FillHeader { get; init; } = "FILL";

    /// <summary>Column header: net.</summary>
    public string NetHeader { get; init; } = "NET";

    /// <summary>Label of the outside-limits row (6.5).</summary>
    public string OutsideLimitsLabel { get; init; } = "OUTSIDE LIMITS";

    /// <summary>Label of the total row.</summary>
    public string TotalLabel { get; init; } = "TOTAL";

    /// <summary>
    /// When true the TOTAL row also adds the displayed OUTSIDE LIMITS row. ASSUMED default false:
    /// A6 says the sheets add up to the summary, so the total is the sheets only. Needs Dan's confirmation.
    /// </summary>
    public bool TotalIncludesOutsideLimits { get; init; } = false;

    /// <summary>Marker appended to the sheet cell of a coverage-flagged row, and leading its footer line.</summary>
    public string CoverageFlagMarker { get; init; } = "*";

    /// <summary>Footer line per analysis: {0} analysis, {1} base, {2} comparison, {3} cut factor, {4} fill factor (E410).</summary>
    public string SurfacesFooterTemplate { get; init; } = "{0}: BASE {1}, COMPARISON {2}, CUT FACTOR {3}, FILL FACTOR {4}";

    /// <summary>Footer line per flagged row: {0} marker, {1} sheet, {2} analysis, {3} coverage percent with sign (E405, E419).</summary>
    public string CoverageFooterTemplate { get; init; } = "{0} SHEET {1} ({2}): SURFACE DATA COVERS {3} OF THE BOUNDARY";

    /// <summary>Footnote when a total row differs from the rounded true total: {0} analysis, {1} list of unrounded totals (A6, E413, E420).</summary>
    public string UnroundedFootnoteTemplate { get; init; } = "{0}: TOTALS ARE SUMS OF ROUNDED ROWS. UNROUNDED {1}";

    /// <summary>One item in the unrounded list: {0} column header, {1} value with unit.</summary>
    public string UnroundedItemTemplate { get; init; } = "{0} {1}";

    /// <summary>Separator between unrounded items.</summary>
    public string UnroundedItemSeparator { get; init; } = "; ";

    /// <summary>Footnote when the partition check was skipped (E215): {0} analysis, {1} reason.</summary>
    public string PartitionSkippedFootnoteTemplate { get; init; } = "{0}: PARTITION CHECK NOT RUN. {1}";

    /// <summary>Style, layer and text height.</summary>
    public TableStyleSettings Style { get; init; } = new();
}

/// <summary>The HCFCD segment table (6.16).</summary>
public sealed record HcfcdConfig
{
    /// <summary>
    /// Path of the label-bound template. Relative paths resolve against the plugin's directory;
    /// the shipped file is <c>Output/Templates/hcfcd-segment-table.json</c>. The shipped template is a
    /// PLACEHOLDER until Dan supplies the HCFCD table (section 13).
    /// </summary>
    public string TemplatePath { get; init; } = "Templates/hcfcd-segment-table.json";

    /// <summary>
    /// False (the default, D7): HCFCD rows 6–9 and the average report the in-place (bank),
    /// unfactored cut, as a Section 404 permit and the HCFCD segment table do; shrink/swell
    /// factors are an earthwork-balance adjustment, not an impact quantity. True applies the
    /// analysis cut factor to the HCFCD cut volume and average, as to every other reported
    /// volume. To be confirmed against Dan's HCFCD sample table when it arrives.
    /// </summary>
    public bool ImpactVolumesUseCutFactor { get; init; } = false;

    /// <summary>Style, layer and text height.</summary>
    public TableStyleSettings Style { get; init; } = new();
}

/// <summary>Names the paper writers look up (6.7).</summary>
public sealed record PlacementConfig
{
    /// <summary>Block whose insertion point is the table's top-left corner on a layout (6.7). PLACEHOLDER (A7).</summary>
    public string TableAnchorBlock { get; init; } = "SV_TABLE_ANCHOR";
}

/// <summary>Persistence in the drawing (6.9).</summary>
public sealed record PersistenceConfig
{
    /// <summary>Named Objects Dictionary entry holding one Xrecord per run (6.9).</summary>
    public string DictionaryName { get; init; } = "SHEETVOL";

    /// <summary>Maximum characters per Xrecord string (6.9: 250 or fewer).</summary>
    public int ChunkLength { get; init; } = RunPersistence.MaxChunkLength;
}

/// <summary>CSV and JSON exports (6.8).</summary>
public sealed record ExportConfig
{
    /// <summary>
    /// CSV header text by column key (see <see cref="CsvExport.ColumnKeys"/>). A column not
    /// listed uses its key as the header. An unknown key is a config error, never ignored.
    /// The JSON property names are fixed by the export schema version.
    /// </summary>
    public IReadOnlyDictionary<string, string> CsvHeaders { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// The C2 config record: every unit constant, decimal, tolerance, label, table style name,
/// layer, text height, anchor name and file path that Core.Volumes and Core.Output use. No
/// such literal appears outside it (HANDOFF.md 6.13). Defaults marked PLACEHOLDER stand until
/// LAN names arrive (A7); defaults marked ASSUMED are judgement calls awaiting confirmation.
/// Loads from JSON with <see cref="FromJson"/>; unknown keys are rejected.
/// </summary>
public sealed record ReportConfig
{
    /// <summary>Units, labels, tolerances and owned-surface naming.</summary>
    public VolumeConfig Volumes { get; init; } = new();

    /// <summary>Table number formats and fixed words.</summary>
    public FormatConfig Formats { get; init; } = new();

    /// <summary>Per-sheet table.</summary>
    public SegmentTableConfig SegmentTable { get; init; } = new();

    /// <summary>Master table.</summary>
    public MasterTableConfig MasterTable { get; init; } = new();

    /// <summary>HCFCD segment table.</summary>
    public HcfcdConfig Hcfcd { get; init; } = new();

    /// <summary>Paper-space anchor names.</summary>
    public PlacementConfig Placement { get; init; } = new();

    /// <summary>Drawing persistence.</summary>
    public PersistenceConfig Persistence { get; init; } = new();

    /// <summary>CSV and JSON exports.</summary>
    public ExportConfig Export { get; init; } = new();

    /// <summary>The built-in defaults.</summary>
    public static ReportConfig Default { get; } = new();

    /// <summary>Setting paths whose built-in default is a placeholder for a LAN name (A7).</summary>
    public static IReadOnlyList<string> PlaceholderSettings { get; } = PlaceholderGetters.Select(g => g.Path).ToList();

    /// <summary>Setting paths whose built-in default is an assumption awaiting confirmation.</summary>
    public static IReadOnlyList<string> AssumedSettings { get; } =
    [
        "volumes.tolerances.volumeZeroTolerance",
        "volumes.tolerances.coverageFlagTolerance",
        "volumes.tolerances.coverageExcessTolerance",
        "masterTable.totalIncludesOutsideLimits",
        "hcfcd.impactVolumesUseCutFactor",
        "formats.acresDecimals",
    ];

    /// <summary>Placeholder settings that still hold their built-in value, for SVSTATUS.</summary>
    public IReadOnlyList<string> PlaceholdersInUse()
        => PlaceholderGetters.Where(g => Equals(g.Get(this), g.Get(Default))).Select(g => g.Path).ToList();

    /// <summary>The culture for table cells. Call <see cref="Validate"/> first; an unknown name throws.</summary>
    public CultureInfo TableCulture() => CultureInfo.GetCultureInfo(Formats.TableCultureName);

    /// <summary>Display decimals for an area unit (6.5).</summary>
    public int AreaDecimals(AreaUnit unit) => unit switch
    {
        AreaUnit.SquareFeet => Formats.SquareFeetDecimals,
        AreaUnit.Acres => Formats.AcresDecimals,
        AreaUnit.SquareMetres => Formats.SquareMetresDecimals,
        AreaUnit.Hectares => Formats.HectaresDecimals,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown area unit."),
    };

    /// <summary>Every problem with the values; empty when the config is usable. Each problem is an SV-E534 error.</summary>
    public IReadOnlyList<Problem> Validate()
    {
        var p = new List<Problem>();
        void Bad(string path, string why) => p.Add(new Problem(OutputCodes.ConfigInvalid, Severity.Error, $"Config {path}: {why}"));
        void Positive(string path, double v) { if (!double.IsFinite(v) || v <= 0) Bad(path, "must be a number above zero."); }
        void NonNegative(string path, double v) { if (!double.IsFinite(v) || v < 0) Bad(path, "must be a number of zero or more."); }
        void Decimals(string path, int v) { if (v < 0 || v > DisplayRounding.MaxDecimals) Bad(path, $"must be 0 to {DisplayRounding.MaxDecimals}."); }
        void Text(string path, string? v) { if (string.IsNullOrWhiteSpace(v)) Bad(path, "must not be empty."); }
        void Template(string path, string? v, int args)
        {
            if (string.IsNullOrWhiteSpace(v)) { Bad(path, "must not be empty."); return; }
            try { _ = string.Format(CultureInfo.InvariantCulture, v, Enumerable.Repeat<object>("x", args).ToArray()); }
            catch (FormatException) { Bad(path, $"is not a valid format with {args} argument(s) {{0}}..{{{args - 1}}}."); }
        }

        var c = Volumes.Constants;
        Positive("volumes.constants.cubicFeetPerCubicYard", c.CubicFeetPerCubicYard);
        Positive("volumes.constants.squareFeetPerAcre", c.SquareFeetPerAcre);
        Positive("volumes.constants.squareMetresPerHectare", c.SquareMetresPerHectare);
        Positive("volumes.constants.metresPerFoot", c.MetresPerFoot);

        var l = Volumes.Labels;
        foreach (var (path, v) in new[]
        {
            ("cubicYards", l.CubicYards), ("cubicMetres", l.CubicMetres), ("squareFeet", l.SquareFeet), ("acres", l.Acres),
            ("squareMetres", l.SquareMetres), ("hectares", l.Hectares), ("linearFeet", l.LinearFeet), ("metres", l.Metres),
            ("cubicYardsPerFoot", l.CubicYardsPerFoot), ("cubicMetresPerMetre", l.CubicMetresPerMetre),
        })
            Text("volumes.labels." + path, v);

        var t = Volumes.Tolerances;
        NonNegative("volumes.tolerances.volumeZeroTolerance", t.VolumeZeroTolerance);
        NonNegative("volumes.tolerances.partitionAbsoluteCubicFeet", t.PartitionAbsoluteCubicFeet);
        NonNegative("volumes.tolerances.partitionRelative", t.PartitionRelative);
        NonNegative("volumes.tolerances.coverageFlagTolerance", t.CoverageFlagTolerance);
        NonNegative("volumes.tolerances.coverageExcessTolerance", t.CoverageExcessTolerance);
        Text("volumes.ownedSurfacePrefix", Volumes.OwnedSurfacePrefix);
        if (Volumes.OwnedSurfaceSeparator is null or "") Bad("volumes.ownedSurfaceSeparator", "must not be empty.");

        var f = Formats;
        if (string.IsNullOrWhiteSpace(f.TableCultureName) || !KnownCultureNames.Value.Contains(f.TableCultureName))
            Bad("formats.tableCultureName", $"'{f.TableCultureName}' is not a predefined culture name.");
        Decimals("formats.volumeDecimals", f.VolumeDecimals);
        Decimals("formats.squareFeetDecimals", f.SquareFeetDecimals);
        Decimals("formats.acresDecimals", f.AcresDecimals);
        Decimals("formats.squareMetresDecimals", f.SquareMetresDecimals);
        Decimals("formats.hectaresDecimals", f.HectaresDecimals);
        Decimals("formats.lengthDecimals", f.LengthDecimals);
        Decimals("formats.averageDecimals", f.AverageDecimals);
        Decimals("formats.coverageDecimals", f.CoverageDecimals);
        if (f.FootnoteExtraDecimals < 0 || f.FootnoteExtraDecimals + Math.Max(f.VolumeDecimals, Math.Max(f.AcresDecimals, f.HectaresDecimals)) > DisplayRounding.MaxDecimals)
            Bad("formats.footnoteExtraDecimals", $"must be zero or more and keep footnotes within {DisplayRounding.MaxDecimals} decimals.");
        Text("formats.factorFormat", f.FactorFormat);
        Text("formats.notApplicable", f.NotApplicable);
        Template("formats.volumeCellTemplate", f.VolumeCellTemplate, 2);
        Template("formats.netCellTemplate", f.NetCellTemplate, 3);
        Text("formats.cutTag", f.CutTag);
        Text("formats.fillTag", f.FillTag);
        Text("formats.outOfDateMarker", f.OutOfDateMarker);

        var s = SegmentTable;
        Template("segmentTable.titleTemplate", s.TitleTemplate, 2);
        Template("segmentTable.analysisHeaderTemplate", s.AnalysisHeaderTemplate, 3);
        Text("segmentTable.cutLabel", s.CutLabel);
        Text("segmentTable.fillLabel", s.FillLabel);
        Text("segmentTable.netLabel", s.NetLabel);
        Text("segmentTable.areaLabel", s.AreaLabel);
        Template("segmentTable.factorFooterTemplate", s.FactorFooterTemplate, 3);

        var m = MasterTable;
        Text("masterTable.title", m.Title);
        Template("masterTable.analysisHeaderTemplate", m.AnalysisHeaderTemplate, 3);
        Text("masterTable.sheetHeader", m.SheetHeader);
        Text("masterTable.beginStationHeader", m.BeginStationHeader);
        Text("masterTable.endStationHeader", m.EndStationHeader);
        Template("masterTable.areaHeaderTemplate", m.AreaHeaderTemplate, 1);
        Text("masterTable.cutHeader", m.CutHeader);
        Text("masterTable.fillHeader", m.FillHeader);
        Text("masterTable.netHeader", m.NetHeader);
        Text("masterTable.outsideLimitsLabel", m.OutsideLimitsLabel);
        Text("masterTable.totalLabel", m.TotalLabel);
        Text("masterTable.coverageFlagMarker", m.CoverageFlagMarker);
        Template("masterTable.surfacesFooterTemplate", m.SurfacesFooterTemplate, 5);
        Template("masterTable.coverageFooterTemplate", m.CoverageFooterTemplate, 4);
        Template("masterTable.unroundedFootnoteTemplate", m.UnroundedFootnoteTemplate, 2);
        Template("masterTable.unroundedItemTemplate", m.UnroundedItemTemplate, 2);
        if (m.UnroundedItemSeparator is null or "") Bad("masterTable.unroundedItemSeparator", "must not be empty.");
        Template("masterTable.partitionSkippedFootnoteTemplate", m.PartitionSkippedFootnoteTemplate, 2);

        Text("hcfcd.templatePath", Hcfcd.TemplatePath);
        foreach (var (path, style) in new[] { ("segmentTable", s.Style), ("masterTable", m.Style), ("hcfcd", Hcfcd.Style) })
        {
            Text(path + ".style.tableStyle", style.TableStyle);
            Text(path + ".style.layer", style.Layer);
            Positive(path + ".style.textHeight", style.TextHeight);
        }

        Text("placement.tableAnchorBlock", Placement.TableAnchorBlock);
        Text("persistence.dictionaryName", Persistence.DictionaryName);
        if (Persistence.ChunkLength < 2 || Persistence.ChunkLength > RunPersistence.MaxChunkLength)
            Bad("persistence.chunkLength", $"must be 2 to {RunPersistence.MaxChunkLength} (6.9; 2 so a surrogate pair is never split).");

        foreach (var key in Export.CsvHeaders.Keys.Where(k => !CsvExport.ColumnKeys.Contains(k)))
            Bad("export.csvHeaders", $"'{key}' is not a CSV column key.");
        foreach (var (key, header) in Export.CsvHeaders)
            if (string.IsNullOrWhiteSpace(header)) Bad("export.csvHeaders." + key, "must not be empty.");

        return p;
    }

    /// <summary>
    /// Reads a config from JSON (camelCase keys, comments and trailing commas allowed). Missing
    /// keys keep their defaults; unknown keys and nulls are errors, never ignored. The result is validated.
    /// </summary>
    public static Outcome<ReportConfig> FromJson(string json)
    {
        ReportConfig? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<ReportConfig>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Outcome<ReportConfig>.Fail(new Problem(OutputCodes.ConfigInvalid, Severity.Error, $"Config JSON is invalid: {ex.Message}"));
        }
        if (cfg is null)
            return Outcome<ReportConfig>.Fail(new Problem(OutputCodes.ConfigInvalid, Severity.Error, "Config JSON is empty."));
        var problems = cfg.Validate();
        return problems.Count > 0 ? Outcome<ReportConfig>.Fail(problems) : Outcome<ReportConfig>.Ok(cfg);
    }

    /// <summary>Writes this config as indented JSON in the same shape <see cref="FromJson"/> reads.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    // Predefined names only: on ICU, GetCultureInfo accepts any well-formed tag (a typo such
    // as "en-UX" would not throw), so the name is checked against the platform's list.
    private static readonly Lazy<HashSet<string>> KnownCultureNames = new(() =>
        CultureInfo.GetCultures(CultureTypes.AllCultures)
            .Select(c => c.Name).Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static IEnumerable<(string Path, Func<ReportConfig, object> Get)> PlaceholderGetters =>
    [
        ("segmentTable.style.tableStyle", c => c.SegmentTable.Style.TableStyle),
        ("segmentTable.style.layer", c => c.SegmentTable.Style.Layer),
        ("segmentTable.style.textHeight", c => c.SegmentTable.Style.TextHeight),
        ("masterTable.style.tableStyle", c => c.MasterTable.Style.TableStyle),
        ("masterTable.style.layer", c => c.MasterTable.Style.Layer),
        ("masterTable.style.textHeight", c => c.MasterTable.Style.TextHeight),
        ("hcfcd.style.tableStyle", c => c.Hcfcd.Style.TableStyle),
        ("hcfcd.style.layer", c => c.Hcfcd.Style.Layer),
        ("hcfcd.style.textHeight", c => c.Hcfcd.Style.TextHeight),
        ("hcfcd.templatePath", c => c.Hcfcd.TemplatePath),
        ("placement.tableAnchorBlock", c => c.Placement.TableAnchorBlock),
    ];
}

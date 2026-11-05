using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SheetVol.Core.Volumes;

namespace SheetVol.Core.Output;

/// <summary>A quantity the HCFCD table binds by label (6.16).</summary>
public enum BoundQuantity
{
    /// <summary>Impacted length below OHWM, <c>#,##0</c> (row 6).</summary>
    ImpactedLength,

    /// <summary>Impacted area below OHWM in the run's area unit, acres <c>0.00</c> by default (row 7).</summary>
    ImpactedArea,
}

/// <summary>A merged range in the template, zero-based and inclusive. <see cref="LastRow"/> defaults to <see cref="Row"/>.</summary>
public sealed record TemplateMerge
{
    /// <summary>First row.</summary>
    public int Row { get; init; }

    /// <summary>Last row; null for a single-row merge.</summary>
    public int? LastRow { get; init; }

    /// <summary>First column.</summary>
    public int FirstColumn { get; init; }

    /// <summary>Last column.</summary>
    public int LastColumn { get; init; }
}

/// <summary>A value found by the label text in its row, never by position (6.16).</summary>
public sealed record TemplateBinding
{
    /// <summary>Which quantity is written.</summary>
    public BoundQuantity Quantity { get; init; }

    /// <summary>The row whose label cell starts with this text receives the value (case-insensitive, trimmed).</summary>
    public string LabelPrefix { get; init; } = "";

    /// <summary>Zero-based column of the value cell.</summary>
    public int ValueColumn { get; init; }

    /// <summary>Zero-based column that receives the unit label, so a unit switch (E418, E1020) updates it; null for none.</summary>
    public int? UnitColumn { get; init; }
}

/// <summary>The material block: one row per analysis under a header, added when the table is first created (6.16, E1018).</summary>
public sealed record MaterialRowsSpec
{
    /// <summary>Label prefix of the header row the material rows sit under.</summary>
    public string HeaderLabelPrefix { get; init; } = "";

    /// <summary>Zero-based column for the cut volume, <c>#,##0</c>.</summary>
    public int CutVolumeColumn { get; init; }

    /// <summary>Zero-based column for the average, <c>0.00</c> or N/A.</summary>
    public int AverageColumn { get; init; }
}

/// <summary>The template file as stored (<c>hcfcd-segment-table.json</c>).</summary>
public sealed record HcfcdTemplateFile
{
    /// <summary>Template schema version; this build reads <see cref="HcfcdTemplate.SupportedSchemaVersion"/>.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Template name.</summary>
    public string Name { get; init; } = "";

    /// <summary>True while the layout is a stand-in for the real HCFCD table.</summary>
    public bool Placeholder { get; init; }

    /// <summary>Free text for the maintainer.</summary>
    public string? Note { get; init; }

    /// <summary>Column count.</summary>
    public int Columns { get; init; }

    /// <summary>Zero-based column holding each row's label.</summary>
    public int LabelColumn { get; init; }

    /// <summary>Rows top to bottom; each holds exactly <see cref="Columns"/> cells of fixed text.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = [];

    /// <summary>Merged ranges.</summary>
    public IReadOnlyList<TemplateMerge> Merges { get; init; } = [];

    /// <summary>Values bound by label.</summary>
    public IReadOnlyList<TemplateBinding> Bindings { get; init; } = [];

    /// <summary>The material block.</summary>
    public MaterialRowsSpec Materials { get; init; } = new();
}

/// <summary>One material (analysis) for the HCFCD table.</summary>
/// <param name="AnalysisId">The analysis id.</param>
/// <param name="Name">The analysis name; it is the material row's label (6.16).</param>
/// <param name="Impact">The integrator's result for this segment and analysis.</param>
/// <param name="CutFactor">The analysis cut factor, applied when <see cref="HcfcdConfig.ImpactVolumesUseCutFactor"/> is on.</param>
public sealed record HcfcdMaterial(Guid AnalysisId, string Name, ImpactResult Impact, double CutFactor);

/// <summary>One cell to write, zero-based.</summary>
public sealed record CellWrite(int Row, int Column, string Text);

/// <summary>Result of binding values into an existing HCFCD table.</summary>
/// <param name="Bound">Every bound cell and the text it should hold.</param>
/// <param name="Changes">Only the bound cells whose text differs from the table now, so an unchanged regen writes nothing (E811).</param>
/// <param name="Problems">Labels not found or ambiguous, and invalid values. A problem skips only its own value.</param>
public sealed record HcfcdBindResult(IReadOnlyList<CellWrite> Bound, IReadOnlyList<CellWrite> Changes, IReadOnlyList<Problem> Problems);

/// <summary>
/// The HCFCD label-bound table template (HANDOFF.md 6.16). Loads and validates the JSON,
/// builds the table the first time (material rows added under the header), and on every regen
/// finds each bound value by its row label and returns the cells to write. Center coordinates,
/// aquatic-feature rows and any row the user adds are never touched.
/// </summary>
public sealed partial class HcfcdTemplate
{
    /// <summary>The template schema version this build reads.</summary>
    public const int SupportedSchemaVersion = 1;

    private static readonly string[] KnownTokens = ["volumeUnit", "lengthUnit", "areaUnit", "averageUnit"];

    private HcfcdTemplate(HcfcdTemplateFile definition) => Definition = definition;

    /// <summary>The validated template.</summary>
    public HcfcdTemplateFile Definition { get; }

    /// <summary>
    /// Resolves the configured path: an absolute path is used as is, a relative one is taken
    /// from <paramref name="baseDirectory"/> (the plugin's directory).
    /// </summary>
    public static string ResolvePath(string configuredPath, string baseDirectory)
        => Path.IsPathRooted(configuredPath) ? configuredPath : Path.GetFullPath(Path.Combine(baseDirectory, configuredPath));

    /// <summary>Reads and validates the template from <see cref="HcfcdConfig.TemplatePath"/>.</summary>
    public static Outcome<HcfcdTemplate> Load(string configuredPath, string baseDirectory)
    {
        string path = ResolvePath(configuredPath, baseDirectory);
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Outcome<HcfcdTemplate>.Fail(new Problem(OutputCodes.TemplateNotFound, Severity.Error,
                $"HCFCD table template not readable at {path}: {ex.Message}",
                Fix: "Set hcfcd.templatePath in the SheetVol config, or restore the shipped template."));
        }
        return Parse(json, path);
    }

    /// <summary>Parses and validates template JSON. <paramref name="source"/> names it in messages.</summary>
    public static Outcome<HcfcdTemplate> Parse(string json, string source = "template")
    {
        HcfcdTemplateFile? file;
        try
        {
            file = JsonSerializer.Deserialize<HcfcdTemplateFile>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Outcome<HcfcdTemplate>.Fail(Invalid(source, $"JSON is invalid: {ex.Message}"));
        }
        if (file is null) return Outcome<HcfcdTemplate>.Fail(Invalid(source, "file is empty."));

        var problems = Validate(file, source);
        if (problems.Any(p => p.Severity == Severity.Error)) return Outcome<HcfcdTemplate>.Fail(problems);
        if (file.Placeholder)
            problems.Add(new Problem(OutputCodes.TemplatePlaceholder, Severity.Info,
                $"HCFCD table template '{file.Name}' ({source}) is a placeholder layout.",
                Fix: "Replace it with the HCFCD table as it should look in CAD."));
        return Outcome<HcfcdTemplate>.Ok(new HcfcdTemplate(file), problems);
    }

    /// <summary>
    /// Builds the table for first creation: the template rows with unit tokens resolved, and one
    /// material row per analysis under the material header, labelled with the analysis name
    /// (6.16, E1018). Bound cells start blank; apply <see cref="Bind"/> to fill them.
    /// </summary>
    public Outcome<TableModel> CreateTable(
        Guid segmentId, IReadOnlyList<AnalysisDef> materials, AreaUnit areaUnit, QuantityUnits units, ReportConfig config)
    {
        var problems = new List<Problem>();
        if (units.CheckAreaUnit(areaUnit) is { } mismatch) problems.Add(mismatch with { SegmentId = segmentId });
        if (materials.Any(m => string.IsNullOrWhiteSpace(m.Name)))
            problems.Add(Invalid(Definition.Name, "every material (analysis) needs a name; it is the row label.") with { SegmentId = segmentId });
        foreach (var dup in materials.GroupBy(m => m.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add(Invalid(Definition.Name, $"two materials are both named '{dup.Key}'; their rows could not be told apart.") with { SegmentId = segmentId });
        if (problems.Count > 0) return Outcome<TableModel>.Fail(problems);

        var tokens = new Dictionary<string, string>
        {
            ["volumeUnit"] = units.VolumeLabel,
            ["lengthUnit"] = units.LengthLabel,
            ["areaUnit"] = units.AreaLabel(areaUnit),
            ["averageUnit"] = units.AverageLabel,
        };
        var d = Definition;
        int header = FindTemplateRow(d, d.Materials.HeaderLabelPrefix);
        var rows = new List<TableRowModel>();
        for (int r = 0; r < d.Rows.Count; r++)
        {
            rows.Add(new TableRowModel(RowKind.Fixed, d.Rows[r].Select(c => TokenPattern().Replace(c, m => tokens[m.Groups[1].Value])).ToList(),
                SegmentId: segmentId));
            if (r == header)
                foreach (var m in materials)
                {
                    var cells = Enumerable.Repeat("", d.Columns).ToArray();
                    cells[d.LabelColumn] = m.Name;
                    rows.Add(new TableRowModel(RowKind.Material, cells, m.Id, segmentId));
                }
        }
        var merges = d.Merges.Select(m =>
        {
            int shift = m.Row > header ? materials.Count : 0;
            return new CellRange(m.Row + shift, m.FirstColumn, (m.LastRow ?? m.Row) + shift, m.LastColumn);
        }).ToList();
        return Outcome<TableModel>.Ok(new TableModel(TableKind.Hcfcd, segmentId, d.Columns, rows, merges, config.Hcfcd.Style));
    }

    /// <summary>
    /// Finds each bound value by its label in the table as it stands now and returns the cells to
    /// write (6.16). A missing label (E1015, E1018) or an ambiguous one skips that value with a
    /// warning; the other values still bind. Rows 6 and 7 take the first material's length and
    /// area; a disagreeing second material is reported (SV-E538). The average is computed from
    /// unrounded volume and length (E1019) and is N/A when the length is 0.
    /// </summary>
    /// <param name="currentCells">The table's cell text now, row by row, as read from the drawing.</param>
    /// <param name="segmentId">The segment this table reports.</param>
    /// <param name="materials">Materials in analysis order.</param>
    /// <param name="areaUnit">The run's area unit (acres by default for this preset).</param>
    /// <param name="formatter">Formatter for the run's units and config.</param>
    /// <param name="config">Validated config.</param>
    public HcfcdBindResult Bind(
        IReadOnlyList<IReadOnlyList<string>> currentCells, Guid segmentId, IReadOnlyList<HcfcdMaterial> materials,
        AreaUnit areaUnit, QuantityFormatter formatter, ReportConfig config)
    {
        var d = Definition;
        var units = formatter.Units;
        var f = config.Formats;
        var bound = new List<CellWrite>();
        var problems = new List<Problem>();
        void Warn(string code, string message, string? fix = null) => problems.Add(new Problem(code, Severity.Warning, message, segmentId, fix));

        var valid = new List<HcfcdMaterial>();
        foreach (var m in materials)
        {
            var i = m.Impact;
            if (i.SegmentId != segmentId)
                problems.Add(new Problem(VolumeCodes.InvalidSourceValue, Severity.Error,
                    $"Material '{m.Name}': impact result belongs to segment {i.SegmentId}, not {segmentId}.", segmentId));
            else if (!AllFiniteNonNegative(i.ImpactedLength, i.ImpactedArea, i.CutVolume) || !double.IsFinite(m.CutFactor) || m.CutFactor <= 0)
                problems.Add(new Problem(VolumeCodes.InvalidSourceValue, Severity.Error,
                    $"Material '{m.Name}': impacted length, area or cut volume is not a non-negative number, or the cut factor is invalid.", segmentId));
            else valid.Add(m);
        }

        if (valid.Count > 0)
        {
            var primary = valid[0].Impact;
            foreach (var other in valid.Skip(1))
                if (other.Impact.ImpactedLength != primary.ImpactedLength || other.Impact.ImpactedArea != primary.ImpactedArea)
                    Warn(OutputCodes.MaterialsDisagree,
                        $"Materials '{valid[0].Name}' and '{other.Name}' have different impacted length or area; rows 6 and 7 show '{valid[0].Name}'.");

            var areaMismatch = units.CheckAreaUnit(areaUnit);
            foreach (var b in d.Bindings)
            {
                int? row = FindUnique(currentCells, d.LabelColumn, b.LabelPrefix, 0, currentCells.Count, Warn);
                if (row is null) continue;
                string value, unit;
                if (b.Quantity == BoundQuantity.ImpactedLength)
                {
                    value = formatter.Number(DisplayRounding.Round(units.ToLength(primary.ImpactedLength), f.LengthDecimals), f.LengthDecimals);
                    unit = units.LengthLabel;
                }
                else
                {
                    if (areaMismatch is not null) { problems.Add(areaMismatch with { SegmentId = segmentId }); continue; }
                    value = formatter.AreaNumber(formatter.RoundArea(primary.ImpactedArea, areaUnit), areaUnit);
                    unit = units.AreaLabel(areaUnit);
                }
                AddWrite(bound, currentCells, row.Value, b.ValueColumn, value, b.LabelPrefix, Warn);
                if (b.UnitColumn is int uc) AddWrite(bound, currentCells, row.Value, uc, unit, b.LabelPrefix, Warn);
            }
        }

        if (valid.Count > 0)
        {
            int? header = FindUnique(currentCells, d.LabelColumn, d.Materials.HeaderLabelPrefix, 0, currentCells.Count, Warn);
            foreach (var m in valid)
            {
                if (header is null)
                {
                    Warn(OutputCodes.BoundLabelMissing, $"Material '{m.Name}' skipped: the material header was not found.");
                    continue;
                }
                int? row = FindMaterialRow(currentCells, d.LabelColumn, m.Name, header.Value + 1, Warn);
                if (row is null) continue;
                double factor = config.Hcfcd.ImpactVolumesUseCutFactor ? m.CutFactor : 1.0;
                double volume = units.ToVolumeUnit(m.Impact.CutVolume) * factor;
                double length = units.ToLength(m.Impact.ImpactedLength);
                string avg = length > 0
                    ? formatter.Number(DisplayRounding.Round(volume / length, f.AverageDecimals), f.AverageDecimals)
                    : f.NotApplicable;
                AddWrite(bound, currentCells, row.Value, d.Materials.CutVolumeColumn,
                    formatter.Number(formatter.RoundVolume(volume), f.VolumeDecimals), m.Name, Warn);
                AddWrite(bound, currentCells, row.Value, d.Materials.AverageColumn, avg, m.Name, Warn);
            }
        }

        var changes = bound.Where(w => CellText(currentCells, w.Row, w.Column) != w.Text).ToList();
        return new HcfcdBindResult(bound, changes, problems);
    }

    private static void AddWrite(List<CellWrite> bound, IReadOnlyList<IReadOnlyList<string>> cells, int row, int column,
        string text, string label, Action<string, string, string?> warn)
    {
        if (column >= cells[row].Count)
        {
            warn(OutputCodes.BoundLabelMissing, $"Row '{label}' has no column {column + 1}; that value was skipped.", null);
            return;
        }
        bound.Add(new CellWrite(row, column, text));
    }

    private static string? CellText(IReadOnlyList<IReadOnlyList<string>> cells, int row, int column)
        => column < cells[row].Count ? cells[row][column] : null;

    private static string Label(IReadOnlyList<IReadOnlyList<string>> cells, int row, int labelColumn)
        => labelColumn < cells[row].Count ? (cells[row][labelColumn] ?? "").Trim() : "";

    private static int? FindUnique(IReadOnlyList<IReadOnlyList<string>> cells, int labelColumn, string prefix,
        int from, int to, Action<string, string, string?> warn)
    {
        var hits = Enumerable.Range(from, Math.Max(0, to - from))
            .Where(r => Label(cells, r, labelColumn).StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 1) return hits[0];
        if (hits.Count == 0)
            warn(OutputCodes.BoundLabelMissing, $"No row starts with '{prefix}'; that value was skipped and the others still update.",
                "Restore the row label, or recreate the table.");
        else
            warn(OutputCodes.BoundLabelAmbiguous, $"{hits.Count} rows start with '{prefix}'; that value was skipped rather than guessed.",
                "Make the row labels unique.");
        return null;
    }

    private static int? FindMaterialRow(IReadOnlyList<IReadOnlyList<string>> cells, int labelColumn, string name, int from,
        Action<string, string, string?> warn)
    {
        string n = name.Trim();
        var exact = Enumerable.Range(from, Math.Max(0, cells.Count - from))
            .Where(r => string.Equals(Label(cells, r, labelColumn), n, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1)
        {
            warn(OutputCodes.BoundLabelAmbiguous, $"{exact.Count} material rows are labelled '{name}'; skipped rather than guessed.", "Make the row labels unique.");
            return null;
        }
        int? row = FindUnique(cells, labelColumn, n, from, cells.Count, (code, msg, fix) =>
            warn(code, code == OutputCodes.BoundLabelMissing
                ? $"No material row for '{name}'. Material rows are added only when the table is first created (E1018); add the row by hand or recreate the table."
                : msg, fix));
        return row;
    }

    private static bool AllFiniteNonNegative(params double[] values) => values.All(v => double.IsFinite(v) && v >= 0);

    private static int FindTemplateRow(HcfcdTemplateFile d, string prefix)
    {
        for (int r = 0; r < d.Rows.Count; r++)
            if (d.LabelColumn < d.Rows[r].Count && d.Rows[r][d.LabelColumn].Trim().StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
                return r;
        return -1;
    }

    private static List<Problem> Validate(HcfcdTemplateFile d, string source)
    {
        var p = new List<Problem>();
        void Bad(string why) => p.Add(Invalid(source, why));

        if (d.SchemaVersion != SupportedSchemaVersion)
            Bad($"schemaVersion is {d.SchemaVersion}; this build reads {SupportedSchemaVersion}.");
        if (d.Columns < 1) { Bad("columns must be 1 or more."); return p; }
        if (d.LabelColumn < 0 || d.LabelColumn >= d.Columns) Bad($"labelColumn {d.LabelColumn} is outside 0..{d.Columns - 1}.");
        if (d.Rows.Count == 0) Bad("has no rows.");
        for (int r = 0; r < d.Rows.Count; r++)
        {
            if (d.Rows[r].Count != d.Columns) Bad($"row {r} has {d.Rows[r].Count} cells; columns is {d.Columns}.");
            foreach (Match m in d.Rows[r].SelectMany(c => TokenPattern().Matches(c)))
                if (!KnownTokens.Contains(m.Groups[1].Value)) Bad($"row {r} uses unknown token {m.Value}.");
        }
        if (p.Count > 0) return p;

        var covered = new HashSet<(int, int)>();
        foreach (var m in d.Merges)
        {
            int last = m.LastRow ?? m.Row;
            if (m.Row < 0 || last < m.Row || last >= d.Rows.Count || m.FirstColumn < 0 || m.LastColumn < m.FirstColumn || m.LastColumn >= d.Columns)
            {
                Bad($"merge at row {m.Row} is outside the table or reversed.");
                continue;
            }
            for (int r = m.Row; r <= last; r++)
                for (int c = m.FirstColumn; c <= m.LastColumn; c++)
                    if (!covered.Add((r, c))) Bad($"merges overlap at row {r}, column {c}.");
        }

        void Column(string what, int c) { if (c < 0 || c >= d.Columns || c == d.LabelColumn) Bad($"{what} column {c} must be a non-label column inside 0..{d.Columns - 1}."); }
        void UniqueLabel(string what, string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix)) { Bad($"{what} has an empty label prefix."); return; }
            int hits = d.Rows.Count(r => r[d.LabelColumn].Trim().StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase));
            if (hits != 1) Bad($"{what} label '{prefix}' matches {hits} template rows; it must match exactly one.");
        }

        foreach (var g in d.Bindings.GroupBy(b => b.Quantity).Where(g => g.Count() > 1))
            Bad($"{g.Key} is bound {g.Count()} times.");
        foreach (var b in d.Bindings)
        {
            if (!Enum.IsDefined(b.Quantity)) { Bad($"unknown quantity {b.Quantity}."); continue; }
            UniqueLabel(b.Quantity.ToString(), b.LabelPrefix);
            Column(b.Quantity + " value", b.ValueColumn);
            if (b.UnitColumn is int uc)
            {
                Column(b.Quantity + " unit", uc);
                if (uc == b.ValueColumn) Bad($"{b.Quantity} writes its value and unit to the same column.");
            }
        }
        UniqueLabel("material header", d.Materials.HeaderLabelPrefix);
        Column("material cut volume", d.Materials.CutVolumeColumn);
        Column("material average", d.Materials.AverageColumn);
        if (d.Materials.CutVolumeColumn == d.Materials.AverageColumn) Bad("material cut volume and average share a column.");
        return p;
    }

    private static Problem Invalid(string source, string why)
        => new(OutputCodes.TemplateInvalid, Severity.Error, $"HCFCD table template {source}: {why}");

    [GeneratedRegex(@"\{([A-Za-z]+)\}")]
    private static partial Regex TokenPattern();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
}

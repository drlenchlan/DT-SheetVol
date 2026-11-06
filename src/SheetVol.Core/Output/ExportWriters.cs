using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SheetVol.Core.Output;

/// <summary>
/// CSV text of an export (6.8): RFC 4180, CRLF, invariant culture, round-trip number format,
/// unrounded values (E508). One row per segment and analysis, then per analysis an
/// <c>outside_limits</c> row and a <c>total</c> row of unrounded sums (A6). Every row carries
/// the schema version.
/// </summary>
public static class CsvExport
{
    /// <summary>Column keys in file order. Headers default to the key; <see cref="ExportConfig.CsvHeaders"/> renames them.</summary>
    public static IReadOnlyList<string> ColumnKeys { get; } =
    [
        "schema_version", "row_kind", "run", "analysis_id", "analysis", "base_surface", "comparison_surface",
        "cut_factor", "fill_factor", "segment_id", "segment_label", "start_station", "end_station",
        "start_station_text", "end_station_text", "raw_cut", "raw_fill", "cut", "fill", "net", "volume_unit",
        "area2d_raw", "area2d", "area_unit", "coverage", "coverage_flag", "status",
        "impacted_length_raw", "impacted_length", "impacted_area_raw", "impacted_area",
        "impact_cut_volume_raw", "impact_cut_volume", "impact_average", "impact_intervals",
        "touches_search_limit",
    ];

    /// <summary>Row kind value for a segment row.</summary>
    public const string SegmentRowKind = "segment";

    /// <summary>Row kind value for an outside-limits row.</summary>
    public const string OutsideLimitsRowKind = "outside_limits";

    /// <summary>Row kind value for a total row.</summary>
    public const string TotalRowKind = "total";

    /// <summary>Writes the CSV text. The config must have passed <see cref="ReportConfig.Validate"/>.</summary>
    public static string Write(ExportDocument doc, ReportConfig config)
    {
        var sb = new StringBuilder();
        Line(sb, ColumnKeys.Select(k => config.Export.CsvHeaders.TryGetValue(k, out var h) ? h : k));

        foreach (var a in doc.Analyses)
        {
            Dictionary<string, string?> Base(string kind) => new()
            {
                ["schema_version"] = Num(doc.SchemaVersion), ["row_kind"] = kind, ["run"] = doc.RunName,
                ["analysis_id"] = a.Id.ToString("D"), ["analysis"] = a.Name,
                ["base_surface"] = a.BaseSurface, ["comparison_surface"] = a.ComparisonSurface,
                ["cut_factor"] = Num(a.CutFactor), ["fill_factor"] = Num(a.FillFactor), ["volume_unit"] = doc.VolumeUnit,
            };

            foreach (var r in doc.Rows.Where(r => r.AnalysisId == a.Id))
            {
                var v = Base(SegmentRowKind);
                v["segment_id"] = r.SegmentId.ToString("D");
                v["segment_label"] = r.Label;
                v["start_station"] = Num(r.StartStation);
                v["end_station"] = Num(r.EndStation);
                v["start_station_text"] = r.StartStationText;
                v["end_station_text"] = r.EndStationText;
                v["raw_cut"] = Num(r.RawCut);
                v["raw_fill"] = Num(r.RawFill);
                v["cut"] = Num(r.Cut);
                v["fill"] = Num(r.Fill);
                v["net"] = Num(r.Net);
                v["area2d_raw"] = Num(r.Area2DRaw);
                v["area2d"] = Num(r.Area2D);
                v["area_unit"] = r.AreaUnit;
                v["coverage"] = Num(r.Coverage);
                v["coverage_flag"] = Bool(r.CoverageFlag);
                v["status"] = r.Status;
                if (r.Impact is { } i)
                {
                    v["impacted_length_raw"] = Num(i.ImpactedLengthRaw);
                    v["impacted_length"] = Num(i.ImpactedLength);
                    v["impacted_area_raw"] = Num(i.ImpactedAreaRaw);
                    v["impacted_area"] = Num(i.ImpactedArea);
                    v["impact_cut_volume_raw"] = Num(i.CutVolumeRaw);
                    v["impact_cut_volume"] = Num(i.CutVolume);
                    v["impact_average"] = i.Average is double avg ? Num(avg) : null;
                    v["impact_intervals"] = string.Join(";", i.Intervals.Select(iv => Num(iv.From) + ".." + Num(iv.To)));
                    v["touches_search_limit"] = Bool(i.TouchesSearchLimit);
                }
                Line(sb, ColumnKeys.Select(k => v.GetValueOrDefault(k)));
            }

            var o = Base(OutsideLimitsRowKind);
            o["raw_cut"] = Num(a.OutsideLimits.RawCut);
            o["raw_fill"] = Num(a.OutsideLimits.RawFill);
            o["cut"] = Num(a.OutsideLimits.Cut);
            o["fill"] = Num(a.OutsideLimits.Fill);
            o["net"] = Num(a.OutsideLimits.Net);
            o["status"] = a.OutsideLimitsShown ? "Shown" : "WithinTolerance";
            Line(sb, ColumnKeys.Select(k => o.GetValueOrDefault(k)));

            var t = Base(TotalRowKind);
            t["cut"] = Num(a.Totals.Cut);
            t["fill"] = Num(a.Totals.Fill);
            t["net"] = Num(a.Totals.Net);
            t["area2d_raw"] = Num(a.Totals.Area2DRaw);
            t["area2d"] = Num(a.Totals.Area2D);
            t["area_unit"] = doc.AreaUnit;
            t["status"] = a.PartitionStatus;
            Line(sb, ColumnKeys.Select(k => t.GetValueOrDefault(k)));
        }
        return sb.ToString();
    }

    /// <summary>Round-trip invariant text for a number.</summary>
    public static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Bool(bool b) => b ? "true" : "false";

    private static void Line(StringBuilder sb, IEnumerable<string?> fields)
    {
        sb.Append(string.Join(",", fields.Select(Escape)));
        sb.Append("\r\n");
    }

    private static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        bool quote = field.IndexOfAny([',', '"', '\r', '\n']) >= 0 || field[0] == ' ' || field[^1] == ' ';
        return quote ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }
}

/// <summary>JSON text of an export (6.8): camelCase, indented, unrounded, with <c>schemaVersion</c>.</summary>
public static class JsonExport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new NonFiniteAsNull() },
    };

    /// <summary>
    /// Manual segments carry NaN stations (6.3, C1: a SegmentDef needs stations, a manual
    /// polyline has none). JSON has no NaN, and System.Text.Json throws on it (E311 self-test
    /// on installed bindings, 2026-11-05). A non-finite double is written as null; the CSV
    /// writer is unaffected (its "R" format spells NaN). Exports are never read back by the tool.
    /// </summary>
    private sealed class NonFiniteAsNull : System.Text.Json.Serialization.JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Null ? double.NaN : reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsFinite(value)) writer.WriteNumberValue(value);
            else writer.WriteNullValue();
        }
    }

    /// <summary>Writes the JSON text. JSON numbers are culture-free by definition (E508).</summary>
    public static string Write(ExportDocument doc) => JsonSerializer.Serialize(doc, Options);
}

using System.Globalization;
using System.Text.Json;
using SheetVol.Core.Output;
using SheetVol.Core.Tests.Volumes;
using SheetVol.Core.Volumes;

namespace SheetVol.Core.Tests.Output;

public class ExportTests
{
    private static readonly ReportConfig Cfg = ReportConfig.Default;
    private static readonly DateTimeOffset At = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static RunDefinition Run(Scenario s, AreaUnit unit = AreaUnit.SquareFeet)
        => new("Run1", "S", SegmentDriver.Stations, "0,500,1300,1800,2000", null, -100, 100,
            s.Analyses.Select(a => a.Analysis).ToList(), [], s.Segments, new Dictionary<Guid, SegmentOverrides>(), new ViewOptions(),
            AreaUnit: unit);

    private static ExportDocument Doc(Scenario s, AreaUnit unit = AreaUnit.SquareFeet, ReportConfig? cfg = null)
        => ExportBuilder.Build(Run(s, unit), s.Reconcile(), At, @"C:\proj\sheets.dwg", Scenario.Station, cfg ?? Cfg).Value;

    private static string[] CsvLines(string csv) => csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    private static string Field(string csv, int line, string key)
        => CsvLines(csv)[line].Split(',')[CsvExport.ColumnKeys.ToList().IndexOf(key)];

    [Fact]
    public void T23_export_carries_raw_area_display_value_unit_and_the_unrounded_total()
    {
        var doc = Doc(Scenario.T2(), AreaUnit.Acres);

        Assert.Equal(ExportBuilder.SchemaVersion, doc.SchemaVersion);
        Assert.Equal("AC", doc.AreaUnit);
        var first = doc.Rows[0];
        Assert.Equal(100_000, first.Area2DRaw);
        Assert.Equal(100_000 / 43_560.0, first.Area2D, 12);
        Assert.Equal("AC", first.AreaUnit);
        var totals = doc.Analyses.Single().Totals;
        Assert.Equal(9.1827, totals.Area2D, 4);                  // export 9.1827
        Assert.Equal(9.19m, totals.Area2DDisplayedRowsSum);       // the TOTAL row
        Assert.Equal(400_000, totals.Area2DRaw);
        Assert.All(doc.Rows, r => Assert.Equal(1.0, r.Coverage)); // all coverage 100 %
    }

    [Fact]
    public void Json_carries_schemaVersion_unrounded_values_and_parses_back()
    {
        string json = JsonExport.Write(Doc(Scenario.T2(), AreaUnit.Acres));

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Run1", root.GetProperty("runName").GetString());
        var row = root.GetProperty("rows")[0];
        Assert.Equal(100_000, row.GetProperty("area2DRaw").GetDouble());
        Assert.Equal(100_000 / 43_560.0, row.GetProperty("area2D").GetDouble());
        Assert.Equal(100, row.GetProperty("cut").GetDouble(), 9);
        Assert.Equal(2700, row.GetProperty("rawCut").GetDouble(), 9);
        Assert.Equal("0+00", row.GetProperty("startStationText").GetString());
    }

    [Fact]
    public void E508_csv_uses_the_invariant_culture_under_german_regional_settings()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string csv = CsvExport.Write(Doc(Scenario.T2(), AreaUnit.Acres), Cfg);

            Assert.Equal((100_000 / 43_560.0).ToString("R", CultureInfo.InvariantCulture), Field(csv, 1, "area2d"));
            Assert.Equal("1300", Field(csv, 3, "start_station"));
            Assert.Contains("2.29568", csv);
            Assert.DoesNotContain("2,29568", csv);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Csv_has_a_header_segment_rows_then_outside_limits_and_total_per_analysis()
    {
        string csv = CsvExport.Write(Doc(Scenario.T2()), Cfg);
        var lines = CsvLines(csv);

        Assert.Equal(string.Join(",", CsvExport.ColumnKeys), lines[0]);
        Assert.Equal(1 + 4 + 2, lines.Length);
        Assert.Equal("segment", Field(csv, 1, "row_kind"));
        Assert.Equal("1", Field(csv, 1, "schema_version"));
        Assert.Equal("outside_limits", Field(csv, 5, "row_kind"));
        Assert.Equal("WithinTolerance", Field(csv, 5, "status"));
        Assert.Equal("total", Field(csv, 6, "row_kind"));
        Assert.Equal("200", Field(csv, 6, "cut"));
        Assert.Equal("Passed", Field(csv, 6, "status"));
    }

    [Fact]
    public void E409_export_keeps_the_raw_value_that_the_table_shows_as_zero()
    {
        var s = new Scenario().WithBreaks(0, 500);
        s.AddAnalysis("EG vs FG", [(0.004, 0)]);
        var r = s.Reconcile();

        var table = MasterTableBuilder.Build(r, AreaUnit.SquareFeet, Scenario.Station, Cfg).Value;
        var doc = ExportBuilder.Build(Run(s), r, At, null, null, Cfg).Value;

        Assert.Equal("0 CY", table.RowsOf(RowKind.Segment).Single().Cells[4]);
        Assert.Equal("0 CY", table.RowsOf(RowKind.Segment).Single().Cells[6]);
        Assert.Equal(0.004, doc.Rows.Single().Cut, 12);
        Assert.Equal(0.004, doc.Rows.Single().Net, 12);
    }

    [Fact]
    public void T11_export_shows_factored_values_once_beside_the_raw_values()
    {
        var doc = Doc(Scenario.T2(1.25, 0.9));
        var first = doc.Rows[0];
        Assert.Equal(2700, first.RawCut, 9);
        Assert.Equal(125, first.Cut, 9);
        Assert.Equal(45, first.Fill, 9);
        Assert.Equal(1.25, doc.Analyses.Single().CutFactor);
        Assert.Equal(250, doc.Analyses.Single().Totals.Cut, 9);
    }

    [Fact]
    public void Impact_export_carries_intervals_qa_fields_and_the_average()
    {
        var s = new Scenario().WithBreaks(1000, 1500);
        var id = s.Segments[0].Id;
        var impact = new ImpactResult(id, "SEDIMENT", 310, 9_920, 15_600, [(1000, 1150), (1200, 1360)], 270, 54, true);
        s.AddAnalysis("SEDIMENT", [(577.78, 0)], impacts: new Dictionary<Guid, ImpactResult> { [id] = impact });

        var doc = Doc(s, AreaUnit.Acres);
        var i = doc.Rows.Single().Impact!;
        Assert.Equal(310, i.ImpactedLength);
        Assert.Equal(9_920 / 43_560.0, i.ImpactedArea, 12);
        Assert.Equal(15_600 / 27.0, i.CutVolume, 9);
        Assert.Equal(15_600 / 27.0 / 310, i.Average!.Value, 9);
        Assert.Equal([new ExportInterval(1000, 1150), new ExportInterval(1200, 1360)], i.Intervals);
        Assert.Equal(270, i.CutAboveOhwmRaw);
        Assert.Equal(10, i.CutAboveOhwm, 9);
        Assert.Equal(54, i.FillBelowOhwmRaw);
        Assert.True(i.TouchesSearchLimit);

        string csv = CsvExport.Write(doc, Cfg);
        Assert.Equal("1000..1150;1200..1360", Field(csv, 1, "impact_intervals"));
        Assert.Equal("true", Field(csv, 1, "touches_search_limit"));
    }

    [Fact]
    public void Average_is_null_when_the_impacted_length_is_zero()
    {
        var s = new Scenario().WithBreaks(0, 500);
        var id = s.Segments[0].Id;
        s.AddAnalysis("SEDIMENT", [(0, 0)], impacts: new Dictionary<Guid, ImpactResult> { [id] = new(id, "SEDIMENT", 0, 0, 0, [], 0, 0, false) });
        var doc = Doc(s, AreaUnit.Acres);
        Assert.Null(doc.Rows.Single().Impact!.Average);
        Assert.Equal("", Field(CsvExport.Write(doc, Cfg), 1, "impact_average"));
    }

    [Fact]
    public void A_run_that_did_not_reconcile_is_never_exported()
    {
        var s = Scenario.T2();
        s.Boundaries.RemoveAt(2);
        var o = ExportBuilder.Build(Run(s), s.Reconcile(), At, null, null, Cfg);
        Assert.False(o.Succeeded);
        Assert.Equal(OutputCodes.ResultNotReconciled, o.Problems.Single().Code);
    }

    [Fact]
    public void Csv_headers_come_from_config_and_unknown_keys_are_errors()
    {
        var cfg = Cfg with { Export = new ExportConfig { CsvHeaders = new Dictionary<string, string> { ["cut"] = "Cut (CY)" } } };
        string csv = CsvExport.Write(Doc(Scenario.T2(), cfg: cfg), cfg);
        Assert.Contains(",Cut (CY),", CsvLines(csv)[0]);

        var bad = Cfg with { Export = new ExportConfig { CsvHeaders = new Dictionary<string, string> { ["cutt"] = "x" } } };
        Assert.Contains(bad.Validate(), p => p.Code == OutputCodes.ConfigInvalid && p.Message.Contains("cutt"));
    }

    [Fact]
    public void Csv_quotes_commas_and_doubles_quotes()
    {
        var s = new Scenario().WithBreaks(0, 500);
        s.AddAnalysis("EG vs \"FG\", rev 2", [(1, 0)]);
        string csv = CsvExport.Write(Doc(s), Cfg);
        Assert.Contains("\"EG vs \"\"FG\"\", rev 2\"", csv);
    }
}

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
        Assert.Equal(3, root.GetProperty("schemaVersion").GetInt32());   // rev 3: export schema 3 (OHWM QA columns gone in 2; source kind added in 3)
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
        Assert.Equal("3", Field(csv, 1, "schema_version"));   // rev 3: export schema 3
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
        // Rev 3: the cut_above_ohwm / fill_below_ohwm QA fields are gone (X5); the rest stands.
        var s = new Scenario().WithBreaks(1000, 1500);
        var id = s.Segments[0].Id;
        var impact = new ImpactResult(id, "SEDIMENT", 310, 9_920, 15_600, [(1000, 1150), (1200, 1360)], [], true);
        s.AddAnalysis("SEDIMENT", [(577.78, 0)], impacts: new Dictionary<Guid, ImpactResult> { [id] = impact });

        var doc = Doc(s, AreaUnit.Acres);
        var i = doc.Rows.Single().Impact!;
        Assert.Equal(310, i.ImpactedLength);
        Assert.Equal(9_920 / 43_560.0, i.ImpactedArea, 12);
        Assert.Equal(15_600 / 27.0, i.CutVolume, 9);
        Assert.Equal(15_600 / 27.0 / 310, i.Average!.Value, 9);
        Assert.Equal([new ExportInterval(1000, 1150), new ExportInterval(1200, 1360)], i.Intervals);
        Assert.True(i.TouchesSearchLimit);

        string csv = CsvExport.Write(doc, Cfg);
        Assert.Equal("1000..1150;1200..1360", Field(csv, 1, "impact_intervals"));
        Assert.Equal("true", Field(csv, 1, "touches_search_limit"));
        Assert.DoesNotContain("cut_above_ohwm_raw", CsvExport.ColumnKeys);
        Assert.DoesNotContain("fill_below_ohwm_raw", CsvExport.ColumnKeys);
    }

    [Fact]
    public void Average_is_null_when_the_impacted_length_is_zero()
    {
        var s = new Scenario().WithBreaks(0, 500);
        var id = s.Segments[0].Id;
        s.AddAnalysis("SEDIMENT", [(0, 0)], impacts: new Dictionary<Guid, ImpactResult> { [id] = new(id, "SEDIMENT", 0, 0, 0, [], [], false) });
        var doc = Doc(s, AreaUnit.Acres);
        Assert.Null(doc.Rows.Single().Impact!.Average);
        Assert.Equal("", Field(CsvExport.Write(doc, Cfg), 1, "impact_average"));
    }

    [Fact]
    public void D10_E827_segment_without_an_impact_exports_zeros_and_an_empty_average()
    {
        // Segment 2 is the SV-E827 case: no impact result and no surface in its window (coverage 0 %).
        var s = new Scenario().WithBreaks(1000, 1500, 2000);
        var first = s.Segments[0].Id;
        var impact = new ImpactResult(first, "SEDIMENT", 310, 9_920, 15_600, [(1000, 1150)], [], false);
        s.AddAnalysis("SEDIMENT", [(577.78, 0), (0, 0)], areas: [100_000, 0], impacts: new Dictionary<Guid, ImpactResult> { [first] = impact });
        var run = Run(s, AreaUnit.Acres) with { Preset = ReportPreset.HcfcdSegment };

        var doc = ExportBuilder.Build(run, s.Reconcile(), At, null, Scenario.Station, Cfg).Value;

        var row = doc.Rows[1];
        Assert.True(row.CoverageFlag);
        var i = Assert.IsType<ExportImpact>(row.Impact);
        Assert.Equal("SEDIMENT", i.Material);
        Assert.Equal((0d, 0d, 0d), (i.ImpactedLength, i.ImpactedArea, i.CutVolume));   // rev 3: the two OHWM QA fields are gone
        Assert.Null(i.Average);
        Assert.Empty(i.Intervals);

        string csv = CsvExport.Write(doc, Cfg);
        foreach (var key in new[] { "impacted_length", "impacted_area", "impact_cut_volume" })
            Assert.Equal("0", Field(csv, 2, key));
        Assert.Equal("", Field(csv, 2, "impact_average"));     // N/A
        Assert.Equal("false", Field(csv, 2, "touches_search_limit"));
        Assert.Equal(15_600 / 27.0, doc.Rows[0].Impact!.CutVolume, 9);   // the computed segment is unchanged
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
    public void Rev3_pair_analysis_exports_source_pair_and_an_empty_volume_surface()
    {
        var doc = Doc(Scenario.T2());
        var a = doc.Analyses.Single();
        Assert.Equal(3, ExportBuilder.SchemaVersion);
        Assert.Equal(ExportSourceKinds.Pair, a.Source);
        Assert.Equal("pair", a.Source);
        Assert.Equal("", a.VolumeSurface);
        Assert.Equal(("EG", "FG_A"), (a.BaseSurface, a.ComparisonSurface));

        string csv = CsvExport.Write(doc, Cfg);
        var keys = CsvExport.ColumnKeys.ToList();
        Assert.Equal(keys.IndexOf("analysis") + 1, keys.IndexOf("source"));
        Assert.Equal(keys.IndexOf("source") + 1, keys.IndexOf("volume_surface"));
        Assert.Equal(keys.IndexOf("volume_surface") + 1, keys.IndexOf("base_surface"));
        for (int line = 1; line <= 6; line++)   // 4 segment rows, outside_limits, total
        {
            Assert.Equal("pair", Field(csv, line, "source"));
            Assert.Equal("", Field(csv, line, "volume_surface"));
        }

        using var parsed = JsonDocument.Parse(JsonExport.Write(doc));
        var ja = parsed.RootElement.GetProperty("analyses")[0];
        Assert.Equal("pair", ja.GetProperty("source").GetString());
        Assert.Equal("", ja.GetProperty("volumeSurface").GetString());
    }

    [Fact]
    public void Rev3_volume_surface_analysis_exports_its_kind_and_surface_name_beside_the_pair_and_factors()
    {
        // A volume-surface source next to a pair. Factors: the run's stored ones until rev3-adapters hands the surface's.
        var s = new Scenario().WithBreaks(0, 500, 1300);
        s.AddAnalysis("EG vs FG_A", [(100, 50), (100, 0)]);
        s.AddAnalysis("VOL user", [(10, 5), (20, 0)], cutFactor: 1.15, fillFactor: 0.9, baseSurface: "EG", comparison: "FG_B");
        s.Analyses[1] = s.Analyses[1] with { Analysis = s.Analyses[1].Analysis with { VolumeSurface = "VOL, EG-FG_B" } };

        var doc = Doc(s);
        var pair = doc.Analyses[0];
        var vol = doc.Analyses[1];
        Assert.Equal(("pair", ""), (pair.Source, pair.VolumeSurface));
        Assert.Equal((ExportSourceKinds.VolumeSurface, "VOL, EG-FG_B"), (vol.Source, vol.VolumeSurface));
        Assert.Equal(("EG", "FG_B", 1.15, 0.9), (vol.BaseSurface, vol.ComparisonSurface, vol.CutFactor, vol.FillFactor));

        string csv = CsvExport.Write(doc, Cfg);
        var lines = CsvLines(csv);
        Assert.Equal(1 + 2 * (2 + 2), lines.Length);   // per analysis: 2 segment rows, outside_limits, total
        var volLines = Enumerable.Range(1, lines.Length - 1).Where(l => lines[l].Contains(",VOL user,", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, volLines.Count);
        Assert.All(volLines, l => Assert.Contains(",VOL user,volume_surface,\"VOL, EG-FG_B\",EG,FG_B,1.15,0.9,", lines[l]));
        Assert.Contains(",EG vs FG_A,pair,,EG,FG_A,1,1,", lines[1]);

        using var parsed = JsonDocument.Parse(JsonExport.Write(doc));
        var ja = parsed.RootElement.GetProperty("analyses")[1];
        Assert.Equal("volume_surface", ja.GetProperty("source").GetString());
        Assert.Equal("VOL, EG-FG_B", ja.GetProperty("volumeSurface").GetString());
        Assert.Equal(1.15, ja.GetProperty("cutFactor").GetDouble());
        Assert.Equal(3, parsed.RootElement.GetProperty("schemaVersion").GetInt32());
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

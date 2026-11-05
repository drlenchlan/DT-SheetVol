using SheetVol.Core.Output;
using SheetVol.Core.Tests.Volumes;
using SheetVol.Core.Volumes;

namespace SheetVol.Core.Tests.Output;

internal static class OutputTestKit
{
    public static QuantityFormatter Formatter(ReportConfig? cfg = null, DrawingUnits? units = null)
    {
        cfg ??= ReportConfig.Default;
        return new QuantityFormatter(QuantityUnits.Create(units ?? Scenario.Feet, cfg.Volumes).Value, cfg);
    }

    public static TableRowModel[] Rows(TableModel t, RowKind kind) => t.RowsOf(kind).ToArray();
}

public class QuantityFormatterTests
{
    private static readonly QuantityFormatter F = OutputTestKit.Formatter();

    [Fact]
    public void A5_net_carries_a_cut_or_fill_tag()
    {
        Assert.Equal("50 CY (CUT)", F.NetCell(F.RoundVolume(50)));
        Assert.Equal("50 CY (FILL)", F.NetCell(F.RoundVolume(-50)));
        Assert.Equal("100 CY", F.VolumeCell(F.RoundVolume(100)));
    }

    [Fact]
    public void E414_net_exactly_zero_shows_0_CY_without_a_tag()
    {
        Assert.Equal("0 CY", F.NetCell(F.RoundVolume(0.0)));
    }

    [Fact]
    public void E414_net_that_rounds_to_zero_shows_no_tag()
    {
        Assert.Equal("0 CY", F.NetCell(F.RoundVolume(0.4)));
        Assert.Equal("0 CY", F.NetCell(F.RoundVolume(-0.4)));
    }

    [Fact]
    public void E409_noise_near_zero_displays_as_zero_and_never_as_minus_zero()
    {
        Assert.Equal(0m, F.RoundVolume(-1e-9));
        Assert.Equal("0 CY", F.VolumeCell(F.RoundVolume(-1e-9)));
        Assert.Equal("0 CY", F.NetCell(F.RoundVolume(0.009)));

        var twoPlaces = OutputTestKit.Formatter(ReportConfig.Default with { Formats = new FormatConfig { VolumeDecimals = 2 } });
        Assert.Equal("0.00 CY", twoPlaces.VolumeCell(twoPlaces.RoundVolume(0.009)));     // below ZeroTolerance 0.01
        Assert.Equal("0.02 CY", twoPlaces.VolumeCell(twoPlaces.RoundVolume(0.02)));      // above it
    }

    [Fact]
    public void Numbers_group_thousands_and_follow_unit_decimals()
    {
        Assert.Equal("1,234,568 CY", F.VolumeCell(F.RoundVolume(1_234_567.5)));
        Assert.Equal("100,000", F.AreaNumber(F.RoundArea(100_000, AreaUnit.SquareFeet), AreaUnit.SquareFeet));
        Assert.Equal("2.30", F.AreaNumber(F.RoundArea(100_000, AreaUnit.Acres), AreaUnit.Acres));
        Assert.Equal("2.30 AC", F.AreaCell(F.RoundArea(100_000, AreaUnit.Acres), AreaUnit.Acres));
    }

    [Fact]
    public void Flagged_coverage_never_displays_as_100_percent()
    {
        Assert.Equal("99.9%", F.CoveragePercent(0.99996, flagged: true));
        Assert.Equal("100.0%", F.CoveragePercent(0.9999999999, flagged: false));
        Assert.Equal("85.0%", F.CoveragePercent(0.85, flagged: true));
    }

    [Fact]
    public void E1020_metric_labels()
    {
        var m = OutputTestKit.Formatter(units: Scenario.Metres);
        Assert.Equal("12 m³", m.VolumeCell(m.RoundVolume(12)));
        Assert.Equal("1.50 ha", m.AreaCell(m.RoundArea(15_000, AreaUnit.Hectares), AreaUnit.Hectares));
        Assert.Equal("m³/m", m.Units.AverageLabel);
    }

    [Fact]
    public void Factors_show_every_significant_digit()
    {
        Assert.Equal("1.25", F.Factor(1.25));
        Assert.Equal("0.9", F.Factor(0.9));
        Assert.Equal("1.0", F.Factor(1.0));
        Assert.Equal("1.125", F.Factor(1.125));
    }
}

public class SegmentTableTests
{
    private static readonly ReportConfig Cfg = ReportConfig.Default;

    [Fact]
    public void Per_sheet_table_has_a_title_and_a_three_row_block_per_analysis()
    {
        var r = Scenario.T2().Reconcile();
        var t = SegmentTableBuilder.Build(r, r.Segments[0].Id, AreaUnit.SquareFeet, Scenario.Station, Cfg).Value;

        Assert.Equal(TableKind.Segment, t.Kind);
        Assert.Equal(2, t.ColumnCount);
        Assert.Equal("EARTHWORK STA 0+00 TO STA 5+00", t.Cell(0, 0));
        Assert.Contains(new CellRange(0, 0, 0, 1), t.Merges);
        Assert.Equal(["EG vs FG_A", ""], t.Rows[1].Cells);
        Assert.Equal(["CUT", "100 CY"], t.Rows[2].Cells);
        Assert.Equal(["FILL", "50 CY"], t.Rows[3].Cells);
        Assert.Equal(["NET", "50 CY (CUT)"], t.Rows[4].Cells);
        Assert.Equal(5, t.Rows.Count);
        Assert.Equal("SV-Volumes", t.Style.TableStyle);
    }

    [Fact]
    public void Area_row_appears_only_when_AreaInSegmentTable_is_on()
    {
        var r = Scenario.T2().Reconcile();
        var off = SegmentTableBuilder.Build(r, r.Segments[1].Id, AreaUnit.SquareFeet, Scenario.Station, Cfg).Value;
        Assert.Empty(off.RowsOf(RowKind.Area));

        var cfg = Cfg with { SegmentTable = new SegmentTableConfig { AreaInSegmentTable = true } };
        var on = SegmentTableBuilder.Build(r, r.Segments[1].Id, AreaUnit.Acres, Scenario.Station, cfg).Value;
        Assert.Equal(["AREA", "3.67 AC"], on.RowsOf(RowKind.Area).Single().Cells);
    }

    [Fact]
    public void E505_each_analysis_adds_one_tagged_block()
    {
        var s = new Scenario().WithBreaks(0, 500);
        s.AddAnalysis("EG vs FG", [(100, 50)]);
        s.AddAnalysis("EG vs SUB", [(20, 0)], comparison: "SUB");
        var r = s.Reconcile();
        var t = SegmentTableBuilder.Build(r, r.Segments[0].Id, AreaUnit.SquareFeet, Scenario.Station, Cfg).Value;

        Assert.Equal(2, t.RowsOf(RowKind.AnalysisHeader).Count());
        var second = r.Analyses[1].Analysis.Id;
        Assert.Equal(["CUT", "20 CY"], t.Rows.Single(x => x.Kind == RowKind.Cut && x.AnalysisId == second).Cells);
        Assert.Equal(["NET", "20 CY (CUT)"], t.Rows.Single(x => x.Kind == RowKind.Net && x.AnalysisId == second).Cells);
    }

    [Fact]
    public void T11_and_E410_factored_values_once_and_factors_in_the_footer()
    {
        var r = Scenario.T2(1.25, 0.9).Reconcile();
        var t = SegmentTableBuilder.Build(r, r.Segments[0].Id, AreaUnit.SquareFeet, Scenario.Station, Cfg).Value;

        Assert.Equal("125 CY", t.RowsOf(RowKind.Cut).Single().Cells[1]);
        Assert.Equal("45 CY", t.RowsOf(RowKind.Fill).Single().Cells[1]);
        Assert.Equal("80 CY (CUT)", t.RowsOf(RowKind.Net).Single().Cells[1]);
        Assert.Equal("EG vs FG_A: CUT FACTOR 1.25, FILL FACTOR 0.9", t.RowsOf(RowKind.Footer).Single().Cells[0]);
    }

    [Fact]
    public void No_factor_footer_when_both_factors_are_one()
    {
        var r = Scenario.T2().Reconcile();
        var t = SegmentTableBuilder.Build(r, r.Segments[0].Id, AreaUnit.SquareFeet, Scenario.Station, Cfg).Value;
        Assert.Empty(t.RowsOf(RowKind.Footer));
    }

    [Fact]
    public void Refuses_a_run_that_did_not_reconcile()
    {
        var s = Scenario.T2();
        s.Boundaries.RemoveAt(0);
        var r = s.Reconcile();
        var o = SegmentTableBuilder.Build(r, r.Segments[1].Id, AreaUnit.SquareFeet, Scenario.Station, Cfg);
        Assert.Equal(OutputCodes.ResultNotReconciled, o.Problems.Single().Code);
    }

    [Fact]
    public void Refuses_an_unknown_segment()
    {
        var r = Scenario.T2().Reconcile();
        var o = SegmentTableBuilder.Build(r, Guid.NewGuid(), AreaUnit.SquareFeet, Scenario.Station, Cfg);
        Assert.Equal(OutputCodes.UnknownSegment, o.Problems.Single().Code);
    }

    [Fact]
    public void Out_of_date_marker_goes_on_the_title()
    {
        var r = Scenario.T2().Reconcile();
        var t = SegmentTableBuilder.Build(r, r.Segments[0].Id, AreaUnit.SquareFeet, Scenario.Station, Cfg, outOfDate: true).Value;
        Assert.Equal("EARTHWORK STA 0+00 TO STA 5+00 (OUT OF DATE)", t.Cell(0, 0));
    }
}

public class MasterTableTests
{
    private static readonly ReportConfig Cfg = ReportConfig.Default;

    private static TableModel Master(ReconcileResult r, AreaUnit unit, ReportConfig? cfg = null)
        => MasterTableBuilder.Build(r, unit, Scenario.Station, cfg ?? Cfg).Value;

    [Fact]
    public void Columns_rows_and_the_area_unit_in_the_header()
    {
        var t = Master(Scenario.T2().Reconcile(), AreaUnit.SquareFeet);

        Assert.Equal(7, t.ColumnCount);
        Assert.Equal("EARTHWORK SUMMARY", t.Cell(0, 0));
        Assert.Equal(["SHEET", "BEGIN STA", "END STA", "2D AREA (SF)", "CUT", "FILL", "NET"], t.RowsOf(RowKind.ColumnHeader).Single().Cells);
        var rows = OutputTestKit.Rows(t, RowKind.Segment);
        Assert.Equal(4, rows.Length);
        Assert.Equal(["1", "0+00", "5+00", "100,000", "100 CY", "50 CY", "50 CY (CUT)"], rows[0].Cells);
        Assert.Equal(["3", "13+00", "18+00", "100,000", "0 CY", "150 CY", "150 CY (FILL)"], rows[2].Cells);
        Assert.Equal(["4", "18+00", "20+00", "40,000", "0 CY", "0 CY", "0 CY"], rows[3].Cells);
        Assert.Equal(["TOTAL", "", "", "400,000", "200 CY", "200 CY", "0 CY"], t.RowsOf(RowKind.Total).Single().Cells);
        Assert.Empty(t.RowsOf(RowKind.OutsideLimits));
    }

    [Fact]
    public void T23_acre_rows_total_row_and_footnote()
    {
        var t = Master(Scenario.T2().Reconcile(), AreaUnit.Acres);

        Assert.Equal("2D AREA (AC)", t.RowsOf(RowKind.ColumnHeader).Single().Cells[3]);
        Assert.Equal(["2.30", "3.67", "2.30", "0.92"], t.RowsOf(RowKind.Segment).Select(x => x.Cells[3]));
        Assert.Equal("9.19", t.RowsOf(RowKind.Total).Single().Cells[3]);
        Assert.Contains(t.RowsOf(RowKind.Footer), x => x.Cells[0] == "EG vs FG_A: TOTALS ARE SUMS OF ROUNDED ROWS. UNROUNDED 2D AREA (AC) 9.1827 AC");
        Assert.DoesNotContain(t.RowsOf(RowKind.Footer), x => x.Cells[0].Contains("COVERS"));      // all coverage 100 %
    }

    [Fact]
    public void E413_volume_total_is_the_sum_of_displayed_rows_with_an_unrounded_footnote()
    {
        var s = new Scenario().WithBreaks(0, 100, 200, 300);
        s.AddAnalysis("EG vs FG", [(10.4, 0), (10.4, 0), (10.4, 0)]);
        var t = Master(s.Reconcile(), AreaUnit.SquareFeet);

        Assert.All(t.RowsOf(RowKind.Segment), x => Assert.Equal("10 CY", x.Cells[4]));
        Assert.Equal("30 CY", t.RowsOf(RowKind.Total).Single().Cells[4]);
        string note = t.RowsOf(RowKind.Footer).Single(x => x.Cells[0].Contains("UNROUNDED")).Cells[0];
        Assert.Contains("CUT 31.20 CY", note);
        Assert.Contains("NET 31.20 CY (CUT)", note);
    }

    [Fact]
    public void No_footnote_when_totals_agree()
    {
        var t = Master(Scenario.T2().Reconcile(), AreaUnit.SquareFeet);
        Assert.DoesNotContain(t.RowsOf(RowKind.Footer), x => x.Cells[0].Contains("UNROUNDED"));
    }

    [Fact]
    public void Outside_limits_row_appears_above_tolerance_with_factors_applied()
    {
        var s = new Scenario().WithBreaks(0, 500);
        s.AddAnalysis("EG vs FG", [(100, 50)], cutFactor: 1.25, outsideCy: (10, 0));
        var t = Master(s.Reconcile(), AreaUnit.SquareFeet);

        Assert.Equal(["OUTSIDE LIMITS", "", "", "", "13 CY", "0 CY", "13 CY (CUT)"], t.RowsOf(RowKind.OutsideLimits).Single().Cells);
        Assert.Equal("125 CY", t.RowsOf(RowKind.Total).Single().Cells[4]);      // sheets only by default (ASSUMED)
    }

    [Fact]
    public void Total_includes_outside_limits_when_configured()
    {
        var s = new Scenario().WithBreaks(0, 500);
        s.AddAnalysis("EG vs FG", [(100, 50)], outsideCy: (10, 0));
        var cfg = Cfg with { MasterTable = new MasterTableConfig { TotalIncludesOutsideLimits = true } };
        var t = Master(s.Reconcile(), AreaUnit.SquareFeet, cfg);
        Assert.Equal("110 CY", t.RowsOf(RowKind.Total).Single().Cells[4]);
    }

    [Fact]
    public void E405_E419_coverage_flags_mark_the_sheet_and_explain_in_the_footer()
    {
        var s = new Scenario().WithBreaks(0, 500, 1000);
        s.AddAnalysis("EG vs FG", [(100, 0), (0, 0)], areas: [100_000, 0]);
        var t = Master(s.Reconcile(), AreaUnit.SquareFeet);

        var rows = OutputTestKit.Rows(t, RowKind.Segment);
        Assert.Equal("1", rows[0].Cells[0]);
        Assert.Equal("2*", rows[1].Cells[0]);
        Assert.Equal("0", rows[1].Cells[3]);
        Assert.Contains(t.RowsOf(RowKind.Footer), x => x.Cells[0] == "* SHEET 2 (EG vs FG): SURFACE DATA COVERS 0.0% OF THE BOUNDARY");
    }

    [Fact]
    public void E410_factors_and_surfaces_in_the_footer()
    {
        var t = Master(Scenario.T2(1.25, 0.9).Reconcile(), AreaUnit.SquareFeet);
        Assert.Contains(t.RowsOf(RowKind.Footer), x => x.Cells[0] == "EG vs FG_A: BASE EG, COMPARISON FG_A, CUT FACTOR 1.25, FILL FACTOR 0.9");
        Assert.Equal("250 CY", t.RowsOf(RowKind.Total).Single().Cells[4]);    // 200 × 1.25, applied once
    }

    [Fact]
    public void E215_partition_skip_is_footnoted()
    {
        var s = Scenario.T2();
        s.PartitionSkipReason = "Boundaries 1 and 3 overlap by 250 sq ft.";
        var t = Master(s.Reconcile(), AreaUnit.SquareFeet);
        Assert.Contains(t.RowsOf(RowKind.Footer), x => x.Cells[0] == "EG vs FG_A: PARTITION CHECK NOT RUN. Boundaries 1 and 3 overlap by 250 sq ft.");
    }

    [Fact]
    public void E418_area_unit_switch_is_display_only()
    {
        var s = Scenario.T2();
        var src = (FakeVolumeSource)s.Analyses[0].Source;
        var run = new RunDefinition("Run1", "S", SegmentDriver.Stations, "0,500,1300,1800,2000", null, -100, 100,
            [s.Analyses[0].Analysis], [], s.Segments, new Dictionary<Guid, SegmentOverrides>(), new ViewOptions());
        var r = s.Reconcile();
        int bounded = src.BoundedCalls, area = src.AreaCalls;
        var rawBefore = r.AllRows.Select(x => x.Area2D).ToList();
        var sf = Master(r, run.AreaUnit);

        var change = AreaUnitChange.Apply(run, AreaUnit.Acres, UnitSystem.Imperial).Value;
        var ac = Master(r, change.Run.AreaUnit, null);

        Assert.True(change.TablesOutOfDate);
        Assert.Equal(AreaUnit.Acres, change.Run.AreaUnit);
        Assert.Equal(bounded, src.BoundedCalls);                     // nothing recomputed
        Assert.Equal(area, src.AreaCalls);
        Assert.Equal(rawBefore, r.AllRows.Select(x => x.Area2D));    // raw area unchanged
        Assert.Equal("100,000", sf.RowsOf(RowKind.Segment).First().Cells[3]);
        Assert.Equal("2.30", ac.RowsOf(RowKind.Segment).First().Cells[3]);
        Assert.Equal(sf.RowsOf(RowKind.Segment).Select(x => x.Cells[4]), ac.RowsOf(RowKind.Segment).Select(x => x.Cells[4]));

        Assert.False(AreaUnitChange.Apply(run, AreaUnit.SquareFeet, UnitSystem.Imperial).Value.TablesOutOfDate);
        Assert.Equal(VolumeCodes.AreaUnitMismatch, AreaUnitChange.Apply(run, AreaUnit.Hectares, UnitSystem.Imperial).Problems.Single().Code);
        Assert.Equal(VolumeCodes.AreaUnitMismatch, MasterTableBuilder.Build(r, AreaUnit.Hectares, Scenario.Station, Cfg).Problems.Single().Code);
    }

    [Fact]
    public void E508_tables_use_the_configured_culture_not_the_regional_settings()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var t = Master(Scenario.T2().Reconcile(), AreaUnit.Acres);
            Assert.Equal("2.30", t.RowsOf(RowKind.Segment).First().Cells[3]);

            var big = Master(Scenario.T2().Reconcile(), AreaUnit.SquareFeet);
            Assert.Equal("100,000", big.RowsOf(RowKind.Segment).First().Cells[3]);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Two_analyses_give_two_blocks_each_with_its_own_total()
    {
        var s = new Scenario().WithBreaks(0, 500);
        s.AddAnalysis("EG vs FG", [(100, 50)]);
        s.AddAnalysis("EG vs SUB", [(20, 0)], comparison: "SUB");
        var t = Master(s.Reconcile(), AreaUnit.SquareFeet);

        Assert.Equal(2, t.RowsOf(RowKind.Total).Count());
        Assert.Equal(["EG vs FG", "EG vs SUB"], t.RowsOf(RowKind.AnalysisHeader).Select(x => x.Cells[0]));
    }
}

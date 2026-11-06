using SheetVol.Core.Volumes;

namespace SheetVol.Core.Tests.Volumes;

public class ReconcilerTests
{
    private const double Tol = 1e-9;

    [Fact]
    public void T2_mixed_lengths_reconcile_in_cubic_yards_and_the_partition_check_passes()
    {
        var r = Scenario.T2().Reconcile();

        Assert.True(r.Succeeded, string.Join("; ", r.Problems.Select(p => p.Message)));
        var rows = r.Analyses.Single().Rows;
        Assert.Equal([100.0, 100, 0, 0], rows.Select(x => Math.Round(x.Cut, 9)));
        Assert.Equal([50.0, 0, 150, 0], rows.Select(x => Math.Round(x.Fill, 9)));
        Assert.Equal([50.0, 100, -150, 0], rows.Select(x => Math.Round(x.Net, 9)));
        Assert.Equal([500.0, 800, 500, 200], rows.Select(x => x.EndStation - x.StartStation));
        Assert.All(rows, x => Assert.Equal(SegmentStatus.Current, x.Status));
        Assert.True(r.PartitionCheckPassed);
        Assert.False(r.Analyses.Single().OutsideLimits!.Shown);
    }

    [Fact]
    public void T11_factors_are_applied_exactly_once_and_raw_stays_unadjusted()
    {
        var r = Scenario.T2(cutFactor: 1.25, fillFactor: 0.9).Reconcile();

        var first = r.Analyses.Single().Rows[0];
        Assert.Equal(2700, first.Raw.Cut, Tol);          // unadjusted cubic feet
        Assert.Equal(1350, first.Raw.Fill, Tol);
        Assert.Equal(125, first.Cut, Tol);               // 100 CY × 1.25
        Assert.Equal(45, first.Fill, Tol);               // 50 CY × 0.9
        Assert.Equal(80, first.Net, Tol);
        Assert.True(r.PartitionCheckPassed);             // the check runs on raw volumes, unaffected by factors
    }

    [Fact]
    public void Metric_run_reports_cubic_metres_from_the_supplied_scale()
    {
        var s = new Scenario().WithBreaks(0, 100);
        s.AddAnalysis("EG vs FG", [(10, 5)]);     // the fake stores 270 and 135 cubic drawing units
        var r = s.Reconcile(units: Scenario.Metres);

        var row = r.Analyses.Single().Rows.Single();
        Assert.Equal(270, row.Cut, Tol);                 // m³ are not divided by 27
        Assert.Equal(135, row.Fill, Tol);
    }

    [Fact]
    public void Partition_failure_is_an_error_result_never_a_number()
    {
        var s = Scenario.T2();
        var src = (FakeVolumeSource)s.Analyses[0].Source;
        src.Answer(s.Bands[0], new RawVolume(5400 + 5, 5400), 0);     // band disagrees by 5 ft³
        src.SurfaceTotals = new RawVolume(5405, 5400);

        var r = s.Reconcile();

        Assert.False(r.Succeeded);
        Assert.False(r.PartitionCheckPassed);
        var p = Assert.Single(r.Problems, x => x.Code == VolumeCodes.PartitionMismatch);
        Assert.Equal(Severity.Error, p.Severity);
        Assert.Contains("5400", p.Message);
        Assert.Contains("5405", p.Message);
        Assert.All(r.Analyses.Single().Rows, row =>
        {
            Assert.Equal(SegmentStatus.Error, row.Status);
            Assert.True(double.IsNaN(row.Cut) && double.IsNaN(row.Fill) && double.IsNaN(row.Net) && double.IsNaN(row.Area2D));
        });
    }

    [Fact]
    public void E215_overlapping_boundaries_skip_the_partition_check_with_the_stated_reason()
    {
        var s = Scenario.T2();
        s.PartitionSkipReason = "Boundaries 1 and 3 overlap by 250 sq ft.";
        var src = (FakeVolumeSource)s.Analyses[0].Source;
        src.Answer(s.Bands[0], new RawVolume(1, 1), 0);    // would fail the check if it ran
        src.SurfaceTotals = new RawVolume(1, 1);

        var r = s.Reconcile();

        Assert.True(r.Succeeded);
        Assert.False(r.PartitionCheckPassed);
        Assert.Equal(PartitionStatus.Skipped, r.Analyses[0].Partition.Status);
        var w = Assert.Single(r.Problems, x => x.Code == VolumeCodes.PartitionSkipped);
        Assert.Equal(Severity.Warning, w.Severity);
        Assert.Contains("overlap by 250", w.Message);
    }

    [Fact]
    public void E303_outside_limits_is_surface_totals_minus_band_and_shown_above_tolerance()
    {
        var s = new Scenario().WithBreaks(0, 500);
        s.AddAnalysis("EG vs FG_A", [(100, 50)], outsideCy: (10, 0));   // e.g. a skip gap's volume
        var r = s.Reconcile();

        var o = r.Analyses.Single().OutsideLimits!;
        Assert.True(r.Succeeded);
        Assert.True(o.Shown);
        Assert.Equal(270, o.Difference.Cut, Tol);
        Assert.Equal(0, o.Difference.Fill, Tol);
    }

    [Fact]
    public void E405_core_no_surface_data_gives_zero_volumes_zero_coverage_and_a_flag()
    {
        var s = new Scenario().WithBreaks(0, 500);
        s.AddAnalysis("EG vs FG", [(0, 0)], areas: [0]);
        var r = s.Reconcile();

        var row = r.Analyses.Single().Rows.Single();
        Assert.True(r.Succeeded);
        Assert.Equal(0, row.Cut);
        Assert.Equal(0, row.Fill);
        Assert.Equal(0, row.Coverage);
        Assert.Equal(VolumeCodes.NoSurfaceData, row.Problems.Single().Code);
        Assert.True(Reconciler.IsCoverageFlagged(row));
    }

    [Fact]
    public void E408_core_identical_surfaces_give_exactly_zero_and_full_coverage()
    {
        var s = new Scenario().WithBreaks(0, 500);
        s.AddAnalysis("EG vs EG copy", [(0, 0)], comparison: "EG2");
        var r = s.Reconcile();

        var row = r.Analyses.Single().Rows.Single();
        Assert.Equal(0.0, row.Net);
        Assert.Equal(1.0, row.Coverage);
        Assert.Empty(row.Problems);
        Assert.False(Reconciler.IsCoverageFlagged(row));
    }

    [Fact]
    public void E419_partial_coverage_is_flagged_and_values_still_report()
    {
        var s = new Scenario().WithBreaks(0, 500);
        s.AddAnalysis("EG vs FG", [(100, 0)], areas: [85_000]);
        var r = s.Reconcile();

        var row = r.Analyses.Single().Rows.Single();
        Assert.True(r.Succeeded);
        Assert.Equal(0.85, row.Coverage, Tol);
        Assert.Equal(100, row.Cut, Tol);
        Assert.Equal(VolumeCodes.PartialCoverage, row.Problems.Single().Code);
        Assert.Equal(Severity.Warning, row.Problems.Single().Severity);
    }

    [Fact]
    public void Coverage_noise_within_tolerance_is_not_flagged()
    {
        var s = new Scenario().WithBreaks(0, 500);
        s.AddAnalysis("EG vs FG", [(1, 0)], areas: [100_000 * (1 - 1e-9)]);
        Assert.Empty(s.Reconcile().Analyses.Single().Rows.Single().Problems);
    }

    [Fact]
    public void Area_larger_than_the_boundary_is_an_error()
    {
        var s = new Scenario().WithBreaks(0, 500);
        s.AddAnalysis("EG vs FG", [(1, 0)], areas: [101_000]);
        var r = s.Reconcile();

        Assert.False(r.Succeeded);
        Assert.Equal(VolumeCodes.CoverageExceedsBoundary, r.Analyses.Single().Rows.Single().Problems.Single().Code);
    }

    [Fact]
    public void E421_core_2D_area_is_per_analysis_not_per_segment()
    {
        var s = new Scenario().WithBreaks(0, 500, 1000);
        s.AddAnalysis("EG vs FG", [(10, 0), (10, 0)]);                                     // full cover
        s.AddAnalysis("EG vs SUB", [(5, 0), (5, 0)], areas: [100_000, 40_000], comparison: "SUB"); // SUB stops at 700
        var r = s.Reconcile();

        Assert.True(r.Succeeded);
        var seg2 = r.Segments[1].Id;
        var fg = r.Analyses[0].Rows.Single(x => x.SegmentId == seg2);
        var sub = r.Analyses[1].Rows.Single(x => x.SegmentId == seg2);
        Assert.Equal(100_000, fg.Area2D);
        Assert.Equal(40_000, sub.Area2D);
        Assert.Equal(1.0, fg.Coverage, Tol);
        Assert.Equal(0.4, sub.Coverage, Tol);
        Assert.Equal(r.Analyses[1].Analysis.Id, sub.AnalysisId);
    }

    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(double.NaN, 0.0)]
    [InlineData(0.0, double.PositiveInfinity)]
    public void A_source_value_that_is_not_a_non_negative_number_is_an_error_row(double cut, double fill)
    {
        var s = new Scenario().WithBreaks(0, 500);
        var src = s.AddAnalysis("EG vs FG", [(0, 0)]);
        src.Answer(s.Boundaries[0].Tessellated, new RawVolume(cut, fill), 100_000);
        var r = s.Reconcile();

        Assert.False(r.Succeeded);
        var row = r.Analyses.Single().Rows.Single();
        Assert.Equal(SegmentStatus.Error, row.Status);
        Assert.True(double.IsNaN(row.Cut));
        Assert.Contains(r.Problems, p => p.Code == VolumeCodes.InvalidSourceValue && p.SegmentId == row.SegmentId);
    }

    [Fact]
    public void A_segment_without_a_boundary_is_an_error_row()
    {
        var s = Scenario.T2();
        s.Boundaries.RemoveAt(1);
        var r = s.Reconcile();

        Assert.False(r.Succeeded);
        Assert.Equal(SegmentStatus.Error, r.Analyses[0].Rows[1].Status);
        Assert.Equal(VolumeCodes.MissingBoundary, r.Analyses[0].Rows[1].Problems.Single().Code);
        Assert.Equal(PartitionStatus.Skipped, r.Analyses[0].Partition.Status);
    }

    [Fact]
    public void E410_invalid_factor_refuses_the_run_before_any_volume()
    {
        var s = Scenario.T2(cutFactor: 0);
        var r = s.Reconcile();

        Assert.False(r.Succeeded);
        Assert.Empty(r.Analyses);
        Assert.Equal(VolumeCodes.InvalidFactor, r.Problems.Single().Code);
        Assert.Equal(0, ((FakeVolumeSource)s.Analyses[0].Source).BoundedCalls);
    }

    [Fact]
    public void Invalid_drawing_units_refuse_the_run()
    {
        var r = Scenario.T2().Reconcile(units: new DrawingUnits(UnitSystem.Imperial, 0));
        Assert.False(r.Succeeded);
        Assert.Equal(VolumeCodes.InvalidDrawingUnits, r.Problems.Single().Code);
    }

    [Fact]
    public void Impacts_pass_through_to_their_rows()
    {
        var s = new Scenario().WithBreaks(0, 500);
        var id = s.Segments[0].Id;
        var impact = new ImpactResult(id, "SEDIMENT", 500, 16_000, 26_000, [(0, 500)], [], false);
        s.AddAnalysis("SEDIMENT", [(963, 0)], impacts: new Dictionary<Guid, ImpactResult> { [id] = impact });

        Assert.Same(impact, s.Reconcile().Analyses.Single().Rows.Single().Impact);
    }

    [Fact]
    public void Zero_analyses_reconcile_to_no_rows()
    {
        var s = new Scenario().WithBreaks(0, 500);
        var r = s.Reconcile();
        Assert.True(r.Succeeded);
        Assert.Empty(r.AllRows);
        Assert.False(r.PartitionCheckPassed);
    }
}

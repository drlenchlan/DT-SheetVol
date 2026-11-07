using SheetVol.Core.Volumes;

namespace SheetVol.Core.Tests.Volumes;

public class PartitionCheckTests
{
    private static readonly QuantityUnits Ft = QuantityUnits.Create(Scenario.Feet, new VolumeConfig()).Value;
    private static readonly QuantityUnits M = QuantityUnits.Create(Scenario.Metres, new VolumeConfig()).Value;

    [Fact]
    public void Passes_when_segments_sum_to_the_band_within_half_a_cubic_foot()
    {
        var r = PartitionCheck.Run([new(1000, 500), new(2000, 0)], new RawVolume(3000.4, 499.6), Ft);
        Assert.Equal(PartitionStatus.Passed, r.Status);
        Assert.Equal(0.5, r.Tolerance, 15);
    }

    [Fact]
    public void Fails_beyond_tolerance_and_states_both_numbers()
    {
        var r = PartitionCheck.Run([new(1000, 500), new(2000, 0)], new RawVolume(3000.6, 500), Ft);
        Assert.Equal(PartitionStatus.Failed, r.Status);
        Assert.Contains("3000", r.Note);
        Assert.Contains("3000.6", r.Note);
    }

    [Fact]
    public void Relative_tolerance_governs_large_totals()
    {
        var band = new RawVolume(5e9, 5e9);     // gross 1e10, tolerance 10 ft³
        Assert.Equal(PartitionStatus.Passed, PartitionCheck.Run([new(5e9 + 9, 5e9)], band, Ft).Status);
        Assert.Equal(PartitionStatus.Failed, PartitionCheck.Run([new(5e9 + 11, 5e9)], band, Ft).Status);
    }

    [Fact]
    public void E216_metric_tolerance_is_the_same_volume_in_cubic_metres()
    {
        // 0.5 ft³ = 0.014158 m³
        Assert.Equal(PartitionStatus.Passed, PartitionCheck.Run([new(10.014, 0)], new RawVolume(10, 0), M).Status);
        Assert.Equal(PartitionStatus.Failed, PartitionCheck.Run([new(10.015, 0)], new RawVolume(10, 0), M).Status);
    }

    [Fact]
    public void E215_skip_reason_reports_skipped_and_never_fails()
    {
        var r = PartitionCheck.Run([new(1, 1)], new RawVolume(999, 999), Ft, "Boundaries 2 and 7 overlap by 120 sq ft.");
        Assert.Equal(PartitionStatus.Skipped, r.Status);
        Assert.Equal("Boundaries 2 and 7 overlap by 120 sq ft.", r.Note);
    }

    [Fact]
    public void Outside_limits_is_hidden_within_tolerance_and_shown_above()
    {
        var hidden = PartitionCheck.OutsideLimits(new RawVolume(1000.3, 500), new RawVolume(1000, 500), Ft).Value;
        Assert.False(hidden.Shown);
        Assert.Equal(0.3, hidden.Difference.Cut, 9);

        var shown = PartitionCheck.OutsideLimits(new RawVolume(1270, 500), new RawVolume(1000, 500), Ft).Value;
        Assert.True(shown.Shown);
        Assert.Equal(270, shown.Difference.Cut, 9);
        Assert.Equal(0, shown.Difference.Fill, 9);
    }

    [Fact]
    public void Outside_limits_below_minus_tolerance_is_an_error_never_a_negative_row()
    {
        var o = PartitionCheck.OutsideLimits(new RawVolume(1000, 500), new RawVolume(1001, 500), Ft);
        Assert.False(o.Succeeded);
        Assert.Equal(VolumeCodes.OutsideLimitsNegative, o.Problems.Single().Code);
    }
}

public class PlanAreaTests
{
    [Fact]
    public void Polygon_area_of_a_closed_rectangle()
    {
        Assert.Equal(100_000, PlanArea.Polygon(Scenario.Rect(0, 500, -100, 100)), 9);
    }

    [Fact]
    public void Polygon_area_keeps_precision_at_state_plane_coordinates()
    {
        double x0 = 3_100_000.123, y0 = 13_800_000.456;
        Pt[] p = [new(x0, y0), new(x0 + 500, y0), new(x0 + 500, y0 + 200), new(x0, y0 + 200), new(x0, y0)];
        Assert.Equal(100_000, PlanArea.Polygon(p), 6);
    }

    [Fact]
    public void Area_from_datums_is_the_net_change_over_the_datum_shift()
    {
        // 1,000 sq units of data; raising the datum by 2 changes the net by 2,000.
        var atLow = new RawVolume(0, 10_000);
        var atHigh = new RawVolume(0, 8_000);
        Assert.Equal(1000, PlanArea.FromDatums(atLow, 90, atHigh, 92).Value, 12);
        Assert.Equal(1000, PlanArea.FromDatums(atHigh, 92, atLow, 90).Value, 12);

        // Net changes sign across the datums: cut on one side, fill on the other.
        Assert.Equal(1000, PlanArea.FromDatums(new RawVolume(500, 0), 100, new RawVolume(0, 500), 101).Value, 12);
    }

    [Fact]
    public void Area_from_datums_reads_two_sources_on_the_same_polygon()
    {
        var poly = Scenario.Rect(0, 500, -100, 100);
        var d1 = new FakeVolumeSource();
        var d2 = new FakeVolumeSource();
        d1.Answer(poly, new RawVolume(100_000, 0), 0);
        d2.Answer(poly, new RawVolume(0, 50_000), 0);
        Assert.Equal(100_000, PlanArea.FromDatums(d1, 99, d2, 100.5, poly).Value, 9);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(double.NaN, 100)]
    public void Area_from_datums_refuses_equal_or_invalid_datums(double d1, double d2)
    {
        var o = PlanArea.FromDatums(new RawVolume(1, 0), d1, new RawVolume(0, 1), d2);
        Assert.Equal(VolumeCodes.InvalidDatumPair, o.Problems.Single().Code);
    }

    [Fact]
    public void Area_from_datums_refuses_a_negative_volume()
    {
        var o = PlanArea.FromDatums(new RawVolume(-1, 0), 1, new RawVolume(0, 1), 2);
        Assert.Equal(VolumeCodes.InvalidSourceValue, o.Problems.Single().Code);
    }
}

public class OwnedVolumeSurfacesTests
{
    [Fact]
    public void E411_analyses_sharing_a_pair_share_one_owned_surface()
    {
        var a = new AnalysisDef(Guid.NewGuid(), "EG vs FG", "EG", "FG");
        var b = new AnalysisDef(Guid.NewGuid(), "EG vs FG again", "EG", "FG");
        var c = new AnalysisDef(Guid.NewGuid(), "EG vs SUB", "EG", "SUB");

        var o = OwnedVolumeSurfaces.Plan([a, b, c], new VolumeConfig());

        Assert.True(o.Succeeded);
        Assert.Equal(2, o.Value.Count);
        Assert.Equal("SV-EG-FG", o.Value[0].Name);
        Assert.Equal([a.Id, b.Id], o.Value[0].AnalysisIds);
        Assert.Equal("SV-EG-SUB", o.Value[1].Name);
        Assert.Empty(o.Problems);
    }

    [Fact]
    public void Volume_surface_analysis_plans_no_owned_surface_and_never_shares_one()
    {
        // Rev 3 (X6): the dialog writes the volume surface's pair into BaseSurface / ComparisonSurface
        // (display only). A pair analysis on the same surfaces keeps its own owned surface alone.
        var pair = new AnalysisDef(Guid.NewGuid(), "EG vs FG", "EG", "FG");
        var fromVolume = new AnalysisDef(Guid.NewGuid(), "VOL", "EG", "FG", 1.3, 0.8, VolumeSurface: "USER-VOL");

        var both = OwnedVolumeSurfaces.Plan([pair, fromVolume], new VolumeConfig());
        Assert.True(both.Succeeded);
        var only = Assert.Single(both.Value);
        Assert.Equal([pair.Id], only.AnalysisIds);
        Assert.Equal((1.0, 1.0), (only.CutFactor, only.FillFactor));
        Assert.Empty(both.Problems);   // no E411 factor warning: the volume-surface analysis is not in the group

        var alone = OwnedVolumeSurfaces.Plan([fromVolume], new VolumeConfig());
        Assert.True(alone.Succeeded);
        Assert.Empty(alone.Value);
    }

    [Fact]
    public void E411_shared_pair_with_different_factors_warns()
    {
        var a = new AnalysisDef(Guid.NewGuid(), "A", "EG", "FG", 1.25, 0.9);
        var b = new AnalysisDef(Guid.NewGuid(), "B", "EG", "FG", 1.0, 1.0);

        var o = OwnedVolumeSurfaces.Plan([a, b], new VolumeConfig());

        Assert.True(o.Succeeded);
        Assert.Single(o.Value);
        Assert.Equal(1.25, o.Value[0].CutFactor);
        Assert.Equal(VolumeCodes.SharedPairFactorMismatch, o.Problems.Single().Code);
    }

    [Fact]
    public void A_surface_compared_with_itself_is_refused()
    {
        var o = OwnedVolumeSurfaces.Plan([new AnalysisDef(Guid.NewGuid(), "X", "EG", "EG")], new VolumeConfig());
        Assert.Equal(VolumeCodes.SameSurface, o.Problems.Single().Code);
    }

    [Fact]
    public void Duplicate_analysis_ids_are_refused()
    {
        var id = Guid.NewGuid();
        var o = OwnedVolumeSurfaces.Plan([new AnalysisDef(id, "A", "EG", "FG"), new AnalysisDef(id, "B", "EG", "SUB")], new VolumeConfig());
        Assert.Contains(o.Problems, p => p.Code == VolumeCodes.DuplicateAnalysisId);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, -0.9)]
    [InlineData(double.NaN, 1)]
    public void E410_invalid_factors_are_refused(double cut, double fill)
    {
        var o = OwnedVolumeSurfaces.Plan([new AnalysisDef(Guid.NewGuid(), "A", "EG", "FG", cut, fill)], new VolumeConfig());
        Assert.Equal(VolumeCodes.InvalidFactor, o.Problems.Single().Code);
    }
}

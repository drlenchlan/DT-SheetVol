using SheetVol.Core.Output;
using SheetVol.Core.Volumes;

namespace SheetVol.Core.Tests.Volumes;

public class QuantityUnitsTests
{
    private static QuantityUnits Units(UnitSystem system, double scale = 1) =>
        QuantityUnits.Create(new DrawingUnits(system, scale), new VolumeConfig()).Value;

    [Fact]
    public void Imperial_volume_is_cubic_feet_over_27()
    {
        Assert.Equal(100.0, Units(UnitSystem.Imperial).ToVolumeUnit(2700), 12);
        Assert.Equal("CY", Units(UnitSystem.Imperial).VolumeLabel);
    }

    [Fact]
    public void E216_metric_volume_is_cubic_metres_unchanged()
    {
        var u = Units(UnitSystem.Metric);
        Assert.Equal(123.4, u.ToVolumeUnit(123.4), 12);
        Assert.Equal("m³", u.VolumeLabel);
        Assert.Equal("m", u.LengthLabel);
    }

    [Fact]
    public void E606_scale_is_an_input_never_inferred()
    {
        // A drawing in inches: 1 drawing unit = 1/12 ft, so 46,656 in³ = 27 ft³ = 1 CY.
        Assert.Equal(1.0, Units(UnitSystem.Imperial, 1.0 / 12).ToVolumeUnit(46_656), 12);
        // Same raw value in a drawing in feet is 1,728 CY.
        Assert.Equal(1728.0, Units(UnitSystem.Imperial).ToVolumeUnit(46_656), 9);
    }

    [Fact]
    public void Area_converts_to_SF_AC_m2_and_ha()
    {
        var ft = Units(UnitSystem.Imperial);
        Assert.Equal(43_560.0, ft.ToArea(43_560, AreaUnit.SquareFeet), 9);
        Assert.Equal(1.0, ft.ToArea(43_560, AreaUnit.Acres), 12);
        var m = Units(UnitSystem.Metric);
        Assert.Equal(10_000.0, m.ToArea(10_000, AreaUnit.SquareMetres), 9);
        Assert.Equal(1.0, m.ToArea(10_000, AreaUnit.Hectares), 12);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Rejects_a_scale_that_is_not_positive(double scale)
    {
        var o = QuantityUnits.Create(new DrawingUnits(UnitSystem.Imperial, scale), new VolumeConfig());
        Assert.False(o.Succeeded);
        Assert.Equal(VolumeCodes.InvalidDrawingUnits, o.Problems.Single().Code);
        Assert.Throws<InvalidOperationException>(() => o.Value);
    }

    [Fact]
    public void Area_unit_from_the_other_system_is_refused()
    {
        Assert.Equal(VolumeCodes.AreaUnitMismatch, QuantityUnits.CheckAreaUnit(UnitSystem.Imperial, AreaUnit.Hectares)!.Code);
        Assert.Equal(VolumeCodes.AreaUnitMismatch, QuantityUnits.CheckAreaUnit(UnitSystem.Metric, AreaUnit.Acres)!.Code);
        Assert.Null(QuantityUnits.CheckAreaUnit(UnitSystem.Imperial, AreaUnit.Acres));
        Assert.Throws<ArgumentException>(() => Units(UnitSystem.Imperial).ToArea(1, AreaUnit.SquareMetres));
    }

    [Fact]
    public void E917_offered_and_default_area_units_follow_the_drawing_and_preset()
    {
        Assert.Equal([AreaUnit.SquareFeet, AreaUnit.Acres], QuantityUnits.AreaUnitsFor(UnitSystem.Imperial));
        Assert.Equal([AreaUnit.SquareMetres, AreaUnit.Hectares], QuantityUnits.AreaUnitsFor(UnitSystem.Metric));
        Assert.Equal(AreaUnit.SquareFeet, QuantityUnits.DefaultAreaUnit(UnitSystem.Imperial, ReportPreset.Standard));
        Assert.Equal(AreaUnit.Acres, QuantityUnits.DefaultAreaUnit(UnitSystem.Imperial, ReportPreset.HcfcdSegment));
        Assert.Equal(AreaUnit.SquareMetres, QuantityUnits.DefaultAreaUnit(UnitSystem.Metric, ReportPreset.Standard));
        Assert.Equal(AreaUnit.Hectares, QuantityUnits.DefaultAreaUnit(UnitSystem.Metric, ReportPreset.HcfcdSegment));
    }

    [Fact]
    public void Default_area_decimals_are_0_for_SF_and_m2_and_2_for_AC_and_ha()
    {
        var c = ReportConfig.Default;
        Assert.Equal(0, c.AreaDecimals(AreaUnit.SquareFeet));
        Assert.Equal(2, c.AreaDecimals(AreaUnit.Acres));
        Assert.Equal(0, c.AreaDecimals(AreaUnit.SquareMetres));
        Assert.Equal(2, c.AreaDecimals(AreaUnit.Hectares));
    }

    [Fact]
    public void Partition_tolerance_is_half_a_cubic_foot_or_relative_to_the_total()
    {
        var ft = Units(UnitSystem.Imperial);
        Assert.Equal(0.5, ft.PartitionTolerance(1000), 15);
        Assert.Equal(10.0, ft.PartitionTolerance(1e10), 9);       // 1e-9 × 1e10
    }

    [Fact]
    public void E216_metric_partition_tolerance_is_half_a_cubic_foot_in_cubic_metres()
    {
        var m = Units(UnitSystem.Metric);
        Assert.Equal(0.5 * 0.3048 * 0.3048 * 0.3048, m.PartitionTolerance(1), 15);
    }
}

public class DisplayRoundingTests
{
    [Fact]
    public void T23_and_E420_acre_rows_total_is_the_sum_of_displayed_rows()
    {
        double[] sf = [100_000, 160_000, 100_000, 40_000];
        var ac = sf.Select(a => a / 43_560).ToList();

        var col = DisplayRounding.Column(ac, decimals: 2, zeroTolerance: 0);

        Assert.Equal([2.30m, 3.67m, 2.30m, 0.92m], col.Rows);
        Assert.Equal(9.19m, col.RowsTotal);
        Assert.Equal(9.1827, col.UnroundedTotal, 4);
        Assert.Equal(9.18m, col.UnroundedTotalRounded);
        Assert.True(col.TotalDiffers);
    }

    [Fact]
    public void E413_volume_rows_that_do_not_sum_to_the_rounded_true_total()
    {
        var col = DisplayRounding.Column([10.4, 10.4, 10.4], decimals: 0, zeroTolerance: 0.01);

        Assert.Equal([10m, 10m, 10m], col.Rows);
        Assert.Equal(30m, col.RowsTotal);
        Assert.Equal(31.2, col.UnroundedTotal, 12);
        Assert.Equal(31m, col.UnroundedTotalRounded);
        Assert.True(col.TotalDiffers);
    }

    [Fact]
    public void Totals_that_agree_need_no_footnote()
    {
        var col = DisplayRounding.Column([100, 100, 0, 0], 0, 0.01);
        Assert.Equal(200m, col.RowsTotal);
        Assert.False(col.TotalDiffers);
    }

    [Theory]
    [InlineData(2.675, 2, "2.68")]
    [InlineData(0.5, 0, "1")]
    [InlineData(-0.5, 0, "-1")]
    [InlineData(1.005, 2, "1.01")]
    public void Midpoints_round_away_from_zero_on_the_decimal_value(double value, int decimals, string expected)
    {
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), DisplayRounding.Round(value, decimals));
    }

    [Fact]
    public void A_value_that_rounds_to_zero_is_never_negative_zero()
    {
        decimal r = DisplayRounding.Round(-0.0004, 2);
        Assert.Equal(0m, r);
        Assert.Equal("0.00", r.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void E409_noise_below_the_zero_tolerance_displays_as_zero_but_the_total_keeps_it()
    {
        var col = DisplayRounding.Column([0.004, -0.003, 0.002], decimals: 2, zeroTolerance: 0.01);

        Assert.Equal([0m, 0m, 0m], col.Rows);
        Assert.Equal(0.003, col.UnroundedTotal, 15);     // raw kept for the export
        Assert.Equal(0m, col.UnroundedTotalRounded);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1e300)]
    public void Round_refuses_a_value_that_is_not_a_displayable_number(double v)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayRounding.Round(v, 0));
    }
}

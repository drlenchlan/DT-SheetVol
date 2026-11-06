using SheetVol.Core.Tests.Fakes;
using SheetVol.Core.Views;

namespace SheetVol.Core.Tests.Views;

/// <summary>View math per HANDOFF.md 6.6: T15, T16, T17, E309, E310, E606, E607, E610.</summary>
public class ViewCalculatorTests
{
    private const double Tol = 1e-9;
    private static readonly ViewMathConfig Config = new();
    private static readonly ViewCalculator Calc = new(Config);

    // Alignment S from section 8: straight from (0,0) to (2000,0), station 0 at x = 0, left is +y.
    private static FakeAlignment AlignmentS() => FakeAlignments.S();

    private static SegmentDef Seg(double start, double end, double left = -100, double right = 100, string label = "1")
        => new(Guid.Parse("11111111-1111-1111-1111-111111111111"), label, start, end, left, right);

    private static ViewResult Compute(IAlignmentGeometry a, SegmentDef seg, ViewOptions opt, double w = 12, double h = 5,
                                      SegmentOverrides? ov = null, ViewCalculator? calc = null)
    {
        var src = new FakeBandSource(a, seg.OffsetLeft, seg.OffsetRight);
        return (calc ?? Calc).Compute(a, src, new ViewRequest(seg, opt, w, h, ov));
    }

    [Fact]
    public void T15_straight_0_to_500_at_50_has_twist_0_center_250_0_needs_10_by_4_and_fits()
    {
        var r = Compute(AlignmentS(), Seg(0, 500), new ViewOptions(ModelUnitsPerPaperUnit: 50));

        Assert.True(r.Succeeded, string.Join(" | ", r.Problems.Select(p => p.Message)));
        ViewSpec spec = r.Spec!;
        Assert.Equal(0, spec.TwistRadians, Tol);
        Assert.Equal(250, spec.ModelCenter.X, Tol);
        Assert.Equal(0, spec.ModelCenter.Y, Tol);
        Assert.Equal(10, r.RequiredPaperWidth, Tol);
        Assert.Equal(4, r.RequiredPaperHeight, Tol);
        Assert.Equal(50, spec.ModelUnitsPerPaperUnit);
        Assert.Equal(12, spec.PaperWidth);    // the template viewport the view was fitted to
        Assert.Equal(5, spec.PaperHeight);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void T16_fixed_scale_fails_for_0_to_800()
    {
        var r = Compute(AlignmentS(), Seg(0, 800), new ViewOptions(ScaleMode: ScaleMode.Fixed, ModelUnitsPerPaperUnit: 50));

        Assert.False(r.Succeeded);
        Assert.Null(r.Spec);
        Problem p = Assert.Single(r.Problems);
        Assert.Equal("SV-E309", p.Code);
        Assert.Equal(Severity.Error, p.Severity);
        Assert.Equal(16, r.RequiredPaperWidth, Tol);
    }

    [Fact]
    public void T16_fit_standard_picks_100_for_0_to_800()
    {
        var opt = new ViewOptions(ScaleMode: ScaleMode.FitStandard, StandardScales: [20, 30, 40, 50, 60, 100]);
        var r = Compute(AlignmentS(), Seg(0, 800), opt);

        Assert.True(r.Succeeded);
        Assert.Equal(100, r.Spec!.ModelUnitsPerPaperUnit);
        Assert.Equal(8, r.RequiredPaperWidth, Tol);
        Assert.Equal(2, r.RequiredPaperHeight, Tol);
    }

    [Fact]
    public void T17_north_running_alignment_has_twist_minus_90_and_start_maps_to_left_edge()
    {
        var north = FakeAlignments.Straight(new Pt(0, 0), Math.PI / 2, 2000, name: "N");
        var r = Compute(north, Seg(0, 500), new ViewOptions(ModelUnitsPerPaperUnit: 50));

        Assert.True(r.Succeeded);
        ViewSpec spec = r.Spec!;
        Assert.Equal(-Math.PI / 2, spec.TwistRadians, Tol);
        Assert.Equal(0, spec.ModelCenter.X, Tol);
        Assert.Equal(250, spec.ModelCenter.Y, Tol);
        Assert.Equal(10, r.RequiredPaperWidth, Tol);
        Assert.Equal(4, r.RequiredPaperHeight, Tol);

        Pt start = ViewMath.ToPaper(north.PointAt(0, 0), spec.ModelCenter, spec.TwistRadians, spec.ModelUnitsPerPaperUnit);
        Pt end = ViewMath.ToPaper(north.PointAt(500, 0), spec.ModelCenter, spec.TwistRadians, spec.ModelUnitsPerPaperUnit);
        Assert.Equal(-5, start.X, Tol);          // left edge of the 10-wide view
        Assert.Equal(0, start.Y, Tol);
        Assert.Equal(5, end.X, Tol);
        Assert.Equal(-5, spec.ClipInPaper!.Min(v => v.Point.X), Tol);

        // Left of the alignment (west) is up on the sheet, as on alignment S.
        Pt left = ViewMath.ToPaper(north.PointAt(250, -100), spec.ModelCenter, spec.TwistRadians, spec.ModelUnitsPerPaperUnit);
        Assert.Equal(2, left.Y, Tol);
    }

    [Fact]
    public void E309_fixed_scale_error_states_the_maximum_length_that_fits()
    {
        var r = Compute(AlignmentS(), Seg(0, 800), new ViewOptions(ModelUnitsPerPaperUnit: 50));

        Assert.NotNull(r.MaxFittingLength);
        Assert.Equal(600, r.MaxFittingLength!.Value, 0.01);      // 12 paper units x 50
        Problem p = Assert.Single(r.Problems);
        Assert.Contains("at most 600.00 drawing units", p.Message);
        Assert.Contains("0+00.00", p.Message);
        Assert.NotNull(p.Fix);
        Assert.Equal(r.Problems[0].SegmentId, Seg(0, 800).Id);
    }

    [Fact]
    public void E309_maximum_length_accounts_for_the_view_overlap()
    {
        var r = Compute(AlignmentS(), Seg(500, 1300), new ViewOptions(ModelUnitsPerPaperUnit: 50, ViewOverlap: 25));

        Assert.False(r.Succeeded);
        Assert.Equal(550, r.MaxFittingLength!.Value, 0.01);      // 600 minus 25 at each end
    }

    [Fact]
    public void E309_band_wider_than_the_viewport_reports_that_no_length_fits()
    {
        var r = Compute(AlignmentS(), Seg(0, 500, -200, 200), new ViewOptions(ModelUnitsPerPaperUnit: 50));

        Assert.False(r.Succeeded);
        Assert.Equal(0, r.MaxFittingLength);
        Assert.Contains("band alone is too wide", Assert.Single(r.Problems).Message);
    }

    [Fact]
    public void E309_fit_standard_with_no_fitting_scale_is_an_error_naming_the_scales()
    {
        // Band +/-60 is 4 paper units high at 30, so only the length fails.
        var opt = new ViewOptions(ScaleMode: ScaleMode.FitStandard, StandardScales: [20, 30]);
        var r = Compute(AlignmentS(), Seg(0, 800, -60, 60), opt);

        Assert.False(r.Succeeded);
        Problem p = Assert.Single(r.Problems);
        Assert.Equal("SV-E309", p.Code);
        Assert.Contains("20.00, 30.00", p.Message);
        Assert.Equal(30, r.ModelUnitsPerPaperUnit);
        Assert.Equal(360, r.MaxFittingLength!.Value, 0.01);      // 12 x 30
    }

    [Fact]
    public void E310_tight_curve_fit_uses_the_oriented_box_not_the_chord()
    {
        // Semicircle R = 100 clockwise over the top, from (-100,0) to (100,0); band +/-10.
        // Chord 200 x band 20 would need 4 x 0.4 paper at 50 and fit a 12 x 1 viewport;
        // the oriented box is 220 x 110 (the arc bulges 110 above the chord) and does not.
        var arc = new AlignmentBuilder(new Pt(-100, 0), Math.PI / 2).ArcByLength(100, Math.PI * 100, clockwise: true).Build("C");
        var seg = Seg(0, Math.PI * 100, -10, 10);

        var tooFlat = Compute(arc, seg, new ViewOptions(ModelUnitsPerPaperUnit: 50), w: 12, h: 1);
        Assert.False(tooFlat.Succeeded);
        Assert.Equal("SV-E309", Assert.Single(tooFlat.Problems).Code);
        Assert.Equal(0, tooFlat.TwistRadians, Tol);
        Assert.Equal(220, tooFlat.ModelWidth, 1e-6);
        Assert.Equal(110, tooFlat.ModelHeight, 1e-6);
        Assert.Equal(2.2, tooFlat.RequiredPaperHeight, 1e-6);
        Assert.InRange(tooFlat.MaxFittingLength!.Value, 1, seg.EndStation - seg.StartStation - 1);

        var fits = Compute(arc, seg, new ViewOptions(ModelUnitsPerPaperUnit: 50), w: 12, h: 3);
        Assert.True(fits.Succeeded);
        Assert.Equal(0, fits.Spec!.ModelCenter.X, 1e-6);
        Assert.Equal(55, fits.Spec.ModelCenter.Y, 1e-6);
    }

    [Fact]
    public void E607_clip_larger_than_the_viewport_is_treated_as_E309()
    {
        var fixedScale = Compute(AlignmentS(), Seg(0, 800), new ViewOptions(ModelUnitsPerPaperUnit: 50, Clip: ClipMode.Boundary));
        Assert.False(fixedScale.Succeeded);
        Assert.Equal("SV-E309", Assert.Single(fixedScale.Problems).Code);

        // With FitStandard the chosen scale puts every clip vertex inside the viewport.
        var opt = new ViewOptions(ScaleMode: ScaleMode.FitStandard, StandardScales: [20, 50, 100], Clip: ClipMode.Boundary, ViewOverlap: 40);
        var fit = Compute(AlignmentS(), Seg(500, 1300), opt);
        Assert.True(fit.Succeeded);
        foreach (PolyVertex v in fit.Spec!.ClipInPaper!)
        {
            Assert.InRange(v.Point.X, -6 - Tol, 6 + Tol);
            Assert.InRange(v.Point.Y, -2.5 - Tol, 2.5 + Tol);
        }
    }

    [Fact]
    public void E610_north_mode_has_twist_0_and_fits_the_axis_aligned_box()
    {
        // 45-degree straight run of length 500 (diagonal), band +/-100.
        var diag = FakeAlignments.Straight(new Pt(0, 0), Math.PI / 4, 1000, name: "D");
        var opt = new ViewOptions(Orientation: ViewOrientation.North, ModelUnitsPerPaperUnit: 50);
        var r = Compute(diag, Seg(0, 500), opt, w: 20, h: 20);

        Assert.True(r.Succeeded);
        Assert.Equal(0, r.Spec!.TwistRadians);
        double run = 500 / Math.Sqrt(2);                      // centerline extent along each axis
        double corner = 100 / Math.Sqrt(2);                   // band corner offset along each axis
        Assert.Equal(run + 2 * corner, r.ModelWidth, 1e-6);
        Assert.Equal(r.ModelWidth, r.ModelHeight, 1e-6);
        Assert.Equal(run / 2, r.Spec.ModelCenter.X, 1e-6);
        Assert.Equal(run / 2, r.Spec.ModelCenter.Y, 1e-6);

        // Along the alignment the same segment is 500 x 200: the box depends on the mode.
        var along = Compute(diag, Seg(0, 500), new ViewOptions(ModelUnitsPerPaperUnit: 50), w: 20, h: 20);
        Assert.Equal(-Math.PI / 4, along.TwistRadians, Tol);
        Assert.Equal(500, along.ModelWidth, 1e-6);
        Assert.Equal(200, along.ModelHeight, 1e-6);
    }

    [Fact]
    public void E606_scale_comes_only_from_model_units_per_paper_unit()
    {
        // Metric 1:500 with metres and millimetres is 0.5 model units per paper unit.
        var metric = FakeAlignments.Straight(new Pt(0, 0), 0, 1000, name: "M");
        var r = Compute(metric, Seg(0, 100, -10, 10), new ViewOptions(ModelUnitsPerPaperUnit: 0.5), w: 250, h: 50);
        Assert.True(r.Succeeded);
        Assert.Equal(0.5, r.Spec!.ModelUnitsPerPaperUnit);
        Assert.Equal(200, r.RequiredPaperWidth, Tol);
        Assert.Equal(40, r.RequiredPaperHeight, Tol);

        // The same geometry at 50 is 2 x 0.4: nothing but the number changes the scale.
        var imperial = Compute(metric, Seg(0, 100, -10, 10), new ViewOptions(ModelUnitsPerPaperUnit: 50), w: 250, h: 50);
        Assert.Equal(2, imperial.RequiredPaperWidth, Tol);
        Assert.Equal(0.4, imperial.RequiredPaperHeight, Tol);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void E606_non_positive_or_non_finite_scale_is_an_error(double scale)
    {
        var r = Compute(AlignmentS(), Seg(0, 500), new ViewOptions(ModelUnitsPerPaperUnit: scale));
        Assert.False(r.Succeeded);
        Assert.Equal("SV-E606", Assert.Single(r.Problems).Code);
    }

    [Fact]
    public void Fit_standard_with_an_empty_list_is_an_error()
    {
        var r = Compute(AlignmentS(), Seg(0, 500), new ViewOptions(ScaleMode: ScaleMode.FitStandard, StandardScales: []));
        Assert.Equal("SV-E606", Assert.Single(r.Problems).Code);
    }

    [Fact]
    public void Fit_standard_picks_the_most_detailed_scale_that_fits()
    {
        var opt = new ViewOptions(ScaleMode: ScaleMode.FitStandard, StandardScales: [200, 100, 40, 50]);
        var r = Compute(AlignmentS(), Seg(0, 500), opt);
        Assert.Equal(50, r.Spec!.ModelUnitsPerPaperUnit);    // 40 needs 12.5 wide; 50 fits; 100 and 200 also fit
    }

    [Fact]
    public void Per_segment_scale_override_is_used_as_a_fixed_scale()
    {
        var opt = new ViewOptions(ScaleMode: ScaleMode.FitStandard, StandardScales: [20, 50, 100]);
        var ok = Compute(AlignmentS(), Seg(0, 500), opt, ov: new SegmentOverrides(ModelUnitsPerPaperUnit: 60));
        Assert.Equal(60, ok.Spec!.ModelUnitsPerPaperUnit);

        // Band +/-40 is 4 paper units high at 20, so only the length fails: at most 12 x 20 = 240.
        var tooSmall = Compute(AlignmentS(), Seg(0, 500, -40, 40), opt, ov: new SegmentOverrides(ModelUnitsPerPaperUnit: 20));
        Assert.False(tooSmall.Succeeded);
        Assert.Equal(20, tooSmall.ModelUnitsPerPaperUnit);
        Assert.Equal(240, tooSmall.MaxFittingLength!.Value, 0.01);
    }

    [Fact]
    public void Overlap_extends_the_display_polygon_and_is_clamped_at_the_alignment_ends()
    {
        var inner = Compute(AlignmentS(), Seg(500, 1000), new ViewOptions(ModelUnitsPerPaperUnit: 50, ViewOverlap: 25));
        Assert.Equal(475, inner.DisplayStartStation);
        Assert.Equal(1025, inner.DisplayEndStation);
        Assert.False(inner.OverlapClamped);
        Assert.Equal(550, inner.ModelWidth, Tol);
        Assert.Equal(750, inner.Spec!.ModelCenter.X, Tol);

        var first = Compute(AlignmentS(), Seg(0, 500), new ViewOptions(ModelUnitsPerPaperUnit: 50, ViewOverlap: 25));
        Assert.Equal(0, first.DisplayStartStation);
        Assert.Equal(525, first.DisplayEndStation);
        Assert.True(first.OverlapClamped);
        Assert.Equal(262.5, first.Spec!.ModelCenter.X, Tol);
    }

    [Fact]
    public void Clip_follows_the_overlap_setting_and_none_gives_no_clip()
    {
        // D1: by default the Boundary clip is the segment boundary only; the overlap is opt-in.
        Assert.False(new ViewMathConfig().ClipIncludesOverlap);
        var opt = new ViewOptions(ModelUnitsPerPaperUnit: 50, ViewOverlap: 25, Clip: ClipMode.Boundary);
        var boundaryOnly = Compute(AlignmentS(), Seg(500, 1000), opt);
        Assert.Equal(10, Width(boundaryOnly.Spec!.ClipInPaper!), Tol);

        var withOverlap = Compute(AlignmentS(), Seg(500, 1000), opt, calc: new ViewCalculator(new ViewMathConfig { ClipIncludesOverlap = true }));
        Assert.Equal(11, Width(withOverlap.Spec!.ClipInPaper!), Tol);
        Assert.Equal(withOverlap.Spec.ModelCenter, boundaryOnly.Spec.ModelCenter);   // the view still frames the overlap

        var none = Compute(AlignmentS(), Seg(500, 1000), opt with { Clip = ClipMode.None });
        Assert.Null(none.Spec!.ClipInPaper);

        static double Width(IReadOnlyList<PolyVertex> clip) => clip.Max(v => v.Point.X) - clip.Min(v => v.Point.X);
    }

    [Fact]
    public void Clip_preserves_arcs_as_bulges_and_maps_vertices_relative_to_the_view_center()
    {
        var arc = new AlignmentBuilder(new Pt(-100, 0), Math.PI / 2).ArcByLength(100, Math.PI * 100, clockwise: true).Build("C");
        var seg = Seg(0, Math.PI * 100, -10, 10);
        var src = new FakeBandSource(arc, -10, 10);
        var r = Calc.Compute(arc, src, new ViewRequest(seg, new ViewOptions(ModelUnitsPerPaperUnit: 50), 12, 3));

        IReadOnlyList<PolyVertex> model = src.Build(0, Math.PI * 100);
        IReadOnlyList<PolyVertex> clip = r.Spec!.ClipInPaper!;
        Assert.Equal(model.Count, clip.Count);
        for (int i = 0; i < model.Count; i++)
        {
            Assert.Equal(model[i].Bulge, clip[i].Bulge);
            Pt expected = ViewMath.ToPaper(model[i].Point, r.Spec.ModelCenter, r.Spec.TwistRadians, 50);
            Assert.Equal(expected.X, clip[i].Point.X, Tol);
            Assert.Equal(expected.Y, clip[i].Point.Y, Tol);
        }
        Assert.Contains(clip, v => v.Bulge != 0);
        // Outer arc start (-110, 0) relative to center (0, 55) at 1:50 is (-2.2, -1.1).
        Assert.Equal(-2.2, clip[0].Point.X, 1e-9);
        Assert.Equal(-1.1, clip[0].Point.Y, 1e-9);
    }

    [Fact]
    public void Twist_uses_the_tangent_at_mid_station_when_start_and_end_coincide()
    {
        // Full counter-clockwise circle R = 100 centered at the origin, starting at (100, 0) heading north.
        var circle = new AlignmentBuilder(new Pt(100, 0), Math.PI / 2).ArcByLength(100, Math.Tau * 100, clockwise: false).Build("O");
        Assert.Equal(0, BulgePolyline.Distance(circle.PointAt(0, 0), circle.PointAt(Math.Tau * 100, 0)), 1e-9);

        // Mid-station is at (-100, 0) heading south (bearing -pi/2), so the twist is +pi/2.
        double twist = ViewMath.Twist(circle, 0, Math.Tau * 100, ViewOrientation.AlongAlignment, Config);
        Assert.Equal(Math.PI / 2, twist, 1e-6);
    }

    [Fact]
    public void Twist_is_minus_the_chord_bearing_normalized_to_minus_pi_exclusive_pi_inclusive()
    {
        // West-running: the twist is a half turn, reported inside (-pi, pi].
        var west = FakeAlignments.Straight(new Pt(0, 0), Math.PI, 1000, name: "W");
        double westTwist = ViewMath.Twist(west, 0, 500, ViewOrientation.AlongAlignment, Config);
        Assert.Equal(Math.PI, Math.Abs(westTwist), Tol);
        Assert.InRange(westTwist, -Math.PI + 1e-15, Math.PI);
        Assert.Equal(Math.PI, ViewMath.NormalizeAngle(-Math.PI));

        var southEast = FakeAlignments.Straight(new Pt(0, 0), -Math.PI / 4, 1000, name: "SE");
        Assert.Equal(Math.PI / 4, ViewMath.Twist(southEast, 0, 500, ViewOrientation.AlongAlignment, Config), Tol);
        Assert.Equal(0, ViewMath.Twist(southEast, 0, 500, ViewOrientation.North, Config));
    }

    [Fact]
    public void Template_viewport_and_overlap_are_validated()
    {
        var badTemplate = Compute(AlignmentS(), Seg(0, 500), new ViewOptions(), w: 0, h: 5);
        Assert.Equal("SV-E601", Assert.Single(badTemplate.Problems).Code);

        var badOverlap = Compute(AlignmentS(), Seg(0, 500), new ViewOptions(ViewOverlap: -1));
        Assert.Equal("SV-E304", Assert.Single(badOverlap.Problems).Code);
    }

    [Fact]
    public void Degenerate_display_polygon_is_an_error_not_a_view()
    {
        var seg = Seg(0, 500);
        var flat = new FixedPolygon([new PolyVertex(new Pt(0, 0), 0), new PolyVertex(new Pt(500, 0), 0)]);
        var r = Calc.Compute(AlignmentS(), flat, new ViewRequest(seg, new ViewOptions(), 12, 5));
        Assert.False(r.Succeeded);
        Assert.Equal("SV-E202", Assert.Single(r.Problems).Code);
    }

    [Fact]
    public void State_plane_coordinates_give_the_same_view_as_at_the_origin()
    {
        var far = FakeAlignments.Straight(new Pt(3_100_000.25, 13_800_000.75), 0.3, 2000, name: "SP");
        var near = FakeAlignments.Straight(new Pt(0, 0), 0.3, 2000, name: "SP0");
        var opt = new ViewOptions(ModelUnitsPerPaperUnit: 50);
        var a = Compute(far, Seg(100, 600), opt);
        var b = Compute(near, Seg(100, 600), opt);

        Assert.Equal(b.TwistRadians, a.TwistRadians, 1e-9);
        Assert.Equal(b.ModelWidth, a.ModelWidth, 1e-6);
        Assert.Equal(b.ModelHeight, a.ModelHeight, 1e-6);
        Assert.Equal(b.Spec!.ModelCenter.X + 3_100_000.25, a.Spec!.ModelCenter.X, 1e-6);
        Assert.Equal(b.Spec.ModelCenter.Y + 13_800_000.75, a.Spec.ModelCenter.Y, 1e-6);
    }

    private sealed class FixedPolygon(IReadOnlyList<PolyVertex> polygon) : IDisplayPolygonSource
    {
        public IReadOnlyList<PolyVertex> Build(double fromStation, double toStation) => polygon;
    }
}

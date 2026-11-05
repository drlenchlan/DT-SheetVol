using System.Globalization;
using SheetVol.Core.Geometry;
using SheetVol.Core.Planning;
using SheetVol.Core.Tests.Fakes;

namespace SheetVol.Core.Tests.Geometry;

public class BoundaryBuilderTests
{
    private static readonly PlanningOptions Feet = PlanningOptions.ForUnitScale(1);

    private static PlanResult Plan(IAlignmentGeometry a, string spec, double oL, double oR, PlanningOptions? options = null) =>
        new Planner(options ?? Feet).Plan(TestRuns.Stations(a, spec, oL, oR), a);

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static double RadiusFromBulge(Pt p, Pt q, double bulge)
    {
        double c = Analytic.Dist(p, q);
        return c * (1 + bulge * bulge) / (4 * Math.Abs(bulge));
    }

    private static void AssertWellFormed(Boundary b)
    {
        Assert.True(Polygon.IsClosed(b.Tessellated));
        Assert.True(Polygon.DistinctVertexCount(b.Tessellated) >= 4);
        Assert.True(Polygon.SignedArea(b.Tessellated) > 0);
        Assert.False(Polygon.TryFindSelfIntersection(b.Tessellated, out _));
        for (int i = 0; i + 1 < b.Tessellated.Count; i++)
            Assert.NotEqual(b.Tessellated[i], b.Tessellated[i + 1]);   // no zero-length edge
    }

    [Fact]
    public void Straight_segment_is_a_counter_clockwise_rectangle()
    {
        var plan = Plan(FakeAlignments.S(), "0, 500", -100, 100);
        var b = Assert.Single(plan.Boundaries);
        Assert.Equal(new Pt[] { new(0, -100), new(500, -100), new(500, 100), new(0, 100), new(0, -100) }, b.Tessellated);
        Assert.Equal(100_000, Polygon.SignedArea(b.Tessellated), 9);
        Assert.Equal(4, b.Drawn.Count);
        Assert.All(b.Drawn, v => Assert.Equal(0, v.Bulge));
        AssertWellFormed(b);
    }

    [Fact]
    public void T7_fixture_D_on_C_offsets_minus100_plus100()
    {
        var plan = Plan(FakeAlignments.C(), "3+00, 6+14.159", -100, 100);
        var b = Assert.Single(plan.Boundaries);
        double area = Polygon.Area(b.Tessellated);
        Assert.InRange(Math.Abs(area / (2327.106 * 27) - 1), 0, 2e-4);
        Assert.InRange(Math.Abs(area / 62_831.86 - 1), 0, 2e-4);
        AssertWellFormed(b);
    }

    [Fact]
    public void T8_fixture_D_on_C_offsets_minus50_plus150()
    {
        var plan = Plan(FakeAlignments.C(), "3+00, 6+14.159", -50, 150);
        var b = Assert.Single(plan.Boundaries);
        double area = Polygon.Area(b.Tessellated);
        Assert.InRange(Math.Abs(area / (1939.255 * 27) - 1), 0, 2e-4);
        Assert.InRange(Math.Abs(area / 52_359.9 - 1), 0, 2e-4);
    }

    [Fact]
    public void T9_E201_offset_reaching_the_curve_center_is_an_error_with_no_boundary()
    {
        var plan = Plan(FakeAlignments.C(), "3+00, 6+14.159", -50, 300);
        Assert.False(plan.IsValid);
        Assert.Empty(plan.Boundaries);
        var p = Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E201);
        Assert.Equal(Severity.Error, p.Severity);
        Assert.Equal(plan.Segments[0].Id, p.SegmentId);
        Assert.Contains("Segment 1", p.Message);
        Assert.Contains("curve at 3+00.00", p.Message);
        Assert.Contains("radius 300.00", p.Message);
        Assert.Contains("less than 300.00", p.Message);
        Assert.Contains("300.00", p.Fix);
        var v = new Planner(Feet).Validate(TestRuns.Stations(FakeAlignments.C(), "3+00, 6+14.159", -50, 300), FakeAlignments.C());
        Assert.False(v.IsValid);
        Assert.Empty(v.Boundaries);
    }

    [Fact]
    public void E201_only_segments_that_contain_the_curve_fail()
    {
        var c = FakeAlignments.C();
        var plan = Plan(c, $"START, 3+00, {R(FakeAlignments.C_PT)}, END", -50, 300);
        Assert.Equal(3, plan.Segments.Count);
        Assert.Equal(2, plan.Boundaries.Count);   // tangents build; the arc segment does not
        Assert.Equal(plan.Segments[1].Id, Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E201).SegmentId);
    }

    [Fact]
    public void E203_reverse_curve_switches_bulge_sign_and_inner_side()
    {
        var a = new AlignmentBuilder(new Pt(0, 0), 0).Line(200).Arc(400, 0.5, clockwise: true).Arc(300, 0.6, clockwise: false).Line(200).Build("REV");
        var plan = Plan(a, "START, END", -50, 80);
        var b = Assert.Single(plan.Boundaries);
        AssertWellFormed(b);

        // Drawn: right line forward at 0, PC 200, PRC 400, PT 580, 780; then left line back.
        var d = b.Drawn;
        Assert.Equal(10, d.Count);
        Assert.True(d[1].Bulge < 0);                                            // right, clockwise arc forward
        Assert.True(d[2].Bulge > 0);                                            // right, counter-clockwise arc forward
        Assert.Equal(400 - 80, RadiusFromBulge(d[1].Point, d[2].Point, d[1].Bulge), 6);
        Assert.Equal(300 + 80, RadiusFromBulge(d[2].Point, d[3].Point, d[2].Bulge), 6);
        Assert.True(d[6].Bulge < 0);                                            // left, counter-clockwise arc backward
        Assert.True(d[7].Bulge > 0);                                            // left, clockwise arc backward
        Assert.Equal(300 - 50, RadiusFromBulge(d[6].Point, d[7].Point, d[6].Bulge), 6);
        Assert.Equal(400 + 50, RadiusFromBulge(d[7].Point, d[8].Point, d[7].Bulge), 6);

        double exact = Analytic.BandArea(a, a.StartStation, a.EndStation, -50, 80);
        Assert.Equal(exact, Polygon.Area(b.Tessellated), Feet.Geometry.MidOrdinate * Analytic.Perimeter(b.Tessellated));

        // Right offset 420 passes the center of the clockwise curve only; left -320 that of the counter-clockwise curve only.
        var right = Plan(a, "START, 4+00, END", -50, 420);
        Assert.Equal(right.Segments[0].Id, Assert.Single(right.Problems, p => p.Code == ProblemCodes.E201).SegmentId);
        Assert.Contains("curve at 2+00.00", right.Problems.Single(p => p.Code == ProblemCodes.E201).Message);
        Assert.Equal(right.Segments[1].Id, Assert.Single(right.Boundaries).SegmentId);
        var left = Plan(a, "START, 4+00, END", -320, 50);
        Assert.Equal(left.Segments[1].Id, Assert.Single(left.Problems, p => p.Code == ProblemCodes.E201).SegmentId);
        Assert.Contains("curve at 4+00.00", left.Problems.Single(p => p.Code == ProblemCodes.E201).Message);
        Assert.Equal(left.Segments[0].Id, Assert.Single(left.Boundaries).SegmentId);
    }

    [Fact]
    public void E203_compound_curve_keeps_the_inner_side_and_changes_radius()
    {
        var a = new AlignmentBuilder(new Pt(0, 0), 0).Line(100).Arc(300, 0.4, clockwise: true).Arc(150, 0.5, clockwise: true).Line(100).Build("CMP");
        var plan = Plan(a, "START, END", -60, 60);
        var b = Assert.Single(plan.Boundaries);
        var d = b.Drawn;
        Assert.True(d[1].Bulge < 0 && d[2].Bulge < 0);
        Assert.Equal(300 - 60, RadiusFromBulge(d[1].Point, d[2].Point, d[1].Bulge), 6);
        Assert.Equal(150 - 60, RadiusFromBulge(d[2].Point, d[3].Point, d[2].Bulge), 6);
        double exact = Analytic.BandArea(a, a.StartStation, a.EndStation, -60, 60);
        Assert.Equal(exact, Polygon.Area(b.Tessellated), Feet.Geometry.MidOrdinate * Analytic.Perimeter(b.Tessellated));
    }

    [Fact]
    public void E204_spiral_vertices_come_from_PointAt_and_meet_the_mid_ordinate()
    {
        var sp = FakeAlignments.SP();
        double oL = -100, oR = 100, mid = Feet.Geometry.MidOrdinate;
        var plan = Plan(sp, "START, END", oL, oR);
        var b = Assert.Single(plan.Boundaries);
        AssertWellFormed(b);
        var st = plan.Stations;
        int n = st.Count;
        Assert.Equal(2 * n + 1, b.Tessellated.Count);

        bool InSpiral(double s) => (s >= 200 && s <= 350) || (s >= 550 && s <= 700);
        int spiralEdges = 0;
        for (int i = 0; i + 1 < n; i++)
        {
            Pt r0 = b.Tessellated[i], r1 = b.Tessellated[i + 1];
            Pt l0 = b.Tessellated[2 * n - 1 - i], l1 = b.Tessellated[2 * n - 2 - i];
            // Every vertex, analytic (line, arc) or not (spiral), sits on the alignment's own offset point.
            Assert.True(Analytic.Dist(r0, sp.PointAt(st[i], oR)) < 1e-9);
            Assert.True(Analytic.Dist(l0, sp.PointAt(st[i], oL)) < 1e-9);
            if (!(InSpiral(st[i]) && InSpiral(st[i + 1]))) continue;
            spiralEdges++;
            double sm = 0.5 * (st[i] + st[i + 1]);
            Assert.True(ChordGap(r0, r1, sp.PointAt(sm, oR)) <= mid, $"right chord at {st[i]}");
            Assert.True(ChordGap(l0, l1, sp.PointAt(sm, oL)) <= mid, $"left chord at {st[i]}");
        }
        Assert.True(spiralEdges > 10);
        double exact = Analytic.BandArea(sp, 0, 900, oL, oR);
        Assert.Equal(exact, Polygon.Area(b.Tessellated), mid * Analytic.Perimeter(b.Tessellated));
    }

    [Theory]
    [InlineData(-50, 150)]    // larger offset radius on the left: 350 against 150
    [InlineData(20, 120)]     // band on the right only: larger radius 280 is on the offset nearer the alignment
    [InlineData(-200, -20)]   // band on the left only: larger radius 500
    public void Arc_subdivision_meets_the_mid_ordinate_on_the_larger_offset_radius(double oL, double oR)
    {
        var c = FakeAlignments.C();
        double mid = Feet.Geometry.MidOrdinate;
        var plan = Plan(c, $"START, {R(FakeAlignments.C_PT)}", oL, oR);
        var b = Assert.Single(plan.Boundaries);
        var st = plan.Stations;
        int n = st.Count;
        double worstOuter = 0;
        double rLeft = 300 - oL, rRight = 300 - oR;                          // clockwise arc: offset radius R - o
        bool leftIsOuter = rLeft > rRight;
        for (int i = 0; i + 1 < n; i++)
        {
            if (st[i] < 300 || st[i + 1] > FakeAlignments.C_PT) continue;
            double sm = 0.5 * (st[i] + st[i + 1]);
            double gr = ChordGap(b.Tessellated[i], b.Tessellated[i + 1], c.PointAt(sm, oR));
            double gl = ChordGap(b.Tessellated[2 * n - 1 - i], b.Tessellated[2 * n - 2 - i], c.PointAt(sm, oL));
            Assert.True(gr <= mid && gl <= mid, $"chord gap {gr} / {gl} at {st[i]}");
            worstOuter = Math.Max(worstOuter, leftIsOuter ? gl : gr);
        }
        Assert.True(worstOuter > mid / 4, $"outer chord gap {worstOuter}: subdivision is finer than the tolerance asks");
    }

    private static double ChordGap(Pt a, Pt b, Pt p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Abs(dx * (p.Y - a.Y) - dy * (p.X - a.X)) / Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Alignment whose PointAt disagrees with its own arc data by a fixed shift, as a defective adapter would.</summary>
    private sealed class ShiftedArcAdapter(FakeAlignment inner, double shift) : IAlignmentGeometry
    {
        public string Name => inner.Name;
        public double StartStation => inner.StartStation;
        public double EndStation => inner.EndStation;
        public IReadOnlyList<AlignSeg> Segments => inner.Segments;
        public Pt PointAt(double s, double o)
        {
            var p = inner.PointAt(s, o);
            bool onArc = inner.Segments.Any(g => g.Kind == SegKind.Arc && s > g.StartStation && s < g.EndStation);
            return onArc ? new Pt(p.X, p.Y + shift) : p;
        }
        public string FormatStation(double s) => inner.FormatStation(s);
        public bool TryParseStation(string text, out double raw, out string? error) => inner.TryParseStation(text, out raw, out error);
    }

    [Fact]
    public void E204_analytic_arc_cross_check_catches_an_adapter_that_disagrees()
    {
        var c = FakeAlignments.C();
        var bad = new ShiftedArcAdapter(c, 0.05);
        var plan = new Planner(Feet).Plan(TestRuns.Stations(bad, $"START, 3+00, {R(FakeAlignments.C_PT)}, END"), bad);
        var p = Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E204);
        Assert.Equal(plan.Segments[1].Id, p.SegmentId);
        Assert.Equal(2, plan.Boundaries.Count);
        var good = Plan(c, $"START, 3+00, {R(FakeAlignments.C_PT)}, END", -100, 100);
        Assert.DoesNotContain(good.Problems, x => x.Code == ProblemCodes.E204);
    }

    [Fact]
    public void E205_outside_mitre_lies_on_the_bisector()
    {
        var k = FakeAlignments.K();
        var plan = Plan(k, "START, END", -100, 100);
        var b = Assert.Single(plan.Boundaries);
        AssertWellFormed(b);
        // Ring: R(0), R mitre, R(2000), L(2000), L mitre, L(0), R(0). Left turn: right is outside.
        Pt rm = b.Tessellated[1], lm = b.Tessellated[4];
        double t = Math.Tan(Math.PI / 12);
        Assert.Equal(1000 + 100 * t, rm.X, 9);
        Assert.Equal(-100, rm.Y, 9);
        Assert.Equal(1000 - 100 * t, lm.X, 9);
        Assert.Equal(100, lm.Y, 9);
        Assert.Equal(100 / Math.Cos(Math.PI / 12), Analytic.Dist(rm, new Pt(1000, 0)), 9);
        Assert.Equal(Analytic.BandArea(k, 0, 2000, -100, 100), Polygon.Area(b.Tessellated), 6);
    }

    [Fact]
    public void E205_break_on_the_angle_point_cuts_along_the_bisector()
    {
        var k = FakeAlignments.K();
        var plan = Plan(k, "START, 10+00, END", -100, 100);
        Assert.Equal(2, plan.Boundaries.Count);
        var b1 = plan.Boundaries[0].Tessellated;
        var b2 = plan.Boundaries[1].Tessellated;
        // Segment 1 ends with the cut R mitre -> L mitre; segment 2 starts with the same two points.
        Pt rm = b1[1], lm = b1[2];
        Assert.Equal(rm, b2[0]);
        Assert.Equal(lm, b2[3]);
        var ap = new Pt(1000, 0);
        double cross = (lm.X - rm.X) * (ap.Y - rm.Y) - (lm.Y - rm.Y) * (ap.X - rm.X);
        Assert.Equal(0, cross, 6);                                             // the cut passes through the angle point
        Assert.Equal(Math.PI / 2 + Math.PI / 12, Math.Atan2(lm.Y - rm.Y, lm.X - rm.X), 9);   // bisector of 0 and 30 degrees, turned to the left
        Assert.Equal(Analytic.BandArea(k, 0, 1000, -100, 100), Polygon.Area(b1), 6);
        Assert.Equal(Analytic.BandArea(k, 1000, 2000, -100, 100), Polygon.Area(b2), 6);
    }

    [Theory]
    [InlineData(149, true)]
    [InlineData(151, false)]
    public void E205_deflection_above_the_mitre_limit_is_an_error(double deflectionDeg, bool valid)
    {
        var a = new AlignmentBuilder(new Pt(0, 0), 0).Line(500).Angle(deflectionDeg * Math.PI / 180).Line(500).Build("SHARP");
        var plan = Plan(a, "START, END", -10, 10);
        Assert.Equal(valid, plan.IsValid);
        if (!valid)
        {
            var p = Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E205);
            Assert.Contains("above the mitre limit of 150 degrees", p.Message);
            Assert.Empty(plan.Boundaries);
        }
    }

    [Theory]
    [InlineData("START, 9+90, END", 1)]    // break 10 before the angle point; inside mitre needs 26.8
    [InlineData("START, 10+10, END", 0)]   // break 10 after it
    public void E205_break_inside_the_mitre_retreat_is_an_error(string spec, int failing)
    {
        var plan = Plan(FakeAlignments.K(), spec, -100, 100);
        var p = Assert.Single(plan.Problems, x => x.Severity == Severity.Error);
        Assert.Equal(ProblemCodes.E205, p.Code);
        Assert.Equal(plan.Segments[failing].Id, p.SegmentId);
        Assert.Contains("folds back", p.Message);
        Assert.Single(plan.Boundaries);
        Assert.Empty(plan.Overlaps);
    }

    [Fact]
    public void E205_non_tangent_curve_join_is_an_error()
    {
        var a = new AlignmentBuilder(new Pt(0, 0), 0).Line(300).Angle(10 * Math.PI / 180).Arc(300, 0.5, clockwise: true).Line(300).Build("KINK");
        var plan = Plan(a, "START, END", -50, 50);
        var p = Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E205);
        Assert.Contains("without tangency", p.Message);
        Assert.Empty(plan.Boundaries);
    }

    [Fact]
    public void E202_band_that_crosses_itself_is_an_error()
    {
        var loop = OverlapTests.AlphaLoop();
        var plan = Plan(loop, "START, END", -50, 50);
        var p = Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E202);
        Assert.Contains("crosses itself", p.Message);
        Assert.Equal(plan.Segments[0].Id, p.SegmentId);
        Assert.Empty(plan.Boundaries);
        Assert.False(new Planner(Feet).Validate(TestRuns.Stations(loop, "START, END", -50, 50), loop).IsValid);
    }

    [Fact]
    public void E206_break_on_or_within_tolerance_of_the_PC_merges_without_a_zero_length_edge()
    {
        var c = FakeAlignments.C();
        var exact = Plan(c, "START, 3+00, END", -100, 100);
        var near = Plan(c, "START, 300.0000005, 299.9999999, END", -100, 100);
        Assert.Equal(2, near.Segments.Count);
        Assert.Equal(300, near.Segments[0].EndStation);                       // snapped to the PC
        Assert.Equal(exact.Stations, near.Stations);
        foreach (var b in near.Boundaries) AssertWellFormed(b);
        Assert.Equal(near.Stations.Count, near.Stations.Distinct().Count());
        for (int i = 0; i + 1 < near.Stations.Count; i++) Assert.True(near.Stations[i + 1] - near.Stations[i] > 1e-6);
    }

    [Theory]
    [InlineData(-500, "START, 0+00, END", 500, 1500)]
    [InlineData(1000, "START, 15+00, END", 500, 1500)]
    public void E207_non_zero_and_negative_start_stations_work_unchanged(double start, string spec, double len1, double len2)
    {
        var s = FakeAlignments.S(start);
        var plan = Plan(s, spec, -100, 100);
        Assert.True(plan.IsValid);
        Assert.Equal(start, plan.Segments[0].StartStation);
        Assert.Equal(len1 * 200, Polygon.Area(plan.Boundaries[0].Tessellated), 6);
        Assert.Equal(len2 * 200, Polygon.Area(plan.Boundaries[1].Tessellated), 6);
        Assert.Equal(new Pt(len1, -100), plan.Boundaries[0].Tessellated[1]);   // geometry follows raw stations, not displayed ones
    }

    [Fact]
    public void E216_metric_tolerances_scale_with_the_unit()
    {
        const double m = 0.3048;
        var feet = FakeAlignments.C();
        var metres = new AlignmentBuilder(new Pt(0, 0), 0).Line(300 * m).Arc(300 * m, Math.PI / 3, clockwise: true).Line(300 * m).Build("C-m", stationBase: 1000);
        var pf = Plan(feet, $"START, 3+00, {R(FakeAlignments.C_PT)}, END", -100, 100);
        var pm = new Planner(PlanningOptions.ForUnitScale(m)).Plan(
            TestRuns.Stations(metres, $"START, {R(300 * m)}, {R(FakeAlignments.C_PT * m)}, END", -100 * m, 100 * m), metres);
        Assert.Equal(0.01 * m, PlanningOptions.ForUnitScale(m).Geometry.MidOrdinate, 15);
        Assert.Equal(pf.Stations.Count, pm.Stations.Count);                   // same subdivision: tolerance scaled with geometry
        for (int i = 0; i < pf.Boundaries.Count; i++)
        {
            double af = Polygon.Area(pf.Boundaries[i].Tessellated), am = Polygon.Area(pm.Boundaries[i].Tessellated);
            Assert.InRange(Math.Abs(am / (af * m * m) - 1), 0, 1e-9);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanningOptions.ForUnitScale(0));
    }

    [Fact]
    public void E218_small_radius_curb_return_follows_the_same_rules()
    {
        var a = new AlignmentBuilder(new Pt(0, 0), 0).Line(50).Arc(25, Math.PI / 2, clockwise: false).Line(50).Build("CURB");
        var ok = Plan(a, "START, END", -10, 10);
        var b = Assert.Single(ok.Boundaries);
        AssertWellFormed(b);
        Assert.Equal(Analytic.BandArea(a, a.StartStation, a.EndStation, -10, 10), Polygon.Area(b.Tessellated), Feet.Geometry.MidOrdinate * Analytic.Perimeter(b.Tessellated));
        var bad = Plan(a, "START, END", -30, 10);   // left is inside a counter-clockwise curve
        var p = Assert.Single(bad.Problems, x => x.Code == ProblemCodes.E201);
        Assert.Contains("left offset 30.00", p.Message);
        Assert.Contains("radius 25.00", p.Message);
    }

    [Fact]
    public void E201_spiral_tightening_below_the_inside_offset_is_an_error()
    {
        var plan = Plan(FakeAlignments.SP(), "START, 2+00, 7+00, END", -50, 520);   // right is inside a right turn; R min 500
        var errors = plan.Problems.Where(x => x.Code == ProblemCodes.E201).ToList();
        Assert.Contains(errors, e => e.Message.Contains("spiral at 2+00.00") && e.Message.Contains("radius 500.00"));
        Assert.Contains(errors, e => e.Message.Contains("spiral at 5+50.00"));
        Assert.All(errors, e => Assert.Equal(plan.Segments[1].Id, e.SegmentId));
        Assert.Equal(2, plan.Boundaries.Count);                                // both tangents still build
    }

    [Fact]
    public void Drawn_and_Tessellated_share_their_vertices()
    {
        var plan = Plan(FakeAlignments.SP(), "START, 2+75, 4+50, 6+25, END", -40, 120);
        foreach (var b in plan.Boundaries)
        {
            var tess = b.Tessellated.ToHashSet();
            Assert.All(b.Drawn, v => Assert.Contains(v.Point, tess));
            Assert.True(b.Drawn.Count < b.Tessellated.Count);
        }
    }
}

using System.Globalization;
using SheetVol.Core.Geometry;
using SheetVol.Core.Planning;
using SheetVol.Core.Tests.Fakes;

namespace SheetVol.Core.Tests.Geometry;

/// <summary>
/// Boundaries when neighbouring segments have different offsets (per-segment overrides, rev 2).
/// The rule under test: at a break the shared cut line runs from the smaller left offset to the
/// larger right offset on the station's cut direction; each ring runs along it between its own
/// offset points, so neighbours touch along it, overlap nowhere and leave no gap.
/// </summary>
public class VaryingOffsetTests
{
    private static readonly PlanningOptions Feet = PlanningOptions.ForUnitScale(1);
    private static readonly string CurveEnd = FakeAlignments.C_PT.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Plans <paramref name="spec"/> once to get GUIDs, then again with the offsets in <paramref name="overrides"/> keyed by segment index.</summary>
    private static PlanResult PlanWith(FakeAlignment a, string spec, double oL, double oR, params (int Index, double Left, double Right)[] overrides)
    {
        var planner = new Planner(Feet);
        var known = planner.Plan(TestRuns.Stations(a, spec, oL, oR), a).Segments;
        var map = overrides.ToDictionary(o => known[o.Index].Id, o => new SegmentOverrides(OffsetLeft: o.Left, OffsetRight: o.Right));
        return planner.Plan(TestRuns.Stations(a, spec, oL, oR, known: known, overrides: map), a);
    }

    /// <summary>End cut of a ring (right end point, left end point) and start cut (left start point, right start point).</summary>
    private static (Pt Right, Pt Left) EndCut(IReadOnlyList<Pt> ring) => (ring[ring.Count / 2 - 1], ring[ring.Count / 2]);

    private static (Pt Right, Pt Left) StartCut(IReadOnlyList<Pt> ring) => (ring[0], ring[ring.Count - 2]);

    private static double Cross(Pt o, Pt a, Pt b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>Ray-casting point-in-polygon on a closed ring.</summary>
    private static bool Inside(IReadOnlyList<Pt> ring, Pt p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 2; i < ring.Count - 1; j = i++)
        {
            Pt a = ring[i], b = ring[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// The two neighbours' cut points at their common break all lie on one line, the outermost
    /// two are the wider pair (smallest left, largest right) at that station, the rings do not
    /// overlap, and each ring has the area of its own band.
    /// </summary>
    private static void AssertSharedCut(FakeAlignment a, PlanResult plan, int first, Pt widerLeft, Pt widerRight)
    {
        SegmentDef sa = plan.Segments[first], sb = plan.Segments[first + 1];
        var ra = plan.Boundaries.Single(b => b.SegmentId == sa.Id).Tessellated;
        var rb = plan.Boundaries.Single(b => b.SegmentId == sb.Id).Tessellated;
        var (aRight, aLeft) = EndCut(ra);
        var (bRight, bLeft) = StartCut(rb);
        Pt[] cut = [aRight, aLeft, bRight, bLeft];

        double span = Analytic.Dist(widerLeft, widerRight);
        foreach (var p in cut)
        {
            Assert.True(Math.Abs(Cross(widerLeft, widerRight, p)) / span < 1e-9, $"cut point {p} is off the shared cut line");
            double t = ((p.X - widerLeft.X) * (widerRight.X - widerLeft.X) + (p.Y - widerLeft.Y) * (widerRight.Y - widerLeft.Y)) / (span * span);
            Assert.InRange(t, -1e-12, 1 + 1e-12);
        }
        // The shared cut line is exactly the wider pair: its ends are cut points of one ring or the other.
        Assert.True(cut.Any(p => Analytic.Dist(p, widerLeft) < 1e-9) && cut.Any(p => Analytic.Dist(p, widerRight) < 1e-9));

        Assert.True(Polygon.IntersectionArea(ra, rb) < 1e-6, "neighbours overlap");
        double tol = Feet.Geometry.MidOrdinate;
        Assert.Equal(Analytic.BandArea(a, sa.StartStation, sa.EndStation, sa.OffsetLeft, sa.OffsetRight), Polygon.Area(ra), tol * Analytic.Perimeter(ra) + 1e-6);
        Assert.Equal(Analytic.BandArea(a, sb.StartStation, sb.EndStation, sb.OffsetLeft, sb.OffsetRight), Polygon.Area(rb), tol * Analytic.Perimeter(rb) + 1e-6);
    }

    [Fact]
    public void Straight_neighbours_of_different_widths_share_the_cut_line_with_no_overlap_and_no_gap()
    {
        var s = FakeAlignments.S();
        var plan = PlanWith(s, "START, 5+00, 10+00, END", -100, 100, (1, -50, 200));
        Assert.True(plan.IsValid);
        Assert.Empty(plan.Overlaps);
        Assert.Equal(new Pt[] { new(0, -100), new(500, -100), new(500, 100), new(0, 100), new(0, -100) }, plan.Boundaries[0].Tessellated);
        Assert.Equal(new Pt[] { new(500, -200), new(1000, -200), new(1000, 50), new(500, 50), new(500, -200) }, plan.Boundaries[1].Tessellated);

        // At 5+00: left offsets -100 and -50, right 100 and 200. Shared cut from y = 100 (left -100) to y = -200 (right 200).
        AssertSharedCut(s, plan, 0, new Pt(500, 100), new Pt(500, -200));
        AssertSharedCut(s, plan, 1, new Pt(1000, 100), new Pt(1000, -200));

        // No gap: across the common width every point just before the cut is in segment 1 and just after it in segment 2.
        var r1 = plan.Boundaries[0].Tessellated;
        var r2 = plan.Boundaries[1].Tessellated;
        for (double y = -99; y <= 49; y += 7)
        {
            Assert.True(Inside(r1, new Pt(500 - 1e-6, y)) && !Inside(r2, new Pt(500 - 1e-6, y)), $"before the cut at y = {y}");
            Assert.True(Inside(r2, new Pt(500 + 1e-6, y)) && !Inside(r1, new Pt(500 + 1e-6, y)), $"after the cut at y = {y}");
        }
        // The step lies on the cut line: past its own width neither ring reaches across the cut.
        Assert.False(Inside(r2, new Pt(500 + 1e-6, 75)));
        Assert.False(Inside(r1, new Pt(500 - 1e-6, -150)));
    }

    [Fact]
    public void Crossing_widths_share_the_cut_from_the_smaller_left_to_the_larger_right()
    {
        // Segment 1 wider on the right, segment 2 wider on the left.
        var s = FakeAlignments.S();
        var plan = PlanWith(s, "START, 10+00, END", -50, 300, (1, -200, 100));
        Assert.True(plan.IsValid);
        Assert.Empty(plan.Overlaps);
        AssertSharedCut(s, plan, 0, new Pt(1000, 200), new Pt(1000, -300));
    }

    [Fact]
    public void Curve_break_between_different_widths_cuts_on_the_radial_line()
    {
        // T9's run with segment 2 narrowed on the right: segment 1 (-50, 300) meets segment 2 (-50, 100) at the PC.
        var c = FakeAlignments.C();
        var plan = PlanWith(c, $"START, 3+00, 4+50, {CurveEnd}, END", -50, 300, (1, -50, 100), (2, -50, 100));
        Assert.True(plan.IsValid, string.Join(" | ", plan.Problems.Select(p => p.Message)));
        Assert.Empty(plan.Overlaps);
        AssertSharedCut(c, plan, 0, c.PointAt(300, -50), c.PointAt(300, 300));
        AssertSharedCut(c, plan, 2, c.PointAt(FakeAlignments.C_PT, -50), c.PointAt(FakeAlignments.C_PT, 300));
    }

    [Fact]
    public void Angle_point_break_between_different_widths_cuts_along_the_bisector()
    {
        // K turns 30 degrees left at 10+00: the shared cut is the bisector through the angle point.
        var k = FakeAlignments.K();
        var plan = PlanWith(k, "START, 10+00, END", -100, 100, (1, -40, 160));
        Assert.True(plan.IsValid, string.Join(" | ", plan.Problems.Select(p => p.Message)));
        Assert.Empty(plan.Overlaps);
        var bisector = new Pt(-Math.Sin(Math.PI / 12), Math.Cos(Math.PI / 12));   // left normal of the bisector direction, 15 degrees
        double reach = 1 / Math.Cos(Math.PI / 12);                                   // mitre distance per unit offset
        Pt Mitre(double o) => new(1000 - o * reach * bisector.X, -o * reach * bisector.Y);
        AssertSharedCut(k, plan, 0, Mitre(-100), Mitre(160));
    }

    [Fact]
    public void Band_pieces_split_where_offsets_change_and_sum_to_the_segments()
    {
        var c = FakeAlignments.C();
        string spec = $"START, 1+00, 3+00, 4+50, {CurveEnd}, 7+00, END";
        var plan = PlanWith(c, spec, -100, 100, (2, -60, 150), (3, -60, 150));
        Assert.True(plan.IsValid, string.Join(" | ", plan.Problems.Select(p => p.Message)));
        Assert.True(plan.PartitionCheckApplicable);
        Assert.Empty(plan.Overlaps);

        var ids = plan.Segments.Select(x => x.Id).ToArray();
        Assert.Equal(3, plan.Band.Count);
        Assert.Equal([ids[0], ids[1]], plan.Band[0].SegmentIds);
        Assert.Equal([ids[2], ids[3]], plan.Band[1].SegmentIds);               // equal overrides share one piece
        Assert.Equal([ids[4], ids[5]], plan.Band[2].SegmentIds);
        Assert.Equal(plan.Segments[0].StartStation, plan.Band[0].StartStation);
        Assert.Equal(plan.Band[0].EndStation, plan.Band[1].StartStation);
        Assert.Equal(plan.Band[1].EndStation, plan.Band[2].StartStation);

        double tol = Feet.Geometry.MidOrdinate;
        foreach (var piece in plan.Band)
        {
            Assert.Null(BoundaryValidator.Check(piece.Tessellated, requireCounterClockwise: true, minDistinctVertices: 4));
            var seg = plan.Segments.First(x => x.Id == piece.SegmentIds[0]);
            double exact = Analytic.BandArea(c, piece.StartStation, piece.EndStation, seg.OffsetLeft, seg.OffsetRight);
            Assert.Equal(exact, Polygon.SignedArea(piece.Tessellated), tol * Analytic.Perimeter(piece.Tessellated) + 1e-6);
        }

        double pieces = plan.Band.Sum(b => Polygon.SignedArea(b.Tessellated));
        double segments = plan.Boundaries.Sum(b => Polygon.SignedArea(b.Tessellated));
        Assert.InRange(Math.Abs(segments - pieces) / pieces, 0, 1e-9);
        for (int i = 0; i < plan.Band.Count; i++)
            for (int j = i + 1; j < plan.Band.Count; j++)
                Assert.True(Polygon.IntersectionArea(plan.Band[i].Tessellated, plan.Band[j].Tessellated) < 1e-6, "band pieces overlap");
    }

    [Fact]
    public void One_station_list_meets_the_mid_ordinate_for_both_widths_on_a_shared_arc()
    {
        // Both segments lie partly on C's arc (3+00 to the PT); segment 2 is wider on the outside (left).
        var c = FakeAlignments.C();
        var plan = PlanWith(c, "START, 4+00, END", -100, 100, (1, -200, 50));
        Assert.True(plan.IsValid);
        double mid = Feet.Geometry.MidOrdinate;
        var st = plan.Stations;
        int arcEdges = 0;
        foreach (var (seg, b) in plan.Segments.Zip(plan.Boundaries))
        {
            int ia = st.ToList().IndexOf(seg.StartStation), ib = st.ToList().IndexOf(seg.EndStation);
            int n = ib - ia + 1;
            for (int i = ia; i < ib; i++)
            {
                if (st[i] < 300 || st[i + 1] > FakeAlignments.C_PT) continue;
                arcEdges++;
                double sm = 0.5 * (st[i] + st[i + 1]);
                int r = i - ia, l = 2 * n - 1 - r;
                Assert.True(ChordGap(b.Tessellated[r], b.Tessellated[r + 1], c.PointAt(sm, seg.OffsetRight)) <= mid, $"right chord at {st[i]}");
                Assert.True(ChordGap(b.Tessellated[l], b.Tessellated[l - 1], c.PointAt(sm, seg.OffsetLeft)) <= mid, $"left chord at {st[i]}");
            }
        }
        Assert.True(arcEdges > 10);
    }

    private static double ChordGap(Pt a, Pt b, Pt p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Abs(dx * (p.Y - a.Y) - dy * (p.X - a.X)) / Math.Sqrt(dx * dx + dy * dy);
    }
}

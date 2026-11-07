using SheetVol.Core.Geometry;
using SheetVol.Core.Planning;
using SheetVol.Core.Tests.Fakes;

namespace SheetVol.Core.Tests.Planning;

/// <summary>Rev 3 (rev3-band): the partition band carries no straight-through vertex at an interior break.</summary>
public class BandRingTests
{
    private static readonly PlanningOptions Feet = PlanningOptions.ForUnitScale(1);
    private static readonly Planner Planner = new(Feet);

    [Fact]
    public void T25_two_segment_band_is_the_one_segment_polygon_over_the_same_range()
    {
        // T25 shape: S straight, breaks 0, 500, 1000, offsets (-100, +100). The builder's band ring has
        // (500, -100) and (500, 100) between the corners; Civil 3D measured it 708,942 fill against 828,000.
        var s = FakeAlignments.S();
        var two = Planner.Plan(TestRuns.Stations(s, "0, 500, 1000", -100, 100), s);
        var one = Planner.Plan(TestRuns.Stations(s, "0, 1000", -100, 100), s);

        Assert.True(two.IsValid);
        var band = Assert.Single(two.Band).Tessellated;
        Assert.Equal(Assert.Single(one.Band).Tessellated, band);
        Assert.Equal(one.Boundaries[0].Tessellated, band);
        Assert.Equal([new Pt(0, -100), new Pt(1000, -100), new Pt(1000, 100), new Pt(0, 100), new Pt(0, -100)], band);
        Assert.Equal(two.Boundaries.Sum(b => Polygon.Area(b.Tessellated)), Polygon.Area(band), 9);
    }

    [Fact]
    public void Segments_keep_their_break_vertices()
    {
        var s = FakeAlignments.S();
        var plan = Planner.Plan(TestRuns.Stations(s, "0, 500, 1000", -100, 100), s);
        Assert.Equal([new Pt(0, -100), new Pt(500, -100), new Pt(500, 100), new Pt(0, 100), new Pt(0, -100)], plan.Boundaries[0].Tessellated);
        Assert.Equal([new Pt(500, -100), new Pt(1000, -100), new Pt(1000, 100), new Pt(500, 100), new Pt(500, -100)], plan.Boundaries[1].Tessellated);
    }

    [Fact]
    public void Band_on_a_curve_keeps_every_arc_vertex_and_the_partition_area()
    {
        // Alignment C: tangent, arc, tangent. Breaks on both tangents and inside the arc: only the
        // breaks on the tangents are straight-through; every arc vertex turns and stays.
        var c = FakeAlignments.C();
        var plan = Planner.Plan(TestRuns.Stations(c, "START, 150, 300, 450, 760, END", -50, 50), c);
        Assert.True(plan.IsValid);
        Assert.Equal(5, plan.Boundaries.Count);
        var band = Assert.Single(plan.Band).Tessellated;
        Assert.Equal(plan.Boundaries.Sum(b => Polygon.Area(b.Tessellated)), Polygon.Area(band), 6);
        Assert.Null(BoundaryValidator.Check(band, requireCounterClockwise: true, minDistinctVertices: 4));
        Assert.DoesNotContain(band, p => Math.Abs(p.X - 150) < 1e-9);   // tangent break at 150 dropped on both sides
        Assert.Empty(StraightThrough(band).ToList());
        // Every segment vertex strictly inside the arc's 60 degree sweep (center (300, -300)) is in the band.
        var arcVertices = plan.Boundaries.SelectMany(b => b.Tessellated).Distinct()
            .Where(p => Math.Atan2(p.X - 300, p.Y + 300) is var t && t > 1e-9 && t < Math.PI / 3 - 1e-9).ToList();
        Assert.True(arcVertices.Count > 10);
        Assert.All(arcVertices, p => Assert.Contains(p, band));
    }

    [Fact]
    public void Drops_repeats_and_near_straight_vertices_within_tolerance_and_wraps_the_start()
    {
        Pt[] ring =
        [
            new(250, 0), new(500, 1e-9), new(500, 1e-9), new(1000, 0), new(1000, 200), new(0, 200), new(0, 0), new(250, 0),
        ];
        var clean = BandRing.DropStraightVertices(ring, 1e-6);
        Assert.Equal([new Pt(1000, 0), new Pt(1000, 200), new Pt(0, 200), new Pt(0, 0), new Pt(1000, 0)], clean);
    }

    [Fact]
    public void Keeps_a_vertex_off_the_line_by_more_than_the_tolerance_and_a_turn_back()
    {
        Pt[] notch = [new(0, 0), new(500, 1e-3), new(1000, 0), new(1000, 200), new(0, 200), new(0, 0)];
        Assert.Equal(notch, BandRing.DropStraightVertices(notch, 1e-6));

        Pt[] spike = [new(0, 0), new(1000, 0), new(1000, 200), new(1000, 300), new(1000, 200), new(0, 200), new(0, 0)];
        Assert.Contains(new Pt(1000, 300), BandRing.DropStraightVertices(spike, 1e-6));
    }

    [Fact]
    public void Open_or_collapsing_rings_are_a_caller_defect()
    {
        Assert.Throws<ArgumentException>(() => BandRing.DropStraightVertices([new(0, 0), new(1, 0), new(2, 0), new(3, 0)], 1e-6));
        Assert.Throws<ArgumentException>(() => BandRing.DropStraightVertices([new(0, 0), new(1, 0), new(2, 0), new(0, 0)], 1e-6));
        Assert.Throws<ArgumentException>(() => BandRing.DropStraightVertices([new(0, 0), new(1, 0), new(1, 1), new(0, 0)], double.NaN));
    }

    private static IEnumerable<Pt> StraightThrough(IReadOnlyList<Pt> ring)
    {
        int n = ring.Count - 1;
        for (int i = 0; i < n; i++)
        {
            Pt a = ring[(i - 1 + n) % n], b = ring[i], c = ring[(i + 1) % n];
            double dx = c.X - a.X, dy = c.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
            double off = Math.Abs((b.X - a.X) * dy - (b.Y - a.Y) * dx) / len;
            double along = ((b.X - a.X) * dx + (b.Y - a.Y) * dy) / len;
            if (off <= 1e-6 && along > 0 && along < len) yield return b;
        }
    }
}

// Station-of-point (closed form on lines and arcs, iterative on spirals) and the interval union.

using SheetVol.Core.Tests.Fakes;
using SheetVol.Core.Volumes.Integrator;

namespace SheetVol.Core.Tests.Integrator;

public class StationLocatorTests
{
    private static StationLocator Make(IAlignmentGeometry al, Pt origin = default)
    {
        Assert.True(StationLocator.TryCreate(al, origin, out var loc, out string? err), err);
        return loc!;
    }

    [Fact]
    public void Alignment_S_station_is_x_and_left_is_negative()
    {
        var loc = Make(FakeAlignments.S());
        var r = loc.Locate(new Pt(123.4, 56.7));
        Assert.Equal(123.4, r.Station, 12);
        Assert.Equal(-56.7, r.Offset, 12);          // +y is left of an eastbound alignment
        Assert.Equal(56.7, r.Distance, 12);
        Assert.Equal(17.0, loc.Locate(new Pt(250, -17)).Offset, 12);
    }

    [Fact]
    public void Points_beyond_the_ends_clamp_to_the_end_station()
    {
        var loc = Make(FakeAlignments.S());
        var before = loc.Locate(new Pt(-30, 40));
        Assert.Equal((0.0, 50.0), (before.Station, before.Distance));
        Assert.Equal(-50.0, before.Offset);
        Assert.Equal(2000.0, loc.Locate(new Pt(2010, 0)).Station);
    }

    [Fact]
    public void Alignment_C_round_trips_PointAt_in_closed_form()
    {
        var c = FakeAlignments.C();
        var loc = Make(c);
        double end = c.EndStation;
        double worst = 0;
        for (double s = 0; s <= end; s += 7.3)
            foreach (double o in new[] { -100.0, -37.5, 0.0, 42.0, 100.0 })
            {
                var r = loc.Locate(c.PointAt(s, o));
                worst = Math.Max(worst, Math.Max(Math.Abs(r.Station - s), Math.Abs(r.Offset - o)));
            }
        Assert.True(worst < 1e-9, $"worst round-trip error {worst:E2}");
        // On the arc, inside (right) of a clockwise curve: radius 300 - 100 = 200 from (300, -300).
        var p = loc.Locate(new Pt(300 + 200 * Math.Cos(Math.PI / 3), -300 + 200 * Math.Sin(Math.PI / 3)));
        Assert.Equal(300 + 300 * (Math.PI / 6), p.Station, 9);   // 30 degrees into the arc
        Assert.Equal(100.0, p.Offset, 9);
    }

    [Fact]
    public void Alignment_SP_round_trips_through_the_spirals_iteratively()
    {
        var sp = FakeAlignments.SP_C5();
        Assert.Contains(sp.Segments, g => g.Kind == SegKind.Spiral);
        var loc = Make(sp);
        double worst = 0;
        for (double s = 0; s <= sp.EndStation; s += 3.7)
            foreach (double o in new[] { -100.0, -12.5, 0.0, 33.0, 100.0 })
            {
                var r = loc.Locate(sp.PointAt(s, o));
                worst = Math.Max(worst, Math.Max(Math.Abs(r.Station - s), Math.Abs(r.Offset - o)));
            }
        Assert.True(worst < 1e-7, $"worst round-trip error {worst:E2}");
    }

    [Fact]
    public void State_plane_alignment_in_a_local_frame_matches_the_origin()
    {
        const double ox = 3_000_000, oy = 10_000_000;
        var c0 = FakeAlignments.C();
        var cf = FakeAlignments.C(new Pt(ox, oy));
        var loc0 = Make(c0);
        var locf = Make(cf, new Pt(ox, oy));
        for (double s = 0; s <= c0.EndStation; s += 11.1)
        {
            var w = cf.PointAt(s, 60);
            var a = loc0.Locate(c0.PointAt(s, 60));
            var b = locf.Locate(new Pt(w.X - ox, w.Y - oy));
            Assert.True(Math.Abs(a.Station - b.Station) < 1e-8 && Math.Abs(a.Offset - b.Offset) < 1e-8, $"s={s}: {a} vs {b}");
        }
    }

    [Fact]
    public void Window_restricts_the_search()
    {
        var c = FakeAlignments.C();
        var loc = Make(c);
        var onArc = c.PointAt(450, 0);
        Assert.Equal(450.0, loc.Locate(onArc).Station, 9);
        Assert.Equal(300.0, loc.Locate(onArc, 0, 299).Station, 9);   // only the first tangent searched
    }

    [Fact]
    public void Inconsistent_segments_are_rejected_with_a_reason()
    {
        var c = FakeAlignments.C();
        var flipped = new C5SegmentsOverride(c, [.. c.Segments.Select(g => g.Kind == SegKind.Arc ? g with { Clockwise = !g.Clockwise } : g)]);
        Assert.False(StationLocator.TryCreate(flipped, default, out _, out string? e1));
        Assert.Contains("Clockwise", e1);

        var stretched = new C5SegmentsOverride(c, [.. c.Segments.Select((g, i) => i == 0 ? g with { EndStation = g.EndStation + 1 } : g)]);
        Assert.False(StationLocator.TryCreate(stretched, default, out _, out string? e2));
        Assert.Contains("length", e2);

        var empty = new C5SegmentsOverride(c, []);
        Assert.False(StationLocator.TryCreate(empty, default, out _, out _));
    }
}

public class StationIntervalTests
{
    [Fact]
    public void Union_merges_overlapping_and_touching_and_clamps()
    {
        var u = StationIntervals.Union([(100, 200), (150, 250), (250, 300), (400, 450), (-50, 10), (590, 700)], 0, 600, 0);
        Assert.Equal([(0.0, 10.0), (100.0, 300.0), (400.0, 450.0), (590.0, 600.0)], u.Intervals);
        Assert.Equal(10 + 200 + 50 + 10, u.TotalLength);
    }

    [Fact]
    public void Gap_tolerance_bridges_gaps_shorter_than_it()
    {
        (double, double)[] iv = [(0, 100), (130, 200)];
        Assert.Equal(2, StationIntervals.Union(iv, 0, 500, 30).Intervals.Count);      // gap 30 is not shorter than 30
        var bridged = StationIntervals.Union(iv, 0, 500, 30.0001);
        Assert.Equal((0.0, 200.0), Assert.Single(bridged.Intervals));
        Assert.Equal(200.0, bridged.TotalLength);
    }

    [Fact]
    public void Rounding_gaps_merge_but_real_gaps_do_not()
    {
        var u = StationIntervals.Union([(0, 100), (100 + 1e-11, 200), (200.001, 300)], 0, 500, 0);
        Assert.Equal(2, u.Intervals.Count);
        Assert.Equal((0.0, 200.0), u.Intervals[0]);
    }

    [Fact]
    public void Empty_and_reversed_inputs()
    {
        Assert.Empty(StationIntervals.Union([], 0, 10, 0).Intervals);
        Assert.Equal((2.0, 5.0), Assert.Single(StationIntervals.Union([(5, 2), (7, 7)], 0, 10, 0).Intervals));
        Assert.Throws<ArgumentException>(() => StationIntervals.Union([], 10, 10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => StationIntervals.Union([], 0, 10, -1));
    }
}

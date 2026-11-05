using System.Globalization;
using SheetVol.Core.Geometry;
using SheetVol.Core.Planning;
using SheetVol.Core.Tests.Fakes;

namespace SheetVol.Core.Tests.Geometry;

public class OverlapTests
{
    private static readonly PlanningOptions Feet = PlanningOptions.ForUnitScale(1);

    /// <summary>
    /// Self-crossing alignment: tangent 1000 east from (0,0), arc R 150 counter-clockwise through
    /// 270 degrees (center (1000, 150)), tangent 600 south from (850, 150). The last tangent crosses
    /// the first at (850, 0). With offsets of 50 a side the two bands overlap in a 100 x 100 square.
    /// </summary>
    internal static FakeAlignment AlphaLoop() =>
        new AlignmentBuilder(new Pt(0, 0), 0).Line(1000).Arc(150, 1.5 * Math.PI, clockwise: false).Line(600).Build("LOOP");

    private static string LoopSpec()
    {
        double pt = 1000 + 150 * 1.5 * Math.PI;
        return $"START, 10+00, {pt.ToString("R", CultureInfo.InvariantCulture)}, END";
    }

    [Fact]
    public void E215_looping_alignment_reports_the_overlap_per_pair_and_skips_the_partition_check()
    {
        var loop = AlphaLoop();
        var plan = new Planner(Feet).Plan(TestRuns.Stations(loop, LoopSpec(), -50, 50), loop);
        Assert.True(plan.IsValid);                                             // a warning, not an error
        Assert.Equal(3, plan.Boundaries.Count);
        var ov = Assert.Single(plan.Overlaps);
        Assert.Equal(plan.Segments[0].Id, ov.First);
        Assert.Equal(plan.Segments[2].Id, ov.Second);
        Assert.Equal(10_000, ov.Area, 6);
        var p = Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E215);
        Assert.Equal(Severity.Warning, p.Severity);
        Assert.Contains("Segment 1 and Segment 3", p.Message);
        Assert.Contains("10,000.00", p.Message);
        Assert.False(plan.PartitionCheckApplicable);
        Assert.Contains("partition check is skipped", plan.PartitionCheckSkipReason);
    }

    [Theory]
    [InlineData("C", "START, 1+00, 3+00, 4+00, 5+00, 6+00, END", -100, 100)]
    [InlineData("K", "START, 5+00, 10+00, 15+00, END", -100, 100)]
    [InlineData("SP", "START, 2+50, 3+50, 4+00, 6+00, 7+50, END", -100, 150)]
    public void Adjacent_segments_share_a_cut_line_and_do_not_overlap(string name, string spec, double oL, double oR)
    {
        var a = PartitionTests.Fixture(name);
        var plan = new Planner(Feet).Plan(TestRuns.Stations(a, spec, oL, oR), a);
        Assert.True(plan.IsValid);
        Assert.Empty(plan.Overlaps);
        Assert.True(plan.PartitionCheckApplicable);
        Assert.Null(plan.PartitionCheckSkipReason);
    }

    private static ManualSegmentDef Manual(string label) => new(Guid.NewGuid(), label, "2F1");

    private static ManualOutline Outline(bool closed, params (double X, double Y, double B)[] v) =>
        new(v.Select(p => new PolyVertex(new Pt(p.X, p.Y), p.B)).ToArray(), closed);

    [Fact]
    public void E312_manual_segment_overlapping_a_station_segment_is_reported_with_its_area()
    {
        var s = FakeAlignments.S();
        var m = Manual("M1");
        var run = TestRuns.Stations(s, "START, 5+00, END", manual: [m]);
        var outlines = new Dictionary<Guid, ManualOutline> { [m.Id] = Outline(true, (400, 0, 0), (600, 0, 0), (600, 150, 0), (400, 150, 0)) };
        var plan = new Planner(Feet).Plan(run, s, outlines);
        Assert.True(plan.IsValid);
        Assert.Equal(3, plan.Boundaries.Count);
        Assert.Equal(m.Id, plan.Boundaries[2].SegmentId);
        Assert.Equal(2, plan.Overlaps.Count);                                  // 100 x 100 with each station segment
        Assert.All(plan.Overlaps, o => Assert.Equal(10_000, o.Area, 6));
        var problems = plan.Problems.Where(x => x.Code == ProblemCodes.E312).ToList();
        Assert.Equal(2, problems.Count);
        Assert.All(problems, x => Assert.Contains("Manual segment M1", x.Message));
        Assert.True(plan.PartitionCheckApplicable);                            // station segments alone still partition the band
    }

    [Fact]
    public void E311_manual_polyline_open_self_intersecting_or_missing_is_an_error_for_that_segment()
    {
        var s = FakeAlignments.S();
        var open = Manual("OPEN");
        var bow = Manual("BOW");
        var gone = Manual("GONE");
        var good = Manual("ARC");
        var outlines = new Dictionary<Guid, ManualOutline>
        {
            [open.Id] = Outline(false, (0, 300, 0), (100, 300, 0), (100, 400, 0)),
            [bow.Id] = Outline(true, (0, 500, 0), (100, 600, 0), (100, 500, 0), (0, 600, 0)),
            [good.Id] = Outline(false, (0, 700, 1), (100, 700, 0), (0, 700, 0)),   // half disc, closed by repeating the first vertex
        };
        var run = TestRuns.Stations(s, "START, END", manual: [open, bow, gone, good]);
        var plan = new Planner(Feet).Plan(run, s, outlines);

        Assert.False(plan.IsValid);
        var e311 = plan.Problems.Where(p => p.Code == ProblemCodes.E311).ToList();
        Assert.Equal(3, e311.Count);
        Assert.Contains(e311, p => p.SegmentId == open.Id && p.Message.Contains("open"));
        Assert.Contains(e311, p => p.SegmentId == bow.Id && p.Message.Contains("crosses itself"));
        Assert.Contains(e311, p => p.SegmentId == gone.Id && p.Message.Contains("erased"));

        var arc = Assert.Single(plan.Boundaries, b => b.SegmentId == good.Id);
        Assert.Equal(2, arc.Drawn.Count);                                      // used as drawn, closing repeat dropped
        Assert.Equal(1, arc.Drawn[0].Bulge);
        Assert.Equal(Math.PI * 50 * 50 / 2, Polygon.Area(arc.Tessellated), 0.01 * Analytic.Perimeter(arc.Tessellated));
        Assert.Contains(plan.Boundaries, b => b.SegmentId == plan.Segments[0].Id);   // the station segment is unaffected
    }

    [Fact]
    public void Overlap_detector_ignores_touching_and_measures_crossing()
    {
        var a = new Boundary(Guid.NewGuid(), [], [new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)]);
        var b = new Boundary(Guid.NewGuid(), [], [new(10, 0), new(20, 0), new(20, 10), new(10, 10), new(10, 0)]);
        var c = new Boundary(Guid.NewGuid(), [], [new(5, 5), new(15, 5), new(15, 15), new(5, 15), new(5, 5)]);
        var found = OverlapDetector.Find([a, b, c], 1e-4);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, o => o.First == a.SegmentId && o.Second == c.SegmentId && Math.Abs(o.Area - 25) < 1e-9);
        Assert.Contains(found, o => o.First == b.SegmentId && o.Second == c.SegmentId && Math.Abs(o.Area - 25) < 1e-9);
    }
}

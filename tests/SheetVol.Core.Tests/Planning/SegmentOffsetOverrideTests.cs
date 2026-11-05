using System.Globalization;
using SheetVol.Core.Geometry;
using SheetVol.Core.Planning;
using SheetVol.Core.Tests.Fakes;
using SheetVol.Core.Tests.Geometry;

namespace SheetVol.Core.Tests.Planning;

/// <summary>
/// Per-segment offset overrides (contract rev 2, D8): the planner builds each segment with
/// <see cref="SegmentOverrides.OffsetLeft"/> and <see cref="SegmentOverrides.OffsetRight"/>
/// looked up by the GUID the segment ends up with, falling back to the run's offsets.
/// </summary>
public class SegmentOffsetOverrideTests
{
    private static readonly PlanningOptions Feet = PlanningOptions.ForUnitScale(1);
    private static readonly Planner Planner = new(Feet);
    private static readonly string CurveEnd = FakeAlignments.C_PT.ToString("R", CultureInfo.InvariantCulture);

    private static Dictionary<Guid, SegmentOverrides> Override(Guid id, double? left = null, double? right = null) =>
        new() { [id] = new SegmentOverrides(OffsetLeft: left, OffsetRight: right) };

    [Fact]
    public void T9_override_on_one_curve_segment_clears_E201_while_the_others_keep_failing()
    {
        // C: tangent to 3+00, arc R 300 to the PT, tangent. Right offset 300 reaches the arc center.
        var c = FakeAlignments.C();
        string spec = $"START, 3+00, 4+50, {CurveEnd}, END";
        var first = Planner.Plan(TestRuns.Stations(c, spec, -50, 300), c);
        Assert.Equal(
            [first.Segments[1].Id, first.Segments[2].Id],
            first.Problems.Where(p => p.Code == ProblemCodes.E201).Select(p => p.SegmentId!.Value));

        var known = first.Segments;
        var overrides = Override(known[1].Id, -50, 100);
        var run = TestRuns.Stations(c, spec, -50, 300, known: known, overrides: overrides);
        var plan = Planner.Plan(run, c);

        Assert.Equal(known.Select(k => k.Id), plan.Segments.Select(s => s.Id));
        Assert.Equal([(-50.0, 300.0), (-50.0, 100.0), (-50.0, 300.0), (-50.0, 300.0)], plan.Segments.Select(s => (s.OffsetLeft, s.OffsetRight)));
        var e201 = Assert.Single(plan.Problems, p => p.Code == ProblemCodes.E201);
        Assert.Equal(plan.Segments[2].Id, e201.SegmentId);                    // segment 3 keeps the run's 300 and still fails
        Assert.Equal(Severity.Error, e201.Severity);
        Assert.False(plan.IsValid);
        Assert.Equal([plan.Segments[0].Id, plan.Segments[1].Id, plan.Segments[3].Id], plan.Boundaries.Select(b => b.SegmentId));
        Assert.Empty(plan.Overlaps);

        var seg2 = plan.Boundaries[1];
        double exact = Analytic.BandArea(c, 300, 450, -50, 100);
        Assert.Equal(exact, Polygon.Area(seg2.Tessellated), Feet.Geometry.MidOrdinate * Analytic.Perimeter(seg2.Tessellated));

        // The same fix on segment 3 too: the run validates.
        overrides[known[2].Id] = new SegmentOverrides(OffsetLeft: -50, OffsetRight: 100);
        var fixedPlan = Planner.Validate(run with { Overrides = overrides }, c);
        Assert.True(fixedPlan.IsValid, string.Join(" | ", fixedPlan.Problems.Select(p => p.Message)));
        Assert.Equal(4, fixedPlan.Boundaries.Count);
    }

    [Fact]
    public void One_sided_override_keeps_the_run_offset_on_the_other_side()
    {
        var s = FakeAlignments.S();
        var known = Planner.Plan(TestRuns.Stations(s, "START, 5+00, 10+00, END"), s).Segments;
        var plan = Planner.Plan(TestRuns.Stations(s, "START, 5+00, 10+00, END", known: known, overrides: Override(known[1].Id, right: 250)), s);
        Assert.True(plan.IsValid);
        Assert.Equal((-100.0, 250.0), (plan.Segments[1].OffsetLeft, plan.Segments[1].OffsetRight));
        Assert.Equal(500 * 350, Polygon.Area(plan.Boundaries[1].Tessellated), 9);
        Assert.All(new[] { plan.Segments[0], plan.Segments[2] }, x => Assert.Equal((-100.0, 100.0), (x.OffsetLeft, x.OffsetRight)));
    }

    [Fact]
    public void Override_follows_the_GUID_across_a_split()
    {
        var s = FakeAlignments.S();
        var known = Planner.Plan(TestRuns.Stations(s, "START..END@500"), s).Segments;
        var overrides = Override(known[1].Id, -50, 200);
        var plan = Planner.Plan(TestRuns.Stations(s, "START..END@500, 7+50", known: known, overrides: overrides), s);

        Assert.True(plan.IsValid);
        var left = plan.Segments[1];
        var right = plan.Segments[2];
        Assert.Equal((known[1].Id, 500.0, 750.0), (left.Id, left.StartStation, left.EndStation));   // split: the left part keeps the GUID
        Assert.Equal((-50.0, 200.0), (left.OffsetLeft, left.OffsetRight));                           // and the override with it
        Assert.DoesNotContain(right.Id, known.Select(k => k.Id));
        Assert.Equal((-100.0, 100.0), (right.OffsetLeft, right.OffsetRight));                       // the new right part takes the run's
        Assert.Equal(250 * 250, Polygon.Area(plan.Boundaries[1].Tessellated), 9);
        Assert.Equal(250 * 200, Polygon.Area(plan.Boundaries[2].Tessellated), 9);
    }

    [Fact]
    public void Override_follows_the_GUID_across_a_merge()
    {
        var s = FakeAlignments.S();
        var known = Planner.Plan(TestRuns.Stations(s, "START..END@500"), s).Segments;

        // Merge segments 2 and 3: the merged segment keeps the left GUID and so the left override.
        var keep = Planner.Plan(TestRuns.Stations(s, "START, 5+00, 15+00, END", known: known, overrides: Override(known[1].Id, -50, 200)), s);
        var merged = keep.Segments[1];
        Assert.Equal((known[1].Id, 500.0, 1500.0), (merged.Id, merged.StartStation, merged.EndStation));
        Assert.Equal((-50.0, 200.0), (merged.OffsetLeft, merged.OffsetRight));
        Assert.Equal(1000 * 250, Polygon.Area(keep.Boundaries[1].Tessellated), 9);

        // An override on the right one lapses with its GUID.
        var lapse = Planner.Plan(TestRuns.Stations(s, "START, 5+00, 15+00, END", known: known, overrides: Override(known[2].Id, -50, 200)), s);
        Assert.Equal(known[1].Id, lapse.Segments[1].Id);
        Assert.Equal([known[2].Id], lapse.DroppedSegmentIds);
        Assert.All(lapse.Segments, x => Assert.Equal((-100.0, 100.0), (x.OffsetLeft, x.OffsetRight)));
    }

    [Theory]
    [InlineData(150.0, null)]                       // left 150 against the run's right 100
    [InlineData(60.0, 60.0)]                        // zero width
    [InlineData(double.NaN, null)]
    public void E210_override_without_width_is_an_error_on_that_segment_only(double? left, double? right)
    {
        var s = FakeAlignments.S();
        var known = Planner.Plan(TestRuns.Stations(s, "START, 5+00, 10+00, END"), s).Segments;
        var plan = Planner.Plan(TestRuns.Stations(s, "START, 5+00, 10+00, END", known: known, overrides: Override(known[1].Id, left, right)), s);

        Assert.False(plan.IsValid);
        var p = Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E210);
        Assert.Equal((Severity.Error, known[1].Id), (p.Severity, p.SegmentId));
        Assert.Contains("Segment 2", p.Message);
        Assert.Contains("override", p.Message);
        Assert.Contains("clear its override", p.Fix);
        Assert.Contains("-100.00 and 100.00", p.Fix);
        Assert.Equal(3, plan.Segments.Count);                                  // the row is still listed for the dialog
        Assert.Equal([known[0].Id, known[2].Id], plan.Boundaries.Select(b => b.SegmentId));
        Assert.Equal(2, plan.Band.Count);                                      // the failed segment leaves a gap in the band
        Assert.True(plan.PartitionCheckApplicable);
        Assert.False(Planner.Validate(TestRuns.Stations(s, "START, 5+00, 10+00, END", known: known, overrides: Override(known[1].Id, left, right)), s).IsValid);
    }

    [Fact]
    public void Unusable_run_offsets_are_one_run_level_E210_and_a_usable_override_still_builds()
    {
        var s = FakeAlignments.S();
        var known = Planner.Plan(TestRuns.Stations(s, "START, 5+00, 10+00, END"), s).Segments;
        var plan = Planner.Plan(TestRuns.Stations(s, "START, 5+00, 10+00, END", 100, -100, known: known, overrides: Override(known[1].Id, -50, 50)), s);

        var p = Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E210);
        Assert.Null(p.SegmentId);                                              // rows 1 and 3 use the run's offsets: one run-level error
        Assert.Equal(known[1].Id, Assert.Single(plan.Boundaries).SegmentId);
        Assert.False(plan.IsValid);
    }
}

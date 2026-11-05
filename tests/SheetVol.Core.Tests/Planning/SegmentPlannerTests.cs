using SheetVol.Core.Planning;
using SheetVol.Core.Tests.Fakes;

namespace SheetVol.Core.Tests.Planning;

public class SegmentPlannerTests
{
    private static readonly PlanningOptions Feet = PlanningOptions.ForUnitScale(1);
    private static readonly Planner Planner = new(Feet);

    private static FakeAlignment Straight(double length) => new AlignmentBuilder(new Pt(0, 0), 0).Line(length).Build("S");

    private static PlanResult Plan(IAlignmentGeometry a, string spec, IReadOnlyList<SegmentDef>? known = null, IReadOnlyDictionary<Guid, SegmentOverrides>? overrides = null) =>
        Planner.Plan(TestRuns.Stations(a, spec, known: known, overrides: overrides), a);

    [Fact]
    public void E301_T2_mixed_lengths_are_accepted_and_independent()
    {
        var plan = Plan(FakeAlignments.S(), "0, 500, 1300, 1800, 2000");
        Assert.True(plan.IsValid);
        Assert.Equal([500, 800, 500, 200], plan.Segments.Select(s => s.EndStation - s.StartStation));
        Assert.Equal(["1", "2", "3", "4"], plan.Segments.Select(s => s.Label));
        Assert.All(plan.Segments, s => Assert.Equal((-100.0, 100.0), (s.OffsetLeft, s.OffsetRight)));
        Assert.Equal(4, plan.Boundaries.Count);
        Assert.Equal(4, plan.Segments.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void T1_every_500_gives_four_segments()
    {
        var plan = Plan(FakeAlignments.S(), "START..END@500");
        Assert.Equal([0, 500, 1000, 1500], plan.Segments.Select(s => s.StartStation));
        Assert.All(plan.Segments, s => Assert.Equal(500, s.EndStation - s.StartStation));
    }

    [Fact]
    public void E303_skip_gap_has_no_segment_and_is_reported()
    {
        var plan = Plan(FakeAlignments.S(), "START, 5+00, skip 5+00..8+00, END");
        Assert.Equal([(0.0, 500.0), (800.0, 2000.0)], plan.Segments.Select(s => (s.StartStation, s.EndStation)));
        var p = Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E303);
        Assert.Equal(Severity.Info, p.Severity);
        Assert.Contains("OUTSIDE LIMITS", p.Message);
        Assert.Equal(["1", "2"], plan.Segments.Select(s => s.Label));
    }

    [Fact]
    public void E305_break_outside_the_alignment_is_rejected_with_the_valid_range()
    {
        var plan = Plan(FakeAlignments.S(), "START, END, 25+00");
        Assert.False(plan.IsValid);
        var run = Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E305 && x.SegmentId is null);
        Assert.Contains("0+00.00 to 20+00.00", run.Message);
        Assert.Contains("25+00", run.Message);
        Assert.Equal(2, plan.Segments.Count);
        var outside = plan.Segments[1];
        Assert.Equal((2000.0, 2500.0), (outside.StartStation, outside.EndStation));
        Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E305 && x.SegmentId == outside.Id);
        Assert.Equal(plan.Segments[0].Id, Assert.Single(plan.Boundaries).SegmentId);
    }

    [Fact]
    public void E307_T13_break_inserted_mid_run_keeps_the_left_GUID_and_renumbers_labels()
    {
        var s = FakeAlignments.S();
        var first = Plan(s, "START..END@500");
        var known = first.Segments;
        var second = Plan(s, "START..END@500, 2+50", known);

        Assert.Equal(5, second.Segments.Count);
        Assert.Equal(known[0].Id, second.Segments[0].Id);                      // left part keeps GUID
        Assert.Equal((0.0, 250.0), (second.Segments[0].StartStation, second.Segments[0].EndStation));
        Assert.DoesNotContain(second.Segments[1].Id, known.Select(k => k.Id)); // right part is new
        Assert.Equal(known.Skip(1).Select(k => k.Id), second.Segments.Skip(2).Select(k => k.Id));
        Assert.Equal("2", known[1].Label);
        Assert.Equal("3", second.Segments[2].Label);                           // derived, renumbered
        Assert.Empty(second.DroppedSegmentIds);
    }

    [Fact]
    public void E308_break_removed_keeps_the_left_GUID_and_reports_the_right_one_dropped()
    {
        var s = FakeAlignments.S();
        var known = Plan(s, "START..END@500").Segments;
        var plan = Plan(s, "START, 10+00, 15+00, END", known);

        Assert.Equal(known[0].Id, plan.Segments[0].Id);
        Assert.Equal((0.0, 1000.0), (plan.Segments[0].StartStation, plan.Segments[0].EndStation));
        Assert.Equal([known[1].Id], plan.DroppedSegmentIds);
        var p = Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E308);
        Assert.Equal((Severity.Info, known[1].Id), (p.Severity, p.SegmentId));
        Assert.Contains("orphan", p.Message);
        Assert.Equal([known[2].Id, known[3].Id], plan.Segments.Skip(1).Select(x => x.Id));
    }

    [Fact]
    public void Moving_a_break_keeps_both_GUIDs()
    {
        var s = FakeAlignments.S();
        var known = Plan(s, "START, 5+00, END").Segments;
        var plan = Plan(s, "START, 6+00, END", known);
        Assert.Equal(known.Select(k => k.Id), plan.Segments.Select(x => x.Id));
        Assert.Empty(plan.DroppedSegmentIds);
    }

    [Fact]
    public void Identity_rules_split_merge_move()
    {
        var k1 = new SegmentDef(Guid.NewGuid(), "1", 0, 500, -1, 1);
        var k2 = new SegmentDef(Guid.NewGuid(), "2", 500, 1000, -1, 1);
        // Split k1 at 250: left keeps k1, right is new, k2 stays.
        var (ids, claimed) = SegmentPlanner.AssignIdentity([(0, 250), (250, 500), (500, 1000)], [k1, k2], 1e-6);
        Assert.Equal(new Guid?[] { k1.Id, null, k2.Id }, ids);
        Assert.All(claimed, Assert.True);
        // Merge: the merged segment keeps the left GUID; k2 is unclaimed.
        (ids, claimed) = SegmentPlanner.AssignIdentity([(0, 1000)], [k1, k2], 1e-6);
        Assert.Equal(new Guid?[] { k1.Id }, ids);
        Assert.Equal(new[] { true, false }, claimed);
        // START moved earlier: the first segment keeps k1 by overlap.
        (ids, _) = SegmentPlanner.AssignIdentity([(-100, 500), (500, 1000)], [k1, k2], 1e-6);
        Assert.Equal(new Guid?[] { k1.Id, k2.Id }, ids);
    }

    [Fact]
    public void E211_shortened_alignment_reports_breaks_beyond_the_end_and_END_follows()
    {
        var known = Plan(Straight(2000), "START, 5+00, 13+00, 18+00, END").Segments;
        var shorter = Straight(1500);
        var plan = Plan(shorter, "START, 5+00, 13+00, 18+00, END", known);

        Assert.False(plan.IsValid);
        Assert.DoesNotContain(plan.Problems, x => x.Code == ProblemCodes.E306);   // END moving below 18+00 is not "out of order"
        var brk = Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E211 && x.SegmentId is null);
        Assert.Equal(Severity.Error, brk.Severity);
        Assert.Contains("now runs 0+00.00 to 15+00.00", brk.Message);

        // Breaks keep their values; END follows to 15+00, so 13+00..15+00 keeps the GUID of 13+00..18+00.
        Assert.Equal([(0.0, 500.0), (500.0, 1300.0), (1300.0, 1500.0), (1500.0, 1800.0)], plan.Segments.Select(x => (x.StartStation, x.EndStation)));
        Assert.Equal(known.Take(3).Select(k => k.Id), plan.Segments.Take(3).Select(x => x.Id));
        var beyond = plan.Segments[3];
        Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E211 && x.SegmentId == beyond.Id && x.Severity == Severity.Error);
        Assert.Equal(3, plan.Boundaries.Count);
        Assert.DoesNotContain(plan.Boundaries, b => b.SegmentId == beyond.Id);

        // The known 18+00..20+00 segment is not silently dropped.
        Assert.Equal([known[3].Id], plan.DroppedSegmentIds);
        Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E211 && x.SegmentId == known[3].Id && x.Severity == Severity.Warning);
    }

    [Fact]
    public void T14_lengthened_alignment_extends_the_END_segment()
    {
        var known = Plan(Straight(2000), "START, 5+00, 13+00, 18+00, END").Segments;
        var plan = Plan(Straight(2500), "START, 5+00, 13+00, 18+00, END", known);
        Assert.True(plan.IsValid);
        Assert.Equal(known.Select(k => k.Id), plan.Segments.Select(x => x.Id));
        Assert.Equal(2500, plan.Segments[^1].EndStation);
        Assert.Empty(plan.DroppedSegmentIds);
    }

    [Fact]
    public void Target_layout_comes_from_overrides_then_from_the_known_segment()
    {
        var s = FakeAlignments.S();
        var known = Plan(s, "START, 10+00, END").Segments;
        known = [known[0] with { TargetLayout = "Sheet A" }, known[1] with { TargetLayout = "Sheet B" }];
        var overrides = new Dictionary<Guid, SegmentOverrides> { [known[1].Id] = new SegmentOverrides(TargetLayout: "Sheet Z") };
        var plan = Plan(s, "START, 10+00, END", known, overrides);
        Assert.Equal(["Sheet A", "Sheet Z"], plan.Segments.Select(x => x.TargetLayout));
        Assert.All(plan.Segments, x => Assert.Null(x.SourceKey));
    }

    [Fact]
    public void Labels_follow_the_configured_format()
    {
        var planner = new Planner(Feet with { SegmentLabelFormat = "S-{0:00}" });
        var s = FakeAlignments.S();
        var plan = planner.Plan(TestRuns.Stations(s, "START..END@500"), s);
        Assert.Equal(["S-01", "S-02", "S-03", "S-04"], plan.Segments.Select(x => x.Label));
    }
}

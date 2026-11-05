using SheetVol.Core.Geometry;
using SheetVol.Core.Planning;
using SheetVol.Core.Tests.Fakes;

namespace SheetVol.Core.Tests.Planning;

public class PlannerTests
{
    private static readonly PlanningOptions Feet = PlanningOptions.ForUnitScale(1);
    private static readonly Planner Planner = new(Feet);

    [Fact]
    public void Validate_returns_the_frozen_result_for_the_session()
    {
        var s = FakeAlignments.S();
        var run = TestRuns.Stations(s, "0, 500, 1300, 1800, 2000");
        var v = Planner.Validate(run, s);
        Assert.True(v.IsValid);
        Assert.Equal(4, v.Segments.Count);
        Assert.Equal(v.Segments.Select(x => x.Id), v.Boundaries.Select(b => b.SegmentId));
        Assert.Empty(v.Problems);
        Assert.Equal([100_000.0, 160_000.0, 100_000.0, 40_000.0], v.Boundaries.Select(b => Math.Round(Polygon.Area(b.Tessellated), 6)));
    }

    [Fact]
    public void Every_problem_has_a_stable_code_and_errors_have_a_fix()
    {
        var s = FakeAlignments.S();
        var v = Planner.Validate(TestRuns.Stations(s, "START, 5+0x, 25+00, 0..100"), s);
        Assert.False(v.IsValid);
        Assert.All(v.Problems, p => Assert.Matches(@"^SV-E[23]\d\d$", p.Code));
        Assert.All(v.Problems.Where(p => p.Severity == Severity.Error), p => Assert.False(string.IsNullOrWhiteSpace(p.Fix)));
    }

    [Theory]
    [InlineData(10, 50)]
    [InlineData(-50, -10)]
    public void E209_band_entirely_on_one_side_is_allowed(double oL, double oR)
    {
        var s = FakeAlignments.S();
        var plan = Planner.Plan(TestRuns.Stations(s, "START, 5+00, END", oL, oR), s);
        Assert.True(plan.IsValid);
        Assert.Equal(40 * 500, Polygon.Area(plan.Boundaries[0].Tessellated), 9);
        Assert.Equal(-oR, plan.Boundaries[0].Tessellated[0].Y, 9);             // right line at y = -oR on S
    }

    [Theory]
    [InlineData(100, -100)]
    [InlineData(50, 50)]
    [InlineData(double.NaN, 100)]
    [InlineData(-100, double.PositiveInfinity)]
    public void E210_left_not_less_than_right_or_zero_width_is_rejected(double oL, double oR)
    {
        var s = FakeAlignments.S();
        var plan = Planner.Plan(TestRuns.Stations(s, "START, 5+00, END", oL, oR), s);
        Assert.False(plan.IsValid);
        var p = Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E210);
        Assert.Null(p.SegmentId);
        Assert.Equal(2, plan.Segments.Count);                                  // rows still listed for the dialog
        Assert.Empty(plan.Boundaries);
    }

    [Fact]
    public void E214_alignment_with_a_gap_is_an_error_naming_the_gap_station()
    {
        AlignSeg[] segs =
        [
            new(SegKind.Line, 0, 500, new Pt(0, 0), new Pt(500, 0), 0, 0),
            new(SegKind.Line, 500, 1000, new Pt(510, 0), new Pt(1010, 0), 0, 0),
        ];
        var broken = new FakeAlignment("GAP", segs);
        var plan = Planner.Plan(TestRuns.Stations(broken, "START, END"), broken);
        var p = Assert.Single(plan.Problems, x => x.Code == ProblemCodes.E214);
        Assert.Contains("gap at station 5+00.00", p.Message);
        Assert.Empty(plan.Boundaries);
    }

    [Fact]
    public void Empty_break_list_and_single_break_are_errors()
    {
        var s = FakeAlignments.S();
        Assert.Contains(Planner.Plan(TestRuns.Stations(s, ""), s).Problems, p => p.Code == ProblemCodes.E300 && p.Message.Contains("empty"));
        Assert.Contains(Planner.Plan(TestRuns.Stations(s, "START"), s).Problems, p => p.Code == ProblemCodes.E300 && p.Message.Contains("no segment"));
        Assert.Contains(Planner.Plan(TestRuns.Stations(s, "skip START..END"), s).Problems, p => p.Code == ProblemCodes.E300);
    }

    [Fact]
    public void Manual_only_run_needs_no_break_list()
    {
        var s = FakeAlignments.S();
        var m = new ManualSegmentDef(Guid.NewGuid(), "M", "1A");
        var outline = new ManualOutline([new(new Pt(0, 0), 0), new(new Pt(10, 0), 0), new(new Pt(10, 10), 0), new(new Pt(0, 10), 0)], true);
        var plan = Planner.Plan(TestRuns.Stations(s, "", manual: [m]), s, new Dictionary<Guid, ManualOutline> { [m.Id] = outline });
        Assert.True(plan.IsValid);
        Assert.Empty(plan.Segments);
        Assert.Equal(m.Id, Assert.Single(plan.Boundaries).SegmentId);
        Assert.False(plan.PartitionCheckApplicable);
    }

    [Fact]
    public void ViewFrames_driver_is_reported_not_guessed()
    {
        var s = FakeAlignments.S();
        var run = TestRuns.Stations(s, "START, END") with { Driver = SegmentDriver.ViewFrames };
        var plan = Planner.Plan(run, s);
        Assert.False(plan.IsValid);
        Assert.Single(plan.Problems, p => p.Code == ProblemCodes.E300 && p.Message.Contains("ViewFrames"));
        Assert.Empty(plan.Segments);
    }

    [Fact]
    public void Planner_requires_options()
    {
        Assert.Throws<ArgumentNullException>(() => new Planner(null!));
    }
}

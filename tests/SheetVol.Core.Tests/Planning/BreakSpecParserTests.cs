using SheetVol.Core.Planning;
using SheetVol.Core.Tests.Fakes;

namespace SheetVol.Core.Tests.Planning;

public class BreakSpecParserTests
{
    private static readonly PlanningOptions Feet = PlanningOptions.ForUnitScale(1);

    private static ParsedBreakSpec Parse(string spec, IAlignmentGeometry? a = null, PlanningOptions? o = null) =>
        BreakSpecParser.Parse(spec, a ?? FakeAlignments.S(), o ?? Feet);

    private static double[] Stations(ParsedBreakSpec p) => p.Breaks.Select(b => b.Station).ToArray();

    private static FakeAlignment Straight(double length) => new AlignmentBuilder(new Pt(0, 0), 0).Line(length).Build("L");

    [Fact]
    public void Single_breaks_and_keywords()
    {
        var p = Parse("START, 5+00, 13+00, 18+00, END");
        Assert.Equal([0, 500, 1300, 1800, 2000], Stations(p));
        Assert.Empty(p.Problems);
        Assert.Equal([0, 500, 2000], Stations(Parse("start, 500, End")));
    }

    [Fact]
    public void Mixed_forms_example_from_section_6_3()
    {
        var p = Parse("START..20+00@500, 28+00, skip 28+00..31+50, 31+50..END@400", Straight(4000));
        Assert.Equal([0, 500, 1000, 1500, 2000, 2800, 3150, 3550, 3950, 4000], Stations(p));
        Assert.Equal([new StationRange(2800, 3150)], p.Skips);
        Assert.DoesNotContain(p.Problems, x => x.Severity != Severity.Info);
        Assert.DoesNotContain(p.Problems, x => x.Code == ProblemCodes.E306);
        Assert.Single(p.Problems, x => x.Code == ProblemCodes.E302);   // 50 remainder after 39+50
        Assert.Equal(
            [BreakTokenKind.Every, BreakTokenKind.Single, BreakTokenKind.Skip, BreakTokenKind.Every],
            p.Tokens.Select(t => t.Kind));
    }

    [Fact]
    public void Equal_parts()
    {
        Assert.Equal([0, 250, 500, 750, 1000], Stations(Parse("0..10+00/4")));
        Assert.Equal([0, 2000], Stations(Parse("START..END/1")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("START, 5+0x")]
    [InlineData("0..1000")]
    [InlineData("0..1000@abc")]
    [InlineData("0..1000@-5")]
    [InlineData("0..1000/0")]
    [InlineData("0..1000/2.5")]
    [InlineData("0..1000@500/2")]
    [InlineData("skip 0..100@50")]
    [InlineData("skip 100")]
    [InlineData("1000..0@100")]
    [InlineData("5+00..5+00/2")]
    public void E300_entries_that_do_not_parse_are_errors(string spec)
    {
        var p = Parse(spec);
        var e = Assert.Single(p.Problems, x => x.Code == ProblemCodes.E300);
        Assert.Equal(Severity.Error, e.Severity);
        Assert.False(string.IsNullOrWhiteSpace(e.Fix));
    }

    [Fact]
    public void A_bad_entry_does_not_hide_the_others()
    {
        var p = Parse("START, 5+0x, END");
        Assert.Equal([0, 2000], Stations(p));
        Assert.Contains("Entry 2", Assert.Single(p.Problems).Message);
    }

    [Fact]
    public void Empty_entries_between_commas_are_ignored()
    {
        var p = Parse("START,, END,");
        Assert.Equal([0, 2000], Stations(p));
        Assert.Empty(p.Problems);
    }

    [Fact]
    public void E302_short_remainder_is_kept_and_reported()
    {
        var p = Parse("START..END@500", Straight(2100));
        Assert.Equal([0, 500, 1000, 1500, 2000, 2100], Stations(p));
        var r = Assert.Single(p.Problems);
        Assert.Equal((ProblemCodes.E302, Severity.Info), (r.Code, r.Severity));
        Assert.Contains("100.00 remainder", r.Message);
        Assert.Contains("kept", r.Message);
    }

    [Fact]
    public void E302_remainder_below_MinSegmentLength_merges_into_the_previous_segment()
    {
        var opts = Feet with { MinSegmentLength = 150 };
        var p = Parse("START..END@500", Straight(2100), opts);
        Assert.Equal([0, 500, 1000, 1500, 2100], Stations(p));
        var r = Assert.Single(p.Problems);
        Assert.Equal((ProblemCodes.E302, Severity.Warning), (r.Code, r.Severity));
        Assert.Contains("removed", r.Message);
    }

    [Fact]
    public void E302_merge_does_not_remove_a_break_another_entry_places()
    {
        var opts = Feet with { MinSegmentLength = 150 };
        var p = Parse("START..END@500, 20+00", Straight(2100), opts);
        Assert.Equal([0, 500, 1000, 1500, 2000, 2100], Stations(p));
        var r = Assert.Single(p.Problems, x => x.Code == ProblemCodes.E302);
        Assert.Equal(Severity.Info, r.Severity);
        Assert.Contains("Another entry", r.Fix);
    }

    [Fact]
    public void E304_overlapping_ranges_are_rejected_and_abutting_ranges_are_not()
    {
        var bad = Parse("0..1000@500, 800..1500@100");
        var e = Assert.Single(bad.Problems, x => x.Code == ProblemCodes.E304);
        Assert.Equal(Severity.Error, e.Severity);
        Assert.Contains("Entries 1", e.Message);
        Assert.Contains("ViewOverlap", e.Fix);
        Assert.Empty(Parse("0..1000@500, 1000..2000/4").Problems);
    }

    [Fact]
    public void E306_unsorted_and_duplicate_breaks_are_sorted_and_deduplicated()
    {
        var p = Parse("13+00, 5+00, START, 5+00, END");
        Assert.Equal([0, 500, 1300, 2000], Stations(p));
        var r = Assert.Single(p.Problems);
        Assert.Equal((ProblemCodes.E306, Severity.Info), (r.Code, r.Severity));
        Assert.Contains("out of order", r.Message);
        Assert.Contains("more than once", r.Message);
    }

    [Fact]
    public void E206_breaks_within_tolerance_merge_into_one()
    {
        Assert.Equal([0, 500, 2000], Stations(Parse("START, 500, 500.0000004, END")));
        Assert.Equal([0, 500.00001, 2000], Stations(Parse("START, 500.00001, END")));
    }

    [Fact]
    public void E208_station_equations_are_honored_and_unresolvable_stations_are_errors()
    {
        // Gap: at raw 1000 stationing jumps to 12+00. Overlap: at raw 1500 it drops back to 15+00 from 17+00.
        var eq = new AlignmentBuilder(new Pt(0, 0), 0).Line(2000).Build("EQ", [new StationEquation(1000, 1200), new StationEquation(1500, 1500)]);

        Assert.Equal([0, 1100, 2000], Stations(Parse("START, 13+00, END", eq)));   // displayed 13+00 is raw 1100

        var gap = Parse("START, 11+00, END", eq);
        var g = Assert.Single(gap.Problems);
        Assert.Equal((ProblemCodes.E208, Severity.Error), (g.Code, g.Severity));
        Assert.Contains("gap", g.Message);

        var amb = Parse("START, 16+00, END", eq);
        var a = Assert.Single(amb.Problems);
        Assert.Equal(ProblemCodes.E208, a.Code);
        Assert.Contains("raw 1400.00", a.Message);
        Assert.Contains("raw 1600.00", a.Message);
    }

    [Fact]
    public void Too_many_breaks_is_an_error()
    {
        var opts = Feet with { MaxBreaks = 10 };
        Assert.Single(Parse("START..END@1", null, opts).Problems, x => x.Code == ProblemCodes.E300);
        Assert.Single(Parse("START..END/50", null, opts).Problems, x => x.Code == ProblemCodes.E300);
    }

    [Fact]
    public void Breaks_snap_to_alignment_geometry_points()
    {
        var c = FakeAlignments.C();
        var p = Parse("START, 314.1592659, 6+14.15926535, END", c);
        Assert.Contains(FakeAlignments.C_PT, Stations(p));
        Assert.Contains(314.1592659, Stations(p));                     // not near a geometry point: kept as typed
    }
}

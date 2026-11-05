using SheetVol.Core.Tests.Fakes;

namespace SheetVol.Core.Tests.Geometry;

/// <summary>Checks the shared fake against closed forms so other tests can trust it.</summary>
public class FakeAlignmentTests
{
    [Fact]
    public void C_has_its_PT_where_the_geometry_says()
    {
        var c = FakeAlignments.C();
        var pt = c.PointAt(FakeAlignments.C_PT, 0);
        Assert.Equal(300 + 300 * Math.Cos(Math.PI / 6), pt.X, 9);
        Assert.Equal(-300 + 300 * Math.Sin(Math.PI / 6), pt.Y, 9);
        Assert.Equal(FakeAlignments.C_PT + 300, c.EndStation, 9);
        Assert.Equal(-Math.PI / 3, c.DirectionAt(FakeAlignments.C_PT + 1), 9);
    }

    [Fact]
    public void Offsets_are_negative_left()
    {
        var s = FakeAlignments.S();
        Assert.Equal(new Pt(250, 100), s.PointAt(250, -100));
        Assert.Equal(new Pt(250, -100), s.PointAt(250, 100));
    }

    [Fact]
    public void SP_entry_spiral_matches_the_clothoid_series()
    {
        // Entry spiral from TS (200, 0) heading east, turning right; A^2 = R * Ls = 500 * 150.
        var sp = FakeAlignments.SP();
        double a2 = 500.0 * 150, l = 150;
        double x = l - Math.Pow(l, 5) / (40 * a2 * a2) + Math.Pow(l, 9) / (3456 * Math.Pow(a2, 4)) - Math.Pow(l, 13) / (599040 * Math.Pow(a2, 6));
        double y = Math.Pow(l, 3) / (6 * a2) - Math.Pow(l, 7) / (336 * Math.Pow(a2, 3)) + Math.Pow(l, 11) / (42240 * Math.Pow(a2, 5)) - Math.Pow(l, 15) / (9676800 * Math.Pow(a2, 7));
        var p = sp.PointAt(350, 0);
        Assert.Equal(200 + x, p.X, 1e-8);
        Assert.Equal(-y, p.Y, 1e-8);
        Assert.Equal(-150.0 / (2 * 500), sp.DirectionAt(350 - 1e-12), 9);
    }

    [Fact]
    public void PointAt_outside_the_alignment_throws()
    {
        var s = FakeAlignments.S();
        Assert.Throws<ArgumentOutOfRangeException>(() => s.PointAt(2000.5, 0));
    }

    [Theory]
    [InlineData(0, "0+00.00")]
    [InlineData(500, "5+00.00")]
    [InlineData(1234.567, "12+34.57")]
    [InlineData(499.996, "5+00.00")]
    [InlineData(-50, "-0+50.00")]
    public void Formats_stations(double raw, string expected)
    {
        var s = new AlignmentBuilder(new Pt(0, 0), 0, -100).Line(2000).Build("S");
        Assert.Equal(expected, s.FormatStation(raw));
    }

    [Theory]
    [InlineData("5+00", 500)]
    [InlineData("500", 500)]
    [InlineData("12+34.5", 1234.5)]
    [InlineData("-0+50", -50)]
    [InlineData("25+00", 2500)]   // outside: extrapolated so the planner can report it
    public void Parses_stations(string text, double raw)
    {
        var s = new AlignmentBuilder(new Pt(0, 0), 0, -100).Line(2000).Build("S");
        Assert.True(s.TryParseStation(text, out double r, out var err), err);
        Assert.Equal(raw, r, 9);
    }

    [Fact]
    public void Station_equations_shift_gap_and_overlap()
    {
        // Gap: at raw 1000 the stationing jumps ahead to 12+00. Overlap: at raw 1500 it drops back to 15+00 from 17+00.
        var s = new AlignmentBuilder(new Pt(0, 0), 0).Line(2000).Build("S",
            [new StationEquation(1000, 1200), new StationEquation(1500, 1500)]);
        Assert.Equal("13+00.00", s.FormatStation(1100));
        Assert.True(s.TryParseStation("13+00", out double r, out _));
        Assert.Equal(1100, r, 9);
        Assert.False(s.TryParseStation("11+00", out _, out var gap));
        Assert.Contains("gap", gap);
        Assert.False(s.TryParseStation("16+00", out _, out var amb));
        Assert.Contains("raw 1400.00", amb);
        Assert.Contains("raw 1600.00", amb);
    }
}

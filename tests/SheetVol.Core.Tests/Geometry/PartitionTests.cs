using System.Globalization;
using SheetVol.Core.Geometry;
using SheetVol.Core.Planning;
using SheetVol.Core.Tests.Fakes;

namespace SheetVol.Core.Tests.Geometry;

/// <summary>
/// Partition-check geometry (6.4 step 2, 6.5, T10): segments and the whole band take their
/// lateral vertices from one station list, so the segment areas sum to the band area to
/// rounding, on every fixture alignment.
/// </summary>
public class PartitionTests
{
    private static readonly PlanningOptions Feet = PlanningOptions.ForUnitScale(1);

    internal static FakeAlignment Fixture(string name) => name switch
    {
        "S" => FakeAlignments.S(),
        "C" => FakeAlignments.C(),
        "K" => FakeAlignments.K(),
        "SP" => FakeAlignments.SP(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static readonly string Pt = FakeAlignments.C_PT.ToString("R", CultureInfo.InvariantCulture);

    public static TheoryData<string, string, double, double> Cases => new()
    {
        { "S", "START..END@500", -100, 100 },
        { "S", "0, 500, 1300, 1800, 2000", -30, 15 },
        { "C", $"START, 1+00, 3+00, 4+50, {Pt}, 7+00, END", -100, 100 },
        { "C", "START, 3+00, 6+14.159, END", -50, 150 },
        { "C", "START..END/7", 20, 120 },                 // E209: band entirely right
        { "C", "START..END@250", -120, -20 },             // E209: band entirely left
        { "K", "START, 5+00, 10+00, 15+00, END", -100, 100 },   // a break on the angle point
        { "K", "START, 7+00, 13+00, END", -80, 40 },
        { "SP", "START, 1+00, 2+50, 3+50, 4+00, 6+00, 7+50, END", -100, 150 },
        { "SP", "START..END@100", -200, -50 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Segment_areas_sum_to_the_band_area(string name, string spec, double oL, double oR)
    {
        var a = Fixture(name);
        var plan = new Planner(Feet).Plan(TestRuns.Stations(a, spec, oL, oR), a);
        Assert.True(plan.IsValid, string.Join(" | ", plan.Problems.Select(p => p.Message)));
        Assert.Equal(plan.Segments.Count, plan.Boundaries.Count);
        var band = Assert.Single(plan.Band);
        Assert.Equal(plan.Segments.Select(s => s.Id), band.SegmentIds);
        Assert.Null(BoundaryValidator.Check(band.Tessellated, requireCounterClockwise: true, minDistinctVertices: 4));
        Assert.True(plan.PartitionCheckApplicable);

        // Segments and band share one vertex array, so this sum holds by construction; it guards
        // the ring indexing. The closed-form checks below are the independent ones.
        double bandArea = Polygon.SignedArea(band.Tessellated);
        double sum = plan.Boundaries.Sum(b => Polygon.SignedArea(b.Tessellated));
        Assert.InRange(Math.Abs(sum - bandArea) / bandArea, 0, 1e-9);

        double tol = Feet.Geometry.MidOrdinate;
        double bandExact = Analytic.BandArea(a, band.StartStation, band.EndStation, oL, oR);
        Assert.Equal(bandExact, bandArea, tol * Analytic.Perimeter(band.Tessellated) + 1e-6);

        // Each segment against the closed-form band area, within the tessellation tolerance.
        foreach (var (seg, b) in plan.Segments.Zip(plan.Boundaries))
        {
            double exact = Analytic.BandArea(a, seg.StartStation, seg.EndStation, oL, oR);
            Assert.Equal(exact, Polygon.SignedArea(b.Tessellated), tol * Analytic.Perimeter(b.Tessellated) + 1e-6);
        }
    }

    [Fact]
    public void Skip_gap_splits_the_band_and_the_sum_still_holds()
    {
        var s = FakeAlignments.S();
        var plan = new Planner(Feet).Plan(TestRuns.Stations(s, "START..10+00@500, skip 10+00..12+00, 12+00..END@400"), s);
        Assert.Equal(2, plan.Band.Count);
        Assert.Equal(1000, plan.Band[0].EndStation);
        Assert.Equal(1200, plan.Band[1].StartStation);
        double bands = plan.Band.Sum(b => Polygon.SignedArea(b.Tessellated));
        double segs = plan.Boundaries.Sum(b => Polygon.SignedArea(b.Tessellated));
        Assert.Equal(1800 * 200, bands, 6);
        Assert.InRange(Math.Abs(segs - bands) / bands, 0, 1e-9);
    }

    [Fact]
    public void Band_and_segments_hold_at_state_plane_coordinates()
    {
        // C moved to 3,100,000 E / 13,800,000 N: local-origin arithmetic keeps the sum exact and the area unchanged.
        var far = new AlignmentBuilder(new Pt(3_100_000, 13_800_000), 0).Line(300).Arc(300, Math.PI / 3, clockwise: true).Line(300).Build("C-far");
        var near = FakeAlignments.C();
        string spec = $"START, 3+00, {Pt}, END";
        var pf = new Planner(Feet).Plan(TestRuns.Stations(far, spec), far);
        var pn = new Planner(Feet).Plan(TestRuns.Stations(near, spec), near);
        double bandFar = Polygon.SignedArea(Assert.Single(pf.Band).Tessellated);
        double bandNear = Polygon.SignedArea(Assert.Single(pn.Band).Tessellated);
        Assert.InRange(Math.Abs(bandFar / bandNear - 1), 0, 1e-9);
        Assert.InRange(Math.Abs(pf.Boundaries.Sum(b => Polygon.SignedArea(b.Tessellated)) / bandFar - 1), 0, 1e-9);
    }
}

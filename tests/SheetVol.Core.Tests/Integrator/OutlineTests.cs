// Rev 3 (NOW.md D13, rev3-integrator): ImpactResult.Outlines by edge cancellation over the step 5
// pieces, and the depth overloads for a volume-surface source (contracts ITriangleSource note).
//
// Outline rules checked here: closed rings (first == last, world coordinates), outer rings
// counter-clockwise and holes clockwise, collinear vertices merged, and the signed ring areas sum to
// ImpactedArea within 1e-9 relative.

using SheetVol.Core.Tests.Fakes;
using SheetVol.Core.Volumes.Integrator;
using SheetVol.Core.Volumes.Integrator.Fixtures;
using Xunit.Abstractions;

namespace SheetVol.Core.Tests.Integrator;

public class OutlineTests(ITestOutputHelper output)
{
    private const double Rel = 1e-9;
    private static readonly FakeAlignment S = FakeAlignments.S();

    /// <summary>Signed shoelace area of a closed ring (first == last), relative to its first vertex: positive counter-clockwise.</summary>
    internal static double RingArea(IReadOnlyList<Pt> ring)
    {
        double a2 = 0, x0 = ring[0].X, y0 = ring[0].Y;
        for (int i = 0; i + 1 < ring.Count; i++)
            a2 += (ring[i].X - x0) * (ring[i + 1].Y - y0) - (ring[i].Y - y0) * (ring[i + 1].X - x0);
        return 0.5 * a2;
    }

    private static ImpactResult Ok(SegmentImpactResult r)
    {
        Assert.True(r.Succeeded, string.Join("; ", r.Problems.Select(p => p.Code + " " + p.Message)));
        return r.Impact!;
    }

    /// <summary>Every ring closed, at least 3 distinct vertices, and the signed areas summing to ImpactedArea.</summary>
    private void AssertRingsMatchArea(ImpactResult i, string what)
    {
        foreach (var ring in i.Outlines)
        {
            Assert.True(ring.Count >= 4, $"{what}: ring with {ring.Count} points");
            Assert.Equal(ring[0], ring[^1]);
        }
        double sum = i.Outlines.Sum(RingArea);
        output.WriteLine($"{what}: {i.Outlines.Count} ring(s), vertices {string.Join("/", i.Outlines.Select(r => r.Count - 1))}, ring area sum {sum:R}, ImpactedArea {i.ImpactedArea:R}");
        C5.Rel(i.ImpactedArea, sum, Rel, what: $"{what}: ring area sum vs ImpactedArea");
    }

    private static void AssertVertex(Pt expected, Pt actual, double tol, string what) =>
        Assert.True(Math.Abs(actual.X - expected.X) <= tol && Math.Abs(actual.Y - expected.Y) <= tol,
            $"{what}: expected ({expected.X:R}, {expected.Y:R}), got ({actual.X:R}, {actual.Y:R})");

    private static SegmentImpactResult Hcfcd(double s0, double s1, double z, double offL = -100, double offR = 100) =>
        OverlayIntegrator.IntegrateSegment(ImpactTests.FlatBase(z), FixtureSurfaces.FgH(), S, C5.Seg(s0, s1, offL, offR),
            FakeBands.Band(S, s0, s1, offL, offR), "SEDIMENT");

    // ---------------------------------------------------------------------------------------
    // One rectangular cut: OHWM_100 vs FG_H on [0, 500] cuts |y| < 22 (ImpactTests T25), so the
    // outline is the rectangle (0, -22), (500, -22), (500, 22), (0, 22), counter-clockwise from the
    // lowest-x vertex, closed: 5 points, area 44 * 500 = 22,000.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Single_rectangular_cut_is_one_four_vertex_ring()
    {
        var i = Ok(Hcfcd(0, 500, 100.0));
        var ring = Assert.Single(i.Outlines);
        Assert.Equal(5, ring.Count);
        Pt[] corners = [new(0, -22), new(500, -22), new(500, 22), new(0, 22), new(0, -22)];
        for (int k = 0; k < 5; k++) AssertVertex(corners[k], ring[k], 1e-9, $"vertex {k}");
        C5.Rel(22000, RingArea(ring), Rel, what: "ring area");
        AssertRingsMatchArea(i, "single cut");
    }

    // ---------------------------------------------------------------------------------------
    // E1005 analogue: two disjoint cut stretches (C5Fixtures.TwoStretches, depth > 0 on x in (90, 310)
    // and (590, 810)). Each stretch is one ring of 6 vertices: x = 90 on the bed, the ramp-in diagonals
    // to (100, +-16), |y| = 16 to x = 310, x = 310 across. Area per stretch: ramp in 200 + 2 * 30, full
    // 200 * 32 = 6,400, ramp out 200 + 2 * 60 = 6,980; both 13,960.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Two_disjoint_stretches_give_two_rings()
    {
        var (b, c) = C5Fixtures.TwoStretches();
        var i = Ok(OverlayIntegrator.IntegrateSegment(b, c, S, C5.Seg(0, 1000), FakeBands.Band(S, 0, 1000, -100, 100), "SEDIMENT"));
        Assert.Equal(2, i.Outlines.Count);
        C5.Rel(13960, i.ImpactedArea, Rel, what: "impacted area");
        var byX = i.Outlines.OrderBy(r => r.Min(p => p.X)).ToList();
        foreach (var (ring, x0, x1) in new[] { (byX[0], 90.0, 310.0), (byX[1], 590.0, 810.0) })
        {
            Assert.Equal(7, ring.Count);
            C5.Rel(6980, RingArea(ring), Rel, what: $"ring {x0}..{x1} area");
            C5.Rel(x0, ring.Min(p => p.X), Rel); C5.Rel(x1, ring.Max(p => p.X), Rel);
            C5.Rel(-16, ring.Min(p => p.Y), Rel); C5.Rel(16, ring.Max(p => p.Y), Rel);
            Pt[] expected = [new(x0, -10), new(x0 + 10, -16), new(x1, -16), new(x1, 16), new(x0 + 10, 16), new(x0, 10)];
            for (int k = 0; k < 6; k++) AssertVertex(expected[k], ring[k], 1e-9, $"ring {x0}..{x1} vertex {k}");
        }
        AssertRingsMatchArea(i, "two stretches");
    }

    // ---------------------------------------------------------------------------------------
    // A cut with an island of no cut: base flat 100 over [0, 1000] x [-100, 100]; comparison on grid
    // x = 0, 400, 600, 1000, y = -100, -50, 50, 100 at 99 except the four island corners
    // (400 or 600, -50 or 50) at 100. Depth is 1 at the outer nodes and 0 at the island corners, so
    // the island cell [400, 600] x [-50, 50] is flat at d = 0 (no cut) and every other cell is cut
    // except on its edge with the island. Outline: the band rectangle counter-clockwise (+200,000) and
    // the island clockwise (-20,000); ImpactedArea 180,000. The cut reaches the lateral edges (E1002).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Island_of_no_cut_gives_an_outer_ring_and_a_hole()
    {
        var b = FixtureSurfaces.Flat("B", 100, 0, -100, 1000, 100);
        static bool Island(double x, double y) => (x == 400 || x == 600) && (y == -50 || y == 50);
        var c = FixtureSurfaces.Grid("C", [0, 400, 600, 1000], [-100, -50, 50, 100], (x, y) => Island(x, y) ? 100 : 99,
                                     GridDiagonal.SouthwestToNortheast);
        var i = Ok(OverlayIntegrator.IntegrateSegment(b, c, S, C5.Seg(0, 1000), FakeBands.Band(S, 0, 1000, -100, 100), "SEDIMENT"));
        C5.Rel(180000, i.ImpactedArea, Rel, what: "impacted area");
        Assert.True(i.TouchesSearchLimit);
        Assert.Equal(2, i.Outlines.Count);
        var outer = i.Outlines[0];
        var hole = i.Outlines[1];
        Assert.Equal(5, outer.Count);
        Assert.Equal(5, hole.Count);
        C5.Rel(200000, RingArea(outer), Rel, what: "outer ring area (counter-clockwise)");
        C5.Rel(-20000, RingArea(hole), Rel, what: "hole area (clockwise)");
        Pt[] holeCorners = [new(400, -50), new(400, 50), new(600, 50), new(600, -50), new(400, -50)];
        for (int k = 0; k < 5; k++) AssertVertex(holeCorners[k], hole[k], 1e-9, $"hole vertex {k}");
        AssertRingsMatchArea(i, "island");
    }

    // ---------------------------------------------------------------------------------------
    // Ring areas sum to ImpactedArea on the existing impact cases, including a curved band (E1008:
    // the ring is the arc band itself), the pond (two separate regions), the transition segment,
    // Fixture H rotated and moved to state-plane coordinates, and a MinCutDepth trim.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Ring_areas_sum_to_impacted_area_on_every_impact_case()
    {
        AssertRingsMatchArea(Ok(Hcfcd(500, 1000, 100.0)), "T25 [500, 1000]");
        AssertRingsMatchArea(Ok(Hcfcd(0, 500, 97.0)), "T26");
        AssertRingsMatchArea(Ok(Hcfcd(500, 1000, 95.0)), "T27");
        AssertRingsMatchArea(Ok(Hcfcd(1000, 1500, 100.0)), "T28");
        AssertRingsMatchArea(Ok(Hcfcd(0, 500, 100.0, -12, 12)), "T30");

        var (pb, pc) = C5Fixtures.ChannelAndPond(1.0);
        var pond = Ok(OverlayIntegrator.IntegrateSegment(pb, pc, S, C5.Seg(0, 1000), FakeBands.Band(S, 0, 1000, -100, 100), "SEDIMENT"));
        Assert.Equal(2, pond.Outlines.Count);
        AssertRingsMatchArea(pond, "channel and pond");

        var arc = FakeAlignments.C();
        double s0 = 300, s1 = FakeAlignments.C_PT;
        var arcBand = FakeBands.Band(arc, s0, s1, 50, 100);
        var curved = Ok(OverlayIntegrator.IntegrateSegment(FixtureSurfaces.Eg(), FixtureSurfaces.Flat("FG99", 99, -200, -300, 2200, 300),
            arc, new SegmentDef(Guid.NewGuid(), "arc", s0, s1, 50, 100), arcBand, "SEDIMENT"));
        var ring = Assert.Single(curved.Outlines);
        C5.Rel(FakeBands.Area(arcBand), RingArea(ring), Rel, what: "curved band ring vs band polygon");
        Assert.Equal(arcBand.Count, ring.Count);   // every tessellation vertex kept, no extra vertex
        AssertRingsMatchArea(curved, "curved band (E1008)");

        double a = 17 * Math.PI / 180, dx = 3_000_000 + 1234.567, dy = 10_000_000 - 890.123;
        var al = new AlignmentBuilder(new Pt(dx, dy), a).Line(2000).Build("S_rot");
        var rotated = Ok(OverlayIntegrator.IntegrateSegment(new C5RotatedSource(ImpactTests.FlatBase(100), a, dx, dy),
            new C5RotatedSource(FixtureSurfaces.FgH(maxCellLength: 50), a, dx, dy), al, C5.Seg(1000, 1500), FakeBands.Band(al, 1000, 1500, -100, 100), "SEDIMENT"));
        C5.Rel(22000, RingArea(Assert.Single(rotated.Outlines)), Rel, what: "rotated state-plane ring area");
        AssertRingsMatchArea(rotated, "T28 rotated 17 degrees at state-plane coordinates");

        var trimmed = Ok(OverlayIntegrator.IntegrateSegment(FixtureSurfaces.EgH(), FixtureSurfaces.FgH(), S, C5.Seg(0, 500),
            FakeBands.Band(S, 0, 500, -100, 100), "SEDIMENT", new IntegratorSettings(MinCutDepth: 0.5)));
        AssertRingsMatchArea(trimmed, "EG_H vs FG_H, MinCutDepth 0.5");
    }

    // ---------------------------------------------------------------------------------------
    // Depth overloads (volume-surface source). EG_H and FG_H are built on the same grid
    // (FixtureSurfaces.Grid, same lines and diagonal rule), so their triangle lists match vertex for
    // vertex in plan and the depth TIN EG_H - FG_H, vertex-wise, is exactly the pair's depth on every
    // triangle. The depth path must reproduce the pair path within 1e-9 relative.
    // ---------------------------------------------------------------------------------------

    private static TriangleListSource DepthEgHMinusFgH()
    {
        var eg = FixtureSurfaces.EgH().All;
        var fg = FixtureSurfaces.FgH().All;
        Assert.Equal(eg.Count, fg.Count);
        var tris = new List<Tri3>(eg.Count);
        for (int k = 0; k < eg.Count; k++)
        {
            Pt3 D(Pt3 e, Pt3 f)
            {
                Assert.True(e.X == f.X && e.Y == f.Y, $"triangle {k}: EG_H and FG_H vertices differ in plan");
                return new Pt3(e.X, e.Y, e.Z - f.Z);
            }
            tris.Add(new Tri3(D(eg[k].A, fg[k].A), D(eg[k].B, fg[k].B), D(eg[k].C, fg[k].C)));
        }
        return new TriangleListSource("D_H", tris);
    }

    [Theory]
    [InlineData(0, 500, -100, 100)]
    [InlineData(1000, 1500, -100, 100)]
    [InlineData(0, 2000, -100, 100)]
    [InlineData(0, 500, -12, 12)]
    public void Depth_source_EG_H_minus_FG_H_matches_the_pair_path(double s0, double s1, double offL, double offR)
    {
        var seg = C5.Seg(s0, s1, offL, offR);
        var band = FakeBands.Band(S, s0, s1, offL, offR);
        var pair = OverlayIntegrator.IntegrateSegment(FixtureSurfaces.EgH(), FixtureSurfaces.FgH(), S, seg, band, "SEDIMENT");
        var depth = OverlayIntegrator.IntegrateSegment(DepthEgHMinusFgH(), S, seg, band, "SEDIMENT");
        var p = Ok(pair);
        var d = Ok(depth);
        output.WriteLine($"[{s0}, {s1}] ({offL}, {offR}): pair cut {pair.Band!.Cut:R} fill {pair.Band.Fill:R} area {p.ImpactedArea:R} length {p.ImpactedLength:R} rings {p.Outlines.Count}" +
                         $" | depth cut {depth.Band!.Cut:R} fill {depth.Band.Fill:R} area {d.ImpactedArea:R} length {d.ImpactedLength:R} rings {d.Outlines.Count}");
        C5.Rel(pair.Band.Cut, depth.Band.Cut, Rel, what: "band cut");
        C5.Rel(pair.Band.Fill, depth.Band.Fill, Rel, what: "band fill");
        C5.Rel(pair.Band.PlanArea, depth.Band.PlanArea, Rel, what: "band plan area");
        C5.Rel(pair.BaseArea, depth.BaseArea, Rel, what: "base area");
        C5.Rel(p.ImpactedArea, d.ImpactedArea, Rel, what: "impacted area");
        C5.Rel(p.ImpactedLength, d.ImpactedLength, Rel, what: "impacted length");
        C5.Rel(p.CutVolume, d.CutVolume, Rel, what: "cut volume");
        Assert.Equal(p.TouchesSearchLimit, d.TouchesSearchLimit);
        Assert.Equal(p.Intervals.Count, d.Intervals.Count);
        for (int k = 0; k < p.Intervals.Count; k++)
        {
            C5.Rel(p.Intervals[k].From, d.Intervals[k].From, Rel); C5.Rel(p.Intervals[k].To, d.Intervals[k].To, Rel);
        }
        Assert.Equal(p.Outlines.Count, d.Outlines.Count);
        double tol = Rel * Math.Max(s1 - s0, offR - offL);
        for (int r = 0; r < p.Outlines.Count; r++)
        {
            Assert.Equal(p.Outlines[r].Count, d.Outlines[r].Count);
            for (int k = 0; k < p.Outlines[r].Count; k++) AssertVertex(p.Outlines[r][k], d.Outlines[r][k], tol, $"ring {r} vertex {k}");
        }
        AssertRingsMatchArea(d, "depth path");
        Assert.Equal(pair.Problems.Select(x => x.Code), depth.Problems.Select(x => x.Code));

        var bandPair = OverlayIntegrator.IntegrateBand(FixtureSurfaces.EgH(), FixtureSurfaces.FgH(), band).Totals!;
        var bandDepth = OverlayIntegrator.IntegrateBand(DepthEgHMinusFgH(), band).Totals!;
        C5.Rel(bandPair.Cut, bandDepth.Cut, Rel, what: "IntegrateBand cut");
        C5.Rel(bandPair.Fill, bandDepth.Fill, Rel, what: "IntegrateBand fill");
        C5.Rel(bandPair.PlanArea, bandDepth.PlanArea, Rel, what: "IntegrateBand plan area");
    }

    // ---------------------------------------------------------------------------------------
    // A flat depth pair at d = 2 over a 100 x 100 boundary: cut 2 * 10,000 = 20,000, fill 0, plan
    // area 10,000. As a segment on S [0, 100], offsets (-50, +50): impacted area 10,000, cut 20,000,
    // length 100, one 4-vertex ring (the boundary), and the cut reaches both lateral edges (E1002).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Flat_depth_pair_at_2_over_100_by_100_gives_cut_20000_and_fill_0()
    {
        var depth = FixtureSurfaces.Flat("D2", 2.0, 0, -50, 100, 50);
        var band = OverlayIntegrator.IntegrateBand(depth, FakeBands.Rect(0, -50, 100, 50));
        Assert.True(band.Succeeded);
        var t = band.Totals!;
        output.WriteLine($"flat d = 2: cut {t.Cut:R} fill {t.Fill:R} plan {t.PlanArea:R}");
        C5.Rel(20000, t.Cut, Rel, what: "cut");
        Assert.Equal(0.0, t.Fill);
        C5.Rel(10000, t.PlanArea, Rel, what: "plan area");
        Assert.Empty(band.Problems);

        var r = OverlayIntegrator.IntegrateSegment(depth, S, C5.Seg(0, 100, -50, 50), FakeBands.Band(S, 0, 100, -50, 50), "SEDIMENT");
        var i = Ok(r);
        C5.Rel(20000, r.Band!.Cut, Rel, what: "segment band cut");
        C5.Rel(20000, i.CutVolume, Rel, what: "impact cut");
        C5.Rel(10000, i.ImpactedArea, Rel, what: "impacted area");
        C5.Rel(100, i.ImpactedLength, Rel, what: "impacted length");
        C5.Rel(10000, r.BaseArea, Rel, what: "base area (depth triangles)");
        Assert.True(i.TouchesSearchLimit);
        Assert.Equal(5, Assert.Single(i.Outlines).Count);
        AssertRingsMatchArea(i, "flat depth");
    }

    [Fact]
    public void Empty_depth_source_is_an_error_with_no_numbers()
    {
        var r = OverlayIntegrator.IntegrateSegment(new C5EmptySource(), S, C5.Seg(0, 500), FakeBands.Band(S, 0, 500, -100, 100), "SEDIMENT");
        Assert.False(r.Succeeded);
        var p = Assert.Single(r.Problems);
        Assert.Equal(IntegratorCodes.EmptySurface, p.Code);
        Assert.Contains("volume surface", p.Message);
        Assert.False(OverlayIntegrator.IntegrateBand(new C5EmptySource(), FakeBands.Rect(0, -50, 100, 50)).Succeeded);
    }
}

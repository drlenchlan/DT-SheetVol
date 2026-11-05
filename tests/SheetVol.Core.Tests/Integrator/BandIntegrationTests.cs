// T29 at the Core level: integrator band cut and fill on Fixtures A, B, D, H with no OHWM limit
// equal the analytic values from HANDOFF.md section 8 within 1e-9 relative. Plus coverage (E1011),
// no-overlap (E405), empty surface (E403) and invalid boundaries (E202).
//
// Units: ft, ft2, ft3 (drawing units). 1 CY = 27 ft3.

using SheetVol.Core.Tests.Fakes;
using SheetVol.Core.Volumes.Integrator;
using SheetVol.Core.Volumes.Integrator.Fixtures;

namespace SheetVol.Core.Tests.Integrator;

public class BandIntegrationTests
{
    private const double Rel = 1e-9;

    private static BandTotals Band(ITriangleSource b, ITriangleSource c, IReadOnlyList<Pt> boundary)
    {
        var r = OverlayIntegrator.IntegrateBand(b, c, boundary);
        Assert.True(r.Succeeded, string.Join("; ", r.Problems.Select(p => p.Code + " " + p.Message)));
        return r.Totals!;
    }

    private static readonly FakeAlignment S = FakeAlignments.S();
    private static readonly FakeAlignment C = FakeAlignments.C();

    // ---------------------------------------------------------------------------------------
    // Fixture A: pyramid volume = 8100 * h / 3. h = 1.0 -> 2700 ft3 = 100 CY; h = 0.5 -> 1350 = 50 CY.
    // P1 (x 125) cut 2700, P2 (375) fill 1350, P3 (1125) cut 2700, P4 (1375) fill 1350, P5 (1750) fill 2700.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 500, 2700, 1350)]       // T1 row 1: C 100, F 50 CY
    [InlineData(500, 1000, 0, 0)]          // T1 row 2
    [InlineData(1000, 1500, 2700, 1350)]   // T1 row 3
    [InlineData(1500, 2000, 0, 2700)]      // T1 row 4: F 100 CY
    [InlineData(500, 1300, 2700, 0)]       // T2 row 2: P3 only
    [InlineData(1300, 1800, 0, 4050)]      // T2 row 3: P4 + P5 = 150 CY fill
    [InlineData(1800, 2000, 0, 0)]         // T2 row 4
    [InlineData(0, 125, 1350, 0)]          // T3: break through P1's apex, half of P1 = 50 CY
    [InlineData(125, 500, 1350, 1350)]     // T3: other half of P1 + P2
    public void T29_FixtureA_on_S_matches_pyramid_volumes(double s0, double s1, double cut, double fill)
    {
        var t = Band(FixtureSurfaces.Eg(), FixtureSurfaces.FgA(), FakeBands.Band(S, s0, s1, -100, 100));
        C5.Rel(cut, t.Cut, Rel, what: "cut");
        C5.Rel(fill, t.Fill, Rel, what: "fill");
        // T23: 2D area = length * 200, coverage 100%.
        C5.Rel((s1 - s0) * 200, t.PlanArea, Rel, what: "plan area");
        C5.Rel(1.0, t.Coverage, Rel, what: "coverage");
    }

    [Fact]
    public void T29_FixtureA_T4_lateral_edge_through_both_apexes()
    {
        // Offsets (-100, 0): left half (+y) only; the edge y = 0 passes through P1 and P2's apexes.
        // Half of P1 = 1350 cut (50 CY), half of P2 = 675 fill (25 CY).
        var t = Band(FixtureSurfaces.Eg(), FixtureSurfaces.FgA(), FakeBands.Band(S, 0, 500, -100, 0));
        C5.Rel(1350, t.Cut, Rel, what: "cut");
        C5.Rel(675, t.Fill, Rel, what: "fill");
    }

    [Fact]
    public void T29_FixtureA_partition_sums_to_the_whole_band()
    {
        double cut = 0, fill = 0, area = 0;
        foreach (var (a, b) in new[] { (0.0, 500.0), (500.0, 1000.0), (1000.0, 1500.0), (1500.0, 2000.0) })
        {
            var t = Band(FixtureSurfaces.Eg(), FixtureSurfaces.FgA(), FakeBands.Band(S, a, b, -100, 100));
            cut += t.Cut; fill += t.Fill; area += t.PlanArea;
        }
        var whole = Band(FixtureSurfaces.Eg(), FixtureSurfaces.FgA(), FakeBands.Band(S, 0, 2000, -100, 100));
        C5.Rel(5400, whole.Cut, Rel); C5.Rel(5400, whole.Fill, Rel);   // 200 CY cut, 200 CY fill (G1 probe totals)
        C5.Rel(whole.Cut, cut, Rel); C5.Rel(whole.Fill, fill, Rel); C5.Rel(whole.PlanArea, area, Rel);
    }

    [Fact]
    public void T29_FixtureA_on_C_first_tangent_holds_P1()
    {
        // Alignment C stations 0..300 is the tangent x = 0..300; P1 (x 80..170) is inside, P2 (330..420) is not.
        var t = Band(FixtureSurfaces.Eg(), FixtureSurfaces.FgA(), FakeBands.Band(C, 0, 300, -100, 100));
        C5.Rel(2700, t.Cut, Rel, what: "cut");
        C5.Rel(0, t.Fill, Rel, what: "fill");
        C5.Rel(60000, t.PlanArea, Rel, what: "plan area");
    }

    [Fact]
    public void FixtureA_on_C_arc_with_curved_edge_through_P2_agrees_with_brute_force_sampling()
    {
        // Offsets (-100, 0) on the arc: the band is radius 300..400 around (300,-300). The arc r = 300
        // cuts through P2, so only the part of P2 with r >= 300 counts. Independent check: midpoint
        // sampling of the analytic pyramid over P2's square with analytic membership r >= 300
        // (every P2 point is within the arc's 30..90 degree sweep and inside r = 400).
        var boundary = FakeBands.Band(C, 300, FakeAlignments.C_PT, -100, 0, curveStep: 1.0);
        var t = Band(FixtureSurfaces.Eg(), FixtureSurfaces.FgA(), boundary);

        const double h = 0.1;
        double sampled = 0;
        for (double x = 330 + h / 2; x < 420; x += h)
            for (double y = -45 + h / 2; y < 45; y += h)
            {
                double r = Math.Sqrt((x - 300) * (x - 300) + (y + 300) * (y + 300));
                if (r < 300) continue;
                double zc = 100 + 0.5 * (1 - Math.Max(Math.Abs(x - 375), Math.Abs(y)) / 45);
                sampled += (zc - 100) * h * h;
            }
        Assert.InRange(sampled, 100, 1300);                 // a real partial pyramid, not all or nothing
        C5.Rel(sampled, t.Fill, 2e-3, what: "fill vs sampling");
        C5.Rel(0, t.Cut, Rel, what: "cut");
    }

    // ---------------------------------------------------------------------------------------
    // Fixture B: ridge cross-section 30 ft2 -> 500 * 30 = 15,000 ft3 = 555.556 CY per 500 ft.
    // T6: offsets (-30, +15): section from y = -15 (right side, z 100.5) to y = +30 (left, z 100):
    // 0..30 left gives 15 ft2, 0..15 right gives 15 * (1 + 0.5) / 2 = 11.25 ft2; 26.25 * 500 = 13,125 ft3 = 486.111 CY.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 500)]
    [InlineData(500, 1000)]
    [InlineData(1000, 1500)]
    [InlineData(1500, 2000)]
    public void T29_FixtureB_T5_fill_555_556_CY_per_500_ft(double s0, double s1)
    {
        var t = Band(FixtureSurfaces.Eg(), FixtureSurfaces.FgB(), FakeBands.Band(S, s0, s1, -100, 100));
        C5.Rel(15000, t.Fill, Rel, what: "fill");
        C5.Rel(0, t.Cut, Rel, what: "cut");
        Assert.Equal(555.556, t.Fill / C5.CubicFeetPerCubicYard, 3);
    }

    [Fact]
    public void T29_FixtureB_T6_asymmetric_offsets()
    {
        var t = Band(FixtureSurfaces.Eg(), FixtureSurfaces.FgB(), FakeBands.Band(S, 0, 500, -30, 15));
        C5.Rel(13125, t.Fill, Rel, what: "fill");
        Assert.Equal(486.111, t.Fill / C5.CubicFeetPerCubicYard, 3);
    }

    // ---------------------------------------------------------------------------------------
    // Fixture D: fill = plan area * 1.00.
    // T7: arc segment [300, 300 + 100 pi], offsets +-100: annular sector radii 200..400 over 60 degrees
    //     = (pi/6)(400^2 - 200^2) = 20,000 pi = 62,831.85 ft2 -> 2,327.106 CY.
    // T8: offsets (-50, +150): radii 150..350 -> (pi/6)(350^2 - 150^2) = 52,359.88 ft2 -> 1,939.255 CY.
    // The tessellated boundary is a polygon, so the integrator must equal ITS area to 1e-9 and the
    // analytic sector to the handoff's 2e-4 tessellation tolerance.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void T29_FixtureD_on_S_fill_equals_plan_area()
    {
        var t = Band(FixtureSurfaces.Eg(), FixtureSurfaces.FgD(), FakeBands.Band(S, 0, 500, -100, 100));
        C5.Rel(100000, t.Fill, Rel); C5.Rel(0, t.Cut, Rel);
    }

    [Theory]
    [InlineData(-100, 100, 2327.106)]   // T7
    [InlineData(-50, 150, 1939.255)]    // T8
    public void T29_FixtureD_on_C_arc_fill_equals_boundary_area(double offL, double offR, double expectedCy)
    {
        var boundary = FakeBands.Band(C, 300, FakeAlignments.C_PT, offL, offR);
        var t = Band(FixtureSurfaces.Eg(), FixtureSurfaces.FgD(), boundary);
        double polyArea = FakeBands.Area(boundary);
        C5.Rel(polyArea, t.Fill, Rel, what: "fill vs polygon area");
        C5.Rel(polyArea, t.PlanArea, Rel, what: "plan area vs polygon area");
        C5.Rel(polyArea, t.BoundaryArea, 1e-12, what: "boundary area");
        double rOut = 300 - offL, rIn = 300 - offR;
        C5.Rel(Math.PI / 6 * (rOut * rOut - rIn * rIn), t.Fill, 2e-4, what: "fill vs analytic sector");
        C5.Rel(expectedCy, t.Fill / C5.CubicFeetPerCubicYard, 2e-4, what: "fill CY vs handoff");
        C5.Rel(0, t.Cut, Rel, what: "cut");
    }

    // ---------------------------------------------------------------------------------------
    // Fixture H with no OHWM: full cut section 52 ft2 (bed 20 * 2 + two wedges 6 * 2 / 2).
    // [0,500]: 26,000 ft3 = 962.963 CY. [1000,1500]: 300 * 52 + transition 280 = 15,880 ft3.
    // [0,2000]: 1300 * 52 + 280 = 67,880 ft3. No fill anywhere.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 500, 26000)]
    [InlineData(500, 1000, 26000)]
    [InlineData(1000, 1500, 15880)]
    [InlineData(1500, 2000, 0)]
    [InlineData(0, 2000, 67880)]
    public void T29_FixtureH_band_cut(double s0, double s1, double cut)
    {
        var t = Band(FixtureSurfaces.EgH(), FixtureSurfaces.FgH(), FakeBands.Band(S, s0, s1, -100, 100));
        C5.Rel(cut, t.Cut, Rel, what: "cut");
        Assert.Equal(0.0, t.Fill);
        C5.Rel((s1 - s0) * 200, t.PlanArea, Rel, what: "plan area");
    }

    // ---------------------------------------------------------------------------------------
    // Boundaries
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Concave_and_clockwise_boundaries_integrate_exactly()
    {
        // L shape: 0..400 x 0..100 plus 0..100 x 100..250 -> 40,000 + 15,000 = 55,000 ft2.
        IReadOnlyList<Pt> l = [new(0, 0), new(400, 0), new(400, 100), new(100, 100), new(100, 250), new(0, 250), new(0, 0)];
        // U shape: 0..300 x -200..200 minus the notch 100..200 x -100..200 -> 120,000 - 30,000 = 90,000 ft2.
        IReadOnlyList<Pt> u = [new(0, -200), new(300, -200), new(300, 200), new(200, 200), new(200, -100),
                               new(100, -100), new(100, 200), new(0, 200), new(0, -200)];
        foreach (var (poly, area) in new[] { (l, 55000.0), (u, 90000.0) })
        {
            var ccw = Band(FixtureSurfaces.Eg(), FixtureSurfaces.FgD(), poly);
            var cw = Band(FixtureSurfaces.Eg(), FixtureSurfaces.FgD(), [.. poly.Reverse()]);
            C5.Rel(area, ccw.Fill, 1e-12); C5.Rel(area, cw.Fill, 1e-12); C5.Rel(area, ccw.BoundaryArea, 1e-12);
        }
    }

    [Fact]
    public void Random_concave_boundaries_over_crossing_triangulations_give_fill_equal_to_area()
    {
        // Base flat 100 on a 37 x 23 grid, comparison flat 101 on a 29 x 31 grid (and the other
        // diagonal), so every piece is a genuine triangle-triangle intersection. Fill = plan area.
        static double[] Lines(double from, double to, double step)
        {
            var l = new List<double>();
            for (double v = from; v < to; v += step) l.Add(v);
            l.Add(to);
            return [.. l];
        }
        var b = FixtureSurfaces.Grid("B", Lines(-200, 2200, 37), Lines(-300, 300, 23), (_, _) => 100.0, GridDiagonal.SouthwestToNortheast);
        double[] cy0 = [.. Lines(-300, 0, 31), .. Lines(0, 300, 31).Skip(1)];   // TowardCenterline needs y = 0
        var c = FixtureSurfaces.Grid("C", Lines(-200, 2200, 29), cy0, (_, _) => 101.0, GridDiagonal.TowardCenterline);
        // Vertices at sorted random angles around a center with random radii. When an angular gap
        // exceeds pi the polygon may cross itself; an independent brute-force crossing test decides,
        // and the integrator must agree: simple -> fill = area, crossing -> SV-E202 and no numbers.
        var rng = new Random(20261105);
        int simpleCount = 0, crossingCount = 0;
        for (int k = 0; k < 60; k++)
        {
            int n = rng.Next(5, 60);
            double cx = rng.NextDouble() * 1600, cy = rng.NextDouble() * 200 - 100;
            var angles = Enumerable.Range(0, n).Select(_ => rng.NextDouble() * 2 * Math.PI).OrderBy(a => a).ToArray();
            var pts = angles.Select(a => { double r = 20 + rng.NextDouble() * 150; return new Pt(cx + r * Math.Cos(a), cy + r * Math.Sin(a)); }).ToList();
            pts.Add(pts[0]);
            var r = OverlayIntegrator.IntegrateBand(b, c, pts);
            if (BruteForceCrosses(pts))
            {
                crossingCount++;
                Assert.False(r.Succeeded, $"star {k}: crossing polygon accepted");
                Assert.Equal(IntegratorCodes.InvalidBoundary, Assert.Single(r.Problems).Code);
                continue;
            }
            simpleCount++;
            Assert.True(r.Succeeded, $"star {k}: simple polygon rejected: {string.Join("; ", r.Problems.Select(p => p.Message))}");
            double area = FakeBands.Area(pts);
            C5.Rel(area, r.Totals!.Fill, Rel, what: $"star {k} fill");
            C5.Rel(area, r.Totals.PlanArea, Rel, what: $"star {k} plan area");
            Assert.True(r.Totals.Cut == 0, $"star {k} cut {r.Totals.Cut}");
        }
        Assert.True(simpleCount >= 20 && crossingCount >= 1, $"simple {simpleCount}, crossing {crossingCount}");
    }

    /// <summary>Independent check: any two non-adjacent edges properly cross (strict orientation signs).</summary>
    private static bool BruteForceCrosses(IReadOnlyList<Pt> closed)
    {
        int n = closed.Count - 1;
        static double O(Pt a, Pt b, Pt p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
        for (int i = 0; i < n; i++)
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue;
                Pt a = closed[i], b2 = closed[i + 1], p = closed[j], q = closed[j + 1];
                if (Math.Sign(O(a, b2, p)) * Math.Sign(O(a, b2, q)) < 0 && Math.Sign(O(p, q, a)) * Math.Sign(O(p, q, b2)) < 0) return true;
            }
        return false;
    }

    [Fact]
    public void Collinear_vertices_along_edges_are_harmless()
    {
        var pts = new List<Pt>();
        for (int i = 0; i < 50; i++) pts.Add(new Pt(i * 10, -100));
        for (int i = 0; i < 20; i++) pts.Add(new Pt(500, -100 + i * 10));
        for (int i = 0; i < 50; i++) pts.Add(new Pt(500 - i * 10, 100));
        for (int i = 0; i < 20; i++) pts.Add(new Pt(0, 100 - i * 10));
        pts.Add(pts[0]);
        var t = Band(FixtureSurfaces.EgH(), FixtureSurfaces.FgH(), pts);
        C5.Rel(26000, t.Cut, Rel); C5.Rel(100000, t.BoundaryArea, 1e-12);
    }

    public static TheoryData<string, Pt[]> InvalidBoundaries => new()
    {
        { "not closed", [new(0, 0), new(100, 0), new(100, 100), new(0, 100)] },
        { "too few points", [new(0, 0), new(100, 0), new(0, 0)] },
        { "bow tie", [new(0, 0), new(100, 100), new(100, 0), new(0, 100), new(0, 0)] },
        { "collinear", [new(0, 0), new(100, 0), new(200, 0), new(0, 0)] },
        { "non-finite", [new(0, 0), new(double.NaN, 0), new(100, 100), new(0, 0)] },
        { "touches itself", [new(0, 0), new(200, 0), new(200, 100), new(100, 0.0), new(0, 100), new(0, 0)] },
        { "spike", [new(0, 0), new(100, 0), new(100, 100), new(100, 300), new(100, 150), new(0, 100), new(0, 0)] },
    };

    [Theory]
    [MemberData(nameof(InvalidBoundaries))]
    public void E202_invalid_boundary_is_an_error_with_no_numbers(string why, Pt[] boundary)
    {
        var id = Guid.NewGuid();
        var r = OverlayIntegrator.IntegrateBand(FixtureSurfaces.Eg(), FixtureSurfaces.FgD(), boundary, id);
        Assert.False(r.Succeeded, why);
        Assert.Null(r.Totals);
        var p = Assert.Single(r.Problems);
        Assert.Equal((IntegratorCodes.InvalidBoundary, Severity.Error, (Guid?)id), (p.Code, p.Severity, p.SegmentId));
    }

    // ---------------------------------------------------------------------------------------
    // Coverage (E1011), no overlap (E405), empty surface (E403)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void E1011_boundary_past_the_surface_edge_gives_coverage_below_one()
    {
        // Offsets (-100, +400) on S: band y = -400..100; EG and FG_D stop at y = -300.
        // Covered 500 * 400 = 200,000 of 250,000 -> coverage 0.8; fill = covered area.
        var r = OverlayIntegrator.IntegrateBand(FixtureSurfaces.Eg(), FixtureSurfaces.FgD(), FakeBands.Band(S, 0, 500, -100, 400));
        Assert.True(r.Succeeded);
        C5.Rel(200000, r.Totals!.Fill, Rel); C5.Rel(250000, r.Totals.BoundaryArea, 1e-12); C5.Rel(0.8, r.Totals.Coverage, Rel);
        var p = Assert.Single(r.Problems);
        Assert.Equal((IntegratorCodes.PartialCoverage, Severity.Warning), (p.Code, p.Severity));
    }

    [Fact]
    public void E1011_comparison_missing_over_part_contributes_nothing()
    {
        // FG_D on a 50 ft grid, keeping only cells with centroid x < 250: covers x = -200..250.
        double[] xs = [.. Enumerable.Range(0, 49).Select(i => -200.0 + 50 * i)];
        var fine = FixtureSurfaces.Grid("FG_D50", xs, [-300, -100, 0, 100, 300], (_, _) => 101.0, GridDiagonal.SouthwestToNortheast);
        var partial = new C5FilteredSource(fine, (x, _) => x < 250);
        var r = OverlayIntegrator.IntegrateBand(FixtureSurfaces.Eg(), partial, FakeBands.Band(S, 0, 500, -100, 100));
        C5.Rel(50000, r.Totals!.Fill, Rel); C5.Rel(0.5, r.Totals.Coverage, Rel);
        Assert.Contains(r.Problems, p => p.Code == IntegratorCodes.PartialCoverage);
    }

    [Fact]
    public void E1011_base_with_a_hole_contributes_nothing_there()
    {
        // EG on a grid with lines at the hole edges; drop the 100 x 100 cell at (100..200, -50..50).
        var eg = FixtureSurfaces.Grid("EG_HOLE", [-200, 100, 200, 2200], [-300, -50, 50, 300], (_, _) => 100.0, GridDiagonal.SouthwestToNortheast);
        var holed = new C5FilteredSource(eg, (x, y) => !(x > 100 && x < 200 && y > -50 && y < 50));
        var r = OverlayIntegrator.IntegrateBand(holed, FixtureSurfaces.FgD(), FakeBands.Band(S, 0, 500, -100, 100));
        C5.Rel(90000, r.Totals!.Fill, Rel); C5.Rel(90000, r.Totals.PlanArea, Rel); C5.Rel(0.9, r.Totals.Coverage, Rel);
    }

    [Fact]
    public void E405_surfaces_that_do_not_overlap_give_zero_with_a_warning()
    {
        var b = FixtureSurfaces.Flat("B", 100, 0, -100, 250, 100);
        var c = FixtureSurfaces.Flat("C", 101, 250, -100, 500, 100);
        var r = OverlayIntegrator.IntegrateBand(b, c, FakeBands.Rect(0, -100, 500, 100));
        Assert.True(r.Succeeded);
        Assert.Equal((0.0, 0.0, 0.0), (r.Totals!.PlanArea, r.Totals.Cut, r.Totals.Fill));
        var p = Assert.Single(r.Problems);
        Assert.Equal((IntegratorCodes.NoOverlap, Severity.Warning), (p.Code, p.Severity));
    }

    [Fact]
    public void E403_empty_surface_is_an_error_with_no_numbers()
    {
        var r = OverlayIntegrator.IntegrateBand(new C5EmptySource(), FixtureSurfaces.FgD(), FakeBands.Rect(0, -100, 500, 100));
        Assert.False(r.Succeeded);
        var p = Assert.Single(r.Problems);
        Assert.Equal((IntegratorCodes.EmptySurface, Severity.Error), (p.Code, p.Severity));
        Assert.Contains("base", p.Message);
    }

    [Fact]
    public void Degenerate_triangles_are_ignored_and_noted()
    {
        var withVertical = new TriangleListSource("FG_D+", FixtureSurfaces.FgD().All.Append(
            new Tri3(new Pt3(10, 10, 101), new Pt3(20, 20, 105), new Pt3(30, 30, 101))));
        var r = OverlayIntegrator.IntegrateBand(FixtureSurfaces.Eg(), withVertical, FakeBands.Rect(0, -100, 500, 100));
        C5.Rel(100000, r.Totals!.Fill, Rel);
        var p = Assert.Single(r.Problems);
        Assert.Equal((IntegratorCodes.DegenerateTriangles, Severity.Info), (p.Code, p.Severity));
    }
}

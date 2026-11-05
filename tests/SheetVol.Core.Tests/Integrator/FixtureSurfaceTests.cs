// Checks the fixture generators directly (no integrator): tiling, elevations, and volumes from
// prism sums, so the integrator tests compare against surfaces known to be the section 8 shapes.

using SheetVol.Core.Volumes.Integrator.Fixtures;

namespace SheetVol.Core.Tests.Integrator;

public class FixtureSurfaceTests
{
    private static double PlanArea(Tri3 t) => 0.5 * ((t.B.X - t.A.X) * (t.C.Y - t.A.Y) - (t.B.Y - t.A.Y) * (t.C.X - t.A.X));

    /// <summary>Prism volume above z = 0: plan area * mean vertex z (exact for a planar triangle).</summary>
    private static double Prism(Tri3 t) => PlanArea(t) * (t.A.Z + t.B.Z + t.C.Z) / 3.0;

    [Fact]
    public void Every_fixture_tiles_its_extent_with_counter_clockwise_triangles()
    {
        foreach (var (src, area) in new[]
        {
            (FixtureSurfaces.Eg(), 2400.0 * 600), (FixtureSurfaces.FgA(), 2400.0 * 600),
            (FixtureSurfaces.FgB(), 2400.0 * 600), (FixtureSurfaces.FgD(), 2400.0 * 600),
            (FixtureSurfaces.EgH(), 2000.0 * 200), (FixtureSurfaces.FgH(), 2000.0 * 200),
        })
        {
            Assert.All(src.All, t => Assert.True(PlanArea(t) > 0, $"{src.Name}: clockwise or degenerate triangle"));
            C5.Rel(area, src.All.Sum(PlanArea), 1e-14, what: $"{src.Name} plan area");
        }
    }

    [Fact]
    public void FgA_has_five_apex_to_corner_pyramids_with_the_section_8_volumes()
    {
        var fg = FixtureSurfaces.FgA();
        foreach (var (cx, apex) in FixtureSurfaces.PyramidsA)
        {
            var faces = fg.All.Where(t => t.C.X == cx && t.C.Y == 0 && t.C.Z == apex).ToList();
            Assert.Equal(4, faces.Count);
            // Volume relative to EG (z = 100): 8100 * (apex - 100) / 3; P1 = -2700 ft3 = 100 CY cut.
            double v = faces.Sum(t => PlanArea(t) * ((t.A.Z - 100.0) + (t.B.Z - 100.0) + (t.C.Z - 100.0)) / 3.0);
            C5.Rel(8100.0 * (apex - 100.0) / 3.0, v, 1e-14, what: $"pyramid at x={cx}");
        }
        // Everything else is flat at 100.
        Assert.All(fg.All.Where(t => !(t.C.Y == 0 && t.C.Z != 100)), t => Assert.True(t.A.Z == 100 && t.B.Z == 100 && t.C.Z == 100));
    }

    [Fact]
    public void FgB_ridge_section_is_30_square_feet()
    {
        // Volume above EG over x = 0..500 equals 500 * 30: integrate the cells inside x in [-100, 2100].
        var fg = FixtureSurfaces.FgB();
        double v = fg.All.Where(t => Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)) >= -100 && Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)) <= 2100)
                         .Sum(t => PlanArea(t) * ((t.A.Z + t.B.Z + t.C.Z) / 3.0 - 100.0));
        C5.Rel(2200.0 * 30.0, v, 1e-14, what: "ridge volume over 2200 ft");
    }

    [Fact]
    public void FgH_differs_from_EgH_only_inside_the_cut_and_matches_52_square_feet_and_the_280_transition()
    {
        var eg = FixtureSurfaces.EgH();
        var fg = FixtureSurfaces.FgH();
        Assert.Equal(eg.All.Count, fg.All.Count);
        Assert.Equal(48, eg.All.Count);   // 3 columns x 8 strips x 2

        int differ = 0;
        double full = 0, transition = 0;
        for (int i = 0; i < eg.All.Count; i++)
        {
            var e = eg.All[i];
            var f = fg.All[i];
            Assert.Equal((e.A.X, e.A.Y, e.B.X, e.B.Y, e.C.X, e.C.Y), (f.A.X, f.A.Y, f.B.X, f.B.Y, f.C.X, f.C.Y));
            if (e == f) continue;
            differ++;
            double cut = Prism(e) - Prism(f);
            double maxX = Math.Max(e.A.X, Math.Max(e.B.X, e.C.X));
            if (maxX <= 1300) full += cut; else transition += cut;
            // Only cells with |y| <= 16 and x <= 1310 may differ.
            Assert.True(maxX <= 1310 && Math.Max(Math.Abs(e.A.Y), Math.Max(Math.Abs(e.B.Y), Math.Abs(e.C.Y))) <= 16);
        }
        Assert.Equal(16, differ);
        C5.Rel(1300.0 * 52.0, full, 1e-14, what: "full cut 0..1300");
        C5.Rel(280.0, transition, 1e-14, what: "transition 1300..1310");
    }

    [Fact]
    public void Refined_H_grid_is_the_same_surface()
    {
        var a = FixtureSurfaces.FgH();
        var b = FixtureSurfaces.FgH(maxCellLength: 25);
        Assert.True(b.All.Count > a.All.Count);
        C5.Rel(a.All.Sum(Prism), b.All.Sum(Prism), 1e-13, what: "FG_H volume coarse vs refined");
        // The transition column is never refined: exactly one cell edge pair spans 1300 to 1310.
        Assert.Equal(2 * FixtureSurfaces.HOffsets.Count - 2,
            b.All.Count(t => Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)) == 1300 && Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)) == 1310));
    }

    [Fact]
    public void Window_query_returns_only_overlapping_triangles()
    {
        var fg = FixtureSurfaces.FgH(maxCellLength: 100);
        var hits = fg.Triangles(new Pt(450, -5), new Pt(550, 5)).ToList();
        Assert.NotEmpty(hits);
        Assert.All(hits, t => Assert.True(Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)) >= 450 && Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)) <= 550));
        Assert.True(hits.Count < fg.All.Count / 4);
    }
}

// A1 adapters: SVADAPTERS steps for the triangle source against Civil 3D's bounded volume
// (T29, E1012, E1013, E1021 as far as headless goes, the Fixture H reconciliation, P23 scale).

using System.Diagnostics;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using SheetVol.Civil3D.SelfTest;
using SheetVol.Core;
using SheetVol.Core.Volumes.Integrator;
using SheetVol.Core.Volumes.Integrator.Fixtures;
using N = SheetVol.Civil3D.SelfTest.FixtureSpec.Names;
using C5 = SheetVol.Core.Volumes.Integrator.Fixtures.FixtureSurfaces;

namespace SheetVol.Civil3D.Adapters;

public static partial class AdapterCheckCommands
{
    private static double _hBandIntegratorCut = double.NaN, _hBandApiCut = double.NaN;

    private static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
        [new Pt(x0, y0), new Pt(x1, y0), new Pt(x1, y1), new Pt(x0, y1), new Pt(x0, y0)];

    /// <summary>One T29 comparison line; returns true when it passes.</summary>
    private static bool CompareBand(string label, ITriangleSource baseTri, ITriangleSource compTri, VolumeSourceAdapter src, IReadOnlyList<Pt> poly, out BandTotals? totals)
    {
        var ir = OverlayIntegrator.IntegrateBand(baseTri, compTri, poly);
        totals = ir.Totals;
        var api = src.Bounded(poly);
        if (ir.Totals is not { } t)
        {
            Problems(label, ir.Problems);
            Say($"T29 | {label} | integrator refused: {string.Join(" | ", ir.Problems.Select(p => p.Code))} | api cut {R(api.Cut)} fill {R(api.Fill)} | FAIL");
            return false;
        }
        double area = src.Area2D(poly);
        double gross = Math.Max(api.Cut + api.Fill, t.Cut + t.Fill);
        double diff = Math.Max(Math.Abs(t.Cut - api.Cut), Math.Abs(t.Fill - api.Fill));
        bool ok = gross > 0 ? diff <= AdapterCheckSettings.T29Relative * gross : diff == 0;
        double rel = gross > 0 ? diff / gross : diff;
        double areaRel = area > 0 ? (t.PlanArea - area) / area : t.PlanArea;
        Say($"T29 | {label} | int cut {R(t.Cut)} | api cut {R(api.Cut)} | int fill {R(t.Fill)} | api fill {R(api.Fill)} | rel {E(rel)} | 2D int {F3(t.PlanArea)} api {F3(area)} ({E(areaRel)}) | {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    /// <summary>T29 and E1012: C5's integrator on the adapter's triangles against GetBoundedVolumes on the owned surface, Fixtures A, B, D, H on S and C.</summary>
    private static void StepT29()
    {
        using var tr = _db.TransactionManager.StartTransaction();
        var alS = OpenAlignment(tr, N.AlignmentS);
        var alC = OpenAlignment(tr, N.AlignmentC);
        var arc = alC.Segments.Single(g => g.Kind == SegKind.Arc);
        var runs = new (AlignmentAdapter Al, string Breaks)[]
        {
            (alS, "START..END@500"),
            (alC, $"START, {R(arc.StartStation)}, {R(arc.EndStation)}, END"),
        };
        Say("T29: tolerance = 1e-9 x the larger gross volume (cut + fill) of the two; both zero must be exactly zero. 2D area columns are the 6.5 cross-check (integrator plan area vs datum method), reported only.");
        foreach (var (fx, b, c) in new[] { ("A", N.EG, N.FgA), ("B", N.EG, N.FgB), ("D", N.EG, N.FgD), ("H", N.EgH, N.FgH) })
        {
            var def = new AnalysisDef(Guid.NewGuid(), fx, b, c);
            var binding = Ensure(tr, [def])[0];
            SayBinding(binding);
            var src = Source(tr, binding);
            var baseTri = TriangleSourceAdapter.Read(tr, binding.Owned.Sources.Base.Id, _cfg).Value;
            var compTri = TriangleSourceAdapter.Read(tr, binding.Owned.Sources.Comparison.Id, _cfg).Value;
            Say($"  triangles: {baseTri.Name} {baseTri.Count} visible ({F3(baseTri.ReadSeconds * 1000)} ms), {compTri.Name} {compTri.Count} visible ({F3(compTri.ReadSeconds * 1000)} ms), origin {baseTri.Origin}/{compTri.Origin}");
            int ok = 0, total = 0;
            foreach (var (al, breaks) in runs)
            {
                var plan = Plan(Run($"T29-{fx}-{al.Name}", al.Name, breaks, [def]), al);
                foreach (var seg in plan.Segments)
                {
                    var poly = plan.Boundaries.Single(x => x.SegmentId == seg.Id).Tessellated;
                    total++;
                    if (CompareBand($"{fx} | {al.Name,-2} | {Seg(seg)}", baseTri, compTri, src, poly, out _)) ok++;
                }
                foreach (var piece in plan.Band)
                {
                    total++;
                    if (CompareBand($"{fx} | {al.Name,-2} | band {F(piece.StartStation)}-{F(piece.EndStation)}", baseTri, compTri, src, piece.Tessellated, out var t)) ok++;
                    if (fx == "H" && al.Name == N.AlignmentS && t is not null)
                    {
                        _hBandIntegratorCut = t.Cut;
                        _hBandApiCut = src.Bounded(piece.Tessellated).Cut;
                    }
                }
            }
            Check($"T29/E1012 Fixture {fx}", ok == total, $"{ok} of {total} polygons agree within 1e-9 relative");
        }
        tr.Commit();
    }

    /// <summary>NOW.md carry 2: FX's 67,860 ft3 against C5's 67,880 ft3 on Fixture H, explained from the real triangles.</summary>
    private static void StepFixtureH()
    {
        var fxVol = Fixtures.CreateVolume(_db, N.EgH, N.FgH);
        using var tr = _db.TransactionManager.StartTransaction();
        var def = new AnalysisDef(Guid.NewGuid(), "H", N.EgH, N.FgH);
        var binding = Ensure(tr, [def])[0];
        var src = Source(tr, binding);
        var baseTri = TriangleSourceAdapter.Read(tr, binding.Owned.Sources.Base.Id, _cfg).Value;
        var compTri = TriangleSourceAdapter.Read(tr, binding.Owned.Sources.Comparison.Id, _cfg).Value;
        var alS = OpenAlignment(tr, N.AlignmentS);
        var band = Plan(Run("H-band", N.AlignmentS, "START, END", [def]), alS).Band.Single().Tessellated;
        var gen = OverlayIntegrator.IntegrateBand(C5.EgH(), C5.FgH(), band).Totals!;
        var real = OverlayIntegrator.IntegrateBand(baseTri, compTri, band).Totals!;
        var api = src.Bounded(band);
        Say($"Fixture H cut, ft3: FX volume surface '{fxVol.Name}' GetVolumeProperties {R(fxVol.UnadjustedCut)} | owned '{binding.Owned.Name}' SurfaceTotals {R(src.SurfaceTotals.Cut)} | GetBoundedVolumes over band S [0,2000] (-100,100) {R(api.Cut)} | integrator on the adapter's (Civil 3D's) triangles {R(real.Cut)} | integrator on C5's generated triangles {R(gen.Cut)}");
        Say($"  from the T29 step: band integrator {R(_hBandIntegratorCut)}, band API {R(_hBandApiCut)}");

        // The only non-planar FG_H cells are x 1300..1310, |y| 10..16 (corner depths 2, 0, 0, 0).
        var sides = new List<string>();
        double civilTransition = 0, c5Transition = 0;
        // Bed |y| <= 10 across the 10 ft transition: depth falls linearly from 2 to 0, a planar cell whatever the diagonal.
        double bed = 2 * FixtureSpec.ChannelHalfBed * (FixtureSpec.TransitionToX - FixtureSpec.TransitionFromX) * (FixtureSpec.ChannelBedZ - FixtureSpec.SedimentBedZ) / 2;
        foreach (int side in new[] { 1, -1 })
        {
            double y10 = 10 * side, y16 = 16 * side;
            var cell = Rect(1300, Math.Min(y10, y16), 1310, Math.Max(y10, y16));
            var tris = compTri.All.Where(t => new[] { t.A, t.B, t.C }.All(p => p.X >= 1300 - 1e-9 && p.X <= 1310 + 1e-9 && Math.Abs(p.Y) >= 10 - 1e-9 && Math.Abs(p.Y) <= 16 + 1e-9 && Math.Sign(p.Y) == side)).ToList();
            bool HasEdge(Tri3 t, (double X, double Y) a, (double X, double Y) b)
            {
                var v = new[] { t.A, t.B, t.C };
                bool Is(Pt3 p, (double X, double Y) q) => Math.Abs(p.X - q.X) < 1e-9 && Math.Abs(p.Y - q.Y) < 1e-9;
                return v.Any(p => Is(p, a)) && v.Any(p => Is(p, b));
            }
            bool throughDeep = tris.Count == 2 && tris.All(t => HasEdge(t, (1300, y10), (1310, y16)));
            bool other = tris.Count == 2 && tris.All(t => HasEdge(t, (1310, y10), (1300, y16)));
            var r = OverlayIntegrator.IntegrateBand(baseTri, compTri, cell).Totals!;
            var g = OverlayIntegrator.IntegrateBand(C5.EgH(), C5.FgH(), cell).Totals!;
            var a = src.Bounded(cell);
            Say($"  transition cell y {F(Math.Min(y10, y16))}..{F(Math.Max(y10, y16))}: Civil 3D FG_H triangles {string.Join(" ", tris.Select(t => $"[({F(t.A.X)},{F(t.A.Y)},{F(t.A.Z)}) ({F(t.B.X)},{F(t.B.Y)},{F(t.B.Z)}) ({F(t.C.X)},{F(t.C.Y)},{F(t.C.Z)})]"))}");
            string diag = throughDeep ? "through the depth-2 corner (C5's choice)" : other ? "along (1310, |10|)-(1300, |16|), away from the depth-2 corner" : "neither (see triangles)";
            Say($"    diagonal: {diag}; cut here: integrator on Civil's triangles {R(r.Cut)}, GetBoundedVolumes {R(a.Cut)}, C5's generated triangles {R(g.Cut)}");
            sides.Add($"{(side > 0 ? "+y" : "-y")} cell {diag}, {F(r.Cut)} ft3");
            civilTransition += r.Cut;
            c5Transition += g.Cut;
        }
        Say($"  Explanation: both builds use the same lines and elevations; only the two non-planar transition cells (x 1300..1310, |y| 10..16, corner depths 2, 0, 0, 0) depend on which diagonal the TIN takes. Civil 3D, read through the adapter: {string.Join("; ", sides)}. "
            + $"C5's generator takes the depth-2 diagonal on both sides ({F(c5Transition)} ft3). Whole-band cut = 52 x 1300 + {F(bed)} (bed transition) + cells: Civil {F(FixtureSpec.HCutMinFt3 + bed + civilTransition)}, C5 {F(FixtureSpec.HCutMinFt3 + bed + c5Transition)}. "
            + "The difference is triangulation of one cell, not an error in either; the integrator on Civil's own triangles equals GetBoundedVolumes (T29), so the product number is Civil's. C5's T28 band (577.78 to 597.04 CY) admits both.");
        Check("Fixture H reconciliation", Math.Abs(real.Cut - api.Cut) <= AdapterCheckSettings.T29Relative * api.Cut && Math.Abs(api.Cut - fxVol.UnadjustedCut) <= AdapterCheckSettings.T29Relative * api.Cut,
            $"API band {R(api.Cut)} = integrator on real triangles {R(real.Cut)} = FX whole surface {R(fxVol.UnadjustedCut)}; C5's generated {R(gen.Cut)} differs by {R(gen.Cut - api.Cut)} ft3 (diagonal choice)");
        tr.Commit();
    }

    /// <summary>E1013: a hide boundary on FG_A; only visible triangles match Civil 3D.</summary>
    private static void StepHiddenTriangles()
    {
        var h = AdapterCheckSettings.HideOnFgA;
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var fg = SurfaceByName<TinSurface>(tr, N.FgA);
            fg.UpgradeOpen();
            using (var pts = new Point3dCollection { new(h.X0, h.Y0, 0), new(h.X1, h.Y0, 0), new(h.X1, h.Y1, 0), new(h.X0, h.Y1, 0), new(h.X0, h.Y0, 0) })
            using (fg.BoundariesDefinition.AddBoundaries(pts, AdapterCheckSettings.HideMidOrdinate, SurfaceBoundaryType.Hide, true)) { }
            fg.Rebuild();
            tr.Commit();
        }
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var def = new AnalysisDef(Guid.NewGuid(), "A", N.EG, N.FgA);
            var binding = Ensure(tr, [def])[0];
            var src = Source(tr, binding);
            var baseTri = TriangleSourceAdapter.Read(tr, binding.Owned.Sources.Base.Id, _cfg).Value;
            var compTri = TriangleSourceAdapter.Read(tr, binding.Owned.Sources.Comparison.Id, _cfg).Value;
            var fg = SurfaceByName<TinSurface>(tr, N.FgA);
            var all = new List<Tri3>();
            using (var tris = fg.GetTriangles(true))
                foreach (TinSurfaceTriangle t in tris)
                {
                    var a = t.Vertex1.Location; var b = t.Vertex2.Location; var c = t.Vertex3.Location;
                    t.Dispose();
                    all.Add(new Tri3(new Pt3(a.X, a.Y, a.Z), new Pt3(b.X, b.Y, b.Z), new Pt3(c.X, c.Y, c.Z)));
                }
            var withHidden = new TriangleListSource(N.FgA + " incl. hidden", all);
            Say($"FG_A after a hide boundary {F(h.X0)}..{F(h.X1)} x {F(h.Y0)}..{F(h.Y1)}: GetTriangles(false) {compTri.Count}, GetTriangles(true) {all.Count} ({all.Count - compTri.Count} hidden)");
            var p1 = AdapterCheckSettings.AroundP1;
            var band = Plan(Run("E1013", N.AlignmentS, "0, 500", [def]), OpenAlignment(tr, N.AlignmentS)).Boundaries.Single().Tessellated;
            int ok = 0;
            foreach (var (label, poly) in new[] { ("P1 polygon", Rect(p1.X0, p1.Y0, p1.X1, p1.Y1)), ("T1 segment 0-500", band) })
            {
                if (CompareBand($"E1013 | {label} | visible only", baseTri, compTri, src, poly, out var vis)) ok++;
                var hid = OverlayIntegrator.IntegrateBand(baseTri, withHidden, poly).Totals!;
                Say($"E1013 | {label} | hidden included: integrator cut {R(hid.Cut)} vs API {R(src.Bounded(poly).Cut)} (analytic with the hole: 2700 - 1125.926 = 1574.074; whole pyramid 2700)");
            }
            Check("E1013 hidden triangles excluded like Civil 3D", ok == 2, "visible-only integrator equals GetBoundedVolumes on both polygons; including hidden triangles would not");
            tr.Commit();
        }
    }

    /// <summary>P23 at scale through the adapter: 1 M triangles, read whole and with a band read window; window queries from the index.</summary>
    private static void StepTriangleScale()
    {
        int n = AdapterCheckSettings.ScaleGridSide;
        double sp = AdapterCheckSettings.ScaleGridSpacing;
        string name = $"SV-A1-GRID{n}";
        ObjectId id;
        var sw = Stopwatch.StartNew();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            id = TinSurface.Create(_db, name);
            var s = (TinSurface)tr.GetObject(id, OpenMode.ForWrite);
            var pts = new Point3dCollection();
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    pts.Add(new Point3d(i * sp, j * sp, 100.0 + 0.01 * ((i * 7 + j * 13) % 17)));
            using (s.AddVertices(pts)) { }
            s.Rebuild();
            tr.Commit();
        }
        double build = sw.Elapsed.TotalSeconds;
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var whole = TriangleSourceAdapter.Read(tr, id, _cfg).Value;
            double side = (n - 1) * sp;
            var wmin = new Pt(0, side / 2 - 100);
            var wmax = new Pt(side, side / 2 + 100);
            var windowed = TriangleSourceAdapter.Read(tr, id, _cfg, wmin, wmax).Value;
            var q = Stopwatch.StartNew();
            int queries = 0, returned = 0;
            for (double x = 0; x + 100 <= side; x += 50, queries++)
                returned += windowed.Triangles(new Pt(x, side / 2 - 50), new Pt(x + 100, side / 2 + 50)).Count();
            double querySeconds = q.Elapsed.TotalSeconds;
            var probeMin = new Pt(300, side / 2 - 50);
            var probeMax = new Pt(400, side / 2 + 50);
            int fromIndex = windowed.Triangles(probeMin, probeMax).Count();
            int brute = whole.All.Count(t => Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)) >= probeMin.X && Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)) <= probeMax.X
                                           && Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)) >= probeMin.Y && Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)) <= probeMax.Y);
            Say($"P23 scale through TriangleSourceAdapter: '{name}' {n * n} points built in {build:0.00} s; whole read {whole.Count} triangles in {whole.ReadSeconds:0.00} s; band read window (200 wide) kept {windowed.Count} of {windowed.VisibleTotal} in {windowed.ReadSeconds:0.00} s; {queries} segment-window queries returned {returned} triangles in {querySeconds * 1000:0.0} ms; one window: index {fromIndex}, brute force {brute}");
            Check("triangle window index matches brute force", fromIndex == brute && fromIndex > 0, $"{fromIndex} triangles");
            tr.Commit();
        }
    }

    private static T SurfaceByName<T>(Transaction tr, string name) where T : Autodesk.Civil.DatabaseServices.Surface
    {
        var civ = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(_db);
        foreach (ObjectId id in civ.GetSurfaceIds())
            if (tr.GetObject(id, OpenMode.ForRead) is T s && s.Name == name) return s;
        throw new InvalidOperationException($"surface '{name}' ({typeof(T).Name}) not found");
    }
}

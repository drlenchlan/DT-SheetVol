// S1 probes: fixture setup, T1/T2 printed check, P1, P2, P3, P4, P5, P19, P22.
// Run order matters and is the order of tools/scripts/probeA.scr: P4 adds a hide
// boundary to FG_A, so T1/T2 and P1-P3, P5 run before it.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using static SheetVol.Civil3D.SelfTest.ProbeASettings;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using N = SheetVol.Civil3D.SelfTest.FixtureSpec.Names;

namespace SheetVol.Civil3D.SelfTest;

public static partial class ProbeACommands
{
    /// <summary>Builds Fixtures A, B, D, alignments S, C, K, SP and the three owned-style
    /// volume surfaces with FX's API, timing each step.</summary>
    [CommandMethod("SVPROBEA_SETUP", CommandFlags.Modal)]
    public static void Setup() => Run("SVPROBEA_SETUP", (doc, db) =>
    {
        void Timed(string what, Func<string> step)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string r = step();
            Say($"setup {what}: {r} ({sw.Elapsed.TotalSeconds:0.000} s)");
        }
        static string Surf(SurfaceFixture f) => $"base {f.BaseName}{(f.BaseReused ? " (reused)" : "")}, comparison {f.ComparisonName}, {f.Checks.Count} checks passed";
        static string Al(AlignmentFixture f) => $"{f.Name} stations {F(f.StartStation)}..{F(f.EndStation)}";
        static string Vol(VolumeFixture v) => $"{v.Name} triangles={v.Triangles} UnadjustedCut={R(v.UnadjustedCut)} UnadjustedFill={R(v.UnadjustedFill)} UnadjustedNet={R(v.UnadjustedNet)}";

        Timed("Fixture A", () => Surf(Fixtures.CreateA(db)));
        Timed("Fixture B", () => Surf(Fixtures.CreateB(db)));
        Timed("Fixture D", () => Surf(Fixtures.CreateD(db)));
        Timed("Alignment S", () => Al(Fixtures.CreateAlignmentS(db)));
        Timed("Alignment C", () => Al(Fixtures.CreateAlignmentC(db)));
        Timed("Alignment K", () => Al(Fixtures.CreateAlignmentK(db)));
        Timed("Alignment SP", () => Al(Fixtures.CreateAlignmentSP(db)));
        Timed("volume A", () => Vol(Fixtures.CreateVolume(db, N.EG, N.FgA, VolA)));
        Timed("volume B", () => Vol(Fixtures.CreateVolume(db, N.EG, N.FgB, VolB)));
        Timed("volume D", () => Vol(Fixtures.CreateVolume(db, N.EG, N.FgD, VolD)));
    });

    /// <summary>T1 and T2 as a printed check: bounded cut and fill per segment on Fixture A,
    /// raw API values and CY beside the section 8 expectations, plus the reconciliation of
    /// the segment sums with the whole band and GetVolumeProperties.</summary>
    [CommandMethod("SVPROBEA_T12", CommandFlags.Modal)]
    public static void T12() => Run("SVPROBEA_T12", (doc, db) =>
    {
        using var tr = db.TransactionManager.StartTransaction();
        var vol = Surf<TinVolumeSurface>(tr, db, VolA);
        var s = Align(tr, db, N.AlignmentS);
        var props = vol.GetVolumeProperties();
        Say($"{vol.Name} GetVolumeProperties: UnadjustedCut={R(props.UnadjustedCutVolume)} UnadjustedFill={R(props.UnadjustedFillVolume)} UnadjustedNet={R(props.UnadjustedNetVolume)} CutFactor={R(props.CutFactor)} FillFactor={R(props.FillFactor)}");
        Say($"mapping used below: real cut = SurfaceVolumeInfo.Cut, real fill = SurfaceVolumeInfo.Fill (P1); units cubic drawing units = ft3, CY = ft3 / 27 (P2)");

        int failures = 0;
        void Table(string test, (double From, double To, double CutFt3, double FillFt3)[] rows)
        {
            Say($"{test}: alignment S, offsets ({F(OffsetLeft)}, {F(OffsetRight)}), polygon corners from PointLocation");
            Say($"{test} | segment     | API Cut ft3  | API Fill ft3 | Cut CY   | Fill CY  | expected C/F CY | diff C/F CY        | result");
            double sumCut = 0, sumFill = 0;
            foreach (var r in rows)
            {
                var poly = Band(s, new[] { r.From, r.To }, OffsetLeft, OffsetRight);
                var v = vol.GetBoundedVolumes(poly);
                double dc = Cy(v.Cut) - Cy(r.CutFt3), df = Cy(v.Fill) - Cy(r.FillFt3);
                bool ok = Math.Abs(dc) <= TolCy && Math.Abs(df) <= TolCy;
                if (!ok) failures++;
                sumCut += v.Cut; sumFill += v.Fill;
                Say($"{test} | {F(r.From),5}-{F(r.To),-5} | {R(v.Cut),12} | {R(v.Fill),12} | {F3(Cy(v.Cut)),8} | {F3(Cy(v.Fill)),8} | {F3(Cy(r.CutFt3)),7}/{F3(Cy(r.FillFt3)),-7} | {E(dc)}/{E(df)} | {(ok ? "PASS" : "FAIL")}");
            }
            var whole = vol.GetBoundedVolumes(Band(s, new[] { rows[0].From, rows[^1].To }, OffsetLeft, OffsetRight));
            Say($"{test} sum of segments: Cut={R(sumCut)} Fill={R(sumFill)}; whole band [{F(rows[0].From)},{F(rows[^1].To)}]: Cut={R(whole.Cut)} Fill={R(whole.Fill)}; partition diff Cut={E(sumCut - whole.Cut)} Fill={E(sumFill - whole.Fill)}");
            Say($"{test} outside-limits = GetVolumeProperties - band: Cut={R(props.UnadjustedCutVolume - whole.Cut)} Fill={R(props.UnadjustedFillVolume - whole.Fill)}");
            Say($"{test} band totals in CY: Cut={F3(Cy(whole.Cut))} Fill={F3(Cy(whole.Fill))} (G1 wants 200 / 200)");
        }
        Table("T1", FixtureSpec.Expected.T1);
        Table("T2", FixtureSpec.Expected.T2);
        Say($"T1+T2: {failures} segment(s) outside +-{F(TolCy)} CY");
        tr.Commit();
    });

    /// <summary>P1 (which of Cut/Fill is real cut) and P2 (units).</summary>
    [CommandMethod("SVPROBEA_P1", CommandFlags.Modal)]
    public static void P1() => Run("SVPROBEA_P1", (doc, db) =>
    {
        using var tr = db.TransactionManager.StartTransaction();
        var vol = Surf<TinVolumeSurface>(tr, db, VolA);
        var volD = Surf<TinVolumeSurface>(tr, db, VolD);
        var eg = Surf<TinSurface>(tr, db, N.EG);
        var fg = Surf<TinSurface>(tr, db, N.FgA);
        var p1 = Rect(AroundP1);
        var p2 = Rect(AroundP2);
        Say($"P1 polygon {F(AroundP1.X0)}..{F(AroundP1.X1)} x {F(AroundP1.Y0)}..{F(AroundP1.Y1)} holds pyramid P1 only (apex 99.0 below EG 100: real CUT 2700 ft3 = 100 CY)");
        Say($"P2 polygon {F(AroundP2.X0)}..{F(AroundP2.X1)} x {F(AroundP2.Y0)}..{F(AroundP2.Y1)} holds pyramid P2 only (apex 100.5 above EG: real FILL 1350 ft3 = 50 CY)");

        var props = vol.GetVolumeProperties();
        Say($"{vol.Name} IsVolumeSurface={vol.IsVolumeSurface} whole surface: UnadjustedCut={R(props.UnadjustedCutVolume)} UnadjustedFill={R(props.UnadjustedFillVolume)} UnadjustedNet={R(props.UnadjustedNetVolume)}");
        var pd = volD.GetVolumeProperties();
        Say($"{volD.Name} whole surface (FG_D is 1.00 ABOVE EG everywhere, analytic fill {R(FixtureSpec.DFillFt3)}): UnadjustedCut={R(pd.UnadjustedCutVolume)} UnadjustedFill={R(pd.UnadjustedFillVolume)} UnadjustedNet={R(pd.UnadjustedNetVolume)}");

        Bounded(vol, p1, "P1 polygon");
        Bounded(vol, p2, "P2 polygon");
        Bounded(vol, p1, "P1 polygon", 0.0);
        Bounded(vol, p2, "P2 polygon", 0.0);
        Bounded(volD, p1, "P1 polygon");
        Say($"(D over the P1 polygon: FG_D above EG, plan area {F(Math.Abs(SignedArea(p1)))} x 1.00 = fill {F(Math.Abs(SignedArea(p1)))} ft3)");

        Say("-- the base TinSurface EG (not a volume surface), and FG_A, over the same polygons --");
        Say($"{eg.Name} IsVolumeSurface={eg.IsVolumeSurface}");
        Bounded(eg, p1, "P1 polygon");
        Bounded(eg, p1, "P1 polygon", 0.0);
        Bounded(eg, p1, "P1 polygon", 99.5);
        Bounded(fg, p1, "P1 polygon");
        Bounded(fg, p1, "P1 polygon", 100.0);
        Bounded(fg, p2, "P2 polygon", 100.0);

        Say("-- P2 units --");
        var civ = Civil(db);
        Say($"drawing INSUNITS={db.Insunits}; Civil DrawingUnits={civ.Settings.DrawingSettings.UnitZoneSettings.DrawingUnits}; pyramid P1 analytic {R(FixtureSpec.Pyramids[0].VolumeFt3)} ft3 = {R(Cy(FixtureSpec.Pyramids[0].VolumeFt3))} CY");
        tr.Commit();
    });

    /// <summary>P3: are the surface's factors applied to bounded results? Is FillFactor settable?</summary>
    [CommandMethod("SVPROBEA_P3", CommandFlags.Modal)]
    public static void P3() => Run("SVPROBEA_P3", (doc, db) =>
    {
        var p1 = Rect(AroundP1);
        var p2 = Rect(AroundP2);
        void Read(string tag)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var vol = Surf<TinVolumeSurface>(tr, db, VolA);
            var p = vol.GetVolumeProperties();
            Say($"[{tag}] {vol.Name}.CutFactor={R(vol.CutFactor)} FillFactor={R(vol.FillFactor)}; GetVolumeProperties: CutFactor={R(p.CutFactor)} FillFactor={R(p.FillFactor)} UnadjustedCut={R(p.UnadjustedCutVolume)} AdjustedCut={R(p.AdjustedCutVolume)} UnadjustedFill={R(p.UnadjustedFillVolume)} AdjustedFill={R(p.AdjustedFillVolume)} AdjustedNet={R(p.AdjustedNetVolume)}");
            Bounded(vol, p1, $"P1 polygon [{tag}]");
            Bounded(vol, p2, $"P2 polygon [{tag}]");
            tr.Commit();
        }
        void Set(double cut, double fill)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var vol = Surf<TinVolumeSurface>(tr, db, VolA, OpenMode.ForWrite);
            Try($"set CutFactor={F(cut)}", () => vol.CutFactor = cut);
            Try($"set FillFactor={F(fill)}", () => vol.FillFactor = fill);
            Say($"after set in the same transaction: CutFactor={R(vol.CutFactor)} FillFactor={R(vol.FillFactor)} IsOutOfDate={vol.IsOutOfDate}");
            tr.Commit();
        }
        var setter = typeof(TinVolumeSurface).GetProperty(nameof(TinVolumeSurface.FillFactor))?.SetMethod;
        Say($"reflection: TinVolumeSurface.FillFactor has a public setter: {setter is not null && setter.IsPublic}; CutFactor: {typeof(TinVolumeSurface).GetProperty(nameof(TinVolumeSurface.CutFactor))?.SetMethod?.IsPublic == true}");
        Read("factors 1/1");
        Set(1.25, 1.0);
        Read("CutFactor 1.25");
        Set(1.25, 0.9);
        Read("CutFactor 1.25, FillFactor 0.9");
        Set(1.0, 1.0);
        Read("reset 1/1");
    });

    /// <summary>P5: winding of the bounding polygon.</summary>
    [CommandMethod("SVPROBEA_P5", CommandFlags.Modal)]
    public static void P5() => Run("SVPROBEA_P5", (doc, db) =>
    {
        using var tr = db.TransactionManager.StartTransaction();
        var vol = Surf<TinVolumeSurface>(tr, db, VolA);
        foreach (var (name, r) in new[] { ("P1", AroundP1), ("P2", AroundP2) })
        {
            var ccw = Rect(r, true);
            var cw = Rect(r, false);
            Say($"{name}: CCW signed area {F(SignedArea(ccw))}, CW signed area {F(SignedArea(cw))}");
            var a = Bounded(vol, ccw, $"{name} CCW");
            var b = Bounded(vol, cw, $"{name} CW");
            Bounded(vol, ccw, $"{name} CCW", 0.0);
            Bounded(vol, cw, $"{name} CW", 0.0);
            // Same ring, different start vertex.
            var rot = Closed(new[] { (r.X1, r.Y1), (r.X0, r.Y1), (r.X0, r.Y0), (r.X1, r.Y0) });
            var c = Bounded(vol, rot, $"{name} CCW from NE corner");
            if (a is { } va && b is { } vb && c is { } vc)
                Say($"{name}: CW-CCW diff Cut={E(vb.Cut - va.Cut)} Fill={E(vb.Fill - va.Fill)} Net={E(vb.Net - va.Net)}; start-vertex diff Cut={E(vc.Cut - va.Cut)}");
        }
        tr.Commit();
    });

    /// <summary>P4: polygon partly outside, wholly outside, malformed, and over a hole
    /// (a hide boundary added to FG_A). Leaves the hole in FG_A for P19 and P23.</summary>
    [CommandMethod("SVPROBEA_P4", CommandFlags.Modal)]
    public static void P4() => Run("SVPROBEA_P4", (doc, db) =>
    {
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var vol = Surf<TinVolumeSurface>(tr, db, VolA);
            var volD = Surf<TinVolumeSurface>(tr, db, VolD);
            var part = Rect(PartlyOutside);
            Say($"partly outside: x {F(PartlyOutside.X0)}..{F(PartlyOutside.X1)}, y {F(PartlyOutside.Y0)}..{F(PartlyOutside.Y1)} (EG ends at y 300); holds P1 entirely; polygon area {F(Math.Abs(SignedArea(part)))}, on-surface area {F((PartlyOutside.X1 - PartlyOutside.X0) * (300 - PartlyOutside.Y0))}");
            Bounded(vol, part, "partly outside");
            Bounded(volD, part, "partly outside");
            var a0 = Bounded(volD, part, "partly outside", 0.0);
            var a1 = Bounded(volD, part, "partly outside", 1.0);
            if (a0 is { } x0 && a1 is { } x1) Say($"partly outside on D: |Net(0)-Net(1)| = {R(Math.Abs(x0.Net - x1.Net))} (area of data inside)");
            var whole = Rect(WhollyOutside);
            Say($"wholly outside: x {F(WhollyOutside.X0)}..{F(WhollyOutside.X1)}, y {F(WhollyOutside.Y0)}..{F(WhollyOutside.Y1)}");
            Bounded(vol, whole, "wholly outside");
            Bounded(vol, whole, "wholly outside", 0.0);

            Say("-- malformed polygons (5.1 says ArgumentException) --");
            var open = new Autodesk.AutoCAD.Geometry.Point3dCollection();
            foreach (Autodesk.AutoCAD.Geometry.Point3d p in Rect(AroundP1)) open.Add(p);
            open.RemoveAt(open.Count - 1);
            Bounded(vol, open, "P1 rectangle not closed (4 points, first != last)");
            Bounded(vol, Closed(new[] { (60.0, -60.0), (190.0, -60.0), (125.0, 60.0) }), "closed triangle (4 points)");
            var three = new Autodesk.AutoCAD.Geometry.Point3dCollection { new(60, -60, 0), new(190, -60, 0), new(60, -60, 0) };
            Bounded(vol, three, "degenerate closed (3 points)");
            tr.Commit();
        }

        Say($"-- hole: hide boundary {F(Hole.X0)}..{F(Hole.X1)} x {F(Hole.Y0)}..{F(Hole.Y1)} on {N.FgA}, non-destructive --");
        // Analytic: P1 depth 1 - max(|dx|,|dy|)/45 over the 40 x 40 square: 1600 - (8 * 20^3 / 3) / 45.
        double a = (Hole.X1 - Hole.X0) / 2.0;
        double holeCut = (2 * a) * (2 * a) - (8.0 * a * a * a / 3.0) / FixtureSpec.PyramidHalfBase;
        double expectCut = FixtureSpec.Pyramids[0].VolumeFt3 - holeCut;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var fg = Surf<TinSurface>(tr, db, N.FgA, OpenMode.ForWrite);
            Say($"before: {TriCounts(fg)}");
            Try("AddBoundaries(Hide)", () =>
            {
                using var op = fg.BoundariesDefinition.AddBoundaries(Rect(Hole), BoundaryMidOrdinate, SurfaceBoundaryType.Hide, true);
                Say($"AddBoundaries returned {op.GetType().Name} BoundaryType={op.BoundaryType} Count={op.Count}");
            });
            Say($"{N.FgA} IsOutOfDate after AddBoundaries (same transaction) = {fg.IsOutOfDate}");
            fg.Rebuild();
            Say($"after Rebuild: {TriCounts(fg)}");
            tr.Commit();
        }
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var vol = Surf<TinVolumeSurface>(tr, db, VolA, OpenMode.ForWrite);
            Say($"{vol.Name} IsOutOfDate after FG_A changed (new transaction) = {vol.IsOutOfDate}");
            Bounded(vol, Rect(AroundP1), "P1 polygon, volume surface not rebuilt");
            vol.Rebuild();
            Say($"{vol.Name} rebuilt; IsOutOfDate={vol.IsOutOfDate}");
            Say($"analytic P1 cut with the hole removed from the data: 2700 - {R(holeCut)} = {R(expectCut)} ft3");
            var v = Bounded(vol, Rect(AroundP1), "P1 polygon over the hole");
            if (v is { } vv) Say($"diff from analytic = {E(vv.Cut - expectCut)}");
            Bounded(vol, Rect(InsideHole), "polygon wholly inside the hole");
            var d0 = Bounded(vol, Rect(AroundP1), "P1 polygon over the hole", 0.0);
            var d1 = Bounded(vol, Rect(AroundP1), "P1 polygon over the hole", 1.0);
            if (d0 is { } y0 && d1 is { } y1)
                Say($"area of data inside P1 polygon = |Net(0)-Net(1)| = {R(Math.Abs(y0.Net - y1.Net))}; polygon {F(Math.Abs(SignedArea(Rect(AroundP1))))} minus hole {F((Hole.X1 - Hole.X0) * (Hole.Y1 - Hole.Y0))} = {F(Math.Abs(SignedArea(Rect(AroundP1))) - (Hole.X1 - Hole.X0) * (Hole.Y1 - Hole.Y0))}");
            var p = vol.GetVolumeProperties();
            Say($"{vol.Name} whole surface after hole: UnadjustedCut={R(p.UnadjustedCutVolume)} UnadjustedFill={R(p.UnadjustedFillVolume)} (expected cut {R(FixtureSpec.ACutFt3 - holeCut)})");
            tr.Commit();
        }
    });

    /// <summary>P19: FindElevationAtXY outside the surface and inside a hidden area.</summary>
    [CommandMethod("SVPROBEA_P19", CommandFlags.Modal)]
    public static void P19() => Run("SVPROBEA_P19", (doc, db) =>
    {
        using var tr = db.TransactionManager.StartTransaction();
        void Z(CivSurface s, double x, double y, string why)
        {
            try { Say($"{s.Name}.FindElevationAtXY({F(x)}, {F(y)}) [{why}] = {R(s.FindElevationAtXY(x, y))}"); }
            catch (System.Exception ex) { Say($"{s.Name}.FindElevationAtXY({F(x)}, {F(y)}) [{why}] threw {Describe(ex)}"); }
        }
        var eg = Surf<TinSurface>(tr, db, N.EG);
        var fg = Surf<TinSurface>(tr, db, N.FgA);
        var vol = Surf<TinVolumeSurface>(tr, db, VolA);
        Z(eg, Outside.X, Outside.Y, "far outside");
        Z(eg, 2200.0, 0.0, "on the east edge");
        Z(eg, 2200.000001, 0.0, "1e-6 outside the east edge");
        Z(fg, 125.0, 0.0, "inside the P4 hide boundary");
        Z(fg, 125.0, 30.0, "P1 face outside the hide boundary");
        Z(vol, Outside.X, Outside.Y, "volume surface, far outside");
        Z(vol, 125.0, 0.0, "volume surface, inside the hole");
        Z(vol, 125.0, 30.0, "volume surface, P1 face outside the hole");
        tr.Commit();
    });

    /// <summary>P22 and T24: |Net(0) - Net(1)| from GetBoundedVolumes(polygon, datum) against
    /// the plan area of the band polygon on Fixture D, alignment C [300, 614.159] x (-100, +100),
    /// and on a band that runs off the surface edge.</summary>
    [CommandMethod("SVPROBEA_P22", CommandFlags.Modal)]
    public static void P22() => Run("SVPROBEA_P22", (doc, db) =>
    {
        using var tr = db.TransactionManager.StartTransaction();
        var vol = Surf<TinVolumeSurface>(tr, db, VolD);
        var c = Align(tr, db, N.AlignmentC);

        // Find the arc and size the tessellation so the mid-ordinate on the larger offset
        // radius is at most MidOrdinate (6.4 rule 1).
        AlignmentSubEntityArc? arc = null;
        for (int i = 0; i < c.Entities.Count && arc is null; i++)
        {
            var e = c.Entities.GetEntityByOrder(i);
            for (int j = 0; j < e.SubEntityCount; j++) if (e[j] is AlignmentSubEntityArc sa) { arc = sa; break; }
        }
        if (arc is null) throw new InvalidOperationException("alignment C has no arc");
        double s0 = arc.StartStation, s1 = arc.EndStation, rOuter = arc.Radius + Math.Max(Math.Abs(OffsetLeft), Math.Abs(OffsetRight));
        double step = 2.0 * Math.Acos(1.0 - MidOrdinate / rOuter);
        int n = (int)Math.Ceiling((s1 - s0) / arc.Radius / step);
        var stations = Enumerable.Range(0, n + 1).Select(k => s0 + (s1 - s0) * k / n).ToList();
        var poly = Band(c, stations, OffsetLeft, OffsetRight);
        double polyArea = Math.Abs(SignedArea(poly));
        double sector = FixtureSpec.Expected.T24AreaFt2;
        double maxMid = rOuter * (1 - Math.Cos((s1 - s0) / arc.Radius / n / 2));
        Say($"band on C [{R(s0)}, {R(s1)}] x ({F(OffsetLeft)}, {F(OffsetRight)}): arc R={F(arc.Radius)}, outer radius {F(rOuter)}, {n} chords (mid-ordinate {E(maxMid)} <= {F(MidOrdinate)}), {poly.Count} polygon points");
        Say($"polygon shoelace area = {R(polyArea)}; analytic annular sector 20000*pi = {R(sector)}; polygon vs sector rel diff {E((polyArea - sector) / sector)}");

        var nets = new Dictionary<double, double>();
        foreach (var d in Datums)
            if (Bounded(vol, poly, "C band", d) is { } v) nets[d] = v.Net;
        var whole = Bounded(vol, poly, "C band");
        if (nets.TryGetValue(0.0, out var n0) && nets.TryGetValue(1.0, out var n1))
        {
            double api = Math.Abs(n0 - n1);
            Say($"P22: |Net(0) - Net(1)| = {R(api)}; polygon area {R(polyArea)}; rel diff {E((api - polyArea) / polyArea)}; vs analytic sector rel diff {E((api - sector) / sector)} (T24 tolerance 2e-4)");
            foreach (var d in Datums.Where(x => x != 0.0 && nets.ContainsKey(x)))
                Say($"P22 linearity: (Net(0) - Net({F(d)})) / {F(d)} = {R((n0 - nets[d]) / d)}");
            if (whole is { } w) Say($"P22: Fill without datum = {R(w.Fill)} (slab 1.00 thick, so also the area); Fill vs polygon area rel diff {E((w.Fill - polyArea) / polyArea)}; T7 fill {F3(Cy(w.Fill))} CY vs {F3(Cy(FixtureSpec.Expected.T7FillFt3))} CY");
        }

        // A band that runs off the surface edge: C's out-tangent (bearing -60 deg) leaves EG at y = -300.
        double t0 = s1, t1 = c.EndingStation;
        var off = Band(c, new[] { t0, t1 }, OffsetLeft, OffsetRight);
        var ring = new List<(double X, double Y)>();
        for (int i = 0; i + 1 < off.Count; i++) ring.Add((off[i].X, off[i].Y));
        var eg = FixtureSpec.EgCorners;
        var clipped = ClipHalfPlane(ring, 0, 1, -eg[0].Y);          // y >= -300
        clipped = ClipHalfPlane(clipped, 0, -1, eg[2].Y);            // y <= 300
        clipped = ClipHalfPlane(clipped, 1, 0, -eg[0].X);            // x >= -200
        clipped = ClipHalfPlane(clipped, -1, 0, eg[2].X);            // x <= 2200
        double offArea = Math.Abs(SignedArea(off)), onArea = Area(clipped);
        Say($"off-edge band on C [{R(t0)}, {R(t1)}] x ({F(OffsetLeft)}, {F(OffsetRight)}): polygon area {R(offArea)}; analytic part inside EG {R(onArea)} (coverage {F(onArea / offArea)})");
        var o0 = Bounded(vol, off, "off-edge band", 0.0);
        var o1 = Bounded(vol, off, "off-edge band", 1.0);
        if (o0 is { } q0 && o1 is { } q1)
        {
            double api = Math.Abs(q0.Net - q1.Net);
            Say($"P22 off-edge: |Net(0) - Net(1)| = {R(api)}; analytic on-surface area {R(onArea)}; rel diff {E((api - onArea) / onArea)}; coverage api/polygon = {F(api / offArea)}");
        }
        tr.Commit();
    });
}

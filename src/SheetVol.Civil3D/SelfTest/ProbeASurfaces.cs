// S1 probes: P18 (out-of-date and rebuild behaviour) and P23 (reading visible triangles:
// API, hidden triangles, boundaries, masks, cost at scale). P18 and the P23 boundary and
// mask steps edit FG_B only, so Fixtures A and D stay as the volume probes left them.

using System.Diagnostics;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using SheetVol.Core;
using static SheetVol.Civil3D.SelfTest.ProbeASettings;
using N =SheetVol.Civil3D.SelfTest.FixtureSpec.Names;

namespace SheetVol.Civil3D.SelfTest;

public static partial class ProbeACommands
{
    [CommandMethod("SVPROBEA_P18", CommandFlags.Modal)]
    public static void P18() => Run("SVPROBEA_P18", (doc, db) =>
    {
        void State(string tag)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var fg = Surf<TinSurface>(tr, db, N.FgB);
            var eg = Surf<TinSurface>(tr, db, N.EG);
            var vol = Surf<TinVolumeSurface>(tr, db, VolB);
            double z = double.NaN; string zs;
            try { z = fg.FindElevationAtXY(P18Vertex.X, P18Vertex.Y); zs = R(z); } catch (System.Exception ex) { zs = "threw " + ex.GetType().Name; }
            var p = vol.GetVolumeProperties();
            Say($"[{tag}] {fg.Name}: IsOutOfDate={fg.IsOutOfDate} AutoRebuild={fg.AutoRebuild} IsReferenceStale={fg.IsReferenceStale} triangles={fg.GetTinProperties().NumberOfTriangles} z({F(P18Vertex.X)},{F(P18Vertex.Y)})={zs} | {eg.Name}: IsOutOfDate={eg.IsOutOfDate} | {vol.Name}: IsOutOfDate={vol.IsOutOfDate} AutoRebuild={vol.AutoRebuild} UnadjustedFill={R(p.UnadjustedFillVolume)} UnadjustedCut={R(p.UnadjustedCutVolume)}");
            tr.Commit();
        }
        void Edit(string tag, Action<TinSurface> edit, bool rebuild)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var fg = Surf<TinSurface>(tr, db, N.FgB, OpenMode.ForWrite);
            edit(fg);
            Say($"[{tag}] {fg.Name}.IsOutOfDate inside the editing transaction = {fg.IsOutOfDate}");
            if (rebuild) { fg.Rebuild(); Say($"[{tag}] {fg.Name}.Rebuild(); IsOutOfDate = {fg.IsOutOfDate}"); }
            tr.Commit();
        }
        void RebuildVol(string tag)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var vol = Surf<TinVolumeSurface>(tr, db, VolB, OpenMode.ForWrite);
            vol.Rebuild();
            Say($"[{tag}] {vol.Name}.Rebuild(); IsOutOfDate = {vol.IsOutOfDate}");
            tr.Commit();
        }

        State("as built");
        Edit("AddVertex", fg => { using (fg.AddVertex(P18Vertex)) { } }, rebuild: false);
        State("after AddVertex, no Rebuild");
        Edit("Rebuild FG_B", _ => { }, rebuild: true);
        State("after FG_B.Rebuild");
        RebuildVol("volume");
        State("after volume Rebuild");

        Say("-- a definition edit (breakline) with AutoRebuild off and no Rebuild --");
        Edit("AddStandardBreaklines", fg => { using (fg.BreaklinesDefinition.AddStandardBreaklines(new Point3dCollection { new(500, 60, 100), new(1500, 60, 100) }, 0.1, 0, 0, 0)) { } }, rebuild: false);
        State("after a breakline, AutoRebuild off, no Rebuild");
        Edit("Rebuild FG_B", _ => { }, rebuild: true);
        State("after FG_B.Rebuild");
        RebuildVol("volume");
        State("after volume Rebuild");

        Say("-- AutoRebuild = true on FG_B and on the volume surface, then vertex edits --");
        using (var tr = db.TransactionManager.StartTransaction())
        {
            Surf<TinSurface>(tr, db, N.FgB, OpenMode.ForWrite).AutoRebuild = true;
            Surf<TinVolumeSurface>(tr, db, VolB, OpenMode.ForWrite).AutoRebuild = true;
            tr.Commit();
        }
        State("AutoRebuild on");
        Edit("MoveVertex", fg => { using (fg.MoveVertex(fg.FindVertexAtXY(P18Vertex.X, P18Vertex.Y), new Point2d(P18Vertex.X + 10, P18Vertex.Y))) { } }, rebuild: false);
        State("after MoveVertex with AutoRebuild on");
        Edit("SetVertexElevation", fg => { using (fg.SetVertexElevation(fg.FindVertexAtXY(P18Vertex.X + 10, P18Vertex.Y), P18VertexZ2)) { } }, rebuild: false);
        State("after SetVertexElevation with AutoRebuild on (end of SVPROBEA_P18; SVPROBEA_P18B reads the state in the next command)");
    });

    /// <summary>P18, second half: runs as the next command, to see whether AutoRebuild acts at
    /// command end; then rebuilds the volume surface explicitly and turns AutoRebuild off.</summary>
    [CommandMethod("SVPROBEA_P18B", CommandFlags.Modal)]
    public static void P18B() => Run("SVPROBEA_P18B", (doc, db) =>
    {
        void State(string tag)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var fg = Surf<TinSurface>(tr, db, N.FgB);
            var vol = Surf<TinVolumeSurface>(tr, db, VolB);
            var p = vol.GetVolumeProperties();
            Say($"[{tag}] {fg.Name}: IsOutOfDate={fg.IsOutOfDate} AutoRebuild={fg.AutoRebuild} z({F(P18Vertex.X + 10)},{F(P18Vertex.Y)})={R(fg.FindElevationAtXY(P18Vertex.X + 10, P18Vertex.Y))} | {vol.Name}: IsOutOfDate={vol.IsOutOfDate} AutoRebuild={vol.AutoRebuild} UnadjustedFill={R(p.UnadjustedFillVolume)}");
            tr.Commit();
        }
        State("next command, nothing done yet");
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var vol = Surf<TinVolumeSurface>(tr, db, VolB, OpenMode.ForWrite);
            vol.Rebuild();
            tr.Commit();
        }
        State("after explicit volume Rebuild");
        using (var tr = db.TransactionManager.StartTransaction())
        {
            Surf<TinSurface>(tr, db, N.FgB, OpenMode.ForWrite).AutoRebuild = false;
            Surf<TinVolumeSurface>(tr, db, VolB, OpenMode.ForWrite).AutoRebuild = false;
            tr.Commit();
        }
        State("AutoRebuild off again");
    });

    /// <summary>P23 part 1: the triangle API on native fixtures: counts with and without hidden
    /// triangles (FG_A has P4's hide boundary), an outer boundary and a mask on FG_B, vertex
    /// access, and what volume surfaces offer.</summary>
    [CommandMethod("SVPROBEA_P23", CommandFlags.Modal)]
    public static void P23() => Run("SVPROBEA_P23", (doc, db) =>
    {
        using (var tr = db.TransactionManager.StartTransaction())
        {
            foreach (var name in new[] { N.EG, N.FgA, N.FgB, N.FgD })
                Say(TriCounts(Surf<TinSurface>(tr, db, name)));
            var fg = Surf<TinSurface>(tr, db, N.FgA);
            int shown = 0;
            foreach (TinSurfaceTriangle t in fg.GetTriangles(true))
            {
                if (t.IsVisible || shown >= 3) continue;
                shown++;
                Say($"{fg.Name} hidden triangle: {P(t.Vertex1.Location)} {P(t.Vertex2.Location)} {P(t.Vertex3.Location)} IsVisible={t.IsVisible} IsValid={t.IsValid}");
            }
            var tri = fg.FindTriangleAtXY(125, 30);
            Say($"{fg.Name}.FindTriangleAtXY(125, 30) (visible P1 face): {P(tri.Vertex1.Location)} {P(tri.Vertex2.Location)} {P(tri.Vertex3.Location)} IsVisible={tri.IsVisible}");
            Try($"{fg.Name}.FindTriangleAtXY(125, 0) (inside the hide boundary)", () => { var h = fg.FindTriangleAtXY(125, 0); Say($"{fg.Name}.FindTriangleAtXY(125, 0): IsVisible={h?.IsVisible}"); });
            var vol = Surf<TinVolumeSurface>(tr, db, VolA);
            Say($"{vol.Name} is a {vol.GetType().Name} (base {vol.GetType().BaseType?.Name}); it has no GetTriangles: triangles come from the base and comparison TinSurfaces. GetTinProperties().NumberOfTriangles={vol.GetTinProperties().NumberOfTriangles}");
            tr.Commit();
        }

        Say($"-- outer boundary {F(OuterB.X0)}..{F(OuterB.X1)} x {F(OuterB.Y0)}..{F(OuterB.Y1)} on {N.FgB} (non-destructive) --");
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var fg = Surf<TinSurface>(tr, db, N.FgB, OpenMode.ForWrite);
            Say($"before: {TriCounts(fg)}");
            using (fg.BoundariesDefinition.AddBoundaries(Rect(OuterB), BoundaryMidOrdinate, SurfaceBoundaryType.Outer, true)) { }
            fg.Rebuild();
            Say($"after outer boundary: {TriCounts(fg)}");
            int outside = 0, hiddenOutside = 0;
            foreach (TinSurfaceTriangle t in fg.GetTriangles(true))
            {
                double cx = (t.Vertex1.Location.X + t.Vertex2.Location.X + t.Vertex3.Location.X) / 3, cy = (t.Vertex1.Location.Y + t.Vertex2.Location.Y + t.Vertex3.Location.Y) / 3;
                if (cx < OuterB.X0 || cx > OuterB.X1 || cy < OuterB.Y0 || cy > OuterB.Y1) { outside++; if (!t.IsVisible) hiddenOutside++; }
            }
            Say($"triangles with centroid outside the outer boundary: {outside}, of which hidden: {hiddenOutside}");
            tr.Commit();
        }

        Say($"-- mask {F(MaskB.X0)}..{F(MaskB.X1)} x {F(MaskB.Y0)}..{F(MaskB.Y1)} on {N.FgB} --");
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var fg = Surf<TinSurface>(tr, db, N.FgB, OpenMode.ForWrite);
            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
            var pl = new Polyline();
            var r = MaskB;
            pl.AddVertexAt(0, new Point2d(r.X0, r.Y0), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(r.X1, r.Y0), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(r.X1, r.Y1), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(r.X0, r.Y1), 0, 0, 0);
            pl.Closed = true;
            pl.SetDatabaseDefaults(db);
            var plId = ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            var mats = (DBDictionary)tr.GetObject(db.MaterialDictionaryId, OpenMode.ForRead);
            var matId = mats.Contains(MaskMaterial) ? mats.GetAt(MaskMaterial) : ObjectId.Null;
            Say($"material '{MaskMaterial}' found: {!matId.IsNull}");
            Try("Masks.Add", () =>
            {
                var data = new SurfaceMaskCreationData("PA-MASK", "P23 probe", fg.ObjectId, new ObjectIdCollection { plId }, BoundaryMidOrdinate, matId, SurfaceMaskType.InSide, false);
                var m = fg.Masks.Add(data);
                Say($"mask added: Masks.Count={fg.Masks.Count} type={m.Type}");
            });
            Say($"after mask (no rebuild): {TriCounts(fg)}; IsOutOfDate={fg.IsOutOfDate}");
            fg.Rebuild();
            Say($"after mask + Rebuild: {TriCounts(fg)}");
            tr.Commit();
        }
    });

    /// <summary>P23 part 2: cost of reading visible triangles at scale. Builds a grid TIN of
    /// n x n points (2(n-1)^2 triangles) with AddVertices, then times GetTriangles(false) into the
    /// contract's Tri3 and a window filter. Prompts for n.</summary>
    [CommandMethod("SVPROBEA_P23SCALE", CommandFlags.Modal)]
    public static void P23Scale() => Run("SVPROBEA_P23SCALE", (doc, db) =>
    {
        var pr = doc.Editor.GetInteger(new PromptIntegerOptions("\nGrid points per side: ") { LowerLimit = 3, UpperLimit = 2000 });
        if (pr.Status != PromptStatus.OK) { Say($"{FailCode} SVPROBEA_P23SCALE: no grid size"); return; }
        int n = pr.Value;
        string name = $"{GridPrefix}{n}";
        var proc = Process.GetCurrentProcess();
        long ws0 = proc.WorkingSet64;
        var sw = Stopwatch.StartNew();
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var id = TinSurface.Create(db, name);
            var s = (TinSurface)tr.GetObject(id, OpenMode.ForWrite);
            var pts = new Point3dCollection();
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    pts.Add(new Point3d(i * GridSpacing, j * GridSpacing, GridZ + 0.01 * ((i * 7 + j * 13) % 17)));
            double tPts = sw.Elapsed.TotalSeconds;
            using (s.AddVertices(pts)) { }
            double tAdd = sw.Elapsed.TotalSeconds;
            s.Rebuild();
            double tBuild = sw.Elapsed.TotalSeconds;
            Say($"{name}: {n * n} points, triangles={s.GetTinProperties().NumberOfTriangles} (expected {2 * (n - 1) * (n - 1)}); point list {tPts:0.00} s, AddVertices {tAdd - tPts:0.00} s, Rebuild {tBuild - tAdd:0.00} s");
            tr.Commit();
        }
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var s = Surf<TinSurface>(tr, db, name);
            proc.Refresh(); long ws1 = proc.WorkingSet64;
            var t0 = Stopwatch.StartNew();
            var list = new List<Tri3>();
            using (var tris = s.GetTriangles(false))
            {
                double tGet = t0.Elapsed.TotalSeconds;
                foreach (TinSurfaceTriangle t in tris)
                {
                    var a = t.Vertex1.Location; var b = t.Vertex2.Location; var c = t.Vertex3.Location;
                    list.Add(new Tri3(new Pt3(a.X, a.Y, a.Z), new Pt3(b.X, b.Y, b.Z), new Pt3(c.X, c.Y, c.Z)));
                    t.Dispose();
                }
                Say($"{name}: GetTriangles(false) call {tGet:0.000} s; read {list.Count} triangles with 3 vertex Locations each into Tri3 in {t0.Elapsed.TotalSeconds:0.00} s ({(list.Count / Math.Max(t0.Elapsed.TotalSeconds, 1e-9)):0} tri/s)");
            }
            proc.Refresh(); long ws2 = proc.WorkingSet64;
            var t1 = Stopwatch.StartNew();
            int count2 = 0;
            foreach (TinSurfaceTriangle t in s.Triangles) { _ = t.Vertex1.Location; _ = t.Vertex2.Location; _ = t.Vertex3.Location; count2++; }
            Say($"{name}: Triangles property enumeration (no Dispose per triangle) {count2} triangles in {t1.Elapsed.TotalSeconds:0.00} s");
            var t2 = Stopwatch.StartNew();
            double wx0 = n * GridSpacing * 0.25, wx1 = n * GridSpacing * 0.5;
            int inWin = list.Count(t => Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)) >= wx0 && Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)) <= wx1
                                   && Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)) >= wx0 && Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)) <= wx1);
            Say($"{name}: window filter over the cached Tri3 list: {inWin} triangles in [{F(wx0)},{F(wx1)}]^2 in {t2.Elapsed.TotalMilliseconds:0} ms");
            Say($"{name}: working set before read {ws1 / 1048576} MB, after Tri3 list {ws2 / 1048576} MB (process start of command {ws0 / 1048576} MB)");
            tr.Commit();
        }
        Say($"{name}: total {sw.Elapsed.TotalSeconds:0.00} s");
    });
}

// S1 probes: P9 (what headless Civil 3D needs), P10 (CivilDocument lookup in Core
// Console, active and side databases) and P6 (volume surface over data-shortcut
// references). P6 reaches AeccDataShortcutMgd.dll by reflection: the csproj is frozen
// to AcCoreMgd, AcDbMgd, AecBaseMgd and AeccDbMgd (HANDOFF.md 6.1), and a probe must
// not change that.

using System.Reflection;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DataShortcuts;
using static SheetVol.Civil3D.SelfTest.ProbeASettings;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using N = SheetVol.Civil3D.SelfTest.FixtureSpec.Names;

namespace SheetVol.Civil3D.SelfTest;

public static partial class ProbeACommands
{
    /// <summary>P9: prints the host's switches and loaded Civil modules, then tries each kind of
    /// object creation in its own method, so a missing Civil assembly shows as one failed step.</summary>
    [CommandMethod("SVPROBEA_P9", CommandFlags.Modal)]
    public static void P9() => Run("SVPROBEA_P9", (doc, db) =>
    {
        Say($"command line: {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}");
        Say($"process: {Environment.ProcessPath}");
        Try("SECURELOAD", () => Say($"SECURELOAD={AcApp.GetSystemVariable("SECURELOAD")} (read only, never set)"));
        Try("DynamicLinker.GetLoadedModules", () =>
        {
            var mods = SystemObjects.DynamicLinker.GetLoadedModules().Cast<string>().ToList();
            var civil = mods.Where(m => Path.GetFileName(m).StartsWith("Aec", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName).ToList();
            Say($"loaded ARX/DBX modules: {mods.Count}; Aec*: {civil.Count}: {string.Join(", ", civil)}");
        });
        var asms = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name is { } n && (n.StartsWith("Aecc", StringComparison.OrdinalIgnoreCase) || n.StartsWith("AecBase", StringComparison.OrdinalIgnoreCase)))
            .Select(a => $"{a.GetName().Name} {a.GetName().Version} ({(a.IsDynamic ? "dynamic" : a.Location)})").ToList();
        Say($"managed Aec*/Aecc* assemblies loaded before any Civil call: {asms.Count}: {string.Join("; ", asms)}");

        Try("P9 step 1 CivilApplication.ActiveDocument", () => P9Active());
        Try("P9 step 2 CivilDocument.GetCivilDocument(db)", () => P9GetCivil(db));
        Try("P9 step 3 TinSurface create + volume surface", () => P9Tin(db));
        Try("P9 step 4 Alignment create", () => P9Alignment(db));
        var after = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name is { } n && n.StartsWith("Aecc", StringComparison.OrdinalIgnoreCase)).Select(a => a.GetName().Name).ToList();
        Say($"managed Aecc* assemblies loaded after the Civil calls: {string.Join(", ", after)}");
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void P9Active()
    {
        var civ = CivilApplication.ActiveDocument;
        Say($"CivilApplication.ActiveDocument: {(civ is null ? "null" : "ok")}; ActiveProduct={CivilApplication.ActiveProduct}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void P9GetCivil(Database db)
    {
        var civ = CivilDocument.GetCivilDocument(db);
        Say($"CivilDocument.GetCivilDocument(db): {(civ is null ? "null" : $"ok, {civ.GetSurfaceIds().Count} surfaces, {civ.GetAlignmentIds().Count} alignments, {civ.Styles.SurfaceStyles.Count} surface styles")}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void P9Tin(Database db)
    {
        using var tr = db.TransactionManager.StartTransaction();
        ObjectId Make(string name, double z)
        {
            var id = TinSurface.Create(db, name);
            var s = (TinSurface)tr.GetObject(id, OpenMode.ForWrite);
            using (s.AddVertices(new Autodesk.AutoCAD.Geometry.Point3dCollection
                   { new(0, 0, 100), new(100, 0, 100), new(100, 100, 100), new(0, 100, 100), new(50, 50, z) })) { }
            s.Rebuild();
            Say($"TinSurface '{name}' created: {s.GetTinProperties().NumberOfTriangles} triangles");
            return id;
        }
        string tag = Guid.NewGuid().ToString("N")[..6];
        var a = Make($"PA-P9-BASE-{tag}", 100);
        var b = Make($"PA-P9-COMP-{tag}", 103);
        var vid = TinVolumeSurface.Create($"PA-P9-VOL-{tag}", a, b);
        var v = (TinVolumeSurface)tr.GetObject(vid, OpenMode.ForWrite);
        if (v.IsOutOfDate) v.Rebuild();
        var p = v.GetVolumeProperties();
        Say($"TinVolumeSurface created: UnadjustedFill={R(p.UnadjustedFillVolume)} (cone 10000 x 3 / 3 = 10000) UnadjustedCut={R(p.UnadjustedCutVolume)}");
        tr.Commit();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void P9Alignment(Database db)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var civ = CivilDocument.GetCivilDocument(db);
        string name = $"PA-P9-AL-{Guid.NewGuid().ToString("N")[..6]}";
        var id = Alignment.Create(civ, name, ObjectId.Null, db.LayerZero, civ.Styles.AlignmentStyles[0], civ.Styles.LabelSetStyles.AlignmentLabelSetStyles[0]);
        var al = (Alignment)tr.GetObject(id, OpenMode.ForWrite);
        al.Entities.AddFixedLine(new Autodesk.AutoCAD.Geometry.Point3d(0, 0, 0), new Autodesk.AutoCAD.Geometry.Point3d(100, 0, 0));
        Say($"Alignment '{name}' created: stations {R(al.StartingStation)}..{R(al.EndingStation)}");
        tr.Commit();
    }

    /// <summary>P10: CivilApplication.ActiveDocument vs CivilDocument.GetCivilDocument on the
    /// active database, on side databases read with ReadDwgFile, and on a new empty database.</summary>
    [CommandMethod("SVPROBEA_P10", CommandFlags.Modal)]
    public static void P10() => Run("SVPROBEA_P10", (doc, db) =>
    {
        var active = CivilApplication.ActiveDocument;
        var byDb = CivilDocument.GetCivilDocument(db);
        Say($"active db: ActiveDocument {(active is null ? "null" : "ok")}, GetCivilDocument(db) {(byDb is null ? "null" : "ok")}, ReferenceEquals={ReferenceEquals(active, byDb)}, Equals={active?.Equals(byDb)}");
        Say($"active db: ActiveDocument surfaces={active?.GetSurfaceIds().Count} alignments={active?.GetAlignmentIds().Count}; GetCivilDocument(db) surfaces={byDb?.GetSurfaceIds().Count} alignments={byDb?.GetAlignmentIds().Count}");
        Say($"HostApplicationServices.WorkingDatabase is the active db: {HostApplicationServices.WorkingDatabase == db}");

        foreach (var path in new[] { BlankDrawing, SourcePath })
        {
            if (!File.Exists(path)) { Say($"side db {path}: file not found, skipped"); continue; }
            Try($"side db {Path.GetFileName(path)}", () =>
            {
                using var side = new Database(false, true);
                side.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
                side.CloseInput(true);
                var civ = CivilDocument.GetCivilDocument(side);
                Say($"side db {Path.GetFileName(path)}: GetCivilDocument {(civ is null ? "null" : "ok")}; surfaces={civ?.GetSurfaceIds().Count} alignments={civ?.GetAlignmentIds().Count} surface styles={civ?.Styles.SurfaceStyles.Count}; DrawingUnits={civ?.Settings.DrawingSettings.UnitZoneSettings.DrawingUnits}");
                Say($"side db {Path.GetFileName(path)}: ActiveDocument still the active drawing: surfaces={CivilApplication.ActiveDocument.GetSurfaceIds().Count}; ReferenceEquals(side, active)={ReferenceEquals(civ, active)}");
                if (civ is null) return;
                using var tr = side.TransactionManager.StartTransaction();
                foreach (ObjectId id in civ.GetSurfaceIds())
                {
                    var s = (Autodesk.Civil.DatabaseServices.Surface)tr.GetObject(id, OpenMode.ForRead);
                    string extra = "";
                    if (s is TinSurface t) extra = $" triangles={t.GetTinProperties().NumberOfTriangles} z(125,0)={R(t.FindElevationAtXY(125, 0))}";
                    Say($"side db {Path.GetFileName(path)}:   {s.Name} {s.GetType().Name}{extra} (WorkingDatabase NOT switched)");
                }
                tr.Commit();
            });
        }
        Try("new Database(true, true)", () =>
        {
            using var fresh = new Database(true, true);
            var civ = CivilDocument.GetCivilDocument(fresh);
            Say($"new Database(true, true): GetCivilDocument {(civ is null ? "null" : "ok")}; surfaces={civ?.GetSurfaceIds().Count} surface styles={civ?.Styles.SurfaceStyles.Count} alignment styles={civ?.Styles.AlignmentStyles.Count}");
        });
    });

    /// <summary>P6: reference checks on native surfaces, then a data-shortcut reference built
    /// without a shortcut project, if the API allows it: source drawing (Fixture A) and host
    /// drawing are side databases from blank-c3d.dwg; the host references EG and FG_A from the
    /// source with DataShortcuts.CreateReference and builds a TinVolumeSurface on them.</summary>
    [CommandMethod("SVPROBEA_P6", CommandFlags.Modal)]
    public static void P6() => Run("SVPROBEA_P6", (doc, db) =>
    {
        using (var tr = db.TransactionManager.StartTransaction())
        {
            foreach (var name in new[] { N.EG, N.FgA, VolA })
            {
                var s = Surf<Autodesk.Civil.DatabaseServices.Surface>(tr, db, name);
                Say($"native {s.Name}: {RefFlags(s)}");
            }
            tr.Commit();
        }

        // The managed data-shortcut API.
        var dsAsm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name + ".dll", DataShortcutAssembly, StringComparison.OrdinalIgnoreCase));
        Say($"{DataShortcutAssembly} already loaded: {dsAsm is not null}");
        if (dsAsm is null)
        {
            var dir = Path.GetDirectoryName(typeof(CivilDocument).Assembly.Location)!;
            if (!Try($"Assembly.LoadFrom({DataShortcutAssembly})", () => dsAsm = System.Reflection.Assembly.LoadFrom(Path.Combine(dir, DataShortcutAssembly)))) return;
        }
        var ds = dsAsm!.GetType(DataShortcutType) ?? throw new InvalidOperationException($"{DataShortcutType} not found");
        Say($"{DataShortcutType} public static methods: {string.Join(", ", ds.GetMethods(BindingFlags.Public | BindingFlags.Static).Select(m => m.Name).Distinct())}");
        Say($"methods named *Sync*: {ds.GetMethods().Count(m => m.Name.Contains("Sync", StringComparison.OrdinalIgnoreCase))}");
        // Read only, and only whether they are set: the values are the user's own project
        // paths and names, which do not belong in a log or in docs/api-findings.md.
        Try("DataShortcuts.GetWorkingFolder (read only)", () => Say($"working folder set: {!string.IsNullOrEmpty(ds.GetMethod("GetWorkingFolder")!.Invoke(null, null) as string)} (value not printed)"));
        Try("DataShortcuts.GetCurrentProjectFolder (read only)", () => Say($"current shortcut project set: {!string.IsNullOrEmpty(ds.GetMethod("GetCurrentProjectFolder")!.Invoke(null, null) as string)} (value not printed)"));
        var createRef = ds.GetMethod("CreateReference", new[] { typeof(Database), typeof(string), typeof(string), typeof(DataShortcutEntityType) })
            ?? throw new InvalidOperationException("CreateReference(Database, string, string, DataShortcutEntityType) not found");
        var createRefDb = ds.GetMethod("CreateReference", new[] { typeof(Database), typeof(Database), typeof(string), typeof(DataShortcutEntityType) })
            ?? throw new InvalidOperationException("CreateReference(Database, Database, string, DataShortcutEntityType) not found");

        Directory.CreateDirectory(OutDir);
        var prevWorking = HostApplicationServices.WorkingDatabase;
        Database? src = null, host = null;
        try
        {
            // 1. Source drawing with Fixture A.
            src = new Database(false, true);
            src.ReadDwgFile(BlankDrawing, FileOpenMode.OpenForReadAndAllShare, true, null);
            src.CloseInput(true);
            HostApplicationServices.WorkingDatabase = src;
            var a = Fixtures.CreateA(src);
            HostApplicationServices.WorkingDatabase = prevWorking;
            if (File.Exists(SourcePath)) File.Delete(SourcePath);
            src.SaveAs(SourcePath, DwgVersion.Current);
            Say($"source: Fixture A built in a side db ({a.Checks.Count} checks) and saved to {SourcePath}");

            // 2. Host drawing referencing EG and FG_A.
            host = new Database(false, true);
            host.ReadDwgFile(BlankDrawing, FileOpenMode.OpenForReadAndAllShare, true, null);
            host.CloseInput(true);
            HostApplicationServices.WorkingDatabase = host;
            var refIds = new Dictionary<string, ObjectId>();
            foreach (var name in new[] { N.EG, N.FgA })
            {
                Try($"CreateReference(host, source file name, '{name}', Surface)", () =>
                {
                    var ids = (ObjectIdCollection)createRef.Invoke(null, new object[] { host, SourcePath, name, DataShortcutEntityType.Surface })!;
                    Say($"CreateReference '{name}' (file name overload): {ids.Count} id(s): {string.Join(", ", ids.Cast<ObjectId>().Select(i => i.ObjectClass.DxfName))}");
                    if (ids.Count > 0) refIds[name] = ids[0];
                });
                if (refIds.ContainsKey(name)) continue;
                Try($"CreateReference(host, source Database, '{name}', Surface)", () =>
                {
                    var ids = (ObjectIdCollection)createRefDb.Invoke(null, new object[] { host, src, name, DataShortcutEntityType.Surface })!;
                    Say($"CreateReference '{name}' (Database overload): {ids.Count} id(s): {string.Join(", ", ids.Cast<ObjectId>().Select(i => i.ObjectClass.DxfName))}");
                    if (ids.Count > 0) refIds[name] = ids[0];
                });
            }
            if (refIds.Count == 2)
            {
                using (var tr = host.TransactionManager.StartTransaction())
                {
                    foreach (var (name, id) in refIds)
                    {
                        var s = (TinSurface)tr.GetObject(id, OpenMode.ForRead);
                        Say($"host reference {s.Name}: {RefFlags(s)}; triangles visible={s.GetTriangles(false).Count} all={s.GetTriangles(true).Count}; z(125,0)={R(s.FindElevationAtXY(125, 0))}");
                        Try($"host reference {s.Name}.GetReferenceInfo", () => { var k = s.GetReferenceInfo(); Say($"  GetReferenceInfo: Name={k?.Name} Type={k?.Type} SourceDrawing={k?.SourceDrawing} exists={k?.IsSourceDrawingExistent}"); });
                    }
                    tr.Commit();
                }
                ObjectId volId = ObjectId.Null;
                Try("TinVolumeSurface.Create on the two references", () => volId = TinVolumeSurface.Create(VolA, refIds[N.EG], refIds[N.FgA]));
                if (!volId.IsNull)
                {
                    using var tr = host.TransactionManager.StartTransaction();
                    var v = (TinVolumeSurface)tr.GetObject(volId, OpenMode.ForWrite);
                    Say($"host volume surface: IsOutOfDate={v.IsOutOfDate} {RefFlags(v)}");
                    if (v.IsOutOfDate) v.Rebuild();
                    var p = v.GetVolumeProperties();
                    Say($"host volume surface on references: UnadjustedCut={R(p.UnadjustedCutVolume)} UnadjustedFill={R(p.UnadjustedFillVolume)} (native Fixture A: 5400 / 5400)");
                    Bounded(v, Rect(AroundP1), "P1 polygon (host, references)");
                    tr.Commit();
                }

                // 3. Change the source (P1 apex 99 -> 98), save it over the referenced file.
                HostApplicationServices.WorkingDatabase = src;
                using (var tr = src.TransactionManager.StartTransaction())
                {
                    var fg = (TinSurface)tr.GetObject(a.ComparisonId, OpenMode.ForWrite);
                    var apex = fg.FindVertexAtXY(FixtureSpec.Pyramids[0].CenterX, 0);
                    using (fg.SetVertexElevation(apex, FixtureSpec.Pyramids[0].ApexZ - 1.0)) { }
                    fg.Rebuild();
                    Say($"source: P1 apex set to {F(FixtureSpec.Pyramids[0].ApexZ - 1.0)}; source FG_A z(125,0)={R(fg.FindElevationAtXY(125, 0))}");
                    tr.Commit();
                }
                HostApplicationServices.WorkingDatabase = prevWorking;
                var tmp = Path.Combine(OutDir, "probeA-src-2.dwg");
                if (File.Exists(tmp)) File.Delete(tmp);
                src.SaveAs(tmp, DwgVersion.Current);
                Try("copy the changed source over the referenced file", () => File.Copy(tmp, SourcePath, true));

                HostApplicationServices.WorkingDatabase = host;
                using (var tr = host.TransactionManager.StartTransaction())
                {
                    foreach (var (name, id) in refIds)
                    {
                        var s = (TinSurface)tr.GetObject(id, OpenMode.ForRead);
                        Say($"host reference {s.Name} after the source changed: {RefFlags(s)}; z(125,0)={R(s.FindElevationAtXY(125, 0))}");
                    }
                    if (!volId.IsNull)
                    {
                        var v = (TinVolumeSurface)tr.GetObject(volId, OpenMode.ForRead);
                        Say($"host volume surface after the source changed: IsOutOfDate={v.IsOutOfDate}");
                        Bounded(v, Rect(AroundP1), "P1 polygon (host, before any synchronize)");
                    }
                    tr.Commit();
                }
                HostApplicationServices.WorkingDatabase = prevWorking;
                if (File.Exists(HostPath)) File.Delete(HostPath);
                host.SaveAs(HostPath, DwgVersion.Current);
                Say($"host saved to {HostPath} for the manual synchronize step");
            }
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = prevWorking;
            src?.Dispose();
            host?.Dispose();
        }
    });

    private static string RefFlags(Autodesk.Civil.DatabaseServices.Entity e)
    {
        string Get(Func<object> f) { try { return f().ToString() ?? "null"; } catch (System.Exception ex) { return "threw " + ex.GetType().Name; } }
        return $"IsReferenceObject={Get(() => e.IsReferenceObject)} IsReferenceStale={Get(() => e.IsReferenceStale)} IsReferenceValid={Get(() => e.IsReferenceValid)} IsReadOnlyReferenceObject={Get(() => e.IsReadOnlyReferenceObject)} IsReferencedSourceExisting={Get(() => e.IsReferencedSourceExisting)}";
    }
}

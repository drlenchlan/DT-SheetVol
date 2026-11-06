// S4 probe D commands. Run order is tools/scripts/probeD.scr and matters:
//   SVPROBED              items 1 (members, values), 2 (user vs owned bounded), 3 (factors)
//   SVPROBED_OUTER        item 6 (outer boundary on a second user volume surface)
//   SVPROBED_EDIT         item 4: lowers P3's apex on FG_A and rebuilds FG_A only
//   SVPROBED_STALE        item 4: the next command, nothing rebuilt
//   SVPROBED_REBUILD      item 4: rebuilds the user surface beside an undo marker
//   _.UNDO 1, SVPROBED_UNDONE   item 4: did the rebuild go on the undo stack?
//   SVPROBED_OWNED        item 4: A1's path picks the change up
//   SVPROBED_ERASE, SVPROBED_AFTERERASE   item 5: EG erased (last: it breaks every pair)

using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using static SheetVol.Civil3D.SelfTest.ProbeACommands;
using static SheetVol.Civil3D.SelfTest.ProbeDSettings;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using N = SheetVol.Civil3D.SelfTest.FixtureSpec.Names;

namespace SheetVol.Civil3D.SelfTest;

public static partial class ProbeDCommands
{
    private static Type[] AeccTypes()
    {
        try { return typeof(TinVolumeSurface).Assembly.GetExportedTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null).ToArray()!; }
    }

    /// <summary>Items 1, 2, 3.</summary>
    [CommandMethod("SVPROBED", CommandFlags.Modal)]
    public static void Probe() => Run("SVPROBED", (doc, db) =>
    {
        // ---------- item 1: where the members hang, and their values ----------
        var asm = typeof(TinVolumeSurface).Assembly;
        Say($"D1 loaded {asm.GetName().Name} {asm.GetName().Version} from {asm.Location}");
        var types = AeccTypes();
        foreach (var name in new[] { "BaseSurfaceId", "ComparisonSurfaceId", "BaseSurface", "ComparisonSurface", "CutFactor", "FillFactor", "BoundariesDefinition" })
        {
            var hits = types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(p => p.Name == name).Select(p => $"{t.FullName}.{p.Name}:{p.PropertyType.Name}{(p.SetMethod?.IsPublic == true ? " get/set" : " get")}"))
                .ToList();
            Say($"D1 public properties named {name}: {(hits.Count == 0 ? "none" : string.Join("; ", hits))}");
        }
        Say($"D1 VolumeSurfaceProperties public properties: {string.Join(", ", typeof(VolumeSurfaceProperties).GetProperties().Select(p => $"{p.Name}:{p.PropertyType.Name}"))}");
        Say($"D1 TinVolumeSurface declared public properties: {string.Join(", ", typeof(TinVolumeSurface).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => $"{p.Name}:{p.PropertyType.Name}{(p.SetMethod?.IsPublic == true ? " get/set" : "")}"))}");

        using (var tr = db.TransactionManager.StartTransaction())
        {
            var vol = Surf<TinVolumeSurface>(tr, db, UserVol);
            var p = vol.GetVolumeProperties();
            Say($"D1 {vol.Name} handle {vol.Handle}: props.BaseSurface={NameOf(tr, p.BaseSurface)} props.ComparisonSurface={NameOf(tr, p.ComparisonSurface)}");
            Say($"D1 {vol.Name}: props.CutFactor={R(p.CutFactor)} props.FillFactor={R(p.FillFactor)} vol.CutFactor={R(vol.CutFactor)} vol.FillFactor={R(vol.FillFactor)}");
            Say($"D1 {vol.Name}: UnadjustedCut={R(p.UnadjustedCutVolume)} UnadjustedFill={R(p.UnadjustedFillVolume)} UnadjustedNet={R(p.UnadjustedNetVolume)} AdjustedCut={R(p.AdjustedCutVolume)} AdjustedFill={R(p.AdjustedFillVolume)} AdjustedNet={R(p.AdjustedNetVolume)}");
            Say($"D1 {vol.Name}: IsVolumeSurface={vol.IsVolumeSurface} IsOutOfDate={vol.IsOutOfDate} AutoRebuild={S(() => vol.AutoRebuild)} IsReferenceObject={S(() => vol.IsReferenceObject)} triangles={S(() => vol.GetTinProperties().NumberOfTriangles)}");
            tr.Commit();
        }

        // ---------- item 2: user surface vs A1's owned surface, same pair, same polygons ----------
        Say($"D2 FixtureSpec.Expected.T2 holds {Segs.Length} segments (breaks {string.Join(", ", Segs.Select(g => F(g.From)).Append(F(Segs[^1].To)))}); offsets ({F(ProbeASettings.OffsetLeft)}, {F(ProbeASettings.OffsetRight)}) on {N.AlignmentS}");
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var owned = EnsureOwned(tr, db, "D2") ?? throw new InvalidOperationException("owned surface not ensured");
            var vol = Surf<TinVolumeSurface>(tr, db, UserVol);
            var s = Align(tr, db, N.AlignmentS);
            var u = Segments(vol, s, "user");
            var o = Segments(owned, s, "owned");
            Say("D2 | seg | user Cut     | owned Cut    | diff Cut  | user Fill    | owned Fill   | diff Fill | expected C/F ft3");
            for (int i = 0; i < Segs.Length; i++)
                Say($"D2 | {i + 1}   | {S(() => u[i]?.Cut),12} | {S(() => o[i]?.Cut),12} | {Diff(u[i]?.Cut, o[i]?.Cut),9} | {S(() => u[i]?.Fill),12} | {S(() => o[i]?.Fill),12} | {Diff(u[i]?.Fill, o[i]?.Fill),9} | {F(Segs[i].CutFt3)}/{F(Segs[i].FillFt3)}");
            tr.Commit();
        }

        // ---------- item 3: factors set on the user surface ----------
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var vol = Surf<TinVolumeSurface>(tr, db, UserVol, OpenMode.ForWrite);
            Try($"D3 set CutFactor={F(CutFactor)}", () => vol.CutFactor = CutFactor);
            Try($"D3 set FillFactor={F(FillFactor)}", () => vol.FillFactor = FillFactor);
            State("D3 same transaction as the set", vol);
            tr.Commit();
        }
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var vol = Surf<TinVolumeSurface>(tr, db, UserVol);
            var s = Align(tr, db, N.AlignmentS);
            State("D3 new transaction, no Rebuild", vol);
            var f = Segments(vol, s, "user, factors 1.25/0.9");
            Bounded(vol, Band(s, new[] { Segs[0].From, Segs[^1].To }, ProbeASettings.OffsetLeft, ProbeASettings.OffsetRight), "whole band [0,2000] user, factors 1.25/0.9");
            for (int i = 0; i < Segs.Length; i++)
            {
                if (f[i] is not { } v) continue;
                Say($"D3 seg {i + 1}: bounded Cut={R(v.Cut)} Fill={R(v.Fill)}; raw x factor would be Cut={R(Segs[i].CutFt3 * CutFactor)} Fill={R(Segs[i].FillFt3 * FillFactor)}; raw is Cut={R(Segs[i].CutFt3)} Fill={R(Segs[i].FillFt3)}");
            }
            tr.Commit();
        }
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var vol = Surf<TinVolumeSurface>(tr, db, UserVol, OpenMode.ForWrite);
            Try("D3 Rebuild with factors 1.25/0.9", () => vol.Rebuild());
            State("D3 after Rebuild", vol);
            var s = Align(tr, db, N.AlignmentS);
            Segment(vol, s, 1, "user, factors 1.25/0.9, after Rebuild");
            vol.CutFactor = 1.0;
            vol.FillFactor = 1.0;
            State("D3 factors reset to 1/1", vol);
            tr.Commit();
        }
    });

    /// <summary>Item 6: an outer boundary on a second user volume surface over EG / FG_A.</summary>
    [CommandMethod("SVPROBED_OUTER", CommandFlags.Modal)]
    public static void Outer6() => Run("SVPROBED_OUTER", (doc, db) =>
    {
        var made = Fixtures.CreateVolume(db, N.EG, N.FgA, UserVolOuter);
        Say($"D6 created {made.Name}: base={made.BaseName} comparison={made.ComparisonName} triangles={made.Triangles} UnadjustedCut={R(made.UnadjustedCut)} UnadjustedFill={R(made.UnadjustedFill)}");
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var vol = Surf<TinVolumeSurface>(tr, db, UserVolOuter);
            Segments(vol, Align(tr, db, N.AlignmentS), "no boundary");
            tr.Commit();
        }
        bool added = false;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var vol = Surf<TinVolumeSurface>(tr, db, UserVolOuter, OpenMode.ForWrite);
            Say($"D6 outer boundary x {F(Outer.X0)}..{F(Outer.X1)} y {F(Outer.Y0)}..{F(Outer.Y1)}: holds P1, P2 (segment 1); excludes P3 (segment 2), P4, P5 (segment 3)");
            Say($"D6 {vol.Name}.BoundariesDefinition: {S(() => vol.BoundariesDefinition.GetType().FullName)} Count={S(() => vol.BoundariesDefinition.Count)}");
            foreach (var nonDestructive in new[] { true, false })
            {
                if (added) break;
                added = Try($"D6 AddBoundaries(Outer, nonDestructive={nonDestructive})", () =>
                {
                    using var op = vol.BoundariesDefinition.AddBoundaries(Rect(Outer), BoundaryMidOrdinate, SurfaceBoundaryType.Outer, nonDestructive);
                    Say($"D6 AddBoundaries(Outer, nonDestructive={nonDestructive}) returned {op.GetType().Name} BoundaryType={op.BoundaryType} Count={op.Count}");
                });
            }
            Say($"D6 IsOutOfDate after AddBoundaries (same transaction) = {S(() => vol.IsOutOfDate)}");
            Try("D6 Rebuild", () => vol.Rebuild());
            Say($"D6 after Rebuild: triangles={S(() => vol.GetTinProperties().NumberOfTriangles)} BoundariesDefinition.Count={S(() => vol.BoundariesDefinition.Count)}");
            tr.Commit();
        }
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var vol = Surf<TinVolumeSurface>(tr, db, UserVolOuter);
            var user = Surf<TinVolumeSurface>(tr, db, UserVol);
            var s = Align(tr, db, N.AlignmentS);
            State("D6 bounded copy", vol);
            State("D6 unbounded user surface", user);
            Segments(vol, s, $"outer boundary {(added ? "added" : "NOT added")}");
            Bounded(vol, Rect(Outer), "the boundary polygon itself");
            tr.Commit();
        }
    });

    /// <summary>Item 4, edit: P3's apex on FG_A lowered by 1.0; FG_A rebuilt; volume surfaces untouched.</summary>
    [CommandMethod("SVPROBED_EDIT", CommandFlags.Modal)]
    public static void Edit4() => Run("SVPROBED_EDIT", (doc, db) =>
    {
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var fg = Surf<TinSurface>(tr, db, N.FgA, OpenMode.ForWrite);
            var apex = fg.FindVertexAtXY(P3Apex.X, P3Apex.Y);
            Say($"D4 {fg.Name} vertex at ({F(P3Apex.X)}, {F(P3Apex.Y)}): {P(apex.Location)}; setting z to {F(P3ApexZ2)}");
            using (fg.SetVertexElevation(apex, P3ApexZ2)) { }
            fg.Rebuild();
            Say($"D4 {fg.Name} rebuilt: z({F(P3Apex.X)},0)={S(() => fg.FindElevationAtXY(P3Apex.X, P3Apex.Y))} IsOutOfDate={fg.IsOutOfDate}");
            tr.Commit();
        }
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var s = Align(tr, db, N.AlignmentS);
            Say($"D4 expected segment 2 cut after the edit: {R(Seg2CutAfterEditFt3)} ft3 (was {R(Segs[1].CutFt3)})");
            foreach (var name in new[] { UserVol, OwnedName })
            {
                var vol = Surf<TinVolumeSurface>(tr, db, name);
                State("D4 same command as the edit", vol);
                Segment(vol, s, 2, "same command as the edit, no Rebuild");
            }
            tr.Commit();
        }
    });

    /// <summary>Item 4, the next command: nothing rebuilt.</summary>
    [CommandMethod("SVPROBED_STALE", CommandFlags.Modal | CommandFlags.NoUndoMarker)]   // read-only
    public static void Stale4() => Run("SVPROBED_STALE", (doc, db) =>
    {
        using var tr = db.TransactionManager.StartTransaction();
        var s = Align(tr, db, N.AlignmentS);
        foreach (var name in new[] { UserVol, OwnedName })
        {
            var vol = Surf<TinVolumeSurface>(tr, db, name);
            State("D4 next command, no Rebuild", vol);
            Segment(vol, s, 2, "next command, no Rebuild");
        }
        tr.Commit();
    });

    /// <summary>Item 4: rebuild the user surface, with an undo marker written in the same command.</summary>
    [CommandMethod("SVPROBED_REBUILD", CommandFlags.Modal)]
    public static void Rebuild4() => Run("SVPROBED_REBUILD", (doc, db) =>
    {
        Say($"D4 UNDOCTL={AcApp.GetSystemVariable("UNDOCTL")} (bit 1 = undo enabled)");
        using var tr = db.TransactionManager.StartTransaction();
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
        var xr = new Xrecord { Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, "S4 probe D undo marker")) };
        nod.SetAt(UndoMarkerKey, xr);
        tr.AddNewlyCreatedDBObject(xr, true);
        Say($"D4 marker {UndoMarkerKey} written, handle {xr.Handle}");
        var vol = Surf<TinVolumeSurface>(tr, db, UserVol, OpenMode.ForWrite);
        vol.Rebuild();
        State("D4 after user-surface Rebuild", vol);
        Segment(vol, Align(tr, db, N.AlignmentS), 2, "after user-surface Rebuild");
        tr.Commit();
    });

    /// <summary>Item 4, after the script's UNDO 1: marker and surface state.</summary>
    [CommandMethod("SVPROBED_UNDONE", CommandFlags.Modal | CommandFlags.NoUndoMarker)]   // read-only
    public static void Undone4() => Run("SVPROBED_UNDONE", (doc, db) =>
    {
        using var tr = db.TransactionManager.StartTransaction();
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        Say($"D4 after UNDO 1: marker {UndoMarkerKey} present={nod.Contains(UndoMarkerKey)}");
        var fg = Surf<TinSurface>(tr, db, N.FgA);
        Say($"D4 after UNDO 1: {fg.Name} z({F(P3Apex.X)},0)={S(() => fg.FindElevationAtXY(P3Apex.X, P3Apex.Y))} (control: the edit was an earlier command)");
        var vol = Surf<TinVolumeSurface>(tr, db, UserVol);
        State("D4 after UNDO 1", vol);
        Segment(vol, Align(tr, db, N.AlignmentS), 2, "after UNDO 1");
        tr.Commit();
    });

    /// <summary>Item 4: A1's path (EnsureAnalyses rebuilds the owned surface every call).</summary>
    [CommandMethod("SVPROBED_OWNED", CommandFlags.Modal)]
    public static void Owned4() => Run("SVPROBED_OWNED", (doc, db) =>
    {
        using var tr = db.TransactionManager.StartTransaction();
        var owned = EnsureOwned(tr, db, "D4 owned");
        if (owned is not null)
        {
            State("D4 owned after EnsureAnalyses", owned);
            Segment(owned, Align(tr, db, N.AlignmentS), 2, "owned after EnsureAnalyses");
        }
        tr.Commit();
    });

    private static ObjectId _userVolId;

    /// <summary>Item 5: erase EG, then read the user surface in the same command.</summary>
    [CommandMethod("SVPROBED_ERASE", CommandFlags.Modal)]
    public static void Erase5() => Run("SVPROBED_ERASE", (doc, db) =>
    {
        using (var tr = db.TransactionManager.StartTransaction())
        {
            _userVolId = Surf<TinVolumeSurface>(tr, db, UserVol).ObjectId;
            Say($"D5 before the erase: {UserVol} id {_userVolId} ObjectClass={_userVolId.ObjectClass.Name} wrapper={tr.GetObject(_userVolId, OpenMode.ForRead).GetType().FullName}");
            var eg = Surf<TinSurface>(tr, db, N.EG, OpenMode.ForWrite);
            Try($"D5 {N.EG}.Erase()", () => eg.Erase());
            Say($"D5 {N.EG} IsErased={eg.IsErased}");
            tr.Commit();
        }
        ReadAfterErase("D5 same command as the erase", db, rebuild: false);
    });

    /// <summary>Item 5, the next command: read, try Rebuild, read again, and A1's path.</summary>
    [CommandMethod("SVPROBED_AFTERERASE", CommandFlags.Modal)]
    public static void AfterErase5() => Run("SVPROBED_AFTERERASE", (doc, db) =>
    {
        ReadAfterErase("D5 next command", db, rebuild: true);
        using var tr = db.TransactionManager.StartTransaction();
        EnsureOwned(tr, db, "D5 A1 path with EG erased");
        tr.Commit();
    });

    private static void ReadAfterErase(string tag, Database db, bool rebuild)
    {
        var statusType = typeof(TinVolumeSurface).Assembly.GetType("Autodesk.Civil.SurfaceStatus");
        using var tr = db.TransactionManager.StartTransaction();
        foreach (ObjectId sid in Civil(db).GetSurfaceIds())
            Say($"{tag}: GetSurfaceIds holds {sid} IsErased={sid.IsErased} ObjectClass={sid.ObjectClass.Name} {(sid.IsErased ? "" : $"'{S(() => ((CivSurface)tr.GetObject(sid, OpenMode.ForRead)).Name)}'")}");
        object? obj = null;
        Try($"{tag}: GetObject({UserVol} id {_userVolId})", () => obj = tr.GetObject(_userVolId, OpenMode.ForRead));
        Say($"{tag}: id {_userVolId} IsErased={_userVolId.IsErased} IsValid={_userVolId.IsValid} ObjectClass={S(() => _userVolId.ObjectClass.Name)}; GetObject returned {(obj is null ? "null" : obj.GetType().FullName)}");
        if (obj is not CivSurface surf) { tr.Commit(); return; }
        var vol = surf as TinVolumeSurface;
        string Triangles() => S(() => vol?.GetTinProperties().NumberOfTriangles ?? (surf as TinSurface)?.GetTinProperties().NumberOfTriangles);
        Say($"{tag}: {surf.Name} IsErased={surf.IsErased} IsOutOfDate={S(() => surf.IsOutOfDate)} IsVolumeSurface={S(() => surf.IsVolumeSurface)} CutFactor={S(() => vol?.CutFactor)} triangles={Triangles()}");
        if (vol is not null)
            Try($"{tag}: {vol.Name}.GetVolumeProperties()", () =>
            {
                var p = vol.GetVolumeProperties();
                Say($"{tag}: GetVolumeProperties: BaseSurface={NameOf(tr, p.BaseSurface)} ComparisonSurface={NameOf(tr, p.ComparisonSurface)} UnadjustedCut={R(p.UnadjustedCutVolume)} UnadjustedFill={R(p.UnadjustedFillVolume)}");
            });
        if (statusType is not null)
        {
            Say($"{tag}: {statusType.FullName} values: {string.Join(", ", Enum.GetNames(statusType).Select(n => $"{n}={Convert.ToInt64(Enum.Parse(statusType, n))}"))}");
            var holders = AeccTypes().SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(p => p.PropertyType == statusType)).ToList();
            Say($"{tag}: public properties of type SurfaceStatus: {(holders.Count == 0 ? "none" : string.Join("; ", holders.Select(p => $"{p.DeclaringType!.FullName}.{p.Name}")))}");
            var returners = AeccTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => m.ReturnType == statusType)).ToList();
            Say($"{tag}: public methods returning SurfaceStatus: {(returners.Count == 0 ? "none" : string.Join("; ", returners.Select(m => $"{m.DeclaringType!.FullName}.{m.Name}")))}");
            foreach (var p in holders.Where(p => p.DeclaringType!.IsInstanceOfType(surf)))
                Say($"{tag}: {surf.Name}.{p.Name}={S(() => p.GetValue(surf))}");
        }
        var s = Align(tr, db, N.AlignmentS);
        Segment(surf, s, 1, tag);
        Say($"{tag}: z(125,0)={S(() => surf.FindElevationAtXY(125.0, 0.0))}");
        if (rebuild)
        {
            surf.UpgradeOpen();
            Try($"{tag}: {surf.Name}.Rebuild()", () => surf.Rebuild());
            Say($"{tag} after Rebuild: IsOutOfDate={S(() => surf.IsOutOfDate)} triangles={Triangles()}");
            if (vol is not null)
                Try($"{tag} after Rebuild: GetVolumeProperties()", () =>
                {
                    var p = vol.GetVolumeProperties();
                    Say($"{tag} after Rebuild: GetVolumeProperties: BaseSurface={NameOf(tr, p.BaseSurface)} UnadjustedCut={R(p.UnadjustedCutVolume)} UnadjustedFill={R(p.UnadjustedFillVolume)}");
                });
            Segment(surf, s, 1, tag + " after Rebuild");
        }
        tr.Commit();
    }
}

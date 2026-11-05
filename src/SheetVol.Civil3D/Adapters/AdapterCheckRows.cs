// A1 adapters: SVADAPTERS steps for the resolver and owned-surface lifecycle (E401-E404, E410,
// E411, E412, E442-E445, E803) and the volume source's polygon rule and noise clamp.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Volumes;
using N = SheetVol.Civil3D.SelfTest.FixtureSpec.Names;

namespace SheetVol.Civil3D.Adapters;

public static partial class AdapterCheckCommands
{
    private static bool Has(IEnumerable<Problem> problems, string code, Severity severity) => problems.Any(p => p.Code == code && p.Severity == severity);

    private static string Codes(IEnumerable<Problem> problems) => string.Join(", ", problems.Select(p => $"{p.Code} {p.Severity}"));

    /// <summary>E412 (collision, erased, renamed), E411 (shared pair), E410 (factors written once to the owned surface).</summary>
    private static void StepOwnedLifecycle()
    {
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            string name = OwnedName(N.EG, N.FgB);
            var user = SurfaceByName<TinVolumeSurface>(tr, name);
            var tag = OwnershipTag.Read(tr, user, _cfg);
            var ownedB = Ensure(tr, [new AnalysisDef(Guid.NewGuid(), "B", N.EG, N.FgB)])[0];
            Check("E412 name collision with an untagged look-alike", tag is null && user.CutFactor == AdapterCheckSettings.UserVolumeCutFactor && ownedB.Owned.Name != name,
                $"user '{name}' untagged={tag is null}, CutFactor still {R(user.CutFactor)}; owned surface is '{ownedB.Owned.Name}' (factor {R(ownedB.Owned.CutFactor)})");
            tr.Commit();
        }

        var d = new AnalysisDef(Guid.NewGuid(), "D", N.EG, N.FgD);
        string h0;
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var b0 = Ensure(tr, [d])[0];
            h0 = b0.Owned.Handle;
            Check("owned surface reused on a later regen", !b0.Owned.Created, $"'{b0.Owned.Name}' handle {h0} reused, rebuilt, factors {R(b0.Owned.CutFactor)}/{R(b0.Owned.FillFactor)}");
            ((TinVolumeSurface)tr.GetObject(b0.Owned.Id, OpenMode.ForWrite)).Erase();
            tr.Commit();
        }
        string h1;
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var stored = new Dictionary<Guid, StoredAnalysisRefs> { [d.Id] = new(new StoredRef(N.EG, null), new StoredRef(N.FgD, null), h0) };
            var o = AnalysisResolver.EnsureAnalyses(tr, _db, [d], stored, _cfg, VolCfg);
            Problems("E412 erased", o.Problems);
            h1 = o.Value[0].Owned.Handle;
            Check("E412 owned surface erased by a user", o.Succeeded && o.Value[0].Owned.Created && Has(o.Problems, AdapterCodes.OwnedSurfaceRecreated, Severity.Info),
                $"recreated as '{o.Value[0].Owned.Name}' handle {h1} (was {h0})");
            var vol = (TinVolumeSurface)tr.GetObject(o.Value[0].Owned.Id, OpenMode.ForWrite);
            vol.Name = vol.Name + AdapterCheckSettings.RenamedSuffix;
            tr.Commit();
        }
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var stored = new Dictionary<Guid, StoredAnalysisRefs> { [d.Id] = new(new StoredRef(N.EG, null), new StoredRef(N.FgD, null), h1) };
            var o = AnalysisResolver.EnsureAnalyses(tr, _db, [d], stored, _cfg, VolCfg);
            Problems("E412 renamed", o.Problems);
            string renamed = OwnedName(N.EG, N.FgD) + AdapterCheckSettings.RenamedSuffix;
            var old = SurfaceByName<TinVolumeSurface>(tr, renamed);
            Check("E412 owned surface renamed by a user", o.Succeeded && o.Value[0].Owned.Created && o.Value[0].Owned.Handle != h1 && old.Handle.ToString() == h1
                && Has(o.Problems, AdapterCodes.OwnedSurfaceRecreated, Severity.Warning),
                $"new '{o.Value[0].Owned.Name}' handle {o.Value[0].Owned.Handle}; '{renamed}' (handle {old.Handle}) left in place");
            tr.Commit();
        }

        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var d2 = new AnalysisDef(Guid.NewGuid(), "D second", N.EG, N.FgD);
            var both = Ensure(tr, [d, d2]);
            Check("E411 two analyses share one owned surface", both.Count == 2 && both[0].Owned.Id == both[1].Owned.Id, $"both bound to '{both[0].Owned.Name}' ({both[0].Owned.Handle})");

            var factored = d with { CutFactor = 1.25, FillFactor = 0.9 };
            var bf = Ensure(tr, [factored])[0];
            var src = Source(tr, bf);
            var vol = (TinVolumeSurface)tr.GetObject(bf.Owned.Id, OpenMode.ForRead);
            var props = vol.GetVolumeProperties();
            var poly = Rect(0, -100, 500, 100);
            var raw = src.Bounded(poly);
            Check("E410 factors on the owned surface, raw from the adapter", vol.CutFactor == 1.25 && vol.FillFactor == 0.9 && Math.Abs(raw.Fill - 100000) < 1e-6
                && Math.Abs(props.AdjustedFillVolume - props.UnadjustedFillVolume * 0.9) <= 1e-12 * props.UnadjustedFillVolume,
                $"surface factors {R(vol.CutFactor)}/{R(vol.FillFactor)}; Adjusted fill {R(props.AdjustedFillVolume)} = 0.9 x Unadjusted {R(props.UnadjustedFillVolume)}; bounded fill over 500 x 200 still raw {R(raw.Fill)} (C2 applies the factor once)");
            var spelled = new AnalysisDef(Guid.NewGuid(), "D lower case", N.EG.ToLowerInvariant(), N.FgD, 1.1, 1.0);
            var o = AnalysisResolver.EnsureAnalyses(tr, _db, [d, spelled], null, _cfg, VolCfg);
            Problems("E411 spellings", o.Problems);
            Check("E411 two spellings of one pair share a surface and report its factors", o.Succeeded && o.Value[0].Owned.Id == o.Value[1].Owned.Id
                && o.Value[0].Owned.CutFactor == o.Value[1].Owned.CutFactor && Has(o.Problems, VolumeCodes.SharedPairFactorMismatch, Severity.Warning),
                o.Succeeded ? $"both on '{o.Value[0].Owned.Name}', both report cut factor {R(o.Value[0].Owned.CutFactor)}" : Codes(o.Problems));
            var reset = Ensure(tr, [d])[0];
            Say($"  factors reset to {R(reset.Owned.CutFactor)}/{R(reset.Owned.FillFactor)}");
            tr.Commit();
        }

        // The owned surface on a locked layer is still SheetVol's to update (6.10).
        ObjectId ownedD;
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            ownedD = Ensure(tr, [d])[0].Owned.Id;
            var layers = (LayerTable)tr.GetObject(_db.LayerTableId, OpenMode.ForWrite);
            var ltr = new LayerTableRecord { Name = AdapterCheckSettings.LockedLayer };
            layers.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            ((TinVolumeSurface)tr.GetObject(ownedD, OpenMode.ForWrite)).Layer = AdapterCheckSettings.LockedLayer;
            ltr.IsLocked = true;
            tr.Commit();
        }
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var o = AnalysisResolver.EnsureAnalyses(tr, _db, [d with { CutFactor = 1.2 }], null, _cfg, VolCfg);
            Problems("locked layer", o.Problems);
            var vol = (TinVolumeSurface)tr.GetObject(ownedD, OpenMode.ForRead);
            Check("owned surface on a locked layer is still updated", o.Succeeded && o.Value[0].Owned.Id == ownedD && vol.CutFactor == 1.2,
                $"'{vol.Name}' on locked layer '{vol.Layer}': factor now {R(vol.CutFactor)}, rebuilt");
            Ensure(tr, [d]);
            tr.Commit();
        }
    }

    /// <summary>E401, E402, E403, E442, E445, E803 through the resolver. The scratch grid and empty surfaces are committed and left in the scratch drawing.</summary>
    private static void StepSourceRows()
    {
        using var tr = _db.TransactionManager.StartTransaction();
        void Expect(string label, Outcome<ResolvedPair> o, string code)
        {
            Problems(label, o.Problems);
            Check(label, !o.Succeeded && o.Problems.Any(p => p.Code == code && p.Severity == Severity.Error), Codes(o.Problems));
        }
        void ExpectSource(string label, Outcome<ResolvedSource> o, string code)
        {
            Problems(label, o.Problems);
            Check(label, !o.Succeeded && o.Problems.Any(p => p.Code == code && p.Severity == Severity.Error), Codes(o.Problems));
        }

        Expect("E401 same name", AnalysisResolver.ResolvePair(tr, _db, N.EG, N.EG, null, null, _cfg, "same"), AdapterCodes.SameSurface);
        Expect("E401 same surface by a different spelling", AnalysisResolver.ResolvePair(tr, _db, N.EG, N.EG.ToLowerInvariant(), null, null, _cfg, "case"), AdapterCodes.SameSurface);

        ExpectSource("E402 volume surface picked", AnalysisResolver.ResolveSource(tr, _db, OwnedName(N.EG, N.FgA), null, "base surface", _cfg), AdapterCodes.NotTinSurface);
        GridSurface.Create(_db, AdapterCheckSettings.GridSurface, 10, 10, 0);
        ExpectSource("E402 grid surface picked", AnalysisResolver.ResolveSource(tr, _db, AdapterCheckSettings.GridSurface, null, "comparison surface", _cfg), AdapterCodes.NotTinSurface);

        var emptyId = TinSurface.Create(_db, AdapterCheckSettings.EmptySurface);
        ExpectSource("E403 surface with no triangles", AnalysisResolver.ResolveSource(tr, _db, AdapterCheckSettings.EmptySurface, null, "comparison surface", _cfg), AdapterCodes.NoTriangles);
        var emptyRead = TriangleSourceAdapter.Read(tr, emptyId, _cfg);
        Problems("E403 triangles", emptyRead.Problems);
        Check("E403 triangle source on an empty surface", !emptyRead.Succeeded && Has(emptyRead.Problems, AdapterCodes.NoTriangles, Severity.Error), Codes(emptyRead.Problems));

        ExpectSource("E442 surface not in the drawing", AnalysisResolver.ResolveSource(tr, _db, "NO-SUCH-SURFACE", new StoredRef("NO-SUCH-SURFACE", "FFFFFF"), "base surface", _cfg), AdapterCodes.SourceNotFound);

        var eg = SurfaceByName<TinSurface>(tr, N.EG);
        var fgd = SurfaceByName<TinSurface>(tr, N.FgD);
        var moved = AnalysisResolver.ResolveSource(tr, _db, N.FgD, new StoredRef(N.FgD, eg.Handle.ToString()), "comparison surface", _cfg);
        Problems("E803 handle elsewhere", moved.Problems);
        Check("E803 matched by name, stored handle points elsewhere", moved.Succeeded && moved.Value.Id == fgd.ObjectId && Has(moved.Problems, AdapterCodes.SourceHandleChanged, Severity.Warning), Codes(moved.Problems));
        var gone = AnalysisResolver.ResolveSource(tr, _db, N.FgD, new StoredRef(N.FgD, "FFFFFF"), "comparison surface", _cfg);
        Problems("E803 handle gone", gone.Problems);
        Check("E803 matched by name, stored handle gone", gone.Succeeded && Has(gone.Problems, AdapterCodes.SourceHandleChanged, Severity.Warning), Codes(gone.Problems));
        var repicked = AnalysisResolver.ResolveSource(tr, _db, N.FgD, new StoredRef(N.FgA, "FFFFFF"), "comparison surface", _cfg);
        Check("no E803 when the run now names another surface", repicked.Succeeded && repicked.Problems.Count == 0, $"run edited from FG_A to FG_D: {(repicked.Problems.Count == 0 ? "no warning" : Codes(repicked.Problems))}");
        var renamed = AnalysisResolver.ResolveSource(tr, _db, "FG_D_OLD_NAME", new StoredRef("FG_D_OLD_NAME", fgd.Handle.ToString()), "comparison surface", _cfg);
        Problems("E445 renamed source", renamed.Problems);
        Check("E445 source renamed, matched by stored handle", renamed.Succeeded && renamed.Value.Name == N.FgD && Has(renamed.Problems, AdapterCodes.SourceRenamed, Severity.Warning), Codes(renamed.Problems));
        var misspelled = AnalysisResolver.ResolveSource(tr, _db, "FG_D_MISSPELLED", new StoredRef(N.FgD, fgd.Handle.ToString()), "comparison surface", _cfg);
        Problems("E442 edited name", misspelled.Problems);
        Check("E442 edited run name never falls back to the previous surface's handle", !misspelled.Succeeded && Has(misspelled.Problems, AdapterCodes.SourceNotFound, Severity.Error), Codes(misspelled.Problems));

        var native = AnalysisResolver.ResolveSource(tr, _db, N.FgD, null, "comparison surface", _cfg);
        Check("E404/E1021 origin of a native surface", native.Succeeded && native.Value.Origin == SurfaceOrigin.Native && !native.Value.Rebuilt,
            $"origin {native.Value.Origin}, IsReferenceObject={fgd.IsReferenceObject}, IsOutOfDate={fgd.IsOutOfDate}; reference and corridor surfaces cannot be built headless (P6, P23), so E404's reference branch and E1021 are code paths without a fixture");
        tr.Commit();
    }

    /// <summary>
    /// E404 setup: two native TINs whose data comes from linked objects (a point group, and a 3D
    /// polyline breakline), then an edit to each source. Civil 3D may flag the surfaces out of date
    /// only when the command ends (P18 saw AutoRebuild act then), so SVADAPTERS_E404, the next
    /// command in the script, reads the flags and runs the resolver.
    /// </summary>
    private static void StepOutOfDate()
    {
        var civ = CivilDocument.GetCivilDocument(_db);
        ObjectId pointId, polyId;
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var pts = new Point3dCollection { new(5000, 5000, 10), new(5100, 5000, 10), new(5000, 5100, 10), new(5100, 5100, 12) };
            var ids = civ.CogoPoints.Add(pts, false);
            var numbers = ids.Cast<ObjectId>().Select(i => ((CogoPoint)tr.GetObject(i, OpenMode.ForRead)).PointNumber).ToList();
            pointId = ids[3];
            var groupId = civ.PointGroups.Add(AdapterCheckSettings.OutOfDateGroup);
            var group = (PointGroup)tr.GetObject(groupId, OpenMode.ForWrite);
            group.SetQuery(new StandardPointGroupQuery { IncludeNumbers = string.Join(",", numbers) });
            group.Update();
            var tin = (TinSurface)tr.GetObject(TinSurface.Create(_db, AdapterCheckSettings.OutOfDateSurface), OpenMode.ForWrite);
            tin.PointGroupsDefinition.AddPointGroup(groupId);
            tin.Rebuild();
            Say($"E404 setup: points {string.Join(",", numbers)} in group '{group.Name}' ({group.PointsCount} points) define TIN '{tin.Name}' ({AnalysisResolver.TinTriangles(tin)} triangles, IsOutOfDate={tin.IsOutOfDate})");

            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(_db), OpenMode.ForWrite);
            var pl = new Polyline3d(Poly3dType.SimplePoly, new Point3dCollection { new(6000, 5000, 10), new(6100, 5050, 11), new(6000, 5100, 12), new(5950, 5050, 13) }, true);
            polyId = ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            var tin2 = (TinSurface)tr.GetObject(TinSurface.Create(_db, AdapterCheckSettings.OutOfDateSurface + "-BL"), OpenMode.ForWrite);
            using (tin2.BreaklinesDefinition.AddStandardBreaklines(new ObjectIdCollection { polyId }, 1.0, 0, 0, 0)) { }
            tin2.Rebuild();
            Say($"E404 setup: 3D polyline breakline (linked object) defines TIN '{tin2.Name}' ({AnalysisResolver.TinTriangles(tin2)} triangles, IsOutOfDate={tin2.IsOutOfDate})");
            tr.Commit();
        }
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            ((CogoPoint)tr.GetObject(pointId, OpenMode.ForWrite)).Elevation = 20;
            var pl = (Polyline3d)tr.GetObject(polyId, OpenMode.ForWrite);
            foreach (ObjectId vid in pl)
            {
                var v = (PolylineVertex3d)tr.GetObject(vid, OpenMode.ForWrite);
                v.Position = new Point3d(v.Position.X, v.Position.Y, v.Position.Z + 5);
                break;
            }
            tr.Commit();
        }
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            Say($"E404 same command, after editing both sources: '{AdapterCheckSettings.OutOfDateSurface}' IsOutOfDate={SurfaceByName<TinSurface>(tr, AdapterCheckSettings.OutOfDateSurface).IsOutOfDate}, '{AdapterCheckSettings.OutOfDateSurface}-BL' IsOutOfDate={SurfaceByName<TinSurface>(tr, AdapterCheckSettings.OutOfDateSurface + "-BL").IsOutOfDate}; SVADAPTERS_E404 reads them again in the next command");
            tr.Commit();
        }
    }

    /// <summary>E404, second half: in a new command, reads the flags SVADAPTERS's edits left and runs the resolver with RebuildSources off and on.</summary>
    [Autodesk.AutoCAD.Runtime.CommandMethod("SVADAPTERS_E404", Autodesk.AutoCAD.Runtime.CommandFlags.Modal)]
    public static void CheckOutOfDate()
    {
        var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        _ed = doc?.Editor;
        if (doc is null) return;
        _db = doc.Database;
        _cfg = new AdapterConfig();
        _pass = _fail = _stepFailures = 0;
        Say("===== SVADAPTERS_E404 begin =====");
        Step("E404 next command", () =>
        {
            int outOfDate = 0;
            foreach (var name in new[] { AdapterCheckSettings.OutOfDateSurface, AdapterCheckSettings.OutOfDateSurface + "-BL" })
            {
                using var tr = _db.TransactionManager.StartTransaction();
                var tin = SurfaceByName<TinSurface>(tr, name);
                Say($"E404 '{name}': IsOutOfDate={tin.IsOutOfDate} at the start of the next command");
                if (!tin.IsOutOfDate) { tr.Commit(); continue; }
                outOfDate++;
                var off = AnalysisResolver.ResolveSource(tr, _db, name, null, "base surface", _cfg with { RebuildSources = false });
                Problems("E404 RebuildSources off", off.Problems);
                Check($"E404 '{name}' out of date, RebuildSources off", !off.Succeeded && Has(off.Problems, AdapterCodes.SourceOutOfDate, Severity.Error), Codes(off.Problems));
                var on = AnalysisResolver.ResolveSource(tr, _db, name, null, "base surface", _cfg with { RebuildSources = true });
                Problems("E404 RebuildSources on", on.Problems);
                Check($"E404 '{name}' out of date, RebuildSources on", on.Succeeded && on.Value.Rebuilt && Has(on.Problems, AdapterCodes.SourceOutOfDate, Severity.Info) && !tin.IsOutOfDate,
                    $"{Codes(on.Problems)}; IsOutOfDate now {tin.IsOutOfDate}");
                tr.Commit();
            }
            if (outOfDate == 0)
                Say("E404: neither surface reported out of date headless; the rebuild and fail branches run only when Civil 3D sets IsOutOfDate. Reported, not a pass.");
        });
        Say($"SVADAPTERS_E404 summary: {_pass} PASS, {_fail} FAIL, {_stepFailures} step(s) failed");
        Say("===== SVADAPTERS_E404 end =====");
    }

    /// <summary>The 5.1 polygon rule and the sign-noise clamp on the volume source.</summary>
    private static void StepPolygonRule()
    {
        using var tr = _db.TransactionManager.StartTransaction();
        var a = Ensure(tr, [new AnalysisDef(Guid.NewGuid(), "A", N.EG, N.FgA)])[0];
        var src = Source(tr, a);
        string Throws(IReadOnlyList<Pt> poly)
        {
            try { src.Bounded(poly); return "no exception"; }
            catch (ArgumentException ex) { return $"ArgumentException: {ex.Message}"; }
        }
        string open = Throws([new Pt(0, 0), new Pt(10, 0), new Pt(10, 10), new Pt(0, 10)]);
        string three = Throws([new Pt(0, 0), new Pt(10, 0), new Pt(0, 0)]);
        Check("polygon rule (5.1)", open.StartsWith("ArgumentException", StringComparison.Ordinal) && three.StartsWith("ArgumentException", StringComparison.Ordinal),
            $"open 4-point polygon -> {open}; closed 3-point -> {three}");
        var p1 = AdapterCheckSettings.AroundP1;
        var poly1 = Rect(p1.X0, p1.Y0, p1.X1, p1.Y1);
        var raw = src.RawBounded(poly1);
        var clean = src.Bounded(poly1);
        Say($"sign noise: GetBoundedVolumes over the P1 polygon after the E1013 hide boundary: Cut={R(raw.Cut)} Fill={R(raw.Fill)} Net={R(raw.Net)}; adapter returns Cut={R(clean.Cut)} Fill={R(clean.Fill)}");
        Check("sign noise clamped below 1e-6", Math.Abs(raw.Fill) < _cfg.SignNoiseClamp ? clean.Fill == 0 : clean.Fill == raw.Fill,
            $"raw fill {R(raw.Fill)} -> {R(clean.Fill)} (clamp {E(_cfg.SignNoiseClamp)}); a negative beyond the clamp is passed to C2 (SV-E432)");
        tr.Commit();
    }
}

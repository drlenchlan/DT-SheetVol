// A1 adapters, contract revision 3 (rev3-adapters): SVADAPTERS rows for a user volume surface as an
// analysis source. E1 measures the sign Civil 3D stores on a TIN volume surface's TIN
// (docs/api-findings.md §E); the E423 rows run the regen pipeline (installed bindings, ExportOnly,
// so the drawing is not written) on Fixture A / alignment S with T2's breaks.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using SheetVol.Civil3D.SelfTest;
using SheetVol.Civil3D.Session;
using SheetVol.Core;
using SheetVol.Core.Tracking;
using SheetVol.Core.Volumes;
using SheetVol.Core.Volumes.Integrator;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using N = SheetVol.Civil3D.SelfTest.FixtureSpec.Names;
using S = SheetVol.Civil3D.Adapters.AdapterCheckSettings;

namespace SheetVol.Civil3D.Adapters;

public static partial class AdapterCheckCommands
{
    private static readonly Guid PairId = Guid.Parse("a1a1a1a1-0003-0000-0000-000000000001");
    private static readonly Guid UserId = Guid.Parse("a1a1a1a1-0003-0000-0000-000000000002");
    private static readonly Guid OuterId = Guid.Parse("a1a1a1a1-0003-0000-0000-000000000003");
    private static readonly Guid OwnedId = Guid.Parse("a1a1a1a1-0003-0000-0000-000000000004");
    private const double StoredFactorIgnored = 2.0;   // rule 1: the stored factors of a volume-surface analysis must have no effect

    private static RunDefinition PairRun() => Run("R3-PAIR", N.AlignmentS, S.T2Breaks, [new AnalysisDef(PairId, "pair EG/FG_A", N.EG, N.FgA)]);

    private static RunDefinition VolumeRun(string run, Guid id, string volume) =>
        Run(run, N.AlignmentS, S.T2Breaks, [new AnalysisDef(id, "volume " + volume, N.EG, N.FgA, StoredFactorIgnored, StoredFactorIgnored, volume)]);

    /// <summary>The installed engine (EngineStartup's bindings) on the effective settings with SVSELFTEST's fixture tolerances (1e-9) and the given RebuildSources.</summary>
    private static RegenEngine Engine(bool rebuildSources)
    {
        var load = SettingsProvider.Current;
        if (load.Settings is not { } s) { Problems("settings", load.Problems); throw new InvalidOperationException("SheetVol settings did not load"); }
        if (!EngineBindings.Current.IsWired) throw new InvalidOperationException("EngineBindings.Current is not wired (EngineStartup did not run)");
        var st = s.SelfTest;
        return new RegenEngine(EngineBindings.Current, s with
        {
            Adapters = s.Adapters with { RebuildSources = rebuildSources },
            Integrator = s.Integrator with
            {
                CrossCheck = true,
                CrossCheckRelative = st.FixtureCrossCheckRelative, CrossCheckAbsoluteCubicFeet = st.FixtureCrossCheckAbsoluteCubicFeet,
                AreaCheckRelative = st.FixtureAreaCheckRelative, AreaCheckAbsoluteSquareFeet = st.FixtureAreaCheckAbsoluteSquareFeet,
            },
        }, SettingsProvider.PluginDirectory);
    }

    /// <summary>Steps 1 to 3 and 6 of the regen pipeline (ExportOnly: the transaction is aborted, the drawing is not written).</summary>
    private static RunOutcome Regen(RunDefinition run, bool rebuildSources = true)
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("no active document");
        var o = Engine(rebuildSources).Regenerate(doc, run, RegenMode.ExportOnly, null, CancellationToken.None);
        foreach (var p in o.Result.Problems.Where(p => p.Code != PipelineCodes.DrawingNeverSaved))
            Say($"  [{run.Name}] {p.Code} {p.Severity}: {p.Message}");
        Say($"  [{run.Name}] Succeeded={o.Result.Succeeded} FailedAtStep={o.FailedAtStep} CrossCheckMaxRelative={(o.CrossCheckMaxRelative is { } x ? E(x) : "not run")}");
        return o;
    }

    private static bool Has(RunOutcome o, string code, Severity severity, string? mentions = null) =>
        o.Result.Problems.Any(p => p.Code == code && p.Severity == severity && (mentions is null || p.Message.Contains(mentions, StringComparison.Ordinal)));

    private static IReadOnlyList<SegmentResult> Rows(RunOutcome o) => o.Reconcile?.Analyses.Single().Rows.OrderBy(r => r.StartStation).ToList() ?? [];

    private static ObjectId UserVolume(string name, double cutFactor, double fillFactor, bool outer)
    {
        var v = Fixtures.CreateVolume(_db, N.EG, N.FgA, name);
        using var tr = _db.TransactionManager.StartTransaction();
        var vol = (TinVolumeSurface)tr.GetObject(v.Id, OpenMode.ForWrite);
        vol.CutFactor = cutFactor;
        vol.FillFactor = fillFactor;
        if (outer)
        {
            var o = S.UserOuter;
            using (vol.BoundariesDefinition.AddBoundaries(Rect3(o.X0, o.Y0, o.X1, o.Y1), S.UserOuterMidOrdinate, SurfaceBoundaryType.Outer, true)) { }
            vol.Rebuild();
        }
        Say($"user volume surface '{name}' (handle {vol.Handle}, untagged, made with FX's API): {N.EG} vs {N.FgA}, factors {R(vol.CutFactor)}/{R(vol.FillFactor)}, boundaries {vol.BoundariesDefinition.Count}, triangles {vol.GetTinProperties().NumberOfTriangles}");
        tr.Commit();
        return v.Id;
    }

    /// <summary>A surface by name, skipping erased ids (the E422 rows erase EG first).</summary>
    private static T LiveSurface<T>(Transaction tr, string name) where T : Autodesk.Civil.DatabaseServices.Surface
    {
        var civ = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(_db);
        foreach (ObjectId id in civ.GetSurfaceIds())
            if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is T s && s.Name == name) return s;
        throw new InvalidOperationException($"surface '{name}' ({typeof(T).Name}) not found");
    }

    private static Point3dCollection Rect3(double x0, double y0, double x1, double y1) =>
        new() { new(x0, y0, 0), new(x1, y0, 0), new(x1, y1, 0), new(x0, y1, 0), new(x0, y0, 0) };

    /// <summary>
    /// E423, E1012, rule 1, D4: a user volume surface on EG vs FG_A with factors 1.25 / 0.9 and no
    /// boundary, through the pipeline on alignment S with T2's breaks, against the pair analysis.
    /// </summary>
    private static void StepVolumeSurfacePipeline()
    {
        UserVolume(S.UserVolume, S.UserCutFactor, S.UserFillFactor, outer: false);
        var pair = Regen(PairRun());
        var user = Regen(VolumeRun("R3-VS", UserId, S.UserVolume));
        if (!pair.Result.Succeeded || !user.Result.Succeeded)
        {
            Check("E423 volume-surface source through the pipeline", false, $"pair succeeded={pair.Result.Succeeded}, volume surface succeeded={user.Result.Succeeded}");
            return;
        }
        var pr = Rows(pair);
        var ur = Rows(user);
        var def = user.Reconcile!.Analyses.Single().Analysis;
        Say($"E423 effective analysis (what Core applied and Output reads): VolumeSurface='{def.VolumeSurface}' base '{def.BaseSurface}' comparison '{def.ComparisonSurface}' factors {R(def.CutFactor)}/{R(def.FillFactor)}; stored run factors {R(user.Run.Analyses[0].CutFactor)}/{R(user.Run.Analyses[0].FillFactor)} (ignored), stored VolumeSurface='{user.Run.Analyses[0].VolumeSurface}'");
        Say("E423 | segment     | pair raw cut / fill ft3 | volume raw cut / fill ft3 | diff       | volume cut / fill CY  | raw CY x 1.25 / x 0.9 | result");
        bool allRows = pr.Count == 4 && ur.Count == 4;
        for (int i = 0; i < Math.Min(pr.Count, ur.Count); i++)
        {
            var (p, u) = (pr[i], ur[i]);
            double dc = u.Raw.Cut - p.Raw.Cut, df = u.Raw.Fill - p.Raw.Fill;
            double ec = Cy(u.Raw.Cut) * S.UserCutFactor, ef = Cy(u.Raw.Fill) * S.UserFillFactor;
            bool ok = Math.Abs(dc) <= 1e-9 * Math.Max(1, Math.Abs(p.Raw.Cut)) && Math.Abs(df) <= 1e-9 * Math.Max(1, Math.Abs(p.Raw.Fill))
                && Math.Abs(u.Cut - ec) <= 1e-12 * Math.Max(1, ec) && Math.Abs(u.Fill - ef) <= 1e-12 * Math.Max(1, ef);
            allRows &= ok;
            if (ok) _pass++; else _fail++;
            Say($"E423 | {F(u.StartStation),4}-{F(u.EndStation),-6} | {R(p.Raw.Cut),10} / {R(p.Raw.Fill),-10} | {R(u.Raw.Cut),11} / {R(u.Raw.Fill),-11} | {E(dc)}/{E(df)} | {F6(u.Cut),9} / {F6(u.Fill),-9} | {F6(ec),9} / {F6(ef),-9} | {(ok ? "PASS" : "FAIL")}");
        }
        Check("E423 same raw bounded volumes as the pair, factored by the surface's 1.25 / 0.9", allRows && def.CutFactor == S.UserCutFactor && def.FillFactor == S.UserFillFactor && def.VolumeSurface == S.UserVolume,
            $"4 segments; the stored factors {R(StoredFactorIgnored)} had no effect; effective factors {R(def.CutFactor)}/{R(def.FillFactor)}");
        Check("E1012 integrator on the volume surface's TIN agrees with GetBoundedVolumes (pipeline, 4 segments)",
            user.CrossCheckMaxRelative is { } mr && mr <= S.T29Relative && !Has(user, PipelineCodes.IntegratorMismatch, Severity.Error) && !Has(user, PipelineCodes.IntegratorSkipped, Severity.Warning),
            $"largest relative difference {(user.CrossCheckMaxRelative is { } x ? E(x) : "none")} (fixture tolerance {E(S.T29Relative)}); no SV-E1012, no SV-E827");
        Check("E404 Info and E423 Info name the surface", Has(user, AdapterCodes.SourceOutOfDate, Severity.Info, S.UserVolume) && Has(user, AdapterCodes.VolumeSurfaceSource, Severity.Info, S.UserVolume),
            "rebuilt under RebuildSources (D21); its factors govern");
        DirectIntegrator(VolumeRun("R3-VS", UserId, S.UserVolume), "E1012 direct");
    }

    /// <summary>Per-segment E1012 evidence outside the pipeline, through the same port (depth TIN through the integrator's depth overload vs GetBoundedVolumes), and the D4 state the status reader would compute.</summary>
    private static void DirectIntegrator(RunDefinition run, string tag)
    {
        var settings = Engine(true).Settings;
        using var tr = _db.TransactionManager.StartTransaction();
        var port = new VolumeSurfaceSources(_db, tr, settings);
        var a = run.Analyses[0];
        var o = port.Resolve(a.VolumeSurface!, null, true, a);
        Problems(tag, o.Problems.Where(p => p.Severity != Severity.Info));
        if (!o.Succeeded) { Check($"{tag} resolve", false, string.Join(", ", o.Problems.Select(p => p.Code))); return; }
        var src = o.Value;
        var al = OpenAlignment(tr, run.AlignmentName);
        var plan = Plan(run, al);
        bool ok = true;
        Say($"{tag} | segment     | integrator cut / fill ft3   | bounded cut / fill ft3      | relative diff | coverage");
        foreach (var seg in plan.Segments.OrderBy(x => x.StartStation))
        {
            var tess = plan.Boundaries.Single(b => b.SegmentId == seg.Id).Tessellated;
            var band = OverlayIntegrator.IntegrateBand(src.Depth, tess, seg.Id);
            var bounded = src.Volumes.Bounded(tess);
            if (band.Totals is not { } t)
            {
                bool empty = band.Problems.Any(p => p.Code == IntegratorCodes.EmptySurface) && bounded.Cut == 0 && bounded.Fill == 0;
                ok &= empty;
                Say($"{tag} | {F(seg.StartStation),4}-{F(seg.EndStation),-6} | no triangles in the window   | {R(bounded.Cut),12} / {R(bounded.Fill),-12} | -             | -  ({(empty ? "both empty" : "MISMATCH")})");
                continue;
            }
            double mag = Math.Abs(bounded.Cut) + Math.Abs(bounded.Fill);
            double rel = Math.Max(Math.Abs(t.Cut - bounded.Cut), Math.Abs(t.Fill - bounded.Fill)) / Math.Max(mag, 1);
            ok &= rel <= S.T29Relative;
            Say($"{tag} | {F(seg.StartStation),4}-{F(seg.EndStation),-6} | {R(t.Cut),12} / {R(t.Fill),-12} | {R(bounded.Cut),12} / {R(bounded.Fill),-12} | {E(rel),13} | {F6(t.Coverage)}");
        }
        Check($"{tag}: integrator on the depth TIN = GetBoundedVolumes on every segment", ok, $"tolerance {E(S.T29Relative)} relative");

        // D4: what GetRunStatus reads after the regen equals what the regen fingerprints.
        var state = port.ReadState(a.Id, a.VolumeSurface!, src.Handle);
        var expected = new AnalysisSurfaceState(a.Id, false, src.Volumes.SurfaceTotals, new VolumeSurfaceSourceState(src.Name, src.Handle, src.CutFactor, src.FillFactor));
        Check($"{tag}: D4 status state equals the regen state", state == expected,
            state is null ? "no state" : $"name '{state.VolumeSurface?.Name}' handle {state.VolumeSurface?.Handle} factors {R(state.VolumeSurface?.CutFactor ?? double.NaN)}/{R(state.VolumeSurface?.FillFactor ?? double.NaN)} totals {R(state.LastTotals.Cut)}/{R(state.LastTotals.Fill)} out of date {state.IsOutOfDate}");
        tr.Commit();
    }

    /// <summary>E423 with a user outer boundary on the volume surface (S4 D6): bounded volumes and the integrator are clipped alike.</summary>
    private static void StepVolumeSurfaceOuter()
    {
        UserVolume(S.UserVolumeOuter, 1.0, 1.0, outer: true);
        var pair = Regen(PairRun());
        var user = Regen(VolumeRun("R3-VS-OUTER", OuterId, S.UserVolumeOuter));
        var pr = Rows(pair);
        var ur = Rows(user);
        if (!user.Result.Succeeded || ur.Count != 4 || pr.Count != 4) { Check("E423 user outer boundary", false, $"succeeded={user.Result.Succeeded}, rows {ur.Count}"); return; }
        for (int i = 0; i < 4; i++)
            Say($"E423 outer | {F(ur[i].StartStation),4}-{F(ur[i].EndStation),-6} | pair raw {R(pr[i].Raw.Cut)} / {R(pr[i].Raw.Fill)} | volume surface with outer boundary raw {R(ur[i].Raw.Cut)} / {R(ur[i].Raw.Fill)} | coverage {F6(ur[i].Coverage)}");
        bool clipped = pr[1].Raw.Cut > 0 && ur[1].Raw.Cut == 0 && pr[2].Raw.Fill > 0 && ur[2].Raw.Fill == 0;
        Check("E423 user outer boundary clips the bounded volumes (seg 2 cut and seg 3 fill to 0)", clipped,
            $"seg 2 cut {R(pr[1].Raw.Cut)} -> {R(ur[1].Raw.Cut)}; seg 3 fill {R(pr[2].Raw.Fill)} -> {R(ur[2].Raw.Fill)}");
        // Seg 1 lies inside the boundary, but FG_A carries E1013's hide boundary (105..145 x -20..20) by now:
        // the pair and the unbounded user surface leave the hole out (cut 1574.07), the bounded user surface
        // does not fully (api-findings §E). Reported, not judged: the integrator reads that same TIN and agrees.
        Say($"E423 outer seg 1 (inside the boundary; FG_A carries E1013's hide boundary): pair cut {R(pr[0].Raw.Cut)}, bounded user surface cut {R(ur[0].Raw.Cut)}, fill {R(pr[0].Raw.Fill)} / {R(ur[0].Raw.Fill)}");
        Check("E1012 with the boundary: pipeline cross-check passes where the TIN has triangles",
            user.CrossCheckMaxRelative is { } mr && mr <= S.T29Relative && !Has(user, PipelineCodes.IntegratorMismatch, Severity.Error),
            $"largest relative difference {(user.CrossCheckMaxRelative is { } x ? E(x) : "none")}; segments with no triangles left report {PipelineCodes.IntegratorSkipped} (D10): {user.Result.Problems.Count(p => p.Code == PipelineCodes.IntegratorSkipped)}");
        DirectIntegrator(VolumeRun("R3-VS-OUTER", OuterId, S.UserVolumeOuter), "E423 outer direct");
    }

    /// <summary>E422: SheetVol's own (tagged) volume surface is never a user source; the listing never shows it.</summary>
    private static void StepVolumeSurfaceOwnedRejected()
    {
        string owned = OwnedName(N.EG, N.FgA);
        var o = Regen(VolumeRun("R3-VS-OWNED", OwnedId, owned));
        Check("E422 the guard rejects a SheetVol-owned surface", !o.Result.Succeeded && o.FailedAtStep == 1 && Has(o, AdapterCodes.VolumeSurfaceLost, Severity.Error, "SheetVol's own"),
            string.Join(" | ", o.Result.Problems.Where(p => p.Code == AdapterCodes.VolumeSurfaceLost).Select(p => p.Message)));
        using var tr = _db.TransactionManager.StartTransaction();
        var list = new VolumeSurfaceSources(_db, tr, Engine(true).Settings).List();
        foreach (var v in list) Say($"  listed: '{v.Name}' {v.BaseSurface} vs {v.ComparisonSurface} factors {R(v.CutFactor)}/{R(v.FillFactor)} SourcesIntact={v.SourcesIntact}");
        var tagged = new List<string>();
        foreach (ObjectId id in Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(_db).GetSurfaceIds())
            if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is TinVolumeSurface v && OwnershipTag.Read(tr, v, _cfg) is not null) tagged.Add(v.Name);
        string lookAlike = OwnedName(N.EG, N.FgB);   // the E412 setup's untagged surface with an owned-looking name: the user's
        Check("E422 listing: user surfaces with their factors, never SheetVol-owned", tagged.Count > 0 && list.All(v => !tagged.Contains(v.Name))
            && list.Any(v => v.Name == lookAlike) && list.Any(v => v.Name == S.UserVolume && v.SourcesIntact && v.CutFactor == S.UserCutFactor && v.FillFactor == S.UserFillFactor),
            $"{list.Count} listed; tagged (owned) surfaces {string.Join(", ", tagged)} not among them; the untagged look-alike '{lookAlike}' is listed");
        tr.Commit();
    }

    /// <summary>E404 setup (S4 D4): lower FG_A's P3 apex; Civil 3D flags the volume surface out of date only after this command, so SVADAPTERS_VS reads it.</summary>
    private static void StepVolumeSurfaceStaleSetup()
    {
        using var tr = _db.TransactionManager.StartTransaction();
        var fg = SurfaceByName<TinSurface>(tr, N.FgA);
        fg.UpgradeOpen();
        var apex = fg.FindVertexAtXY(S.P3Apex.X, S.P3Apex.Y);
        double before = apex.Location.Z;
        using (fg.SetVertexElevation(apex, S.P3ApexEditedZ)) { }
        fg.Rebuild();
        var vol = SurfaceByName<TinVolumeSurface>(tr, S.UserVolume);
        Say($"E404 setup: {N.FgA} vertex ({F(S.P3Apex.X)}, {F(S.P3Apex.Y)}) z {R(before)} -> {R(S.P3ApexEditedZ)}; '{S.UserVolume}' IsOutOfDate={vol.IsOutOfDate} in the same command (S4: flags in the next); SVADAPTERS_VS reads it");
        tr.Commit();
    }

    /// <summary>E404 and E422 for a volume-surface source, in the commands after SVADAPTERS's FG_A edit.</summary>
    [CommandMethod("SVADAPTERS_VS", CommandFlags.Modal)]
    public static void CheckVolumeSurfaceNext()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        _ed = doc?.Editor;
        if (doc is null) return;
        _db = doc.Database;
        _cfg = new AdapterConfig();
        _pass = _fail = _stepFailures = 0;
        Say("===== SVADAPTERS_VS begin =====");
        Step("E404 volume surface out of date", () =>
        {
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                Say($"E404 '{S.UserVolume}': IsOutOfDate={SurfaceByName<TinVolumeSurface>(tr, S.UserVolume).IsOutOfDate} at the start of this command");
                tr.Commit();
            }
            var off = Regen(VolumeRun("R3-VS", UserId, S.UserVolume), rebuildSources: false);
            Check("E404 RebuildSources off with the surface out of date fails naming it", !off.Result.Succeeded && off.FailedAtStep == 1
                && Has(off, AdapterCodes.SourceOutOfDate, Severity.Error, $"'{S.UserVolume}' is out of date and RebuildSources is off"),
                string.Join(" | ", off.Result.Problems.Where(p => p.Code == AdapterCodes.SourceOutOfDate).Select(p => $"{p.Severity}: {p.Message}")));
            var on = Regen(VolumeRun("R3-VS", UserId, S.UserVolume), rebuildSources: true);
            var pair = Regen(PairRun());
            var ur = Rows(on);
            var pr = Rows(pair);
            bool follows = on.Result.Succeeded && ur.Count == 4 && pr.Count == 4 && ur.Zip(pr).All(t => t.First.Raw == t.Second.Raw);
            Check("E404 RebuildSources on rebuilds it (Info naming it) and the numbers follow the edit", follows && Has(on, AdapterCodes.SourceOutOfDate, Severity.Info, S.UserVolume),
                ur.Count == 4 ? $"seg 2 raw cut now {R(ur[1].Raw.Cut)} (pair {R(pr[1].Raw.Cut)}; 2700 before the edit)" : "no rows");
        });
        Step("E422 base erased, same command", () =>
        {
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var eg = SurfaceByName<TinSurface>(tr, N.EG);
                eg.UpgradeOpen();
                eg.Erase();
                Say($"E422 setup: {N.EG}.Erase() IsErased={eg.IsErased}; '{S.UserVolume}' is still a {LiveSurface<Autodesk.Civil.DatabaseServices.Surface>(tr, S.UserVolume).GetType().Name} in this command (S4)");
                tr.Commit();
            }
            var o = Regen(VolumeRun("R3-VS", UserId, S.UserVolume));
            Check("E422 erasing EG then regenerating fails naming EG", !o.Result.Succeeded && o.FailedAtStep == 1 && Has(o, AdapterCodes.VolumeSurfaceLost, Severity.Error, $"base surface '{N.EG}'"),
                string.Join(" | ", o.Result.Problems.Where(p => p.Code == AdapterCodes.VolumeSurfaceLost).Select(p => p.Message)));
            using var tr2 = _db.TransactionManager.StartTransaction();
            var listed = new VolumeSurfaceSources(_db, tr2, Engine(true).Settings).List().FirstOrDefault(v => v.Name == S.UserVolume);
            Check("E422 listing marks it unavailable (SourcesIntact false)", listed is { SourcesIntact: false },
                listed is null ? "not listed" : $"'{listed.Name}' {listed.BaseSurface} vs {listed.ComparisonSurface} factors {R(listed.CutFactor)}/{R(listed.FillFactor)} SourcesIntact={listed.SourcesIntact}");
            tr2.Commit();
        });
        Say($"SVADAPTERS_VS summary: {_pass} PASS, {_fail} FAIL, {_stepFailures} step(s) failed");
        Say("===== SVADAPTERS_VS end =====");
    }

    /// <summary>E422 in the command after the erase: the surface now opens as a plain TIN surface (S4 D5).</summary>
    [CommandMethod("SVADAPTERS_VS_ERASED", CommandFlags.Modal)]
    public static void CheckVolumeSurfaceErased()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        _ed = doc?.Editor;
        if (doc is null) return;
        _db = doc.Database;
        _cfg = new AdapterConfig();
        _pass = _fail = _stepFailures = 0;
        Say("===== SVADAPTERS_VS_ERASED begin =====");
        Step("E422 base erased, next command", () =>
        {
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var s = LiveSurface<Autodesk.Civil.DatabaseServices.Surface>(tr, S.UserVolume);
                Say($"E422 '{S.UserVolume}' now opens as {s.GetType().Name}, IsVolumeSurface={s.IsVolumeSurface}");
                tr.Commit();
            }
            var o = Regen(VolumeRun("R3-VS", UserId, S.UserVolume));
            Check("E422 next command: no longer a TIN volume surface, the lost base named", !o.Result.Succeeded && o.FailedAtStep == 1
                && Has(o, AdapterCodes.VolumeSurfaceLost, Severity.Error, "no longer a TIN volume surface") && Has(o, AdapterCodes.VolumeSurfaceLost, Severity.Error, $"base '{N.EG}'"),
                string.Join(" | ", o.Result.Problems.Where(p => p.Code == AdapterCodes.VolumeSurfaceLost).Select(p => p.Message)));
            using var tr2 = _db.TransactionManager.StartTransaction();
            var list = new VolumeSurfaceSources(_db, tr2, Engine(true).Settings).List();
            Check("E422 next command: the listing drops it (it is a plain TIN now)", list.All(v => v.Name != S.UserVolume), $"listed: {string.Join(", ", list.Select(v => $"{v.Name} intact={v.SourcesIntact}"))}");
            tr2.Commit();
        });
        Say($"SVADAPTERS_VS_ERASED summary: {_pass} PASS, {_fail} FAIL, {_stepFailures} step(s) failed");
        Say("===== SVADAPTERS_VS_ERASED end =====");
    }

    /// <summary>E1: base flat 100 and comparison flat 98 over one square; what Z does the volume surface's own TIN carry?</summary>
    private static void StepVolumeTinSign()
    {
        ObjectId volId;
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            ObjectId Flat(string name, double z)
            {
                var id = TinSurface.Create(_db, name);
                var tin = (TinSurface)tr.GetObject(id, OpenMode.ForWrite);
                tin.AddVertices(new Point3dCollection
                {
                    new(S.SignX0, S.SignY0, z), new(S.SignX0 + S.SignSide, S.SignY0, z),
                    new(S.SignX0 + S.SignSide, S.SignY0 + S.SignSide, z), new(S.SignX0, S.SignY0 + S.SignSide, z),
                });
                tin.Rebuild();
                return id;
            }
            var b = Flat(S.SignBase, S.SignBaseZ);
            var c = Flat(S.SignComparison, S.SignComparisonZ);
            volId = TinVolumeSurface.Create(S.SignVolume, b, c);
            ((TinVolumeSurface)tr.GetObject(volId, OpenMode.ForWrite)).Rebuild();
            tr.Commit();
        }
        using (var tr2 = _db.TransactionManager.StartTransaction())
        {
            var vol = (TinVolumeSurface)tr2.GetObject(volId, OpenMode.ForRead);
            var props = vol.GetVolumeProperties();
            double mid = vol.FindElevationAtXY(S.SignX0 + S.SignSide / 2, S.SignY0 + S.SignSide / 2);
            Say($"E1 '{vol.Name}' (handle {vol.Handle}, wrapper {vol.GetType().Name}, ObjectClass {volId.ObjectClass.Name}): base '{S.SignBase}' flat {F(S.SignBaseZ)}, comparison '{S.SignComparison}' flat {F(S.SignComparisonZ)} over [{F(S.SignX0)},{F(S.SignX0 + S.SignSide)}] x [{F(S.SignY0)},{F(S.SignY0 + S.SignSide)}]");
            Say($"E1 GetVolumeProperties: UnadjustedCut={R(props.UnadjustedCutVolume)} UnadjustedFill={R(props.UnadjustedFillVolume)} UnadjustedNet={R(props.UnadjustedNetVolume)}; FindElevationAtXY(centre)={R(mid)}; GetTinProperties().NumberOfTriangles={vol.GetTinProperties().NumberOfTriangles}");
            var raw = TriangleSourceAdapter.ReadVolumeTin(tr2, volId, _cfg, 1.0);
            Problems("E1 raw read", raw.Problems);
            if (!raw.Succeeded) { Check("E1 volume TIN sign", false, "the volume surface's TIN could not be read through the TinSurface view"); tr2.Commit(); return; }
            foreach (var t in raw.Value.All)
                Say($"E1 TIN triangle (raw Z): ({F(t.A.X)}, {F(t.A.Y)}, {R(t.A.Z)}) ({F(t.B.X)}, {F(t.B.Y)}, {R(t.B.Z)}) ({F(t.C.X)}, {F(t.C.Y)}, {R(t.C.Z)})");
            double compMinusBase = S.SignComparisonZ - S.SignBaseZ, baseMinusComp = -compMinusBase;
            var zs = raw.Value.All.SelectMany(t => new[] { t.A.Z, t.B.Z, t.C.Z }).ToList();
            var depth = TriangleSourceAdapter.ReadVolumeDepth(tr2, volId, _cfg);
            var ds = depth.Succeeded ? depth.Value.All.SelectMany(t => new[] { t.A.Z, t.B.Z, t.C.Z }).ToList() : [];
            bool stored = zs.Count > 0 && zs.All(z => z == compMinusBase);
            bool normalised = ds.Count == zs.Count && ds.All(z => z == baseMinusComp);
            Say($"E1 measured: {raw.Value.Count} visible triangles, every vertex Z in {{{string.Join(", ", zs.Distinct().Select(R))}}}; comparison - base = {R(compMinusBase)}, base - comparison = {R(baseMinusComp)}");
            Check("E1 volume TIN sign", stored && normalised && UserVolumeSurfaces.DepthPerTinZ * compMinusBase == baseMinusComp,
                $"the TIN stores comparison - base ({R(compMinusBase)}) = {stored}; DepthPerTinZ = {R(UserVolumeSurfaces.DepthPerTinZ)}; ReadVolumeDepth vertex Z in {{{string.Join(", ", ds.Distinct().Select(R))}}} = base - comparison: {normalised}");
            tr2.Commit();
        }
    }
}

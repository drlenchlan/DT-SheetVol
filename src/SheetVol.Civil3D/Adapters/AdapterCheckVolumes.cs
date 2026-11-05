// A1 adapters: SVADAPTERS steps for the volume source through C1's planner and C2's reconciler
// (T1, T2, T10, T24; E407; the E412 collision setup).

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using SheetVol.Civil3D.SelfTest;
using SheetVol.Core;
using SheetVol.Core.Volumes;
using N = SheetVol.Civil3D.SelfTest.FixtureSpec.Names;

namespace SheetVol.Civil3D.Adapters;

public static partial class AdapterCheckCommands
{
    private static string OwnedName(string b, string c) => VolCfg.OwnedSurfacePrefix + b + VolCfg.OwnedSurfaceSeparator + c;

    private static void SayBinding(AnalysisBinding b) =>
        Say($"  owned surface '{b.Owned.Name}' (handle {b.Owned.Handle}) {(b.Owned.Created ? "created" : "reused")}, {b.Owned.Triangles} triangles, factors {R(b.Owned.CutFactor)}/{R(b.Owned.FillFactor)}; base {b.Owned.Sources.Base.Name} ({b.Owned.Sources.Base.Origin}, {b.Owned.Sources.Base.Triangles} tri), comparison {b.Owned.Sources.Comparison.Name} ({b.Owned.Sources.Comparison.Origin}, {b.Owned.Sources.Comparison.Triangles} tri)");

    /// <summary>E412 setup: an untagged volume surface that already carries the owned name and the same sources, with a factor the adapter must never change.</summary>
    private static void StepCollisionSetup()
    {
        string name = OwnedName(N.EG, N.FgB);
        var v = Fixtures.CreateVolume(_db, N.EG, N.FgB, name);
        using var tr = _db.TransactionManager.StartTransaction();
        var vol = (TinVolumeSurface)tr.GetObject(v.Id, OpenMode.ForWrite);
        vol.CutFactor = AdapterCheckSettings.UserVolumeCutFactor;
        tr.Commit();
        Say($"user volume surface '{name}' (handle {v.Id.Handle}) created with FX's API: same name and sources as the owned surface would have, no ownership tag, CutFactor {R(AdapterCheckSettings.UserVolumeCutFactor)}");
    }

    private static void StepT1T2()
    {
        using var tr = _db.TransactionManager.StartTransaction();
        var al = OpenAlignment(tr, N.AlignmentS);
        var a = new AnalysisDef(Guid.NewGuid(), "A", N.EG, N.FgA);
        var binding = Ensure(tr, [a])[0];
        SayBinding(binding);
        var src = Source(tr, binding);
        Say($"  SurfaceTotals (GetVolumeProperties().Unadjusted*, cleaned): cut {R(src.SurfaceTotals.Cut)} fill {R(src.SurfaceTotals.Fill)}");

        foreach (var (test, breaks, expected) in new[]
        {
            ("T1", "START..END@500", FixtureSpec.Expected.T1),
            ("T2", "0, 5+00, 13+00, 18+00, 20+00", FixtureSpec.Expected.T2),
        })
        {
            var plan = Plan(Run(test, N.AlignmentS, breaks, [a]), al);
            var rec = Reconcile(plan, [new AnalysisInput(a, src)]);
            Problems(test, rec.Problems.Where(p => p.Severity != Severity.Info));
            var ar = rec.Analyses[0];
            Say($"{test}: alignment S (AlignmentAdapter), breaks '{breaks}', offsets ({F(AdapterCheckSettings.OffsetLeft)}, {F(AdapterCheckSettings.OffsetRight)}), C1 Planner boundaries, VolumeSourceAdapter on '{binding.Owned.Name}', C2 Reconciler");
            Say($"{test} | segment             | raw cut ft3        | raw fill ft3       | cut CY   | fill CY  | expected C/F CY  | diff C/F CY         | result");
            foreach (var row in ar.Rows)
            {
                var exp = expected.Single(e => Math.Abs(e.From - row.StartStation) < 1e-6 && Math.Abs(e.To - row.EndStation) < 1e-6);
                double dc = row.Cut - Cy(exp.CutFt3), df = row.Fill - Cy(exp.FillFt3);
                bool ok = row.Status == SegmentStatus.Current && Math.Abs(dc) <= AdapterCheckSettings.TolCy && Math.Abs(df) <= AdapterCheckSettings.TolCy;
                if (ok) _pass++; else _fail++;
                Say($"{test} | {F(row.StartStation),8}-{F(row.EndStation),-10} | {R(row.Raw.Cut),18} | {R(row.Raw.Fill),18} | {F3(row.Cut),8} | {F3(row.Fill),8} | {F3(Cy(exp.CutFt3)),7}/{F3(Cy(exp.FillFt3)),-8} | {E(dc)}/{E(df)} | {(ok ? "PASS" : "FAIL")}");
            }
            var pt = ar.Partition;
            Check($"{test} partition check", pt.Status == PartitionStatus.Passed,
                $"{pt.Status}: segment sum cut {R(pt.SegmentSum.Cut)} fill {R(pt.SegmentSum.Fill)}, band cut {R(pt.Band.Cut)} fill {R(pt.Band.Fill)}, tolerance {R(pt.Tolerance)}");
            var ol = ar.OutsideLimits;
            Check($"{test} outside-limits row", ol is not null && !ol.Shown,
                ol is null ? "not computed" : $"SurfaceTotals - band: cut {R(ol.Difference.Cut)} fill {R(ol.Difference.Fill)}, shown={ol.Shown}");
            double bandCut = ar.Rows.Sum(r => r.Cut), bandFill = ar.Rows.Sum(r => r.Fill);
            Check($"{test} band totals", Math.Abs(bandCut - 200) <= AdapterCheckSettings.TolCy && Math.Abs(bandFill - 200) <= AdapterCheckSettings.TolCy,
                $"cut {F3(bandCut)} CY fill {F3(bandFill)} CY (G1: 200 / 200)");
        }
        tr.Commit();
    }

    /// <summary>T10: Fixture D on K and SP, API bounded volume against C1's polygon area for boundaries built from the real AlignSegs.</summary>
    private static void StepT10()
    {
        using var tr = _db.TransactionManager.StartTransaction();
        var d = new AnalysisDef(Guid.NewGuid(), "D", N.EG, N.FgD);
        var binding = Ensure(tr, [d])[0];
        SayBinding(binding);
        var src = Source(tr, binding);
        foreach (var name in new[] { N.AlignmentK, N.AlignmentSP })
        {
            var al = OpenAlignment(tr, name);
            const string breaks = "START..END@100";
            var plan = Plan(Run("T10-" + name, name, breaks, [d]), al);
            var rec = Reconcile(plan, [new AnalysisInput(d, src)]);
            Problems("T10 " + name, rec.Problems.Where(p => p.Severity != Severity.Info));
            Say($"T10 {name}: Fixture D (slab 1.00, so fill ft3 = plan area), breaks '{breaks}', offsets (-100, 100); polygon = C1 BoundaryBuilder from the adapter's AlignSegs; analytic = 200 x segment length (exact for a symmetric band on any curve)");
            Say($"T10 | {name,-2} | segment             | elements           | API fill ft3        | C1 polygon area     | API vs polygon | polygon vs 200L");
            double worstLineArc = 0, worstSpiral = 0, tessLineArc = 0, tessSpiral = 0;
            foreach (var seg in plan.Segments)
            {
                var b = plan.Boundaries.Single(x => x.SegmentId == seg.Id);
                var raw = src.Bounded(b.Tessellated);
                double area = PlanArea.Polygon(b.Tessellated);
                double rel = Math.Abs(raw.Fill - area) / area;
                double analytic = (AdapterCheckSettings.OffsetRight - AdapterCheckSettings.OffsetLeft) * (seg.EndStation - seg.StartStation);
                double tess = (area - analytic) / analytic;
                var kinds = al.Segments.Where(g => g.StartStation < seg.EndStation - 1e-9 && g.EndStation > seg.StartStation + 1e-9).Select(g => g.Kind).Distinct().ToList();
                bool spiral = kinds.Contains(SegKind.Spiral);
                if (spiral) { worstSpiral = Math.Max(worstSpiral, rel); tessSpiral = Math.Max(tessSpiral, Math.Abs(tess)); }
                else { worstLineArc = Math.Max(worstLineArc, rel); tessLineArc = Math.Max(tessLineArc, Math.Abs(tess)); }
                Say($"T10 | {name,-2} | {F(seg.StartStation),8}-{F(seg.EndStation),-10} | {string.Join("+", kinds),-18} | {R(raw.Fill),19} | {R(area),19} | {E(rel),14} | {E(tess)}");
            }
            Check($"T10 {name} lines and arcs: API = polygon area", worstLineArc <= AdapterCheckSettings.T10Relative, $"worst relative {E(worstLineArc)} (tolerance {E(AdapterCheckSettings.T10Relative)}); tessellation vs 200L worst {E(tessLineArc)}");
            if (name == N.AlignmentSP)
                Check($"T10 {name} spiral segments: API = polygon area", worstSpiral <= AdapterCheckSettings.T10Relative, $"worst relative {E(worstSpiral)} (measured spiral tolerance); tessellation vs 200L worst {E(tessSpiral)}");
            var pt = rec.Analyses[0].Partition;
            Check($"T10 {name} partition check", pt.Status == PartitionStatus.Passed, $"{pt.Status}: segments {R(pt.SegmentSum.Fill)} vs band {R(pt.Band.Fill)} fill ft3, tolerance {R(pt.Tolerance)}");
        }
        tr.Commit();
    }

    /// <summary>T24: 2D area by the datum method on D over C's arc, and a boundary off the surface edge (E407, E419).</summary>
    private static void StepT24()
    {
        using var tr = _db.TransactionManager.StartTransaction();
        var d = new AnalysisDef(Guid.NewGuid(), "D", N.EG, N.FgD);
        var binding = Ensure(tr, [d])[0];
        SayBinding(binding);
        var src = Source(tr, binding);
        var al = OpenAlignment(tr, N.AlignmentC);
        var arc = al.Segments.Single(g => g.Kind == SegKind.Arc);

        var plan = Plan(Run("T24", N.AlignmentC, $"{R(arc.StartStation)}, {R(arc.EndStation)}", [d]), al);
        var row = Reconcile(plan, [new AnalysisInput(d, src)]).Analyses[0].Rows.Single();
        double polygon = PlanArea.Polygon(plan.Boundaries.Single().Tessellated);
        double expected = FixtureSpec.Expected.T24AreaFt2;
        double rel = (row.Area2D - expected) / expected;
        Say($"T24 | C arc [{F6(arc.StartStation)}, {F6(arc.EndStation)}] (-100, 100) | Area2D (datum method, datums {R(_cfg.AreaDatumLow)} and {R(_cfg.AreaDatumHigh)}) {R(row.Area2D)} SF | C1 polygon {R(polygon)} | analytic 20,000 pi = {R(expected)} | rel {E(rel)} | coverage {F6(row.Coverage)}");
        Check("T24 2D area on C", Math.Abs(rel) <= AdapterCheckSettings.T24Relative && row.Coverage > 1 - 1e-6,
            $"{F(row.Area2D)} SF vs 62,831.85 within {E(AdapterCheckSettings.T24Relative)}; Area2D vs its own polygon {E((row.Area2D - polygon) / polygon)}");

        var edge = Plan(Run("T24-edge", N.AlignmentC, $"{R(arc.EndStation)}, END", [d]), al);
        var rec = Reconcile(edge, [new AnalysisInput(d, src)]);
        var er = rec.Analyses[0].Rows.Single();
        double edgePolygon = PlanArea.Polygon(edge.Boundaries.Single().Tessellated);
        Problems("T24 edge", er.Problems);
        Say($"T24 | C out-tangent [{F6(arc.EndStation)}, END] (-100, 100), runs past EG's y = -300 edge | Area2D {R(er.Area2D)} SF | own polygon {R(edgePolygon)} | coverage {F6(er.Coverage)} | fill {R(er.Raw.Fill)} ft3 (= on-surface area; P22 analytic 34,641.016)");
        Check("T24 off-edge boundary (E407, E419)", er.Area2D < edgePolygon && er.Coverage < 1 && er.Problems.Any(p => p.Code == VolumeCodes.PartialCoverage),
            $"2D area {F(er.Area2D)} < boundary {F(edgePolygon)}, coverage {F3(er.Coverage * 100)} % flagged {VolumeCodes.PartialCoverage}; no clip to the surface border was needed (P4)");
        tr.Commit();
    }
}

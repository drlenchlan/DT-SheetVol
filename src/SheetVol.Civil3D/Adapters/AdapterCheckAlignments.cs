// A1 adapters: SVADAPTERS steps for the alignment adapter (P7, P8; E204, E208, E213, E214).

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using SheetVol.Civil3D.SelfTest;
using SheetVol.Core;
using N = SheetVol.Civil3D.SelfTest.FixtureSpec.Names;

namespace SheetVol.Civil3D.Adapters;

public static partial class AdapterCheckCommands
{
    private static double Deg(double rad) => rad * 180.0 / Math.PI;

    private static void StepAlignments()
    {
        using var tr = _db.TransactionManager.StartTransaction();
        foreach (var name in new[] { N.AlignmentS, N.AlignmentC, N.AlignmentK, N.AlignmentSP })
        {
            var al = OpenAlignment(tr, name);
            Say($"{name}: raw {R(al.StartStation)}..{R(al.EndStation)}, {al.Segments.Count} AlignSeg(s), handle {al.Handle}; station format: plus base {F(al.Stations.PlusBase)} read from {al.Stations.FormatEvidence}; equations {al.Stations.EquationCount}");
            foreach (var g in al.Segments)
                Say($"  {g.Kind,-6} raw {F6(g.StartStation)}..{F6(g.EndStation)} ({F6(g.Start.X)}, {F6(g.Start.Y)})->({F6(g.End.X)}, {F6(g.End.Y)}) StartDir {F6(Deg(g.StartDir))} deg EndDir {F6(Deg(g.EndDir))} deg"
                    + (g.Kind == SegKind.Arc ? $" Center ({F6(g.Center!.Value.X)}, {F6(g.Center.Value.Y)}) R {F6(g.Radius)} Clockwise={g.Clockwise}" : ""));
            var x = al.CrossCheck;
            Say($"  cross-check: {x.Method}; offset +-{F(x.Offset)}; {x.AnalyticPoints} closed-form point(s), {x.PointAtPoints} spiral PointAt point(s), {x.JunctionPointsSkipped} kinked-junction vertex(es) skipped; max |computed - PointLocation| = {E(x.MaxDeviation)} {x.Worst}; max direction error {E(x.MaxDirectionError)} rad");
            Check($"E204 construction cross-check {name}", x.MaxDeviation <= _cfg.CrossCheckTolerance && x.AnalyticPoints > 0,
                $"max {E(x.MaxDeviation)} <= {E(_cfg.CrossCheckTolerance)} over {x.AnalyticPoints} closed-form points");

            var raws = new[] { al.StartStation, 0.5 * (al.StartStation + al.EndStation), al.EndStation };
            var trips = raws.Select(r =>
            {
                string text = al.FormatStation(r);
                bool ok = al.TryParseStation(text, out double back, out string? err);
                return (r, text, ok, back, err);
            }).ToList();
            Say("  FormatStation -> TryParseStation: " + string.Join("; ", trips.Select(t => $"raw {F6(t.r)} -> '{t.text}' -> {(t.ok ? "raw " + F6(t.back) : "error " + t.err)}")));
            Check($"station round trip {name}", trips.All(t => t.ok && Math.Abs(t.back - t.r) <= 0.005), "every displayed station parses back within its 0.01 display precision");
        }

        var s = OpenAlignment(tr, N.AlignmentS);
        var left = s.PointAt(1000, -100);
        Check("PointAt sign on S", Math.Abs(left.X - 1000) < 1e-9 && Math.Abs(left.Y - 100) < 1e-9, $"PointAt(1000, -100) = ({F6(left.X)}, {F6(left.Y)}); negative offset is left = +y (contract)");

        var c = OpenAlignment(tr, N.AlignmentC);
        var arc = c.Segments.Single(g => g.Kind == SegKind.Arc);
        bool cOk = arc.Clockwise && Math.Abs(arc.Radius - FixtureSpec.C.Radius) < 1e-9
            && Math.Abs(arc.Center!.Value.X - FixtureSpec.C.Center.X) < 1e-6 && Math.Abs(arc.Center.Value.Y - FixtureSpec.C.Center.Y) < 1e-6
            && Math.Abs(arc.StartDir) < 1e-9 && Math.Abs(arc.EndDir - FixtureSpec.C.OutDirection) < 1e-9;
        Check("C arc AlignSeg", cOk, $"Clockwise={arc.Clockwise} (right turn), R={F6(arc.Radius)}, StartDir={F6(Deg(arc.StartDir))} deg, EndDir={F6(Deg(arc.EndDir))} deg (expected 0 and {F6(Deg(FixtureSpec.C.OutDirection))})");

        var k = OpenAlignment(tr, N.AlignmentK);
        Check("K tangent directions", k.Segments.Count == 2 && Math.Abs(k.Segments[1].StartDir - FixtureSpec.K.Deflection) < 1e-9,
            $"second tangent {F6(Deg(k.Segments[1].StartDir))} deg (expected {F6(FixtureSpec.K.DeflectionDeg)}); angle point at raw {F6(k.Segments[0].EndStation)}");

        var sp = OpenAlignment(tr, N.AlignmentSP);
        var kinds = string.Join(",", sp.Segments.Select(g => g.Kind));
        var spiralIn = sp.Segments.First(g => g.Kind == SegKind.Spiral);
        Check("SP AlignSegs", kinds == "Line,Spiral,Arc,Spiral,Line" && Math.Abs(spiralIn.EndDir - spiralIn.StartDir - FixtureSpec.SP.ThetaS) < 1e-9,
            $"kinds {kinds}; spiral in turns {F6(Deg(spiralIn.EndDir - spiralIn.StartDir))} deg (theta_s {F6(Deg(FixtureSpec.SP.ThetaS))})");
        tr.Commit();
    }

    /// <summary>E208 on alignment S with equations added inside a transaction that is never committed.</summary>
    private static void StepStationEquations()
    {
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var s = CivilAlignment(tr, N.AlignmentS, OpenMode.ForWrite);
            s.StationEquations.Add(1000, 800, StationEquationType.Increasing);
            s.StationEquations.Add(1500, 2000, StationEquationType.Increasing);
            var al = AlignmentAdapter.Open(tr, s.ObjectId, _units, _cfg).Value;
            Say($"S with equations back 1000 = ahead 800 and back 1500 = ahead 2000 (transaction to be aborted): regions {al.Stations.Describe()}");
            var cases = new (string Text, double? Raw, string Why)[]
            {
                ("5+00", 500, "region 0 only"),
                ("9+00", null, "raw 900 and raw 1100 both show 9+00.00 (P8)"),
                ("8+00", null, "raw 800 and raw 1000 (the equation itself shows 8+00.00)"),
                ("12+50", 1450, "second region only"),
                ("17+50", null, "falls in the gap the second equation leaves"),
                ("22+00", 1700, "third region only"),
                ("25+50", 2050, "past the end: extrapolated so the planner reports E305"),
            };
            foreach (var (text, raw, why) in cases)
            {
                bool ok = al.TryParseStation(text, out double got, out string? err);
                bool pass = raw is null ? !ok : ok && Math.Abs(got - raw.Value) < 1e-9;
                Check($"E208 parse '{text}'", pass, ok ? $"raw {F6(got)} ({why})" : $"rejected: {err} ({why})");
            }
            string shown = al.FormatStation(1100);
            bool rt = al.TryParseStation(shown, out _, out string? rtErr);
            Check("E208 FormatStation(1100) does not parse back to one raw", !rt, $"'{shown}' -> {rtErr}");

            var amb = Plan2(Run("E208", N.AlignmentS, "START, 9+00, END", []), al);
            var e208 = amb.Problems.Where(p => p.Code == Core.Planning.ProblemCodes.E208).ToList();
            Check("E208 through the planner", e208.Count == 1 && e208[0].Message.Contains("raw 900") && e208[0].Message.Contains("raw 1100"),
                e208.Count == 1 ? e208[0].Message : $"{e208.Count} E208 problems");
            var ok1250 = Plan2(Run("E208b", N.AlignmentS, "START, 12+50, END", []), al);
            Check("E208 unambiguous break plans", ok1250.IsValid && ok1250.Segments.Count == 2 && Math.Abs(ok1250.Segments[0].EndStation - 1450) < 1e-9,
                $"segments {string.Join(", ", ok1250.Segments.Select(Seg))}");

            while (s.StationEquations.Count > 0) s.StationEquations.Remove(0);
            s.StationEquations.Add(1000, 800, StationEquationType.Decreasing);
            var dec = AlignmentAdapter.Open(tr, s.ObjectId, _units, _cfg).Value;
            bool d1 = dec.TryParseStation("7+00", out _, out string? decErr);
            bool d2 = dec.TryParseStation("-1+00", out double rawNeg, out _);
            Check("E208 decreasing equation", !d1 && d2 && Math.Abs(rawNeg - 1900) < 1e-9,
                $"regions {dec.Stations.Describe()}; '7+00' -> {decErr}; '-1+00' -> raw {F6(rawNeg)}; FormatStation(1900) = '{dec.FormatStation(1900)}'");
            // no Commit: the equations are rolled back
        }
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var s = CivilAlignment(tr, N.AlignmentS, OpenMode.ForRead);
            Check("E208 test left S unchanged", s.StationEquations.Count == 0, $"S has {s.StationEquations.Count} equations after the aborted transaction");
            tr.Commit();
        }
    }

    private static Core.Planning.PlanResult Plan2(RunDefinition run, IAlignmentGeometry al)
    {
        var plan = new Core.Planning.Planner(Core.Planning.PlanningOptions.ForUnitScale(_units.DrawingUnitsPerFoot)).Plan(run, al);
        Problems("plan " + run.Name, plan.Problems.Where(p => p.Severity != Severity.Info));
        return plan;
    }

    /// <summary>
    /// E213 (missing, erased) and E214 (disconnected). The test alignments are committed and left in
    /// the scratch drawing. MEASURED 2026-11-05 (SVADAPTERS_ABORT): a transaction that created an
    /// alignment, or an owned volume surface, aborts cleanly; one that created an alignment and
    /// erased it crashed the Core Console (0xC0000005) at the erase or the abort.
    /// </summary>
    private static void StepAlignmentFailures()
    {
        ObjectId erasedId, brokenId, zigId;
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var missing = AlignmentAdapter.Find(tr, _db, "NO-SUCH-ALIGNMENT", new StoredRef("NO-SUCH-ALIGNMENT", "FFFFFF"), _units, _cfg);
            Problems("E213 missing", missing.Problems);
            Check("E213 alignment not in the drawing", !missing.Succeeded && missing.Problems.Any(p => p.Code == AdapterCodes.AlignmentNotFound), "fails at step 1 with SV-E213");

            string sHandle = CivilAlignment(tr, N.AlignmentS, OpenMode.ForRead).Handle.ToString();
            var renamed = AlignmentAdapter.Find(tr, _db, "S-OLD-NAME", new StoredRef("S-OLD-NAME", sHandle), _units, _cfg);
            Problems("E445 alignment renamed", renamed.Problems);
            Check("E445 alignment renamed, matched by stored handle", renamed.Succeeded && renamed.Value.Name == N.AlignmentS && renamed.Problems.Any(p => p.Code == AdapterCodes.SourceRenamed), $"opened '{(renamed.Succeeded ? renamed.Value.Name : "-")}'");
            var edited = AlignmentAdapter.Find(tr, _db, "S-MISSPELLED", new StoredRef(N.AlignmentS, sHandle), _units, _cfg);
            Problems("E213 edited name", edited.Problems);
            Check("E213 edited run name never falls back to the previous alignment's handle", !edited.Succeeded && edited.Problems.Any(p => p.Code == AdapterCodes.AlignmentNotFound), "stored handle was stored for 'S', the run now asks for 'S-MISSPELLED'");

            var civ = CivilDocument.GetCivilDocument(_db);
            var erased = NewAlignment(tr, civ, AdapterCheckSettings.ErasedAlignment);
            erased.Entities.AddFixedLine(new Point3d(0, 1000, 0), new Point3d(100, 1000, 0));
            erasedId = erased.ObjectId;
            var broken = NewAlignment(tr, civ, AdapterCheckSettings.BrokenAlignment);
            broken.Entities.AddFixedLine(new Point3d(0, 2000, 0), new Point3d(100, 2000, 0));
            broken.Entities.AddFixedLine(new Point3d(200, 2000, 0), new Point3d(300, 2000, 0));
            brokenId = broken.ObjectId;
            Say($"{broken.Name}: two fixed lines 100 apart; Entities.Count={broken.Entities.Count}, stations {R(broken.StartingStation)}..{R(broken.EndingStation)}");

            // Short tangents with 20 degree angle points: at the +-100 cross-check offset C1's mitres
            // fold (E205), so the adapter must fall back to the direct closed-form check and pass.
            var zig = NewAlignment(tr, civ, AdapterCheckSettings.ZigzagAlignment);
            var p = new Point3d(0, 3000, 0);
            for (int i = 0; i < AdapterCheckSettings.ZigzagTangents; i++)
            {
                double dir = (i % 2 == 0 ? 1 : -1) * AdapterCheckSettings.ZigzagDeflectionDeg / 2 * Math.PI / 180;
                var q = new Point3d(p.X + AdapterCheckSettings.ZigzagTangentLength * Math.Cos(dir), p.Y + AdapterCheckSettings.ZigzagTangentLength * Math.Sin(dir), 0);
                zig.Entities.AddFixedLine(p, q);   // end to end, as FX builds K
                p = q;
            }
            zigId = zig.ObjectId;
            tr.Commit();
        }
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            tr.GetObject(erasedId, OpenMode.ForWrite).Erase();
            tr.Commit();
        }
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var gone = AlignmentAdapter.Open(tr, erasedId, _units, _cfg);
            Problems("E213 erased", gone.Problems);
            Check("E213 alignment erased", !gone.Succeeded && gone.Problems.Any(p => p.Code == AdapterCodes.AlignmentNotFound), "fails at step 1 with SV-E213");
            var gap = AlignmentAdapter.Open(tr, brokenId, _units, _cfg);
            Problems("E214", gap.Problems);
            Check("E214 disconnected entities", !gap.Succeeded && gap.Problems.Any(p => p.Code == AdapterCodes.AlignmentGap), gap.Problems.FirstOrDefault()?.Message ?? "no problem raised");

            var zig = AlignmentAdapter.Open(tr, zigId, _units, _cfg);
            Problems("E204 fallback", zig.Problems);
            var zx = zig.Succeeded ? zig.Value.CrossCheck : null;
            if (zx is not null) Say($"{AdapterCheckSettings.ZigzagAlignment}: {zig.Value.Segments.Count} tangents of {F(AdapterCheckSettings.ZigzagTangentLength)} with {F(AdapterCheckSettings.ZigzagDeflectionDeg)} deg angle points; cross-check: {zx.Method}; {zx.AnalyticPoints} closed-form point(s); max {E(zx.MaxDeviation)}");
            Check("E204 direct-check fallback on a kinked alignment", zx is not null && zx.Method.StartsWith("direct", StringComparison.Ordinal) && zx.MaxDeviation <= _cfg.CrossCheckTolerance,
                zx is null ? "adapter refused the alignment" : $"fallback ran and passed ({E(zx.MaxDeviation)})");
            tr.Commit();
        }
    }

    private static Alignment CivilAlignment(Transaction tr, string name, OpenMode mode)
    {
        var civ = CivilDocument.GetCivilDocument(_db);
        foreach (ObjectId id in civ.GetAlignmentIds())
            if (tr.GetObject(id, OpenMode.ForRead) is Alignment a && a.Name == name)
            {
                if (mode == OpenMode.ForWrite) a.UpgradeOpen();
                return a;
            }
        throw new InvalidOperationException($"alignment '{name}' not found");
    }

    private static Alignment NewAlignment(Transaction tr, CivilDocument civ, string name)
    {
        var styles = civ.Styles.AlignmentStyles;
        var labels = civ.Styles.LabelSetStyles.AlignmentLabelSetStyles;
        var id = Alignment.Create(civ, name, ObjectId.Null, _db.LayerZero, styles[0], labels[0]);
        return (Alignment)tr.GetObject(id, OpenMode.ForWrite);
    }
}

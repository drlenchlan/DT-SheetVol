// A1 adapters: the SVADAPTERS headless check (HANDOFF.md section 10, A1; tests T1, T2, T10,
// T24, T29; rows E204, E208, E213, E214, E401-E404, E407, E412, E415, E803, E1012, E1013,
// E1021). Evidence code: every step prints what it measured. Fixtures come from FX's API and
// are built in-process; expectations come from FixtureSpec. Run with
// tools/scripts/adapters.scr through tools/test-headless.ps1.

using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using SheetVol.Civil3D.SelfTest;
using SheetVol.Core;
using SheetVol.Core.Planning;
using SheetVol.Core.Volumes;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(SheetVol.Civil3D.Adapters.AdapterCheckCommands))]

namespace SheetVol.Civil3D.Adapters;

/// <summary>Names and numbers used only by the SVADAPTERS check (the product's come from <see cref="AdapterConfig"/>).</summary>
internal static class AdapterCheckSettings
{
    public const double OffsetLeft = -100.0, OffsetRight = 100.0;   // section 8 bands; negative = left
    public const double TolCy = 0.01;                                 // T1, T2 (section 8)
    public const double T10Relative = 1e-6;                           // T10
    public const double T24Relative = 2e-4;                           // T24
    public const double T29Relative = 1e-9;                           // T29, E1012 on fixtures (6.15)
    public const string BrokenAlignment = "SV-A1-E214";
    public const string ErasedAlignment = "SV-A1-E213";
    public const string ZigzagAlignment = "SV-A1-ZIGZAG";               // E204 fallback: mitres fold at +-100
    public const int ZigzagTangents = 6;
    public const double ZigzagTangentLength = 30.0, ZigzagDeflectionDeg = 20.0;
    public const string GridSurface = "SV-A1-GRID";
    public const string EmptySurface = "SV-A1-EMPTY";
    public const string OutOfDateSurface = "SV-A1-E404";
    public const string OutOfDateGroup = "SV-A1-E404";
    public const string RenamedSuffix = "-renamed-by-user";
    public const string LockedLayer = "SV-A1-LOCKED";
    public const double UserVolumeCutFactor = 1.5;                    // E412: the untagged look-alike keeps this
    public static readonly (double X0, double Y0, double X1, double Y1) AroundP1 = (60, -60, 190, 60);
    public static readonly (double X0, double Y0, double X1, double Y1) HideOnFgA = (105, -20, 145, 20);
    public const double HideMidOrdinate = 0.1;
    public const int ScaleGridSide = 708;                            // P23's 1 M-triangle grid
    public const double ScaleGridSpacing = 2.0;
}

/// <summary>SVADAPTERS: builds FX's fixtures in the open drawing and checks every adapter against them.</summary>
public static partial class AdapterCheckCommands
{
    private static Editor? _ed;
    private static Database _db = null!;
    private static AdapterConfig _cfg = new();
    private static readonly VolumeConfig VolCfg = new();
    private static DrawingUnitInfo _units = null!;
    private static int _pass, _fail, _stepFailures;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Runs every check step; a step that throws prints SV-E195 and the next step runs.</summary>
    [CommandMethod("SVADAPTERS", CommandFlags.Modal)]
    public static void Check()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        _ed = doc?.Editor;
        if (doc is null) { Console.WriteLine($"{AdapterCodes.CheckStepFailed} SVADAPTERS: no active document"); return; }
        _db = doc.Database;
        _cfg = new AdapterConfig();
        _pass = _fail = _stepFailures = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Say("===== SVADAPTERS begin =====");

        Step("config", StepConfig);
        Step("units", StepUnits);
        Step("fixtures", StepFixtures);
        Step("alignments", StepAlignments);
        Step("E208 station equations", StepStationEquations);
        Step("E213 E214 alignment failures", StepAlignmentFailures);
        Step("E412 collision setup", StepCollisionSetup);
        Step("T1 T2", StepT1T2);
        Step("T10", StepT10);
        Step("T24", StepT24);
        Step("T29 E1012", StepT29);
        Step("Fixture H", StepFixtureH);
        Step("E1013", StepHiddenTriangles);
        Step("E412 lifecycle", StepOwnedLifecycle);
        Step("E401 E402 E403 E803", StepSourceRows);
        Step("E404", StepOutOfDate);
        Step("polygon rule", StepPolygonRule);
        Step("triangle scale", StepTriangleScale);

        Say($"SVADAPTERS summary: {_pass} PASS, {_fail} FAIL, {_stepFailures} step(s) failed ({sw.Elapsed.TotalSeconds:0.0} s)");
        Say("===== SVADAPTERS end =====");
    }

    // ------------------------------------------------------------ steps that need no fixtures

    private static void StepConfig()
    {
        var problems = _cfg.Validate();
        Check("config defaults validate", problems.Count == 0, problems.Count == 0 ? "no problems" : string.Join(" | ", problems.Select(p => p.Message)));
        var round = AdapterConfig.FromJson(_cfg.ToJson());
        Check("config JSON round trip", round.Succeeded && round.Value == _cfg, round.Succeeded ? "equal after ToJson/FromJson" : string.Join(" | ", round.Problems.Select(p => p.Message)));
        var bad = AdapterConfig.FromJson("{ \"areaDatumLow\": 1, \"areaDatumHigh\": 1, \"noSuchKey\": 2 }");
        Check("config rejects unknown keys and equal datums", !bad.Succeeded, string.Join(" | ", bad.Problems.Select(p => $"{p.Code} {p.Message}")));
    }

    private static void StepUnits()
    {
        var u = DrawingUnitsReader.Read(_db, VolCfg.Constants);
        Problems("units", u.Problems);
        _units = u.Value;
        Say($"Civil drawing unit={_units.CivilUnit}; INSUNITS={_units.InsunitsIgnored} (ignored, P2); DrawingUnitsPerFoot={R(_units.DrawingUnitsPerFoot)} -> PlanningOptions.ForUnitScale; C2 DrawingUnits=({_units.VolumeUnits.System}, {R(_units.VolumeUnits.BaseUnitsPerDrawingUnit)}); dialog UnitSystem={_units.DialogUnits}");
        Check("unit detection reads Civil Feet, not INSUNITS", _units.CivilUnit == Autodesk.Civil.Settings.DrawingUnitType.Feet && _units.DrawingUnitsPerFoot == 1.0,
            $"MidOrdinate from ForUnitScale = {R(PlanningOptions.ForUnitScale(_units.DrawingUnitsPerFoot).Geometry.MidOrdinate)}");
    }

    private static void StepFixtures()
    {
        static string S(SurfaceFixture f) => $"{f.BaseName}{(f.BaseReused ? " (reused)" : "")}/{f.ComparisonName}, {f.Checks.Count} checks";
        static string A(AlignmentFixture f) => $"{f.Name} {F(f.StartStation)}..{F(f.EndStation)}";
        Say("fixtures (FX API, in-process): " + string.Join("; ", new[]
        {
            S(Fixtures.CreateA(_db)), S(Fixtures.CreateB(_db)), S(Fixtures.CreateD(_db)), S(Fixtures.CreateH(_db)),
            A(Fixtures.CreateAlignmentS(_db)), A(Fixtures.CreateAlignmentC(_db)), A(Fixtures.CreateAlignmentK(_db)), A(Fixtures.CreateAlignmentSP(_db)),
        }));
    }

    // ------------------------------------------------------------ shared plumbing

    private static void Step(string name, Action body)
    {
        Say($"--- {name} ---");
        try { body(); }
        catch (System.Exception ex)
        {
            _stepFailures++;
            Say($"{AdapterCodes.CheckStepFailed} step '{name}' failed: {Describe(ex)}");
        }
    }

    private static void Check(string label, bool pass, string detail)
    {
        if (pass) _pass++; else _fail++;
        Say($"CHECK {label}: {(pass ? "PASS" : "FAIL")} {detail}");
    }

    internal static void Say(string s)
    {
        if (_ed is not null) _ed.WriteMessage("\n" + s);
        else Console.WriteLine(s);
    }

    private static void Problems(string context, IEnumerable<Problem> problems)
    {
        foreach (var p in problems)
            Say($"  [{context}] {p.Code} {p.Severity}: {p.Message}{(p.Fix is null ? "" : $" Fix: {p.Fix}")}");
    }

    private static string Describe(System.Exception ex) => ex switch
    {
        Autodesk.AutoCAD.Runtime.Exception a => $"{a.GetType().FullName} ErrorStatus={a.ErrorStatus} Message='{a.Message}'",
        _ => $"{ex.GetType().FullName} Message='{ex.Message}'" + (ex.InnerException is null ? "" : $" Inner: {Describe(ex.InnerException)}"),
    };

    /// <summary>Opens an alignment through the adapter; prints and throws when it fails.</summary>
    private static AlignmentAdapter OpenAlignment(Transaction tr, string name)
    {
        var o = AlignmentAdapter.Find(tr, _db, name, null, _units, _cfg);
        if (!o.Succeeded) { Problems("alignment " + name, o.Problems); throw new InvalidOperationException($"alignment '{name}' did not open through the adapter"); }
        Problems("alignment " + name, o.Problems);
        return o.Value;
    }

    /// <summary>Ensures owned surfaces for analyses; prints every problem; throws when it fails.</summary>
    private static IReadOnlyList<AnalysisBinding> Ensure(Transaction tr, IReadOnlyList<AnalysisDef> analyses, IReadOnlyDictionary<Guid, StoredAnalysisRefs>? stored = null)
    {
        var o = AnalysisResolver.EnsureAnalyses(tr, _db, analyses, stored, _cfg, VolCfg);
        Problems("ensure", o.Problems);
        if (!o.Succeeded) throw new InvalidOperationException("owned volume surfaces could not be ensured");
        return o.Value;
    }

    private static VolumeSourceAdapter Source(Transaction tr, AnalysisBinding b)
    {
        var o = VolumeSourceAdapter.Open(tr, b.Owned.Id, _cfg);
        Problems("source", o.Problems);
        return o.Value;
    }

    private static RunDefinition Run(string name, string alignment, string breaks, IReadOnlyList<AnalysisDef> analyses,
        double left = AdapterCheckSettings.OffsetLeft, double right = AdapterCheckSettings.OffsetRight) =>
        new(name, alignment, SegmentDriver.Stations, breaks, null, left, right, analyses, [], [], new Dictionary<Guid, SegmentOverrides>(), new ViewOptions());

    private static PlanResult Plan(RunDefinition run, IAlignmentGeometry al)
    {
        var plan = new Planner(PlanningOptions.ForUnitScale(_units.DrawingUnitsPerFoot)).Plan(run, al);
        Problems("plan " + run.Name, plan.Problems.Where(p => p.Severity != Severity.Info));
        if (!plan.IsValid) throw new InvalidOperationException($"run '{run.Name}' did not plan");
        return plan;
    }

    private static ReconcileResult Reconcile(PlanResult plan, IReadOnlyList<AnalysisInput> inputs) =>
        Reconciler.Reconcile(new ReconcileRequest(plan.Segments, plan.Boundaries, plan.Band.Select(b => b.Tessellated).ToList(),
            inputs, _units.VolumeUnits, plan.PartitionCheckSkipReason), VolCfg);

    private static string Seg(SegmentDef s) => $"{F(s.StartStation),9}-{F(s.EndStation),-9}";

    internal static string R(double d) => d.ToString("R", Inv);
    internal static string F(double d) => d.ToString("0.###", Inv);
    internal static string F3(double d) => d.ToString("0.000", Inv);
    internal static string F6(double d) => d.ToString("0.######", Inv);
    internal static string E(double d) => d.ToString("0.00E+00", Inv);
    internal static double Cy(double ft3) => ft3 / VolCfg.Constants.CubicFeetPerCubicYard;
}

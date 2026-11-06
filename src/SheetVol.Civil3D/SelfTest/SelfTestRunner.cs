// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4 (SVSELFTEST driver); extended
// at the Wave 2 integration with the section 8 tests the installed bindings make possible.
//
// The acceptance checks of HANDOFF.md section 8 and the H rows of 7.2-7.8 at the pipeline
// level, against whichever bindings it is given: the fakes in SelfTestFakes.cs, or the real
// A1-A3 factories (SVSELFTEST Installed). Every check records measured against expected; a
// check that cannot run on the given bindings or headless says so with its reason and counts
// as NOT RUN, never as a pass.

using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Civil3D.Commands;
using SheetVol.Civil3D.Ownership;
using SheetVol.Civil3D.Session;
using SheetVol.Civil3D.WritersModel;
using SheetVol.Civil3D.WritersPaper;
using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Tracking;
using SheetVol.Core.Volumes.Integrator;
using AcadDocument = Autodesk.AutoCAD.ApplicationServices.Document;
using CivTinVolumeSurface = Autodesk.Civil.DatabaseServices.TinVolumeSurface;
using RunRecord = SheetVol.Civil3D.Session.RunRecord;
using SourceKinds = SheetVol.Civil3D.Session.SourceKinds;
using Want = SheetVol.Civil3D.SelfTest.FixtureSpec.Expected;

namespace SheetVol.Civil3D.SelfTest;

/// <summary>One check in selftest-result.json.</summary>
/// <param name="Id">Test or row id, for example T1.volumes or E811.</param>
/// <param name="Status"><see cref="Passed"/>, <see cref="Failed"/> or <see cref="NotRun"/>.</param>
/// <param name="Measured">What was measured.</param>
/// <param name="Expected">What section 8 or section 7 requires.</param>
/// <param name="Message">Plain words; for NOT RUN, the reason.</param>
public sealed record SelfTestCheck(string Id, string Status, string Measured, string Expected, string Message)
{
    /// <summary>The check ran and passed.</summary>
    public const string Passed = "PASS";

    /// <summary>The check ran and failed, or threw.</summary>
    public const string Failed = "FAIL";

    /// <summary>The check cannot run on these bindings or headless; the message says why.</summary>
    public const string NotRun = "NOT RUN";

    /// <summary>True only for a check that ran and passed.</summary>
    public bool Pass => Status == Passed;

    /// <summary>A check that ran.</summary>
    public static SelfTestCheck Of(string id, bool pass, string measured, string expected, string message) =>
        new(id, pass ? Passed : Failed, measured, expected, message);
}

/// <summary>Runs the pipeline-level acceptance checks.</summary>
internal sealed class SelfTestRunner
{
    private const string AlignmentS = FixtureSpec.Names.AlignmentS;
    private const string NorthAlignment = "SV-T17-N";
    private readonly AcadDocument _doc;
    private readonly Action<string> _say;
    private readonly SheetVolSettings _settings;
    private readonly IAdapterFactory _adapters;
    private readonly IWriterFactory _writers;
    private readonly ISelfTestWorld? _world;
    private readonly bool _fakes;
    private readonly string _dir;
    private readonly double _tolCy;
    private readonly Guid _analysisA = Guid.Parse("a0a0a0a0-0000-4000-8000-00000000000a");
    private readonly Guid _analysisH = Guid.Parse("a0a0a0a0-0000-4000-8000-00000000000b");
    private readonly List<string> _unexpected = [];
    private readonly List<(string Run, double Relative)> _crossChecks = [];

    public SelfTestRunner(AcadDocument doc, Action<string> say, SheetVolSettings settings, IAdapterFactory adapters, IWriterFactory writers,
        ISelfTestWorld? world, bool fakes, string dir)
    {
        _doc = doc;
        _say = say;
        _settings = settings;
        _adapters = adapters;
        _writers = writers;
        _world = world;
        _fakes = fakes;
        _dir = dir;
        _tolCy = settings.SelfTest.VolumeToleranceCy;
    }

    public List<SelfTestCheck> Checks { get; } = [];

    // ---- plumbing --------------------------------------------------------------------------

    private void Add(string id, bool pass, string measured, string expected, string message)
    {
        var k = SelfTestCheck.Of(id, pass, measured, expected, message);
        Checks.Add(k);
        _say($"{(pass ? "SVSELFTEST" : PipelineCodes.SelfTestFailed + " SVSELFTEST")} {id} {k.Status}: {message} | measured {measured} | expected {expected}");
    }

    private void NotRun(string id, string expected, string reason)
    {
        Checks.Add(new SelfTestCheck(id, SelfTestCheck.NotRun, "not run", expected, reason));
        _say($"SVSELFTEST {id} NOT RUN: {reason}");
    }

    private void Try(string id, Action body)
    {
        try { body(); }
        catch (Exception ex) { Add(id, false, $"{ex.GetType().Name}: {ex.Message}", "no exception", "the check threw"); }
    }

    private RegenEngine Engine(IAdapterFactory? adapters = null, IWriterFactory? writers = null, SheetVolSettings? settings = null) =>
        new(new EngineBindings(adapters ?? _adapters, writers ?? _writers), settings ?? _settings, SettingsProvider.PluginDirectory);

    private RunOutcome Regen(RunDefinition run, RegenEngine? engine = null)
    {
        var o = (engine ?? Engine()).Regenerate(_doc, run, RegenMode.Full, null, CancellationToken.None);
        SessionState.Record(_doc.Database, o);
        _unexpected.AddRange(o.Result.Problems.Where(p => p.Code == PipelineCodes.Unexpected).Select(p => $"{run.Name}: {p.Message}"));
        if (engine is null && o.CrossCheckMaxRelative is { } rel) _crossChecks.Add((run.Name, rel));
        return o;
    }

    private RunDefinition Def(string name, string spec, string alignment = AlignmentS, ReportPreset preset = ReportPreset.Standard,
        IReadOnlyList<AnalysisDef>? analyses = null, AreaUnit area = AreaUnit.SquareFeet,
        double left = -100, double right = 100, ScaleMode scaleMode = ScaleMode.FitStandard) =>
        new(name, alignment, SegmentDriver.Stations, spec, null, left, right,
            analyses ?? [new AnalysisDef(_analysisA, "EG vs FG_A", FixtureSpec.Names.EG, FixtureSpec.Names.FgA)],
            [], [], new Dictionary<Guid, SegmentOverrides>(),
            new ViewOptions(ViewOrientation.AlongAlignment, scaleMode, 50, [20, 30, 40, 50, 60, 100], ClipMode.Boundary, 0),
            preset, area, AutoMode.Views);

    private AnalysisDef[] Pair(string name, string baseSurface, string comparison, double cut = 1, double fill = 1) =>
        [new AnalysisDef(DeterministicId(name), name, baseSurface, comparison, cut, fill)];

    private static Guid DeterministicId(string name) => RunIds.For("analysis:" + name);

    /// <summary>The SEDIMENT analysis on Fixture H: base <paramref name="baseSurface"/> (an OHWM surface for the rev 3 HCFCD checks; EG_H when null), comparison FG_H.</summary>
    private AnalysisDef[] Sediment(string? baseSurface = null) =>
        [new AnalysisDef(_analysisH, "SEDIMENT", baseSurface ?? FixtureSpec.Names.EgH, FixtureSpec.Names.FgH)];

    private static double Cy(double ft3) => ft3 / FixtureSpec.CubicFeetPerCubicYard;

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string E(double v) => v.ToString("0.0E+0", CultureInfo.InvariantCulture);

    private static string Codes(RunOutcome o) => string.Join(",", o.Result.Problems.Where(p => p.Severity != Severity.Info).Select(p => p.Code).Distinct());

    private static string FirstError(RunOutcome o) => o.Result.Problems.FirstOrDefault(p => p.Severity == Severity.Error && p.Code != PipelineCodes.RunFailed)?.Message ?? "";

    private static Dictionary<OwnedRole, int> RoleCounts(RunOutcome o) =>
        (o.Record?.Owned ?? []).GroupBy(x => x.Key.Role).ToDictionary(g => g.Key, g => g.Count());

    private static string? HandleOf(RunRecord? r, Guid segment, OwnedRole role) =>
        r?.Owned.FirstOrDefault(o => o.Key.SegmentId == segment && o.Key.Role == role)?.Handle;

    private IReadOnlyList<LayoutRef> Layouts() => WithTransaction(tr =>
    {
        using var paper = _writers.Paper(_doc.Database, tr, _settings);
        return paper.Layouts();
    });

    private T WithTransaction<T>(Func<Transaction, T> read)
    {
        using var tr = _doc.Database.TransactionManager.StartTransaction();
        var r = read(tr);
        tr.Commit();
        return r;
    }

    /// <summary>Everything an aborted regen must leave unchanged: stored records, owned objects as inspected (live content), model-space and layout counts, the fake dictionaries.</summary>
    private string Snapshot() => WithTransaction(tr =>
    {
        var db = _doc.Database;
        var lines = new List<string>();
        var store = _writers.Store(db, tr, _settings);
        var model = _writers.Model(db, tr, _settings);
        using var paper = _writers.Paper(db, tr, _settings);
        foreach (string n in store.RunNames())
        {
            var load = store.Load(n);
            lines.Add($"run {n} {(load.Record is { } r ? RunRecordCodec.Signature(r) + " " + r.LastRegenUtc?.ToString("O") : "unreadable")}");
            foreach (var o in load.Record?.Owned ?? [])
            {
                var w = model.Roles.Contains(o.Key.Role) ? (IOwnedObjectWriter)model : paper;
                var s = w.Inspect(n, o.Key, o.Handle);
                lines.Add($"  {o.Key} {o.Handle} {s.Exists} {s.Signature}");
            }
        }
        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
        lines.Add($"modelspace {ms.Cast<ObjectId>().Count(id => !id.IsErased)}");
        lines.Add($"layouts {((DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead)).Count}");
        lines.Add($"fake objects {new FakeObjectTable(db, tr, _settings.SelfTest.FakeObjectDictionary).All().Count}");
        return string.Join("\n", lines);
    });

    private void SaveDefinition(RunDefinition run) =>
        WithTransaction(tr =>
        {
            var store = _writers.Store(_doc.Database, tr, _settings);
            var r = store.Load(run.Name).Record;
            store.Save(r is null ? RunRecord.New(run) : r with { Run = run with { KnownSegments = r.Run.KnownSegments } });
            return true;
        });

    // ---- the checks ---------------------------------------------------------------------------

    public void Run()
    {
        // Rev 3: Fixture H's OHWM surfaces. SVFIXTURE H builds them in the drawing (installed bindings);
        // the in-memory fake world gets the same flat triangles here.
        if (_world is FakeWorld fake) Fixtures.AddOhwmSurfaces(fake.Surfaces);
        RunOutcome? t1 = null, t2 = null;
        Try("CONFIG", Config);
        Try("LIFETIME", Lifetime);
        Try("T1", () => t1 = T1());
        if (t1 is not null)
        {
            Try("T19", () => T19(t1));
            Try("T12", T12);
            Try("T13", T13);
        }
        Try("T2", () => t2 = T2());
        Try("T3", T3);
        Try("T4", T4);
        Try("T16", T16Fixed);
        // Rev 3 (NOW.md X5, rule 6): OHWM is a surface. Each HCFCD analysis takes Fixture H's flat OHWM
        // surface at the stated elevation as its base and FG_H as its comparison; every expected value
        // is OHWM minus design, derived in FixtureSpec.Expected.
        Try("T25", () => Hcfcd("T25", "0, 500, 1000", FixtureSpec.Names.Ohwm100, -100, 100));
        Try("T26", () => Hcfcd("T26", "0, 500, 1000", FixtureSpec.Names.Ohwm97, -100, 100));
        Try("T27", () => Hcfcd("T27", "0, 500, 1000", FixtureSpec.Names.Ohwm95, -100, 100));
        Try("T27.E1001", () => Hcfcd("T27.E1001", "1500, 2000", FixtureSpec.Names.Ohwm95, -100, 100));
        Try("T28", () => Hcfcd("T28", "1000, 1500", FixtureSpec.Names.Ohwm100, -100, 100));
        Try("T30", () => Hcfcd("T30", "0, 500, 1000", FixtureSpec.Names.Ohwm100, -12, 12));
        if (t2 is not null)
        {
            Try("E802", () => E802(t2));
            Try("E404", E404);
            Try("E1012", E1012);
            Try("E506-E507", () => E506E507(t2));
        }
        if (_fakes)
            NotRun("INSTALLED-ONLY", "T5-T11, T17, T18, T24, T29, T31",
                "the fake world holds only Fixtures A and H on alignment S and writes Xrecords, not drawing objects; these run with SVSELFTEST Installed");
        else
        {
            Try("T5", T5);
            Try("T6", T6);
            Try("T7-T8-T9-T24", T7T8T9T24);
            Try("T10", T10);
            Try("T11", T11);
            Try("T17", T17);
            Try("T18", T18);
            Try("T31", T31);
            Try("E217", E217);
            Try("E308", E308);
            Try("E311", E311);
            Try("E401-E402-E403", E401E402E403);
            Try("E411-E421", E411E421);
            Try("E412", E412);
            Try("E501", E501);
            Try("E503", E503);
            Try("E505", E505);
            Try("E601", E601);
            Try("E803", E803);
            Try("E808", E808);
            NotRun("E807", "regen refuses a read-only drawing at step 1",
                "Core Console opens the /i drawing for editing and a script cannot reopen it read-only; RegenEngine.ReadOnlyReason is exercised only in full Civil 3D (Dan's M check)");
            NotRun("E812", "batch regen over many drawings, one failure does not stop the batch",
                "the batch runner is L2's tools/regen-sheets.ps1 (Wave 3), not built yet");
            NotRun("E415", "data-shortcut surfaces compute as native ones",
                "data-shortcut and corridor surfaces cannot be built headless (P6, P23, E1021); needs Dan's fixture pair (stop conditions 12.2 and 12.9 open)");
        }
        Try("T14", T14);
        Try("D10", D10);
        Try("T20", T20);
        Try("E801", E801);
        Try("E213", E213);
        Try("T29", T29);
        Try("SESSION", Session);
        Add("E823", _unexpected.Count == 0, _unexpected.Count == 0 ? "none" : string.Join(" | ", _unexpected), "none",
            "no regen in this self-test hit an unexpected exception (before or after its commit)");
    }

    private RunOutcome T1()
    {
        var o = Regen(Def("T1", "START..END@500"));
        if (!o.Result.Succeeded) { Add("T1", false, Codes(o), "succeeded", FirstError(o)); return o; }
        CompareRows("T1.volumes", o, FixtureSpec.Expected.T1, "A, START..END@500, offsets -100/+100 (CY, +-0.01)");

        var rows = o.Result.Segments.OrderBy(r => r.StartStation).ToList();
        bool areas = rows.All(r => Math.Abs(r.Area2D - 100_000) < 1e-6 && Math.Abs(r.Coverage - 1) < 1e-9);
        Add("T1.area", areas, string.Join("/", rows.Select(r => F(r.Area2D))), "100000 each, coverage 1", "2D area per 500 ft segment, 200 ft wide");

        var first = rows[0].SegmentId;
        var v = o.Views.TryGetValue(first, out var vr) ? vr : null;
        bool view = v?.Spec is { } spec && Math.Abs(spec.TwistRadians) < 1e-12 && Math.Abs(spec.ModelCenter.X - 250) < 1e-9 && Math.Abs(spec.ModelCenter.Y) < 1e-9
                    && spec.ModelUnitsPerPaperUnit == 50 && Math.Abs(v.RequiredPaperWidth - 10) < 1e-9 && Math.Abs(v.RequiredPaperHeight - 4) < 1e-9;
        Add("T15", view, v is null ? "no view" : $"twist {F(v.TwistRadians)} center ({F(v.ModelCenter.X)}, {F(v.ModelCenter.Y)}) scale {F(v.ModelUnitsPerPaperUnit)} needs {F(v.RequiredPaperWidth)} x {F(v.RequiredPaperHeight)}",
            "twist 0, center (250, 0), scale 50, needs 10 x 4, fits", "S, 0-500, offsets +-100, 1:50, template viewport 12 x 5, through C3 with the pipeline's display polygon");

        var counts = RoleCounts(o);
        int C(OwnedRole r) => counts.GetValueOrDefault(r);
        // Fakes plan one title-attribute object per layout; FX's template has no title block, so the
        // installed paper writer fills none (E615 notes) and owns one clip per viewport instead.
        bool objects = _fakes
            ? C(OwnedRole.Boundary) == 4 && C(OwnedRole.Layout) == 4 && C(OwnedRole.Viewport) == 4 && C(OwnedRole.PaperTable) == 4
              && C(OwnedRole.TitleAttributes) == 4 && C(OwnedRole.MatchMark) == 12 && C(OwnedRole.MasterTable) == 1 && o.Applied?.Created == 33
            : C(OwnedRole.Boundary) == 4 && C(OwnedRole.Layout) == 4 && C(OwnedRole.Viewport) == 4 && C(OwnedRole.Clip) == 4 && C(OwnedRole.PaperTable) == 4
              && C(OwnedRole.MatchMark) == 12 && C(OwnedRole.MasterTable) == 1 && o.Result.Problems.Any(p => p.Code == PaperCodes.TitleTagMissing);
        Add("T1.objects", objects, string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")) + $"; created {o.Applied?.Created}, writes {o.Applied?.Writes}",
            _fakes ? "Boundary 4, MatchMark 12 (line + text per mark), MasterTable 1, Layout 4, Viewport 4, PaperTable 4, TitleAttributes 4; created 33"
                   : "Boundary 4, MatchMark 12 (line + text per mark), MasterTable 1, Layout 4, Viewport 4, Clip 4, PaperTable 4; SV-E615 (FX template has no title block)",
            "owned objects recorded with handles (6.9)");

        Add("T1.partition", o.Result.PartitionCheckPassed && o.Result.OutsideLimits.Count == 0,
            $"partition {o.Result.PartitionCheckPassed}, outside-limits rows {o.Result.OutsideLimits.Count}", "passed, 0 rows", "every pyramid lies inside the band (6.5)");

        string dwg = RegenEngine.DrawingPath(_doc) is { } p ? Path.GetFileNameWithoutExtension(p) : "";
        bool e809 = o.Result.CsvPath is { } csv && File.Exists(csv) && Path.GetFileName(csv).StartsWith(dwg, StringComparison.OrdinalIgnoreCase)
                    && o.Result.JsonPath is { } json && File.Exists(json);
        Add("E809", e809, $"{o.Result.CsvPath}; {o.Result.JsonPath}", $"files named after drawing '{dwg}' beside it", "exports follow the drawing's current name");
        return o;
    }

    private void CompareRows(string id, RunOutcome o, (double From, double To, double CutFt3, double FillFt3)[] exp, string what)
    {
        var rows = o.Result.Segments.OrderBy(r => r.StartStation).ToList();
        bool ok = rows.Count == exp.Length && rows.Zip(exp).All(p =>
            Math.Abs(p.First.Cut - Cy(p.Second.CutFt3)) <= _tolCy && Math.Abs(p.First.Fill - Cy(p.Second.FillFt3)) <= _tolCy
            && Math.Abs(p.First.StartStation - p.Second.From) < 1e-9 && Math.Abs(p.First.EndStation - p.Second.To) < 1e-9);
        Add(id, ok, string.Join("; ", rows.Select(r => $"{F(r.StartStation)}-{F(r.EndStation)} C {F(r.Cut)} F {F(r.Fill)}")),
            string.Join("; ", exp.Select(e => $"{F(e.From)}-{F(e.To)} C {F(Cy(e.CutFt3))} F {F(Cy(e.FillFt3))}")), what);
    }

    private void T19(RunOutcome first)
    {
        var o = Regen(Def("T1", "START..END@500"));
        bool sameHandles = o.Record is not null && first.Record is not null
            && o.Record.Owned.Select(x => (x.Key, x.Handle)).ToHashSet().SetEquals(first.Record.Owned.Select(x => (x.Key, x.Handle)));
        bool e811 = o.Result.Problems.Any(p => p.Code == PipelineCodes.NothingChanged);
        bool pass = o.Result.Succeeded && o.Applied?.Writes == 0 && o.OnlyTimestamp == true && sameHandles && e811;
        Add("T19", pass, $"writer writes {o.Applied?.Writes}, only timestamp {o.OnlyTimestamp}, same handles {sameHandles}, E811 {e811}",
            "writer writes 0, only timestamp, same handles, SV-E811",
            "regen twice: the second changes no handles and no geometry (E811). Counted: the model and paper writers' writes only; the owned volume surface's Rebuild() on every regen (P18) is a database write the count excludes by design");
    }

    private void T12()
    {
        if (_world is null) { NotRun("T12", "P1 apex moved", "SelfTestWorlds.Installed is not set for these bindings"); return; }
        var before = WithTransaction(tr => _writers.Store(_doc.Database, tr, _settings).Load("T1").Record);
        _world.SetPyramidApex(FixtureSpec.Names.FgA, FixtureSpec.Pyramids[0].CenterX, 98.0);
        try
        {
            var o = Regen(Def("T1", "START..END@500"));
            var seg1 = o.Result.Segments.OrderBy(r => r.StartStation).FirstOrDefault();
            var tableKeys = (before?.Owned ?? []).Where(x => x.Key.Role is OwnedRole.PaperTable or OwnedRole.ModelTable or OwnedRole.MasterTable).ToList();
            bool sameTables = tableKeys.Count > 0 && tableKeys.All(k => o.Record?.Owned.Any(n => n.Key == k.Key && n.Handle == k.Handle) == true);
            bool pass = o.Result.Succeeded && seg1 is not null && Math.Abs(seg1.Cut - 200) <= _tolCy && sameTables && o.Applied?.Created == 0 && o.Applied?.Updated >= 2;
            Add("T12", pass, $"0-500 cut {F(seg1?.Cut ?? double.NaN)} CY, {tableKeys.Count} table handles kept {sameTables}, created {o.Applied?.Created}, updated {o.Applied?.Updated}",
                "cut 200 CY, same table handles, nothing created", "P1 apex 98.0, rebuild, regen");
        }
        finally
        {
            _world.SetPyramidApex(FixtureSpec.Names.FgA, FixtureSpec.Pyramids[0].CenterX, FixtureSpec.Pyramids[0].ApexZ);
        }
    }

    private void T13()
    {
        var engine = Engine();
        var before = WithTransaction(tr => _writers.Store(_doc.Database, tr, _settings).Load("T1").Record)!;
        var known = before.Run.KnownSegments.OrderBy(k => k.StartStation).ToList();
        double tol = _settings.Planning.StationToleranceFeet;
        var edit = DrawingRead.Run(_doc, engine, (src, _) => BreakEditor.Add(before.Run, known, src.Alignment(AlignmentS, null).Value.Geometry, 250, tol));
        if (edit.Error is not null) { Add("T13", false, edit.Error, "break added", "BreakEditor refused"); return; }
        SaveDefinition(edit.Run);
        var o = Regen(edit.Run);
        if (!o.Result.Succeeded) { Add("T13", false, Codes(o), "succeeded", FirstError(o)); return; }

        var segs = o.Run.KnownSegments.OrderBy(k => k.StartStation).ToList();
        var oldFirst = known[0];
        bool leftKeeps = segs[0].Id == oldFirst.Id && Math.Abs(segs[0].EndStation - 250) < 1e-9
            && HandleOf(o.Record, oldFirst.Id, OwnedRole.Layout) == HandleOf(before, oldFirst.Id, OwnedRole.Layout)
            && HandleOf(o.Record, oldFirst.Id, OwnedRole.PaperTable) == HandleOf(before, oldFirst.Id, OwnedRole.PaperTable);
        bool rightNew = segs.Count == 5 && known.All(k => k.Id != segs[1].Id) && HandleOf(o.Record, segs[1].Id, OwnedRole.Layout) is not null;

        var names = Layouts().ToDictionary(l => l.Handle, l => l.Name, StringComparer.OrdinalIgnoreCase);
        var finalNames = segs.Select(x => HandleOf(o.Record, x.Id, OwnedRole.Layout) is { } h && names.TryGetValue(h, out var n) ? n : "?").ToList();
        var expectedNames = Enumerable.Range(1, segs.Count).Select(i => $"T1 - {i}").ToList();
        string? oldSecond = HandleOf(before, known[1].Id, OwnedRole.Layout);
        bool renumbered = finalNames.SequenceEqual(expectedNames) && oldSecond is not null && names.GetValueOrDefault(oldSecond) == "T1 - 3" && o.Applied?.Renamed > 0;
        Add("T13", leftKeeps && rightNew && renumbered,
            $"left keeps GUID/layout/table {leftKeeps}; new segment and layout {rightNew}; layouts {string.Join(" | ", finalNames)}; renames {o.Applied?.Renamed}",
            $"left keeps all; new created; layouts {string.Join(" | ", expectedNames)}", "insert break at 250 with SVBREAK's editor, regen (E307 two-pass rename)");
    }

    private RunOutcome T2()
    {
        var o = Regen(Def("T2", "0, 500, 1300, 1800, 2000"));
        if (!o.Result.Succeeded) { Add("T2", false, Codes(o), "succeeded", FirstError(o)); return o; }
        CompareRows("T2.volumes", o, FixtureSpec.Expected.T2, "A, breaks 0, 500, 1300, 1800, 2000: mixed lengths 500, 800, 500, 200");
        var rows = o.Result.Segments.OrderBy(r => r.StartStation).ToList();

        var areas = rows.Select(r => r.Area2D).ToList();
        bool t23 = areas.Count == 4 && areas.Zip(FixtureSpec.Expected.T23AreaFt2).All(p => Math.Abs(p.First - p.Second) < 1e-6) && rows.All(r => Math.Abs(r.Coverage - 1) < 1e-9);
        Add("T23", t23, string.Join("/", areas.Select(F)) + $", coverage {string.Join("/", rows.Select(r => F(r.Coverage)))}",
            string.Join("/", FixtureSpec.Expected.T23AreaFt2.Select(F)) + ", coverage 1",
            "2D area per segment (raw SF through the pipeline; the AC display rows and the 9.19 total rule are C2's, T23 unit-tested)");

        var scales = rows.Select(r => o.Views.TryGetValue(r.SegmentId, out var v) ? v.ModelUnitsPerPaperUnit : double.NaN).ToList();
        Add("T16.fitStandard", scales.Count == 4 && scales[0] == 50 && scales[1] == 100, string.Join("/", scales.Select(F)), "50/100/..", "FitStandard {20..100}: 500 ft fits at 50, 800 ft needs 100 (T16)");
        return o;
    }

    private void T3()
    {
        var o = Regen(Def("T3", "0, 125, 500"));
        if (!o.Result.Succeeded) { Add("T3", false, Codes(o), "succeeded", FirstError(o)); return; }
        CompareRows("T3", o, FixtureSpec.Expected.T3, "A, breaks 0, 125, 500: the break passes through P1's apex");
    }

    private void T4()
    {
        var o = Regen(Def("T4", "0, 500", left: -100, right: 0));
        if (!o.Result.Succeeded) { Add("T4", false, Codes(o), "succeeded", FirstError(o)); return; }
        var r = o.Result.Segments.Single();
        bool ok = Math.Abs(r.Cut - Cy(FixtureSpec.Expected.T4CutFt3)) <= _tolCy && Math.Abs(r.Fill - Cy(FixtureSpec.Expected.T4FillFt3)) <= _tolCy;
        Add("T4", ok, $"C {F(r.Cut)} F {F(r.Fill)}", $"C {F(Cy(FixtureSpec.Expected.T4CutFt3))} F {F(Cy(FixtureSpec.Expected.T4FillFt3))}", "A, 0-500, offsets (-100, 0): lateral edge through both apexes (E417)");
    }

    private void T16Fixed()
    {
        var before = Snapshot();
        var o = Regen(Def("T16", "0, 800", scaleMode: ScaleMode.Fixed));
        bool refused = !o.Result.Succeeded && o.FailedAtStep == 4 && o.Result.Problems.Any(p => p.Code == "SV-E309") && Snapshot() == before;
        Add("T16.fixed", refused, $"succeeded {o.Result.Succeeded}, failed at step {o.FailedAtStep}, codes {Codes(o)}", "fails at step 4 with SV-E309, nothing written",
            "S, 0-800 at fixed 1:50 does not fit the 12 x 5 viewport (needs 16 x 4)");
    }

    // Rev 3 (NOW.md X5, rule 6): base = Fixture H's flat OHWM surface at the stated elevation, comparison
    // = FG_H, so impact cut is d = OHWM - FG_H > 0. Expected values and their derivations are in
    // FixtureSpec.Expected; the one-line form sits beside each check below. Every impacted segment must
    // also carry its outline (ImpactResult.Outlines, D13): rings whose signed areas sum to the impacted
    // area within 1e-9 relative.
    private void Hcfcd(string id, string breaks, string ohwm, double left, double right)
    {
        var o = Regen(Def(id.Replace(".", "", StringComparison.Ordinal), breaks, preset: ReportPreset.HcfcdSegment, analyses: Sediment(ohwm),
            area: AreaUnit.Acres, left: left, right: right));
        if (!o.Result.Succeeded) { Add(id, false, Codes(o), "succeeded", FirstError(o)); return; }
        var rows = o.Result.Segments.OrderBy(r => r.StartStation).ToList();
        string measured = string.Join("; ", rows.Select(r => r.Impact is { } i
            ? $"L {F(i.ImpactedLength)} A {F(i.ImpactedArea)} ft2 V {F(Cy(i.CutVolume))} CY avg {(i.ImpactedLength > 0 ? F(Cy(i.CutVolume) / i.ImpactedLength) : "N/A")} outline {i.Outlines.Count} ring(s) {F(i.Outlines.Sum(ShoelaceArea))} ft2"
            : "no impact"));
        string what = $"H, {ohwm} vs {FixtureSpec.Names.FgH}, breaks {breaks}, offsets ({F(left)}, {F(right)}), through the pipeline and C5 (HCFCD preset)";
        static bool Outlined(ImpactResult i) =>
            i.Outlines.Count > 0 && Math.Abs(i.Outlines.Sum(ShoelaceArea) - i.ImpactedArea) <= 1e-9 * i.ImpactedArea;
        bool Each(int count, double length, double areaFt2, double volumeFt3) =>
            rows.Count == count && rows.All(r => r.Impact is { } i && Math.Abs(i.ImpactedLength - length) <= 0.01
                && Math.Abs(i.ImpactedArea - areaFt2) <= 0.01 && Math.Abs(Cy(i.CutVolume) - Cy(volumeFt3)) <= _tolCy && Outlined(i));
        string Want3(double areaFt2, double volumeFt3) =>
            $"each segment L 500, A {F(areaFt2)} ft2 ({F(areaFt2 / 43_560)} ac), V {F(Cy(volumeFt3))} CY, avg {F(Cy(volumeFt3) / 500)}; outline rings summing to A";
        switch (id)
        {
            case "T25":   // d = 100 - FG_H > 0 for |y| < 22: A 44 x 500 = 22,000; section 120 + 2 x (24 + 6) = 180 -> V 90,000 ft3
                Add(id, Each(2, 500, Want.T25OhwmAreaFt2, Want.T25OhwmVolumeFt3), measured, Want3(Want.T25OhwmAreaFt2, Want.T25OhwmVolumeFt3), what);
                break;
            case "T26":   // d = 97 - FG_H > 0 for |y| < 14.5: A 29 x 500 = 14,500; section 60 + 2 x 6.75 = 73.5 -> V 36,750 ft3
                Add(id, Each(2, 500, Want.T26OhwmAreaFt2, Want.T26OhwmVolumeFt3), measured, Want3(Want.T26OhwmAreaFt2, Want.T26OhwmVolumeFt3), what);
                break;
            case "T27":   // d = 95 - FG_H > 0 for |y| < 11.5 (1 ft above the 94 sediment bed): A 23 x 500 = 11,500; section 20 + 2 x 0.75 = 21.5 -> V 10,750 ft3
                Add(id, Each(2, 500, Want.T27OhwmAreaFt2, Want.T27OhwmVolumeFt3), measured, Want3(Want.T27OhwmAreaFt2, Want.T27OhwmVolumeFt3), what);
                break;
            case "T27.E1001":   // past the transition FG_H = EG_H >= 96 > 95: d <= -1, nothing impacted
            {
                var i = rows.SingleOrDefault()?.Impact;
                bool note = o.Result.Problems.Any(p => p.Code == IntegratorCodes.NoCut);
                bool ok = i is not null && i.ImpactedLength == 0 && i.ImpactedArea == 0 && i.CutVolume == 0 && i.Intervals.Count == 0 && i.Outlines.Count == 0
                          && ImpactQuantities.Average(i) is null && note;
                Add(id, ok, measured + $"; {IntegratorCodes.NoCut} {note}", $"L 0, A 0, V 0, average N/A, no outline; {IntegratorCodes.NoCut} note", what + ": OHWM_95 is below the design bed (96) past the transition");
                break;
            }
            case "T28":   // cut runs past the transition (FG_H = EG_H < 100 for |y| < 22): L 500, A 22,000; V 54,000 + transition 1,520..1,560 + 24,320
            {
                var i = rows.SingleOrDefault()?.Impact;
                bool ok = i is not null && Math.Abs(i.ImpactedLength - Want.T28Length) <= 0.01 && Math.Abs(i.ImpactedArea - Want.T28AreaFt2) <= 0.01
                          && Cy(i.CutVolume) >= Cy(Want.T28VolumeMinFt3) - _tolCy && Cy(i.CutVolume) <= Cy(Want.T28VolumeMaxFt3) + _tolCy && Outlined(i);
                Add(id, ok, measured,
                    $"L {F(Want.T28Length)} +-0.01; A {F(Want.T28AreaFt2)} ft2; V between {F(Cy(Want.T28VolumeMinFt3))} and {F(Cy(Want.T28VolumeMaxFt3))} CY (the transition diagonal); outline rings summing to A",
                    what + ": as OHWM minus design the cut runs past the 1300-1310 transition");
                break;
            }
            default:   // T30: the cut (|y| < 22) reaches the +-12 edges: SV-E1002; A 24 x 500 = 12,000; section 120 + 2 x (12 - 4/3) -> V 70,666.667 ft3
            {
                bool flagged = o.Result.Problems.Any(p => p.Code == IntegratorCodes.SearchLimit);
                Add(id, flagged && Each(2, 500, Want.T30AreaFt2, Want.T30VolumeFt3), measured + $"; codes {Codes(o)}",
                    $"{IntegratorCodes.SearchLimit} raised (E1002); " + Want3(Want.T30AreaFt2, Want.T30VolumeFt3),
                    what + ": the cut (4.67 ft deep at |y| = 12) reaches the lateral limit");
                break;
            }
        }
    }

    private void E802(RunOutcome t2)
    {
        var seg = t2.Result.Segments.OrderBy(r => r.StartStation).First().SegmentId;
        string? handle = HandleOf(t2.Record, seg, OwnedRole.Boundary);
        if (handle is null) { Add("E802", false, "no boundary handle", "a stored boundary", "T2 has no boundary to erase"); return; }
        string layer = _settings.ModelWriters.BoundaryLayer;
        WithTransaction(tr =>
        {
            EraseByHandle(tr, handle);   // the user erases the boundary
            SetLayerLocked(tr, layer, true);   // and locks its layer (6.10: unlocked and relocked)
            return true;
        });
        var o = Regen(Def("T2", "0, 500, 1300, 1800, 2000"));
        string? repaired = HandleOf(o.Record, seg, OwnedRole.Boundary);
        bool pass = o.Result.Succeeded && o.Applied?.Created == 1 && repaired is not null && repaired != handle
                    && o.Result.Problems.Any(p => p.Code == PipelineCodes.Recreated && p.SegmentId == seg);
        Add("E802", pass, $"created {o.Applied?.Created}, handle {handle} -> {repaired}", "1 created, new handle recorded, SV-E802 note", "stored handle erased: object recreated, mapping repaired");

        bool relocked = WithTransaction(tr => IsLayerLocked(tr, layer));
        bool noted = o.Result.Problems.Any(p => p.Code == PipelineCodes.LayerRelocked);
        Add("E504", relocked && noted, $"layer {layer} locked after regen {relocked}, SV-E504 note {noted}", "locked again, note printed", "owned layer locked during a regen that writes: unlocked, written, relocked");
        WithTransaction(tr => { SetLayerLocked(tr, layer, false); return true; });
    }

    private void EraseByHandle(Transaction tr, string handle)
    {
        long h = long.Parse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (!_doc.Database.TryGetObjectId(new Handle(h), out var id) || id.IsErased) throw new InvalidOperationException($"Object {handle} does not exist.");
        tr.GetObject(id, OpenMode.ForWrite).Erase();
    }

    private void SetLayerLocked(Transaction tr, string name, bool locked)
    {
        var db = _doc.Database;
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        LayerTableRecord rec;
        if (table.Has(name)) rec = (LayerTableRecord)tr.GetObject(table[name], OpenMode.ForWrite);
        else
        {
            table.UpgradeOpen();
            rec = new LayerTableRecord { Name = name };
            table.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }
        rec.IsLocked = locked;
    }

    private bool IsLayerLocked(Transaction tr, string name)
    {
        var table = (LayerTable)tr.GetObject(_doc.Database.LayerTableId, OpenMode.ForRead);
        return table.Has(name) && ((LayerTableRecord)tr.GetObject(table[name], OpenMode.ForRead)).IsLocked;
    }

    /// <summary>Geometry is refused after its transaction, by the fakes and the installed adapters alike (A1's rule, A4's guard).</summary>
    private void Lifetime()
    {
        var leaked = DrawingRead.Run(_doc, Engine(), (src, _) => src.Alignment(AlignmentS, null).Value.Geometry);
        string inside = DrawingRead.Run(_doc, Engine(), (src, _) => src.Alignment(AlignmentS, null).Value.Geometry.FormatStation(500));
        string outside;
        try { outside = "no exception: " + leaked.FormatStation(500); }
        catch (InvalidOperationException) { outside = "InvalidOperationException"; }
        Add("LIFETIME", inside.Length > 0 && outside == "InvalidOperationException", $"inside '{inside}', after the transaction: {outside}",
            "works inside, throws after", "alignment geometry is valid only inside its transaction (A1 contract, TransactionLifetime guard)");
    }

    private void Config()
    {
        var json = SheetVolSettings.Default.ToJson();
        var back = SheetVolSettings.FromJson(json, "round trip");
        var typo = SheetVolSettings.FromJson("{ \"regen\": { \"rebuildSourcez\": true } }", "typo");
        var bad = SheetVolSettings.FromJson("{ \"planning\": { \"mitreLimitDeg\": 200 }, \"report\": { \"formats\": { \"volumeDecimals\": -1 } } }", "bad values");
        var keys = SheetVolSettings.FromJson("{ \"paper\": { \"ownerTagKey\": \"OTHER\" } }", "owner keys");
        bool pass = back.Succeeded && back.Value.ToJson() == json && !typo.Succeeded && !bad.Succeeded && !keys.Succeeded
                    && bad.Problems.Any(p => p.Code == PipelineCodes.SettingsInvalid) && bad.Problems.Any(p => p.Code == OutputCodes.ConfigInvalid);
        Add("CONFIG", pass,
            $"defaults round-trip {back.Succeeded && back.Value.ToJson() == json}; unknown key refused {!typo.Succeeded}; bad values refused {!bad.Succeeded} with {string.Join(",", bad.Problems.Select(p => p.Code).Distinct())}; different owner tag keys refused {!keys.Succeeded}",
            "round-trips (adapters, modelWriters and paper sections embedded); unknown key, out-of-range values and two owner tag keys refused", "settings file: one JSON, validated on load (A7)");
    }

    private void E404()
    {
        var stale = new OutOfDateSurfaceFactory(_adapters, FixtureSpec.Names.FgA);
        var off = _settings with { Adapters = _settings.Adapters with { RebuildSources = false } };
        var before = Snapshot();
        var o = Regen(Def("T2", "0, 500, 1300, 1800, 2000"), Engine(stale, null, off));
        bool unchanged = Snapshot() == before;
        bool refused = !o.Result.Succeeded && o.FailedAtStep == 1 && o.Result.Problems.Any(p => p.Code == PipelineCodes.SourceOutOfDate && p.Message.Contains(FixtureSpec.Names.FgA, StringComparison.Ordinal)) && unchanged;
        var on = Regen(Def("T2", "0, 500, 1300, 1800, 2000"), Engine(stale));
        bool rebuilt = on.Result.Succeeded && on.Result.Problems.Any(p => p.Code == PipelineCodes.SourceOutOfDate && p.Severity == Severity.Info);
        Add("E404", refused && rebuilt, $"rebuildSources off: failed at step {o.FailedAtStep}, codes {Codes(o)}, drawing unchanged {unchanged}; on: succeeded {on.Result.Succeeded} with rebuild note {rebuilt}",
            "off: fail at step 1 naming FG_A, nothing written; on: rebuilt and computed",
            "source surface reported out of date by a decorator over the bindings (the installed adapters' own path is A1's SVADAPTERS_E404)");
    }

    private void E1012()
    {
        var before = Snapshot();
        var o = Regen(Def("T2", "0, 500, 1300, 1800, 2000"), Engine(new BiasedVolumesFactory(_adapters, 1.0)));
        var p = o.Result.Problems.FirstOrDefault(x => x.Code == PipelineCodes.IntegratorMismatch);
        bool pass = !o.Result.Succeeded && o.FailedAtStep == 3 && p is not null && p.Message.Contains("integrator gives cut", StringComparison.Ordinal)
                    && p.Message.Contains("bounded volume gives cut", StringComparison.Ordinal) && Snapshot() == before;
        Add("E1012", pass, p?.Message ?? Codes(o), "run fails at step 3, both numbers printed, nothing written",
            $"bounded volume biased by +1 ft3 against the integrator; fixture tolerance max({E(_settings.Integrator.CrossCheckAbsoluteCubicFeet)} ft3, {E(_settings.Integrator.CrossCheckRelative)} x total)");
    }

    private void E506E507(RunOutcome t2)
    {
        var run = t2.Run;
        var engine = Engine();
        ExportFilesResult Export(string? drawing) => DrawingRead.Run(_doc, engine, (src, _) =>
            ExportFiles.Write(run, t2.Reconcile!, drawing, src.Alignment(AlignmentS, null).Value.Geometry.FormatStation, _settings, DateTimeOffset.UtcNow));
        var never = Export(null);
        bool e506 = never.CsvPath is null && never.JsonPath is null && never.Problems.Any(p => p.Code == PipelineCodes.DrawingNeverSaved);
        Add("E506", e506, $"csv {never.CsvPath ?? "none"}, json {never.JsonPath ?? "none"}, codes {string.Join(",", never.Problems.Select(p => p.Code))}", "both skipped with SV-E506", "drawing never saved");

        string? dwg = RegenEngine.DrawingPath(_doc);
        if (dwg is null || t2.Result.CsvPath is null) { Add("E507", false, "drawing not saved", "a saved drawing", "not run"); return; }
        using (new FileStream(t2.Result.CsvPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = Export(dwg);
            bool e507 = locked.CsvPath is null && locked.JsonPath is not null && locked.Problems.Any(p => p.Code == PipelineCodes.ExportWriteFailed);
            Add("E507", e507, $"csv {locked.CsvPath ?? "not written"}, json {locked.JsonPath ?? "not written"}, codes {string.Join(",", locked.Problems.Select(p => p.Code))}",
                "CSV skipped with SV-E507 warning, JSON written", "export file locked by another program");
        }
    }

    private void T14()
    {
        if (_world is null) { NotRun("T14", "alignment lengthened", "SelfTestWorlds.Installed is not set for these bindings"); return; }
        var engine = Engine();
        var first = Regen(Def("T14", "START, 500, 1300, 1800, END"));
        if (!first.Result.Succeeded) { Add("T14", false, Codes(first), "succeeded", FirstError(first)); return; }
        var lastBefore = first.Run.KnownSegments.OrderBy(k => k.StartStation).Last();
        _world.LengthenAlignment(AlignmentS, 2500);
        var stale = DrawingRead.Run(_doc, engine, (src, store) => RunStatusReader.Read(src, store, "T14", _settings));
        var o = Regen(Def("T14", "START, 500, 1300, 1800, END"));
        var lastAfter = o.Run.KnownSegments.OrderBy(k => k.StartStation).Last();
        var clean = DrawingRead.Run(_doc, engine, (src, store) => RunStatusReader.Read(src, store, "T14", _settings));
        bool pass = stale.IsStale && stale.ChangedComponents.Contains(Fingerprints.AlignmentComponent) && o.Result.Succeeded
                    && lastAfter.Id == lastBefore.Id && Math.Abs(lastAfter.EndStation - 2500) < 1e-9 && !clean.IsStale
                    && o.Result.Problems.Any(p => p.Code == PipelineCodes.StationingChanged);
        Add("T14", pass, $"before regen stale {stale.IsStale} ({string.Join(",", stale.ChangedComponents)}); END segment {F(lastAfter.StartStation)}-{F(lastAfter.EndStation)} same GUID {lastAfter.Id == lastBefore.Id}; after regen stale {clean.IsStale}; E212 warning {o.Result.Problems.Any(p => p.Code == PipelineCodes.StationingChanged)}",
            "stale (alignment) before, END segment 1800-2500 same GUID, clean after, E212 warning", "lengthen alignment S to 2500");
    }

    private void D10()
    {
        var o = Regen(Def("D10", "1000, 1500, 2100, 2400", preset: ReportPreset.HcfcdSegment, analyses: Sediment(),
            area: AreaUnit.Acres));   // rev 3: no OHWM elevation (X5)
        var rows = o.Result.Segments.OrderBy(r => r.StartStation).ToList();
        var last = rows.LastOrDefault();
        bool pass = o.Result.Succeeded && rows.Count == 3 && rows[0].Impact is not null && last?.Impact is null
                    && o.Result.Problems.Any(p => p.Code == PipelineCodes.IntegratorSkipped && p.SegmentId == last?.SegmentId);
        Add("D10", pass, $"succeeded {o.Result.Succeeded}, impacts {string.Join("/", rows.Select(r => r.Impact is null ? "none" : F(r.Impact.ImpactedLength)))}, codes {Codes(o)}",
            "run continues; segment 2100-2400 (no triangles) has a SV-E827 warning and no impact", "E403 from the integrator is per segment (D10)");
    }

    private void T20()
    {
        var before = Snapshot();
        var o = Regen(Def("T20", "START..END@500"), Engine(null, new FailOnWriteFactory(_writers, OwnedRole.Boundary, 3)));
        string after = Snapshot();
        bool absent = WithTransaction(tr => !_writers.Store(_doc.Database, tr, _settings).Load("T20").Exists);
        bool pass = !o.Result.Succeeded && o.FailedAtStep == 5 && o.Result.Problems.Any(p => p.Code == PipelineCodes.RunFailed) && after == before && absent;
        Add("T20", pass, $"succeeded {o.Result.Succeeded}, failed at step {o.FailedAtStep}, owned-object snapshot unchanged {after == before}, no T20 record {absent}",
            "fails at step 5 (apply), drawing equivalent in owned objects, no record",
            "the model writer writes every boundary, mark and the master table, then segment 3's boundary is reported failed: the abort rolls all of it back");
    }

    private void E801()
    {
        var engine = Engine(null, new FailOnWriteFactory(_writers, OwnedRole.Boundary, 3));
        var outcomes = SvCommands.RegenerateAll(_doc, engine, ["T1", "T20"], watchEscape: false);
        var t1 = outcomes.FirstOrDefault(x => x.Result.RunName == "T1");
        var t20 = outcomes.FirstOrDefault(x => x.Result.RunName == "T20");
        bool pass = outcomes.Count == 2 && t1?.Result.Succeeded == true && t20 is not null && !t20.Result.Succeeded
                    && WithTransaction(tr => !_writers.Store(_doc.Database, tr, _settings).Load("T20").Exists);
        Add("E801", pass, string.Join("; ", outcomes.Select(x => $"{x.Result.RunName} {(x.Result.Succeeded ? "ok" : "failed")}")),
            "T1 ok; T20 failed and left unchanged", "one run fails mid-regen; the other still processes; summary lists each");
    }

    private void E213()
    {
        var before = Snapshot();
        var o = Regen(Def("E213", "START..END@500", alignment: "NO-SUCH-ALIGNMENT"));
        bool pass = !o.Result.Succeeded && o.FailedAtStep == 1
                    && o.Result.Problems.Any(p => p.Code == PipelineCodes.AlignmentMissing && p.Message.Contains("NO-SUCH-ALIGNMENT", StringComparison.Ordinal))
                    && Snapshot() == before;
        Add("E213", pass, $"failed at step {o.FailedAtStep}, codes {Codes(o)}", "fails at step 1 naming the alignment; outputs untouched", "alignment erased or reference broken");
    }

    // ---- installed only ---------------------------------------------------------------------------

    private void T5()
    {
        var o = Regen(Def("T5", "START..END@500", analyses: Pair("EG vs FG_B", FixtureSpec.Names.EG, FixtureSpec.Names.FgB)));
        if (!o.Result.Succeeded) { Add("T5", false, Codes(o), "succeeded", FirstError(o)); return; }
        double want = Cy(FixtureSpec.Expected.T5FillFt3PerSegment);
        var rows = o.Result.Segments.OrderBy(r => r.StartStation).ToList();
        bool ok = rows.Count >= 4 && rows.Where(r => r.EndStation <= 2000 + 1e-9).All(r => Math.Abs(r.Fill - want) <= _tolCy && r.Cut <= _tolCy);
        Add("T5", ok, string.Join("; ", rows.Select(r => $"{F(r.StartStation)}-{F(r.EndStation)} C {F(r.Cut)} F {F(r.Fill)}")), $"each 500 ft segment inside 0-2000: F {F(want)}, C 0",
            "B (ridge), START..END@500, offsets +-100 (S runs to 2500 after T14: segments past the ridge end at 2100 hold less)");
    }

    private void T6()
    {
        var o = Regen(Def("T6", "0, 500", analyses: Pair("EG vs FG_B", FixtureSpec.Names.EG, FixtureSpec.Names.FgB), left: -30, right: 15));
        if (!o.Result.Succeeded) { Add("T6", false, Codes(o), "succeeded", FirstError(o)); return; }
        var r = o.Result.Segments.Single();
        double want = Cy(FixtureSpec.Expected.T6FillFt3);
        Add("T6", Math.Abs(r.Fill - want) <= _tolCy && r.Cut <= _tolCy, $"C {F(r.Cut)} F {F(r.Fill)}", $"F {F(want)}, C 0", "B, 0-500, offsets (-30, +15)");
    }

    private void T7T8T9T24()
    {
        var d = Pair("EG vs FG_D", FixtureSpec.Names.EG, FixtureSpec.Names.FgD);
        string arc = string.Create(CultureInfo.InvariantCulture, $"{FixtureSpec.C.ArcStartStation:R}, {FixtureSpec.C.ArcEndStation:R}");
        var t7 = Regen(Def("T7", arc, alignment: FixtureSpec.Names.AlignmentC, analyses: d));
        if (!t7.Result.Succeeded) Add("T7", false, Codes(t7), "succeeded", FirstError(t7));
        else
        {
            var r = t7.Result.Segments.Single();
            double want = Cy(FixtureSpec.Expected.T7FillFt3);
            Add("T7", Math.Abs(r.Fill - want) <= 2e-4 * want, $"F {F(r.Fill)} CY (rel {E((r.Fill - want) / want)})", $"F {F(want)} CY, relative 2e-4", "D on C, the arc [300, 614.159], offsets +-100");
            double area = FixtureSpec.Expected.T24AreaFt2;
            Add("T24.area", Math.Abs(r.Area2D - area) <= 2e-4 * area && Math.Abs(r.Coverage - 1) < 1e-6,
                $"2D area {F(r.Area2D)} SF (rel {E((r.Area2D - area) / area)}), coverage {F(r.Coverage)}", $"{F(area)} SF, relative 2e-4, coverage 1", "D on C, same segment (6.5 datum method)");
        }

        var t8 = Regen(Def("T8", arc, alignment: FixtureSpec.Names.AlignmentC, analyses: d, left: -50, right: 150));
        if (!t8.Result.Succeeded) Add("T8", false, Codes(t8), "succeeded", FirstError(t8));
        else
        {
            var r = t8.Result.Segments.Single();
            double want = Cy(FixtureSpec.Expected.T8FillFt3);
            Add("T8", Math.Abs(r.Fill - want) <= 2e-4 * want, $"F {F(r.Fill)} CY (rel {E((r.Fill - want) / want)})", $"F {F(want)} CY, relative 2e-4", "D on C, same segment, offsets (-50, +150)");
        }

        var before = Snapshot();
        var t9 = Regen(Def("T9", arc, alignment: FixtureSpec.Names.AlignmentC, analyses: d, left: -50, right: 300));
        bool e201 = !t9.Result.Succeeded && t9.FailedAtStep == 2 && t9.Result.Problems.Any(p => p.Code == "SV-E201") && t9.Result.Segments.Count == 0 && Snapshot() == before;
        Add("T9", e201, $"succeeded {t9.Result.Succeeded}, failed at step {t9.FailedAtStep}, codes {Codes(t9)}, rows {t9.Result.Segments.Count}", "SV-E201, no number, nothing written",
            "D on C, offsets (-50, +300): the inner offset reaches the radius (E201)");

        var off = Regen(Def("T24", arc, alignment: FixtureSpec.Names.AlignmentC, analyses: d, left: -500, right: 100));
        if (!off.Result.Succeeded) { Add("T24.offEdge", false, Codes(off), "succeeded", FirstError(off)); return; }
        var ro = off.Result.Segments.Single();
        double ringArea = off.Plan is { } pl ? Math.Abs(ShoelaceArea(pl.Boundaries.Single().Tessellated)) : double.NaN;
        Add("T24.offEdge", ro.Area2D < ringArea - 1 && ro.Coverage < 1 - 1e-6,
            $"2D area {F(ro.Area2D)} SF vs boundary {F(ringArea)} SF, coverage {F(ro.Coverage)}, codes {Codes(off)}", "2D area below the boundary's own area, coverage below 100% (E419)",
            "D on C, same segment, offsets (-500, +100): the band leaves EG's edge at y = 300");
    }

    private void T10()
    {
        var d = Pair("EG vs FG_D", FixtureSpec.Names.EG, FixtureSpec.Names.FgD);
        foreach (string name in new[] { FixtureSpec.Names.AlignmentK, FixtureSpec.Names.AlignmentSP })
        {
            var o = Regen(Def("T10-" + name, "START..END/4", alignment: name, analyses: d));
            string id = "T10." + name;
            if (!o.Result.Succeeded || o.Plan is null) { Add(id, false, Codes(o), "succeeded", FirstError(o)); continue; }
            var rings = o.Plan.Boundaries.ToDictionary(b => b.SegmentId, b => Math.Abs(ShoelaceArea(b.Tessellated)));
            double worst = o.Result.Segments.Max(r => Math.Abs(r.Raw.Fill - rings[r.SegmentId]) / rings[r.SegmentId]);
            Add(id, worst <= 1e-6 && o.Result.PartitionCheckPassed,
                $"max |fill ft3 - polygon area| / area = {E(worst)} over {o.Result.Segments.Count} segments; partition {o.Result.PartitionCheckPassed}",
                "within 1e-6 relative; partition check passes", $"D (+1 ft slab) on {name}: the API volume is the Core polygon's area");
        }
    }

    private void T11()
    {
        var o = Regen(Def("T11", "START..END@500", analyses: Pair("EG vs FG_A factored", FixtureSpec.Names.EG, FixtureSpec.Names.FgA, 1.25, 0.9)));
        if (!o.Result.Succeeded) { Add("T11", false, Codes(o), "succeeded", FirstError(o)); return; }
        bool once = o.Result.Segments.All(r => Math.Abs(r.Cut - Cy(r.Raw.Cut) * 1.25) <= 1e-9 && Math.Abs(r.Fill - Cy(r.Raw.Fill) * 0.9) <= 1e-9);
        string? handle = o.Record?.Sources.FirstOrDefault(s => s.Kind == SourceKinds.VolumeSurface)?.Handle;
        var factors = handle is null ? null : WithTransaction(tr =>
        {
            long h = long.Parse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return _doc.Database.TryGetObjectId(new Handle(h), out var id) && tr.GetObject(id, OpenMode.ForRead) is CivTinVolumeSurface v
                ? new double[] { v.CutFactor, v.FillFactor } : null;
        });
        bool surface = factors is not null && factors[0] == 1.25 && factors[1] == 0.9;
        var first = o.Result.Segments.OrderBy(r => r.StartStation).First();
        Add("T11", once && surface, $"segment 1 raw C {F(Cy(first.Raw.Cut))} -> {F(first.Cut)}, raw F {F(Cy(first.Raw.Fill))} -> {F(first.Fill)}; owned surface factors {(factors is null ? "not found" : $"{F(factors[0])}/{F(factors[1])}")}",
            "output = raw x 1.25 / 0.9 on every row, once; owned surface carries 1.25 / 0.9", "A with factors 1.25 / 0.9 (E410)");
    }

    private void T17()
    {
        if (_world is null) { NotRun("T17", "twist -90", "SelfTestWorlds.Installed is not set"); return; }
        _world.CreateStraightAlignment(NorthAlignment, new Pt(5000, 0), new Pt(5000, 800));
        var o = Regen(Def("T17", "0, 500", alignment: NorthAlignment, analyses: []));
        if (!o.Result.Succeeded) { Add("T17", false, Codes(o), "succeeded", FirstError(o)); return; }
        var seg = o.Plan!.Segments.Single();
        var spec = o.Views[seg.Id].Spec!;
        double twist = Normalize(spec.TwistRadians);
        double c = Math.Cos(spec.TwistRadians), s = Math.Sin(spec.TwistRadians);
        double dx = 5000 - spec.ModelCenter.X, dy = 0 - spec.ModelCenter.Y;
        double paperX = (dx * c - dy * s) / spec.ModelUnitsPerPaperUnit, paperY = (dx * s + dy * c) / spec.ModelUnitsPerPaperUnit;
        string? vpHandle = HandleOf(o.Record, seg.Id, OwnedRole.Viewport);
        double? stored = vpHandle is null ? null : WithTransaction(tr =>
        {
            long h = long.Parse(vpHandle, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return _doc.Database.TryGetObjectId(new Handle(h), out var id) && tr.GetObject(id, OpenMode.ForRead) is Viewport vp ? vp.TwistAngle : (double?)null;
        });
        bool pass = Math.Abs(twist + Math.PI / 2) < 1e-9 && paperX < -4 && Math.Abs(paperY) < 1e-6
                    && stored is { } st && Math.Abs(Normalize(st) - twist) < 1e-9;
        Add("T17", pass, $"twist {F(twist * 180 / Math.PI)} deg; segment start at paper ({F(paperX)}, {F(paperY)}); viewport TwistAngle {(stored is { } t ? F(t * 180 / Math.PI) : "none")} deg",
            "twist -90 deg (stored 270), start on the left edge", "north-running straight alignment, 0-500, views only (no analyses)");
    }

    private void T18()
    {
        var o = Regen(Def("T18", "0, 500, 1000"));
        if (!o.Result.Succeeded || o.Reconcile is null) { Add("T18", false, Codes(o), "succeeded", FirstError(o)); return; }
        var segs = o.Plan!.Segments.OrderBy(x => x.StartStation).ToList();
        var tables = DrawingRead.Run(_doc, Engine(), (src, _) =>
        {
            var al = src.Alignment(AlignmentS, null).Value.Geometry;
            return segs.ToDictionary(x => x.Id, x => SegmentTableBuilder.Build(o.Reconcile, x.Id, o.Run.AreaUnit, al.FormatStation, _settings.Report).Value);
        });
        string path = Path.Combine(_dir, "selftest-t18.dwg");
        if (File.Exists(path)) File.Delete(path);
        _doc.Database.SaveAs(path, DwgVersion.Current);   // a copy; the open drawing keeps its name (A2 measured)
        using var side = new Database(false, true);
        side.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
        side.CloseInput(true);
        using var tr = side.TransactionManager.StartTransaction();
        var owned = PaperWriter.FindOwned(tr, side, _settings.Paper.OwnerTagKey, RunIds.For("T18"));
        var notes = new List<string>();
        bool ok = owned.Count(x => x.Role == OwnedRole.Layout) == segs.Count;
        notes.Add($"layouts {owned.Count(x => x.Role == OwnedRole.Layout)} (plan {segs.Count})");
        foreach (var seg in segs)
        {
            var spec = o.Views[seg.Id].Spec!;
            var vpRef = owned.FirstOrDefault(x => x.Role == OwnedRole.Viewport && x.SegmentId == seg.Id);
            var tableRef = owned.FirstOrDefault(x => x.Role == OwnedRole.PaperTable && x.SegmentId == seg.Id);
            if (vpRef is null || tableRef is null) { ok = false; notes.Add($"sheet {seg.Label}: viewport {vpRef is not null}, table {tableRef is not null}"); continue; }
            var vp = (Viewport)tr.GetObject(Id(side, vpRef.Handle), OpenMode.ForRead);
            double targetErr = Math.Max(Math.Abs(vp.ViewTarget.X - spec.ModelCenter.X), Math.Abs(vp.ViewTarget.Y - spec.ModelCenter.Y));
            double twistErr = Math.Abs(Normalize(vp.TwistAngle - spec.TwistRadians));
            double scaleErr = Math.Abs(vp.CustomScale * spec.ModelUnitsPerPaperUnit - 1);
            int clip = !vp.NonRectClipEntityId.IsNull && tr.GetObject(vp.NonRectClipEntityId, OpenMode.ForRead) is Polyline pl ? pl.NumberOfVertices : 0;
            int clipPlan = spec.ClipInPaper?.Count ?? 0;
            var t = (Table)tr.GetObject(Id(side, tableRef.Handle), OpenMode.ForRead);
            var model = tables[seg.Id];
            var covered = TableRenderer.Covered(model.Merges);
            var cells = TableRenderer.ReadCells(t);
            int mismatches = cells.Count != model.Rows.Count ? int.MaxValue
                : model.Rows.SelectMany((r, i) => r.Cells.Select((text, j) => (i, j, text))).Count(c => !covered.Contains((c.i, c.j)) && cells[c.i][c.j] != c.text);
            bool sheet = targetErr <= 1e-6 && twistErr <= 1e-9 && scaleErr <= 1e-9 && clip == clipPlan && mismatches == 0;
            ok &= sheet;
            notes.Add($"sheet {seg.Label}: target err {E(targetErr)}, twist err {E(twistErr)}, scale err {E(scaleErr)}, clip {clip}/{clipPlan} vertices, table {cells.Count}x{model.ColumnCount} cells mismatched {(mismatches == int.MaxValue ? "shape" : mismatches.ToString(CultureInfo.InvariantCulture))}");
        }
        tr.Commit();
        Add("T18", ok, string.Join("; ", notes), "layout count, viewport target/twist/scale, clip vertex count and table text match the plan after save and reopen",
            $"A, 0-500-1000 from FX's code-built template, saved to {Path.GetFileName(path)} and read back with ReadDwgFile");
    }

    // T31 in rev 3 form (NOW.md X5): T25's run (base OHWM_100, comparison FG_H), type into the centre-
    // coordinates cell, delete WETLANDS, then the OHWM change, which is now a change of base surface from
    // OHWM_100 to OHWM_97 on the same analysis, and regenerate. Rows 6, 7 and 9 must show T26's values as
    // the table formats them (FixtureSpec.Expected.T31*: 500 LF; 14,500 / 43,560 = 0.33 AC; 36,750 / 27 =
    // 1,361 CY; 1,361.111 / 500 = 2.72), with the typed text, the deletion and the table handles kept.
    private void T31()
    {
        var run = Def("T31", "0, 500, 1000", preset: ReportPreset.HcfcdSegment, analyses: Sediment(FixtureSpec.Names.Ohwm100), area: AreaUnit.Acres);
        var first = Regen(run);
        if (!first.Result.Succeeded) { Add("T31", false, Codes(first), "succeeded", FirstError(first)); return; }
        var hcfcd = first.Record!.Owned.Where(o => o.Key.Role == OwnedRole.ModelTable && o.Key.Qualifier == OwnedQualifiers.HcfcdTable).OrderBy(o => o.Handle).ToList();
        if (hcfcd.Count != 2) { Add("T31", false, $"{hcfcd.Count} HCFCD tables", "2", "one HCFCD table per segment in model space"); return; }
        const string typed = "N 13,812,345.67  E 3,101,234.56 (TYPED)";
        string handle = hcfcd[0].Handle;
        int rowsBefore = WithTransaction(tr =>
        {
            var t = (Table)tr.GetObject(Id(_doc.Database, handle), OpenMode.ForWrite);
            t.Cells[FindRow(t, "CENTER COORDINATES"), 1].TextString = typed;
            t.DeleteRows(FindRow(t, "WETLANDS"), 1);
            return t.Rows.Count;
        });
        var second = Regen(run with { Analyses = Sediment(FixtureSpec.Names.Ohwm97) });
        if (!second.Result.Succeeded) { Add("T31", false, Codes(second), "succeeded", FirstError(second)); return; }
        bool handles = hcfcd.All(h => second.Record!.Owned.Any(o => o.Key == h.Key && o.Handle == h.Handle));
        var (kept, gone, rows, length, area, sediment) = WithTransaction(tr =>
        {
            var t = (Table)tr.GetObject(Id(_doc.Database, handle), OpenMode.ForRead);
            string Row(int r) => r < 0 ? "missing" : string.Join(" | ", Enumerable.Range(0, t.Columns.Count).Select(c => TableRenderer.Plain(t, r, c)));
            return (TableRenderer.Plain(t, FindRow(t, "CENTER COORDINATES"), 1) == typed, FindRow(t, "WETLANDS") < 0, t.Rows.Count,
                Row(FindRow(t, "TOTAL IMPACTED LENGTH")), Row(FindRow(t, "TOTAL IMPACTED AREA")), Row(FindRow(t, "SEDIMENT")));
        });
        bool values = length.Contains($"| {Want.T31Length} |", StringComparison.Ordinal) && area.Contains($"| {Want.T31AreaAc} |", StringComparison.Ordinal)
                      && sediment.StartsWith($"SEDIMENT | {Want.T31Volume} | {Want.T31Average}", StringComparison.Ordinal);
        Add("T31", kept && gone && rows == rowsBefore && handles && values,
            $"typed text kept {kept}; WETLANDS deletion kept {gone} ({rows} rows, {rowsBefore} after the edit); handles unchanged {handles}; rows 6, 7, 9: '{length}' ; '{area}' ; '{sediment}'",
            $"typed text and the deletion kept; rows 6, 7 and 9 show T26's values ({Want.T31Length} LF, {Want.T31AreaAc} AC, {Want.T31Volume} CY, {Want.T31Average}); table handles unchanged",
            $"H: HCFCD tables, type into the centre-coordinates cell, delete WETLANDS, base {FixtureSpec.Names.Ohwm100} -> {FixtureSpec.Names.Ohwm97}, regen");
    }

    /// <summary>E217: a run made with a rotated UCS current gives the WCS run's boundaries and volumes exactly (geometry is WCS; only picks are transformed).</summary>
    private void E217()
    {
        var ed = _doc.Editor;
        var saved = ed.CurrentUserCoordinateSystem;
        RunOutcome wcs, ucs;
        try
        {
            wcs = Regen(Def("E217W", "START..END@500"));
            ed.CurrentUserCoordinateSystem = Autodesk.AutoCAD.Geometry.Matrix3d.Rotation(Math.PI / 3, Autodesk.AutoCAD.Geometry.Vector3d.ZAxis, new Autodesk.AutoCAD.Geometry.Point3d(100, 200, 0));
            ucs = Regen(Def("E217U", "START..END@500"));
        }
        finally
        {
            ed.CurrentUserCoordinateSystem = saved;
        }
        if (!wcs.Result.Succeeded || !ucs.Result.Succeeded || wcs.Plan is null || ucs.Plan is null) { Add("E217", false, $"{Codes(wcs)} / {Codes(ucs)}", "both succeed", "regen with a rotated UCS"); return; }
        bool rings = wcs.Plan.Boundaries.Zip(ucs.Plan.Boundaries).All(p => p.First.Tessellated.SequenceEqual(p.Second.Tessellated)) && wcs.Plan.Boundaries.Count == ucs.Plan.Boundaries.Count;
        bool volumes = wcs.Result.Segments.Zip(ucs.Result.Segments).All(p => p.First.Raw == p.Second.Raw && p.First.Area2D == p.Second.Area2D);
        Add("E217", rings && volumes, $"boundaries identical {rings}, raw volumes and 2D areas identical {volumes} over {wcs.Result.Segments.Count} rows",
            "identical to the WCS run", "UCS rotated 60 degrees about (100, 200) while regenerating (non-world UCS active)");
    }

    /// <summary>E308 and E612: a removed break keeps the left segment; the right one's boundary, marks and tables are erased and its layout becomes an orphan.</summary>
    private void E308()
    {
        var first = Regen(Def("E308", "0, 500, 1000"));
        if (!first.Result.Succeeded) { Add("E308", false, Codes(first), "succeeded", FirstError(first)); return; }
        var known = first.Run.KnownSegments.OrderBy(k => k.StartStation).ToList();
        string? rightLayout = HandleOf(first.Record, known[1].Id, OwnedRole.Layout);
        var o = Regen(Def("E308", "0, 1000"));
        if (!o.Result.Succeeded) { Add("E308", false, Codes(o), "succeeded", FirstError(o)); return; }
        var kept = o.Run.KnownSegments.Single();
        bool right = !o.Record!.Owned.Any(x => x.Key.SegmentId == known[1].Id && x.Key.Role is OwnedRole.Boundary or OwnedRole.MatchMark or OwnedRole.PaperTable);
        bool orphan = rightLayout is not null && o.Record.Orphans.Any(x => x.Handle == rightLayout && x.SegmentId == known[1].Id)
                      && Layouts().Any(l => string.Equals(l.Handle, rightLayout, StringComparison.OrdinalIgnoreCase));
        Add("E308", kept.Id == known[0].Id && o.Applied?.Erased >= 1 && right, $"left GUID kept {kept.Id == known[0].Id}, erased {o.Applied?.Erased}, right segment's owned objects gone {right}",
            "left keeps its GUID; right's boundary, marks and tables erased", "break at 500 removed from 0, 500, 1000");
        Add("E612", orphan, $"layout {rightLayout} listed as orphan {o.Record.Orphans.Any(x => x.Handle == rightLayout)}, still in the drawing {Layouts().Any(l => string.Equals(l.Handle, rightLayout, StringComparison.OrdinalIgnoreCase))}",
            "the removed segment's layout is kept and listed (SVSTATUS), never deleted", "segment removed");
    }

    /// <summary>E311: a manual segment is computed on the user's polyline as drawn; an erased polyline is an error for that segment.</summary>
    private void E311()
    {
        string handle = WithTransaction(tr =>
        {
            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(_doc.Database), OpenMode.ForWrite);
            var pl = new Polyline();
            foreach (var (x, y) in new[] { (60.0, -60.0), (190.0, -60.0), (190.0, 60.0), (60.0, 60.0) }) pl.AddVertexAt(pl.NumberOfVertices, new Autodesk.AutoCAD.Geometry.Point2d(x, y), 0, 0, 0);
            pl.Closed = true;
            ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            return pl.Handle.ToString();
        });
        var manual = new ManualSegmentDef(DeterministicId("E311-M1"), "M1", handle);
        var run = Def("E311", "0, 500") with { ManualSegments = [manual] };
        var o = Regen(run);
        var row = o.Result.Segments.FirstOrDefault(r => r.SegmentId == manual.Id);
        bool used = o.Result.Succeeded && row is not null && Math.Abs(row.Cut - 100) <= _tolCy && Math.Abs(row.Area2D - 130 * 120) < 1e-6;
        WithTransaction(tr => { EraseByHandle(tr, handle); return true; });
        var gone = Regen(run);
        bool refused = !gone.Result.Succeeded && gone.Result.Problems.Any(p => p.Code == Core.Planning.ProblemCodes.E311);
        Add("E311", used && refused, $"drawn: cut {F(row?.Cut ?? double.NaN)} CY, 2D area {F(row?.Area2D ?? double.NaN)}; erased: succeeded {gone.Result.Succeeded}, codes {Codes(gone)}",
            "cut 100 CY and area 15600 on the polyline as drawn; erased polyline fails with SV-E311", "manual segment around P1 (130 x 120 rectangle), then the polyline erased");
    }

    /// <summary>E401, E402, E403: a pair that is one surface, a volume surface as comparison, and an empty TIN are refused before anything is written.</summary>
    private void E401E402E403()
    {
        var same = Regen(Def("E401", "0, 500", analyses: Pair("same", FixtureSpec.Names.EG, FixtureSpec.Names.EG)));
        Add("E401", !same.Result.Succeeded && same.Result.Problems.Any(p => p.Code == "SV-E401"), $"succeeded {same.Result.Succeeded}, failed at step {same.FailedAtStep}, codes {Codes(same)}", "refused with SV-E401", "base and comparison are the same surface");
        string volume = _settings.Report.Volumes.OwnedSurfacePrefix + FixtureSpec.Names.EG + _settings.Report.Volumes.OwnedSurfaceSeparator + FixtureSpec.Names.FgA;
        var vol = Regen(Def("E402", "0, 500", analyses: Pair("volume", FixtureSpec.Names.EG, volume)));
        Add("E402", !vol.Result.Succeeded && vol.FailedAtStep == 1 && vol.Result.Problems.Any(p => p.Code == "SV-E402"), $"succeeded {vol.Result.Succeeded}, failed at step {vol.FailedAtStep}, codes {Codes(vol)}",
            "refused at step 1 with SV-E402", $"the owned volume surface '{volume}' picked as comparison");
        const string empty = "T-EMPTY";   // no SV- prefix: that is the owned-surface prefix SESSION.lists filters on
        WithTransaction(tr =>
        {
            var civ = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(_doc.Database);
            bool exists = civ.GetSurfaceIds().Cast<ObjectId>().Any(id => tr.GetObject(id, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Surface s && s.Name == empty);
            if (!exists) Autodesk.Civil.DatabaseServices.TinSurface.Create(_doc.Database, empty);
            return true;
        });
        var before = Snapshot();
        var none = Regen(Def("E403", "0, 500", analyses: Pair("empty", FixtureSpec.Names.EG, empty)));
        bool unchanged = Snapshot() == before;
        Add("E403", !none.Result.Succeeded && none.FailedAtStep == 1 && none.Result.Problems.Any(p => p.Code == "SV-E403") && unchanged,
            $"succeeded {none.Result.Succeeded}, failed at step {none.FailedAtStep}, codes {Codes(none)}, model space and layouts unchanged {unchanged}", "refused at step 1 with SV-E403, nothing written", $"TIN surface '{empty}' with no triangles");
    }

    /// <summary>E411 and E421: two analyses on one pair share one owned surface; analyses whose surfaces cover different extents get their own 2D areas.</summary>
    private void E411E421()
    {
        var analyses = new[]
        {
            new AnalysisDef(DeterministicId("E411-a"), "EG vs FG_A", FixtureSpec.Names.EG, FixtureSpec.Names.FgA),
            new AnalysisDef(DeterministicId("E411-b"), "EG vs FG_A again", FixtureSpec.Names.EG, FixtureSpec.Names.FgA),
            new AnalysisDef(DeterministicId("E411-h"), "EG_H vs FG_H", FixtureSpec.Names.EgH, FixtureSpec.Names.FgH),
        };
        var o = Regen(Def("E411", "0, 500", analyses: analyses, left: -150, right: 150));
        if (!o.Result.Succeeded) { Add("E411", false, Codes(o), "succeeded", FirstError(o)); return; }
        int owned = o.Record!.Sources.Count(s => s.Kind == SourceKinds.VolumeSurface);
        var areas = o.Result.Segments.ToDictionary(r => r.AnalysisId, r => (r.Area2D, r.Coverage));
        Add("E411", owned == 2 && areas.Count == 3, $"{owned} owned volume surface(s) for {areas.Count} analyses", "2 (EG/FG_A shared by two analyses, EG_H/FG_H)", "two analyses on one base/comparison pair");
        var a = areas[analyses[0].Id];
        var h = areas[analyses[2].Id];
        Add("E421", Math.Abs(a.Area2D - 150_000) < 1e-6 && Math.Abs(h.Area2D - 100_000) < 1e-6 && h.Coverage < 1,
            $"EG/FG_A {F(a.Area2D)} SF coverage {F(a.Coverage)}; EG_H/FG_H {F(h.Area2D)} SF coverage {F(h.Coverage)}", "150000 (coverage 1) and 100000 (coverage 0.667)",
            "one segment 0-500, offsets +-150: EG covers it, EG_H only |y| <= 100");
    }

    /// <summary>E412: the owned volume surface erased by a user is recreated on the next regen.</summary>
    private void E412()
    {
        var before = WithTransaction(tr => _writers.Store(_doc.Database, tr, _settings).Load("T2").Record);
        string? handle = before?.Sources.FirstOrDefault(s => s.Kind == SourceKinds.VolumeSurface)?.Handle;
        if (handle is null) { Add("E412", false, "no owned surface recorded for T2", "a recorded handle", "T2 must have run"); return; }
        WithTransaction(tr => { EraseByHandle(tr, handle); return true; });
        var o = Regen(Def("T2", "0, 500, 1300, 1800, 2000"));
        string? now = o.Record?.Sources.FirstOrDefault(s => s.Kind == SourceKinds.VolumeSurface)?.Handle;
        bool pass = o.Result.Succeeded && now is not null && now != handle && o.Result.Problems.Any(p => p.Code == "SV-E412");
        Add("E412", pass, $"handle {handle} -> {now}, codes {Codes(o)}", "recreated with a new handle, SV-E412 note", "owned volume surface erased by a user");
    }

    /// <summary>E501: a table style named in settings but missing from the drawing fails the run before any write, naming it.</summary>
    private void E501()
    {
        var missing = _settings with { Report = _settings.Report with { SegmentTable = _settings.Report.SegmentTable with { Style = _settings.Report.SegmentTable.Style with { TableStyle = "SV-T-MISSING" } } } };
        var before = Snapshot();
        var o = Regen(Def("E501", "0, 500"), Engine(null, null, missing));
        bool pass = !o.Result.Succeeded && o.Result.Problems.Any(p => p.Code == "SV-E501" && p.Message.Contains("SV-T-MISSING", StringComparison.Ordinal)) && Snapshot() == before;
        Add("E501", pass, $"failed at step {o.FailedAtStep}, codes {Codes(o)}", "fails naming SV-T-MISSING, nothing written", "segment table style missing from the drawing");
    }

    /// <summary>E503: an erased table is recreated at its anchor.</summary>
    private void E503()
    {
        var before = WithTransaction(tr => _writers.Store(_doc.Database, tr, _settings).Load("T2").Record);
        string? handle = before?.Owned.FirstOrDefault(o => o.Key.Role == OwnedRole.MasterTable)?.Handle;
        if (handle is null) { Add("E503", false, "no master table recorded for T2", "a recorded handle", "T2 must have run"); return; }
        WithTransaction(tr => { EraseByHandle(tr, handle); return true; });
        var o = Regen(Def("T2", "0, 500, 1300, 1800, 2000"));
        string? now = o.Record?.Owned.FirstOrDefault(x => x.Key.Role == OwnedRole.MasterTable)?.Handle;
        bool pass = o.Result.Succeeded && now is not null && now != handle && o.Applied?.Created == 1 && o.Result.Problems.Any(p => p.Code == "SV-E503");
        Add("E503", pass, $"master table {handle} -> {now}, created {o.Applied?.Created}, codes {string.Join(",", o.Result.Problems.Select(p => p.Code).Distinct())}", "recreated, 1 created, SV-E503 note", "master table erased by a user");
    }

    /// <summary>E505: an analysis added grows every table by one block in place; removed, it shrinks back; handles never change.</summary>
    private void E505()
    {
        var before = WithTransaction(tr => _writers.Store(_doc.Database, tr, _settings).Load("T2").Record);
        var tables = (before?.Owned ?? []).Where(o => o.Key.Role is OwnedRole.PaperTable or OwnedRole.MasterTable).ToList();
        var two = new[]
        {
            new AnalysisDef(_analysisA, "EG vs FG_A", FixtureSpec.Names.EG, FixtureSpec.Names.FgA),
            new AnalysisDef(DeterministicId("E505-copy"), "EG vs FG_A copy", FixtureSpec.Names.EG, FixtureSpec.Names.FgA),
        };
        var grown = Regen(Def("T2", "0, 500, 1300, 1800, 2000", analyses: two));
        var shrunk = Regen(Def("T2", "0, 500, 1300, 1800, 2000"));
        bool Same(RunOutcome o) => tables.Count > 0 && tables.All(t => o.Record?.Owned.Any(n => n.Key == t.Key && n.Handle == t.Handle) == true);
        bool pass = grown.Result.Succeeded && shrunk.Result.Succeeded && Same(grown) && Same(shrunk)
                    && grown.Result.Problems.Any(p => p.Code == "SV-E505") && shrunk.Result.Problems.Any(p => p.Code == "SV-E505");
        Add("E505", pass, $"{tables.Count} table handles kept after grow {Same(grown)} and shrink {Same(shrunk)}; SV-E505 notes {grown.Result.Problems.Count(p => p.Code == "SV-E505")} / {shrunk.Result.Problems.Count(p => p.Code == "SV-E505")}",
            "same handles; tables grow then shrink by one analysis block in place (SV-E505)", "T2: a second analysis added, then removed");
    }

    /// <summary>E601: a sheet template that is not there fails the run before any write, naming the file.</summary>
    private void E601()
    {
        string path = Path.Combine(_dir, "no-such-template.dwg");
        var broken = _settings with { Paper = _settings.Paper with { TemplatePath = path } };
        var before = Snapshot();
        var o = Regen(Def("E601", "0, 500"), Engine(null, null, broken));
        bool pass = !o.Result.Succeeded && o.FailedAtStep == 4 && o.Result.Problems.Any(p => p.Code == "SV-E601" && p.Message.Contains("no-such-template", StringComparison.Ordinal)) && Snapshot() == before;
        Add("E601", pass, $"failed at step {o.FailedAtStep}, codes {Codes(o)}", "fails at step 4 naming the file, nothing written", "paper.templatePath names a missing file");
    }

    /// <summary>E803: a stored alignment handle that no longer resolves falls back to the name, with a warning, and the record is repaired.</summary>
    private void E803()
    {
        WithTransaction(tr =>
        {
            var store = _writers.Store(_doc.Database, tr, _settings);
            var r = store.Load("T2").Record!;
            store.Save(r with { Sources = r.Sources.Select(s => s.Kind == SourceKinds.Alignment ? s with { Handle = "FFFFFF" } : s).ToList() });
            return true;
        });
        var o = Regen(Def("T2", "0, 500, 1300, 1800, 2000"));
        string? repaired = o.Record?.Sources.FirstOrDefault(s => s.Kind == SourceKinds.Alignment)?.Handle;
        bool pass = o.Result.Succeeded && o.Result.Problems.Any(p => p.Code == PipelineCodes.MatchedByName) && repaired is not null && repaired != "FFFFFF";
        Add("E803", pass, $"warning {o.Result.Problems.Any(p => p.Code == PipelineCodes.MatchedByName)}, stored handle FFFFFF -> {repaired}", "matched by name with SV-E803, handle repaired", "stored alignment handle re-referenced");
    }

    /// <summary>E808: two runs on one alignment are independent; two runs placing tables on one layout are warned.</summary>
    private void E808()
    {
        string target = FixtureSpec.Names.TemplateLayout;
        RunOutcome Pin(string name)
        {
            var first = Regen(Def(name, "0, 500"));
            var seg = first.Run.KnownSegments.Single().Id;
            return Regen(Def(name, "0, 500") with { Overrides = new Dictionary<Guid, SegmentOverrides> { [seg] = new SegmentOverrides(TargetLayout: target) } });
        }
        var a = Pin("E808a");
        var b = Pin("E808b");
        bool pass = a.Result.Succeeded && b.Result.Succeeded && b.Result.Problems.Any(p => p.Code == PipelineCodes.SharedLayout)
                    && a.Record!.Owned.Any(o => o.Key.Role == OwnedRole.ModelTable) && b.Record!.Owned.Any(o => o.Key.Role == OwnedRole.ModelTable);
        Add("E808", pass, $"both succeeded {a.Result.Succeeded && b.Result.Succeeded}; second warned {b.Result.Problems.Any(p => p.Code == PipelineCodes.SharedLayout)}; codes {Codes(b)}",
            "independent runs, each with its own table on the layout; SV-E808 warning on the shared layout", $"two runs on S whose first segment targets layout '{target}'");
    }

    private void T29()
    {
        if (_crossChecks.Count == 0) { Add("T29", false, "no cross-check ran", "every regen cross-checked", "the integrator cross-check did not run in any regen"); return; }
        var worst = _crossChecks.MaxBy(c => c.Relative);
        var surfaces = _fakes ? "A, H on S" : "A, B, H on S; D on C, K, SP";
        Add("T29", worst.Relative <= _settings.Integrator.CrossCheckRelative || worst.Relative == 0,
            $"{_crossChecks.Count} regens cross-checked ({surfaces}); worst relative difference {E(worst.Relative)} in run {worst.Run}",
            $"integrator band cut and fill equal the bounded volume within {E(_settings.Integrator.CrossCheckRelative)} relative on every segment of every regen",
            "6.15 checks on every regen at the fixture tolerance; a miss fails the run with SV-E1012 (T29, E1012)");
    }

    private void Session()
    {
        using var session = new SheetVolSession(() => Engine());
        var info = session.ActiveDrawing;
        Add("SESSION.drawing", info is { Units: UnitSystem.Imperial }, info is null ? "null" : $"{info.Name} {info.Units}", "the active drawing, Imperial", "ActiveDrawing (E103: runs in Core Console)");
        var al = session.AlignmentNames;
        var su = session.TinSurfaceNames;
        Add("SESSION.lists", al.Contains(AlignmentS) && su.Contains(FixtureSpec.Names.EG) && su.Contains(FixtureSpec.Names.FgA) && !su.Any(n => n.StartsWith(_settings.Report.Volumes.OwnedSurfacePrefix, StringComparison.Ordinal)),
            $"alignments {string.Join(",", al)}; surfaces {string.Join(",", su)}", "S; EG, FG_A; no owned volume surface", "AlignmentNames, TinSurfaceNames (E402: volume surfaces are not offered)");
        var range = session.StationRange(AlignmentS);
        string sta = session.FormatStation(AlignmentS, 500);
        Add("SESSION.stations", range is { } r && r.Start == 0 && r.End > 0 && sta.Length > 0, $"range {range}, 500 -> '{sta}'", "range starts at 0; station text", "StationRange, FormatStation");
        Add("SESSION.commandActive", session.IsCommandActive, session.IsCommandActive.ToString(), "True inside SVSELFTEST", "IsCommandActive (E905)");
        bool threw = false;
        try { session.Highlight(null); } catch (NotSupportedException) { threw = true; }
        Add("SESSION.editor", threw, threw ? "NotSupportedException" : "no exception", "NotSupportedException", "editor members are U2's (Core Console has none)");
        var v = session.Validate(Def("T2", "0, 500, 1300, 1800, 2000"));
        Add("SESSION.validate", v.IsValid && v.Segments.Select(x => x.EndStation - x.StartStation).SequenceEqual([500.0, 800, 500, 200]),
            $"valid {v.IsValid}, lengths {string.Join("/", v.Segments.Select(x => F(x.EndStation - x.StartStation)))}", "valid, 500/800/500/200", "Validate is pure Core planning");
        var loaded = session.LoadRun("T1");
        Add("SESSION.loadRun", loaded is not null && loaded.KnownSegments.Count >= 5, $"KnownSegments {loaded?.KnownSegments.Count}", ">= 5 after T13", "LoadRun round-trips the stored run");

        var result = session.RegenerateAsync(Def("T2", "0, 500, 1300, 1800, 2000"), new Progress<double>(), CancellationToken.None).GetAwaiter().GetResult();
        var status = session.GetRunStatus("T2");
        Add("SESSION.regen", result.Succeeded && !status.IsStale && ReferenceEquals(status.LastResult, result),
            $"succeeded {result.Succeeded}, stale after {status.IsStale}, last result is it {ReferenceEquals(status.LastResult, result)}",
            "succeeded, clean, LastResult", "RegenerateAsync in command context; GetRunStatus (E912)");
        var exported = session.ExportAsync(Def("T2", "0, 500, 1300, 1800, 2000"), CancellationToken.None).GetAwaiter().GetResult();
        Add("SESSION.export", exported?.CsvPath is { } c && File.Exists(c), exported?.CsvPath ?? "null", "a CSV path beside the drawing", "ExportAsync writes from the cached result, no drawing writes");
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static int FindRow(Table t, string labelPrefix)
    {
        for (int r = 0; r < t.Rows.Count; r++)
            if (TableRenderer.Plain(t, r, 0).StartsWith(labelPrefix, StringComparison.Ordinal)) return r;
        return -1;
    }

    private static ObjectId Id(Database db, string handle)
    {
        long h = long.Parse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return db.TryGetObjectId(new Handle(h), out var id) ? id : throw new InvalidOperationException($"Handle {handle} is not in {db.Filename}.");
    }

    private static double Normalize(double a)
    {
        a %= 2 * Math.PI;
        if (a <= -Math.PI) a += 2 * Math.PI;
        else if (a > Math.PI) a -= 2 * Math.PI;
        return a;
    }

    private static double ShoelaceArea(IReadOnlyList<Pt> ring)
    {
        double sum = 0;
        for (int i = 0; i + 1 < ring.Count; i++) sum += ring[i].X * ring[i + 1].Y - ring[i + 1].X * ring[i].Y;
        return sum / 2;
    }
}

// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4 (SVSELFTEST driver).
//
// The acceptance checks the pipeline can run today (HANDOFF.md section 8 and the H rows of
// 7.2-7.8 at the pipeline level), against whichever bindings it is given: the fakes in
// SelfTestFakes.cs now, the real A1-A3 factories after they merge (SVSELFTEST Installed).
// Every check records measured against expected; a check that cannot run on the given
// bindings says so instead of passing.

using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Civil3D.Commands;
using SheetVol.Civil3D.Session;
using SheetVol.Core;
using SheetVol.Core.Tracking;
using AcadDocument = Autodesk.AutoCAD.ApplicationServices.Document;

namespace SheetVol.Civil3D.SelfTest;

/// <summary>One check in selftest-result.json.</summary>
/// <param name="Id">Test or row id, for example T1.volumes or E811.</param>
/// <param name="Pass">True when it passed; false when it failed or could not run.</param>
/// <param name="Measured">What was measured.</param>
/// <param name="Expected">What section 8 or section 7 requires.</param>
/// <param name="Message">Plain words.</param>
public sealed record SelfTestCheck(string Id, bool Pass, string Measured, string Expected, string Message);

/// <summary>Runs the pipeline-level acceptance checks.</summary>
internal sealed class SelfTestRunner
{
    private const string AlignmentS = FixtureSpec.Names.AlignmentS;
    private readonly AcadDocument _doc;
    private readonly Action<string> _say;
    private readonly SheetVolSettings _settings;
    private readonly IAdapterFactory _adapters;
    private readonly IWriterFactory _writers;
    private readonly ISelfTestWorld? _world;
    private readonly double _tolCy;
    private readonly Guid _analysisA = Guid.Parse("a0a0a0a0-0000-4000-8000-00000000000a");
    private readonly Guid _analysisH = Guid.Parse("a0a0a0a0-0000-4000-8000-00000000000b");

    public SelfTestRunner(AcadDocument doc, Action<string> say, SheetVolSettings settings, IAdapterFactory adapters, IWriterFactory writers, ISelfTestWorld? world)
    {
        _doc = doc;
        _say = say;
        _settings = settings;
        _adapters = adapters;
        _writers = writers;
        _world = world;
        _tolCy = settings.SelfTest.VolumeToleranceCy;
    }

    public List<SelfTestCheck> Checks { get; } = [];

    // ---- plumbing --------------------------------------------------------------------------

    private void Add(string id, bool pass, string measured, string expected, string message)
    {
        Checks.Add(new SelfTestCheck(id, pass, measured, expected, message));
        _say($"{(pass ? "SVSELFTEST" : PipelineCodes.SelfTestFailed + " SVSELFTEST")} {id} {(pass ? "PASS" : "FAIL")}: {message} | measured {measured} | expected {expected}");
    }

    private void Try(string id, Action body)
    {
        try { body(); }
        catch (Exception ex) { Add(id, false, $"{ex.GetType().Name}: {ex.Message}", "no exception", "the check threw"); }
    }

    private RegenEngine Engine(IAdapterFactory? adapters = null, IWriterFactory? writers = null, SheetVolSettings? settings = null) =>
        new(new EngineBindings(adapters ?? _adapters, writers ?? _writers), settings ?? _settings, SettingsProvider.PluginDirectory);

    private readonly List<string> _unexpected = [];

    private RunOutcome Regen(RunDefinition run, RegenEngine? engine = null)
    {
        var o = (engine ?? Engine()).Regenerate(_doc, run, RegenMode.Full, null, CancellationToken.None);
        SessionState.Record(_doc.Database, o);
        _unexpected.AddRange(o.Result.Problems.Where(p => p.Code == PipelineCodes.Unexpected).Select(p => $"{run.Name}: {p.Message}"));
        return o;
    }

    private RunDefinition Def(string name, string spec, string alignment = AlignmentS, ReportPreset preset = ReportPreset.Standard,
        IReadOnlyList<AnalysisDef>? analyses = null, OhwmSpec? ohwm = null, AreaUnit area = AreaUnit.SquareFeet) =>
        new(name, alignment, SegmentDriver.Stations, spec, null, -100, 100,
            analyses ?? [new AnalysisDef(_analysisA, "EG vs FG_A", FixtureSpec.Names.EG, FixtureSpec.Names.FgA)],
            [], [], new Dictionary<Guid, SegmentOverrides>(),
            new ViewOptions(ViewOrientation.AlongAlignment, ScaleMode.FitStandard, 50, [20, 30, 40, 50, 60, 100], ClipMode.Boundary, 0),
            preset, area, ohwm, AutoMode.Views);

    private static double Cy(double ft3) => ft3 / FixtureSpec.CubicFeetPerCubicYard;

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Codes(RunOutcome o) => string.Join(",", o.Result.Problems.Where(p => p.Severity != Severity.Info).Select(p => p.Code).Distinct());

    private static string FirstError(RunOutcome o) => o.Result.Problems.FirstOrDefault(p => p.Severity == Severity.Error && p.Code != PipelineCodes.RunFailed)?.Message ?? "";

    private Dictionary<OwnedRole, int> RoleCounts(RunOutcome o) =>
        (o.Record?.Owned ?? []).GroupBy(x => x.Key.Role).ToDictionary(g => g.Key, g => g.Count());

    private static string? HandleOf(RunRecord? r, Guid segment, OwnedRole role) =>
        r?.Owned.FirstOrDefault(o => o.Key.SegmentId == segment && o.Key.Role == role)?.Handle;

    private IReadOnlyList<LayoutRef> Layouts() =>
        WithTransaction(tr => _writers.Paper(_doc.Database, tr, _settings).Layouts());

    private T WithTransaction<T>(Func<Transaction, T> read)
    {
        using var tr = _doc.Database.TransactionManager.StartTransaction();
        var r = read(tr);
        tr.Commit();
        return r;
    }

    /// <summary>Everything an aborted regen must leave unchanged: stored records, owned objects as inspected, model-space and layout counts, and the fake dictionaries.</summary>
    private string Snapshot() => WithTransaction(tr =>
    {
        var db = _doc.Database;
        var lines = new List<string>();
        var store = _writers.Store(db, tr, _settings);
        var model = _writers.Model(db, tr, _settings);
        var paper = _writers.Paper(db, tr, _settings);
        foreach (string n in store.RunNames())
        {
            var load = store.Load(n);
            lines.Add($"run {n} {(load.Record is { } r ? RunRecordCodec.Signature(r) + " " + r.LastRegenUtc?.ToString("O") : "unreadable")}");
            foreach (var o in load.Record?.Owned ?? [])
            {
                var w = model.Roles.Contains(o.Key.Role) ? (IOwnedObjectWriter)model : paper;
                var s = w.Inspect(o.Key, o.Handle);
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

    public void Run(bool fakes)
    {
        RunOutcome? t1 = null, t2 = null;
        Try("CONFIG", Config);
        if (fakes) Try("LIFETIME", Lifetime);
        Try("T1", () => t1 = T1());
        if (t1 is not null)
        {
            Try("T19", () => T19(t1));
            Try("T12", () => T12());
            Try("T13", () => T13());
        }
        Try("T2", () => t2 = T2());
        Try("T25", T25);
        if (t2 is not null)
        {
            Try("E802", () => E802(t2));
            Try("E404", E404);
            Try("E1012", E1012);
            Try("E506-E507", () => E506E507(t2));
        }
        Try("T14", T14);
        Try("D10", D10);
        Try("T20", T20);
        Try("E801", E801);
        Try("E213", E213);
        Try("SESSION", Session);
        Add("E823", _unexpected.Count == 0, _unexpected.Count == 0 ? "none" : string.Join(" | ", _unexpected), "none",
            "no regen in this self-test hit an unexpected exception (before or after its commit)");
    }

    private RunOutcome T1()
    {
        var o = Regen(Def("T1", "START..END@500"));
        if (!o.Result.Succeeded) { Add("T1", false, Codes(o), "succeeded", FirstError(o)); return o; }

        var rows = o.Result.Segments.OrderBy(r => r.StartStation).ToList();
        var exp = FixtureSpec.Expected.T1;
        bool ok = rows.Count == exp.Length && rows.Zip(exp).All(p =>
            Math.Abs(p.First.Cut - Cy(p.Second.CutFt3)) <= _tolCy && Math.Abs(p.First.Fill - Cy(p.Second.FillFt3)) <= _tolCy
            && Math.Abs(p.First.StartStation - p.Second.From) < 1e-9 && Math.Abs(p.First.EndStation - p.Second.To) < 1e-9);
        Add("T1.volumes", ok, string.Join("; ", rows.Select(r => $"{F(r.StartStation)}-{F(r.EndStation)} C {F(r.Cut)} F {F(r.Fill)}")),
            string.Join("; ", exp.Select(e => $"{F(e.From)}-{F(e.To)} C {F(Cy(e.CutFt3))} F {F(Cy(e.FillFt3))}")), "A, START..END@500, offsets -100/+100 (CY, +-0.01)");

        bool areas = rows.All(r => Math.Abs(r.Area2D - 100_000) < 1e-6 && Math.Abs(r.Coverage - 1) < 1e-9);
        Add("T1.area", areas, string.Join("/", rows.Select(r => F(r.Area2D))), "100000 each, coverage 1", "2D area per 500 ft segment, 200 ft wide");

        var first = rows[0].SegmentId;
        var v = o.Views.TryGetValue(first, out var vr) ? vr : null;
        bool view = v?.Spec is { } spec && Math.Abs(spec.TwistRadians) < 1e-12 && Math.Abs(spec.ModelCenter.X - 250) < 1e-9 && Math.Abs(spec.ModelCenter.Y) < 1e-9
                    && spec.ModelUnitsPerPaperUnit == 50 && Math.Abs(v.RequiredPaperWidth - 10) < 1e-9 && Math.Abs(v.RequiredPaperHeight - 4) < 1e-9;
        Add("T1.views", view, v is null ? "no view" : $"twist {F(v.TwistRadians)} center ({F(v.ModelCenter.X)}, {F(v.ModelCenter.Y)}) scale {F(v.ModelUnitsPerPaperUnit)} needs {F(v.RequiredPaperWidth)} x {F(v.RequiredPaperHeight)}",
            "twist 0, center (250, 0), scale 50, needs 10 x 4 (T15)", "segment 1 view through C3 with the pipeline's display polygon");

        var counts = RoleCounts(o);
        int C(OwnedRole r) => counts.GetValueOrDefault(r);
        bool objects = C(OwnedRole.Boundary) == 4 && C(OwnedRole.Layout) == 4 && C(OwnedRole.Viewport) == 4 && C(OwnedRole.PaperTable) == 4
                       && C(OwnedRole.TitleAttributes) == 4 && C(OwnedRole.MatchMark) == 6 && C(OwnedRole.MasterTable) == 1 && o.Applied?.Created == 27;
        Add("T1.objects", objects, string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")) + $"; created {o.Applied?.Created}",
            "Boundary 4, MatchMark 6, MasterTable 1, Layout 4, Viewport 4, PaperTable 4, TitleAttributes 4; created 27", "owned objects recorded with handles (6.9)");

        Add("T1.partition", o.Result.PartitionCheckPassed && o.Result.OutsideLimits.Count == 0,
            $"partition {o.Result.PartitionCheckPassed}, outside-limits rows {o.Result.OutsideLimits.Count}", "passed, 0 rows", "every pyramid lies inside the band (6.5)");

        string dwg = RegenEngine.DrawingPath(_doc) is { } p ? Path.GetFileNameWithoutExtension(p) : "";
        bool e809 = o.Result.CsvPath is { } csv && File.Exists(csv) && Path.GetFileName(csv).StartsWith(dwg, StringComparison.OrdinalIgnoreCase)
                    && o.Result.JsonPath is { } json && File.Exists(json);
        Add("E809", e809, $"{o.Result.CsvPath}; {o.Result.JsonPath}", $"files named after drawing '{dwg}' beside it", "exports follow the drawing's current name");
        return o;
    }

    private void T19(RunOutcome first)
    {
        var o = Regen(Def("T1", "START..END@500"));
        bool sameHandles = o.Record is not null && first.Record is not null
            && o.Record.Owned.Select(x => (x.Key, x.Handle)).ToHashSet().SetEquals(first.Record.Owned.Select(x => (x.Key, x.Handle)));
        bool pass = o.Result.Succeeded && o.Applied?.Writes == 0 && o.Changes?.OnlyTimestamp == true && sameHandles
                    && o.Result.Problems.Any(p => p.Code == PipelineCodes.NothingChanged);
        Add("T19", pass, $"writes {o.Applied?.Writes}, only timestamp {o.Changes?.OnlyTimestamp}, same handles {sameHandles}, E811 {o.Result.Problems.Any(p => p.Code == PipelineCodes.NothingChanged)}",
            "writes 0, only timestamp, same handles, SV-E811", "regen twice: the second changes no handles and no geometry (E811)");
    }

    private void T12()
    {
        if (_world is null) { Add("T12", false, "no world editor for these bindings", "P1 apex moved", "not run: SelfTestWorlds.Installed is not set"); return; }
        var before = WithTransaction(tr => _writers.Store(_doc.Database, tr, _settings).Load("T1").Record);
        _world.SetPyramidApex(FixtureSpec.Names.FgA, FixtureSpec.Pyramids[0].CenterX, 98.0);
        var o = Regen(Def("T1", "START..END@500"));
        var seg1 = o.Result.Segments.OrderBy(r => r.StartStation).FirstOrDefault();
        var tableKeys = (before?.Owned ?? []).Where(x => x.Key.Role is OwnedRole.PaperTable or OwnedRole.ModelTable or OwnedRole.MasterTable).ToList();
        bool sameTables = tableKeys.Count > 0 && tableKeys.All(k => o.Record?.Owned.Any(n => n.Key == k.Key && n.Handle == k.Handle) == true);
        bool pass = o.Result.Succeeded && seg1 is not null && Math.Abs(seg1.Cut - 200) <= _tolCy && sameTables && o.Applied?.Created == 0 && o.Applied?.Updated >= 2;
        Add("T12", pass, $"0-500 cut {F(seg1?.Cut ?? double.NaN)} CY, {tableKeys.Count} table handles kept {sameTables}, created {o.Applied?.Created}, updated {o.Applied?.Updated}",
            "cut 200 CY, same table handles, nothing created", "P1 apex 98.0, rebuild, regen");
        _world.SetPyramidApex(FixtureSpec.Names.FgA, FixtureSpec.Pyramids[0].CenterX, FixtureSpec.Pyramids[0].ApexZ);
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
        var rows = o.Result.Segments.OrderBy(r => r.StartStation).ToList();
        var exp = FixtureSpec.Expected.T2;
        bool ok = rows.Count == exp.Length && rows.Zip(exp).All(p =>
            Math.Abs(p.First.Cut - Cy(p.Second.CutFt3)) <= _tolCy && Math.Abs(p.First.Fill - Cy(p.Second.FillFt3)) <= _tolCy
            && Math.Abs(p.First.EndStation - p.First.StartStation - (p.Second.To - p.Second.From)) < 1e-9);
        Add("T2.volumes", ok, string.Join("; ", rows.Select(r => $"L {F(r.EndStation - r.StartStation)} C {F(r.Cut)} F {F(r.Fill)}")),
            string.Join("; ", exp.Select(e => $"L {F(e.To - e.From)} C {F(Cy(e.CutFt3))} F {F(Cy(e.FillFt3))}")), "mixed lengths 500, 800, 500, 200");

        var areas = rows.Select(r => r.Area2D).ToList();
        bool t23 = areas.Zip(FixtureSpec.Expected.T23AreaFt2).All(p => Math.Abs(p.First - p.Second) < 1e-6);
        Add("T23.area", t23, string.Join("/", areas.Select(F)), string.Join("/", FixtureSpec.Expected.T23AreaFt2.Select(F)), "2D area per segment (raw SF; AC display is C2's, T23 unit-tested)");

        var scales = rows.Select(r => o.Views.TryGetValue(r.SegmentId, out var v) ? v.ModelUnitsPerPaperUnit : double.NaN).ToList();
        Add("T2.scales", scales.Count == 4 && scales[0] == 50 && scales[1] == 100, string.Join("/", scales.Select(F)), "50/100/..", "FitStandard {20..100}: 500 ft fits at 50, 800 ft needs 100 (T16)");
        return o;
    }

    private void T25()
    {
        var analyses = new[] { new AnalysisDef(_analysisH, "SEDIMENT", FixtureSpec.Names.EgH, FixtureSpec.Names.FgH) };
        var o = Regen(Def("T25", "0, 500, 1000", preset: ReportPreset.HcfcdSegment, analyses: analyses,
            ohwm: new OhwmSpec(100.0, new Dictionary<Guid, double>()), area: AreaUnit.Acres));
        if (!o.Result.Succeeded) { Add("T25", false, Codes(o), "succeeded", FirstError(o)); return; }
        var rows = o.Result.Segments.OrderBy(r => r.StartStation).ToList();
        bool ok = rows.Count == 2 && rows.All(r => r.Impact is { } i
            && Math.Abs(i.ImpactedLength - 500) <= 0.01 && Math.Abs(i.ImpactedArea - FixtureSpec.Expected.T25AreaFt2) <= 0.01
            && Math.Abs(Cy(i.CutVolume) - Cy(FixtureSpec.Expected.T25VolumeFt3)) <= _tolCy);
        Add("T25", ok, string.Join("; ", rows.Select(r => r.Impact is { } i ? $"L {F(i.ImpactedLength)} A {F(i.ImpactedArea)} ft2 V {F(Cy(i.CutVolume))} CY avg {F(Cy(i.CutVolume) / i.ImpactedLength)}" : "no impact")),
            $"L 500, A {F(FixtureSpec.Expected.T25AreaFt2)} ft2 (0.37 ac), V {F(Cy(FixtureSpec.Expected.T25VolumeFt3))} CY, avg 1.93", "H, OHWM 100, through the pipeline and C5 (HCFCD preset)");
    }

    private void E802(RunOutcome t2)
    {
        var seg = t2.Result.Segments.OrderBy(r => r.StartStation).First().SegmentId;
        string? handle = HandleOf(t2.Record, seg, OwnedRole.Boundary);
        if (handle is null) { Add("E802", false, "no boundary handle", "a stored boundary", "T2 has no boundary to erase"); return; }
        string layer = _settings.ModelOutput.BoundaryLayer;
        WithTransaction(tr =>
        {
            _writers.Model(_doc.Database, tr, _settings).Erase(new OwnedKey(seg, OwnedRole.Boundary), handle);
            SetLayerLocked(tr, layer, true);   // 6.10: owned objects on a locked layer are unlocked and relocked
            return true;
        });
        var o = Regen(Def("T2", "0, 500, 1300, 1800, 2000"));
        string? repaired = HandleOf(o.Record, seg, OwnedRole.Boundary);
        bool pass = o.Result.Succeeded && o.Applied?.Created == 1 && repaired is not null && repaired != handle && o.Result.Problems.Any(p => p.Code == PipelineCodes.Recreated && p.SegmentId == seg);
        Add("E802", pass, $"created {o.Applied?.Created}, handle {handle} -> {repaired}", "1 created, new handle recorded, SV-E802 note", "stored handle erased: object recreated, mapping repaired");

        bool relocked = WithTransaction(tr => IsLayerLocked(tr, layer));
        bool noted = o.Result.Problems.Any(p => p.Code == PipelineCodes.LayerRelocked);
        Add("E504", relocked && noted, $"layer {layer} locked after regen {relocked}, SV-E504 note {noted}", "locked again, note printed", "owned layer locked during a regen that writes: unlocked, written, relocked");
        WithTransaction(tr => { SetLayerLocked(tr, layer, false); return true; });
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

    /// <summary>The fakes refuse geometry use after its transaction, as A1's adapters do; without this the checks below could pass on code that fails on real adapters.</summary>
    private void Lifetime()
    {
        var leaked = DrawingRead.Run(_doc, Engine(), (src, _) => src.Alignment(AlignmentS, null).Value.Geometry);
        string inside = DrawingRead.Run(_doc, Engine(), (src, _) => src.Alignment(AlignmentS, null).Value.Geometry.FormatStation(500));
        string outside;
        try { outside = "no exception: " + leaked.FormatStation(500); }
        catch (InvalidOperationException) { outside = "InvalidOperationException"; }
        Add("LIFETIME", inside.Length > 0 && outside == "InvalidOperationException", $"inside '{inside}', after the transaction: {outside}",
            "works inside, throws after", "fake alignment geometry is valid only inside its transaction (A1 contract)");
    }

    private void Config()
    {
        var json = SheetVolSettings.Default.ToJson();
        var back = SheetVolSettings.FromJson(json, "round trip");
        var typo = SheetVolSettings.FromJson("{ \"regen\": { \"rebuildSourcez\": true } }", "typo");
        var bad = SheetVolSettings.FromJson("{ \"planning\": { \"mitreLimitDeg\": 200 }, \"report\": { \"formats\": { \"volumeDecimals\": -1 } } }", "bad values");
        bool pass = back.Succeeded && back.Value.ToJson() == json && !typo.Succeeded && !bad.Succeeded
                    && bad.Problems.Any(p => p.Code == PipelineCodes.SettingsInvalid) && bad.Problems.Any(p => p.Code == Core.Output.OutputCodes.ConfigInvalid);
        Add("CONFIG", pass,
            $"defaults round-trip {back.Succeeded && back.Value.ToJson() == json}; unknown key refused {!typo.Succeeded}; bad values refused {!bad.Succeeded} with {string.Join(",", bad.Problems.Select(p => p.Code).Distinct())}",
            "round-trips; unknown key and out-of-range values refused with SV-E820 and C2's SV-E534", "settings file: one JSON, validated on load (A7)");
    }

    private void E404()
    {
        var stale = new OutOfDateSurfaceFactory(_adapters, FixtureSpec.Names.FgA);
        var off = _settings with { Regen = _settings.Regen with { RebuildSources = false } };
        var before = Snapshot();
        var o = Regen(Def("T2", "0, 500, 1300, 1800, 2000"), Engine(stale, null, off));
        bool unchanged = Snapshot() == before;
        bool refused = !o.Result.Succeeded && o.FailedAtStep == 1 && o.Result.Problems.Any(p => p.Code == PipelineCodes.SourceOutOfDate && p.Message.Contains(FixtureSpec.Names.FgA, StringComparison.Ordinal)) && unchanged;
        var on = Regen(Def("T2", "0, 500, 1300, 1800, 2000"), Engine(stale));
        bool rebuilt = on.Result.Succeeded && on.Result.Problems.Any(p => p.Code == PipelineCodes.SourceOutOfDate && p.Severity == Severity.Info);
        Add("E404", refused && rebuilt, $"rebuildSources off: failed at step {o.FailedAtStep}, codes {Codes(o)}, drawing unchanged {unchanged}; on: succeeded {on.Result.Succeeded} with rebuild note {rebuilt}",
            "off: fail at step 1 naming FG_A, nothing written; on: rebuilt and computed", "source surface out of date");
    }

    private void E1012()
    {
        var before = Snapshot();
        var o = Regen(Def("T2", "0, 500, 1300, 1800, 2000"), Engine(new BiasedVolumesFactory(_adapters, 1.0)));
        var p = o.Result.Problems.FirstOrDefault(x => x.Code == PipelineCodes.IntegratorMismatch);
        bool pass = !o.Result.Succeeded && o.FailedAtStep == 3 && p is not null && p.Message.Contains("integrator gives cut", StringComparison.Ordinal)
                    && p.Message.Contains("bounded volume gives cut", StringComparison.Ordinal) && Snapshot() == before;
        Add("E1012", pass, p?.Message ?? Codes(o), "run fails at step 3, both numbers printed, nothing written", "bounded volume biased by +1 ft3 against the integrator");
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
        if (_world is null) { Add("T14", false, "no world editor for these bindings", "alignment lengthened", "not run: SelfTestWorlds.Installed is not set"); return; }
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
        var analyses = new[] { new AnalysisDef(_analysisH, "SEDIMENT", FixtureSpec.Names.EgH, FixtureSpec.Names.FgH) };
        var o = Regen(Def("D10", "1000, 1500, 2100, 2400", preset: ReportPreset.HcfcdSegment, analyses: analyses,
            ohwm: new OhwmSpec(100.0, new Dictionary<Guid, double>()), area: AreaUnit.Acres));
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
            "fails at step 5 (apply), drawing equivalent in owned objects, no record", "force an error writing segment 3's boundary");
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

    private void Session()
    {
        using var session = new SheetVolSession(() => Engine());
        var info = session.ActiveDrawing;
        Add("SESSION.drawing", info is { Units: UnitSystem.Imperial }, info is null ? "null" : $"{info.Name} {info.Units}", "the active drawing, Imperial", "ActiveDrawing (E103: runs in Core Console)");
        var al = session.AlignmentNames;
        var su = session.TinSurfaceNames;
        Add("SESSION.lists", al.Contains(AlignmentS) && su.Contains(FixtureSpec.Names.EG) && su.Contains(FixtureSpec.Names.FgA),
            $"alignments {string.Join(",", al)}; surfaces {string.Join(",", su)}", "S; EG, FG_A", "AlignmentNames, TinSurfaceNames");
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
        Add("SESSION.loadRun", loaded is not null && loaded.KnownSegments.Count >= 5, $"KnownSegments {loaded?.KnownSegments.Count}", ">= 5 after T13", "LoadRun round-trips the stored run (RunRecordCodec + C2 RunPersistence)");

        var result = session.RegenerateAsync(Def("T2", "0, 500, 1300, 1800, 2000"), new Progress<double>(), CancellationToken.None).GetAwaiter().GetResult();
        var status = session.GetRunStatus("T2");
        Add("SESSION.regen", result.Succeeded && !status.IsStale && ReferenceEquals(status.LastResult, result),
            $"succeeded {result.Succeeded}, stale after {status.IsStale}, last result is it {ReferenceEquals(status.LastResult, result)} (null {status.LastResult is null}, same run {status.LastResult?.RunName == result.RunName}, same elapsed {status.LastResult?.Elapsed == result.Elapsed})",
            "succeeded, clean, LastResult", "RegenerateAsync in command context; GetRunStatus (E912)");
        var exported = session.ExportAsync(Def("T2", "0, 500, 1300, 1800, 2000"), CancellationToken.None).GetAwaiter().GetResult();
        Add("SESSION.export", exported?.CsvPath is { } c && File.Exists(c), exported?.CsvPath ?? "null", "a CSV path beside the drawing", "ExportAsync writes from the cached result, no drawing writes");
    }
}

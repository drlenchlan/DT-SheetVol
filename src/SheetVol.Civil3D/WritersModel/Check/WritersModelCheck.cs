// A2 headless check: SVWRITERSMODEL drives the model writers and the persistence store on
// FX's Fixture A and alignment S, built in-process, with hand-built Core models (the A1
// adapters are not needed: boundaries come from C1's builder on a straight Core alignment that
// matches S, volumes from a pyramid-table source that matches Fixture A, impacts are typed in).
// Evidence code: every step prints what it observed, one "SVWM <row> PASS|FAIL ..." line per
// claim, and a summary. Run with tools/scripts/writersModel.scr through tools/test-headless.ps1.

using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using SheetVol.Civil3D.Ownership;
using SheetVol.Civil3D.SelfTest;
using SheetVol.Core;
using SheetVol.Core.Geometry;
using SheetVol.Core.Output;
using SheetVol.Core.Tracking;
using SheetVol.Core.Volumes;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;

[assembly: CommandClass(typeof(SheetVol.Civil3D.WritersModel.Check.WritersModelCheck))]

namespace SheetVol.Civil3D.WritersModel.Check;

/// <summary>Paths and check-only values. The product's names all come from config defaults.</summary>
internal static class CheckSettings
{
    public static string OutDir => Path.Combine(Path.GetTempPath(), "sheetvol");
    public static string SavePath => Path.Combine(OutDir, "writersModel-out.dwg");
    public static string CopyPath => Path.Combine(OutDir, "writersModel-copy.dwg");

    /// <summary>Source location of the shipped HCFCD template, used when the staged plugin folder has no Templates\ (the runner stages top-level files only).</summary>
    public const string RepoTemplatePath = "src/SheetVol.Core/Output/Templates/hcfcd-segment-table.json";

    /// <summary>Suffix that turns a configured name into one the drawing does not have (E501, E601 checks).</summary>
    public const string MissingSuffix = "-MISSING";

    /// <summary>Suffix of the second table style the "user" restyles a table to (E502).</summary>
    public const string RestyleSuffix = "-RESTYLED";
}

/// <summary>Alignment S as Core sees it: straight from (0,0) to (2000,0), station = x, left (negative offset) = +y.</summary>
internal sealed class StraightAlignment(string name, double length) : IAlignmentGeometry
{
    public string Name { get; } = name;
    public double StartStation => 0;
    public double EndStation => length;
    public IReadOnlyList<AlignSeg> Segments { get; } = [new AlignSeg(SegKind.Line, 0, length, new Pt(0, 0), new Pt(length, 0), 0, 0)];
    public Pt PointAt(double rawStation, double offset) => new(rawStation, -offset);

    public string FormatStation(double rawStation)
    {
        double hundreds = Math.Floor(rawStation / 100);
        double rest = rawStation - hundreds * 100;
        string tail = Math.Abs(rest - Math.Round(rest)) < 1e-9 ? Math.Round(rest).ToString("00", CultureInfo.InvariantCulture) : rest.ToString("00.00", CultureInfo.InvariantCulture);
        return hundreds.ToString("0", CultureInfo.InvariantCulture) + "+" + tail;
    }

    public bool TryParseStation(string text, out double rawStation, out string? error)
    {
        var parts = text.Split('+');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double h)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double r))
        {
            rawStation = h * 100 + r; error = null; return true;
        }
        rawStation = 0; error = $"'{text}' is not a station."; return false;
    }
}

/// <summary>Fixture A's pyramids as point volumes (ft³): a polygon's bounded volume is the sum of the pyramids whose centre x lies in its x range.</summary>
internal sealed class PyramidVolumeSource(IReadOnlyList<(double X, double Cut, double Fill)> pyramids) : IVolumeSource
{
    public RawVolume SurfaceTotals => new(pyramids.Sum(p => p.Cut), pyramids.Sum(p => p.Fill));

    public RawVolume Bounded(IReadOnlyList<Pt> closedPolygon)
    {
        double min = closedPolygon.Min(p => p.X), max = closedPolygon.Max(p => p.X);
        var inside = pyramids.Where(p => p.X >= min && p.X < max).ToList();
        return new RawVolume(inside.Sum(p => p.Cut), inside.Sum(p => p.Fill));
    }

    public double Area2D(IReadOnlyList<Pt> closedPolygon) => PlanArea.Polygon(closedPolygon);
}

/// <summary>Created, modified and erased objects seen through the database events during one apply.</summary>
internal sealed class EventLog : IDisposable
{
    private readonly Database _db;
    public List<string> Appended { get; } = [];
    public List<string> Modified { get; } = [];
    public List<string> Erased { get; } = [];

    public EventLog(Database db)
    {
        _db = db;
        db.ObjectAppended += OnAppended;
        db.ObjectModified += OnModified;
        db.ObjectErased += OnErased;
    }

    private void OnAppended(object sender, ObjectEventArgs e) => Appended.Add(Name(e.DBObject));
    private void OnModified(object sender, ObjectEventArgs e) => Modified.Add(Name(e.DBObject));
    private void OnErased(object sender, ObjectErasedEventArgs e) { if (e.Erased) Erased.Add(Name(e.DBObject)); }
    private static string Name(DBObject o) => $"{o.GetType().Name}:{o.Handle}";

    public string Summary => $"appended={Appended.Count} modified={Modified.Count} erased={Erased.Count}";

    public void Dispose()
    {
        _db.ObjectAppended -= OnAppended;
        _db.ObjectModified -= OnModified;
        _db.ObjectErased -= OnErased;
    }
}

/// <summary>Result of one regen through the writers and the store.</summary>
internal sealed record ApplyOutcome(ModelWriteResult Result, RunStoreWriteResult? Store, EventLog Events, RunStoreReadResult Read)
{
    public IReadOnlyDictionary<OwnedKey, string> Handles => Result.Objects.ToDictionary(o => o.Key, o => o.Handle);
    public int Count(ChangeKind kind) => Result.Changes.Count(c => c.Kind == kind);
    public int Count(ChangeKind kind, OwnedRole role) => Result.Changes.Count(c => c.Kind == kind && c.Key.Role == role);
    public string Kinds => string.Join(" ", Result.Changes.GroupBy(c => c.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"));
    public bool Has(string code) => Result.Problems.Any(p => p.Code == code);
}

/// <summary>SVWRITERSMODEL: the A2 headless check.</summary>
public static partial class WritersModelCheck
{
    private static Editor? _ed;
    private static int _pass, _fail;
    private static Database _db = null!;
    private static readonly ModelWritersConfig Cfg = ModelWritersConfig.Default;
    private static readonly ReportConfig Report = ReportConfig.Default;
    private static readonly DrawingUnits Units = new(UnitSystem.Imperial, 1.0);
    private static readonly GeometryOptions Geom = GeometryOptions.ForUnitScale(1.0);
    private static readonly StraightAlignment AlignmentS = new(FixtureSpec.Names.AlignmentS, FixtureSpec.S.Length);
    private const double Scale = 50.0;   // run view scale, model units per paper unit (ViewOptions default)

    internal static void Say(string s)
    {
        if (_ed is not null) _ed.WriteMessage("\nSVWM " + s);
        else Console.WriteLine("SVWM " + s);
    }

    internal static bool Check(string row, bool ok, string evidence)
    {
        if (ok) _pass++; else _fail++;
        Say($"{row} {(ok ? "PASS" : "FAIL")} {evidence}");
        return ok;
    }

    [CommandMethod("SVWRITERSMODEL", CommandFlags.Modal)]
    public static void Run()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null) { Console.WriteLine($"{ModelWriterCodes.CheckFailed} SVWRITERSMODEL: no active document"); return; }
        _ed = doc.Editor;
        _db = doc.Database;
        _pass = _fail = 0;
        string step = "setup";
        try
        {
            Say("===== SVWRITERSMODEL begin =====");
            Setup();
            step = "standard"; StandardPhase();
            step = "bulge"; BulgePhase();
            step = "hcfcd"; HcfcdPhase();
            step = "outline"; OutlinePhase();
            step = "layout"; LayoutPhase();
            step = "store"; StorePhase();
            Say($"SUMMARY pass={_pass} fail={_fail}");
            Say("===== SVWRITERSMODEL end =====");
        }
        catch (System.Exception ex)
        {
            Say($"{ModelWriterCodes.CheckFailed} SVWRITERSMODEL step '{step}' failed: {ex.GetType().Name}: {ex.Message}");
            Say(ex.StackTrace ?? "");
            Say($"SUMMARY pass={_pass} fail={_fail + 1} (stopped at '{step}')");
            Say("===== SVWRITERSMODEL end (FAILED) =====");
        }
    }

    // ------------------------------------------------------------------ setup

    private static HcfcdTemplate _template = null!;
    private static ObjectId _alignmentId, _egId, _fgaId;

    private static void Setup()
    {
        var a = Fixtures.CreateA(_db);
        var s = Fixtures.CreateAlignmentS(_db);
        Say($"setup Fixture A: base {a.BaseName} ({(a.BaseReused ? "reused" : "created")}), comparison {a.ComparisonName}; alignment {s.Name} stations {F(s.StartStation)}..{F(s.EndStation)}");
        _alignmentId = s.Id;
        _egId = a.BaseId;
        _fgaId = a.ComparisonId;

        using (var tr = _db.TransactionManager.StartTransaction())
        {
            // The Core alignment must match the drawing's S where the writers use it.
            var al = (CivAlignment)tr.GetObject(_alignmentId, OpenMode.ForRead);
            double maxDiff = 0;
            foreach (var (st, off) in new[] { (0.0, 0.0), (500.0, -100.0), (1250.0, 100.0), (2000.0, -100.0) })
            {
                double e = 0, n = 0;
                al.PointLocation(st, off, ref e, ref n);
                Pt core = AlignmentS.PointAt(st, off);
                maxDiff = Math.Max(maxDiff, Math.Max(Math.Abs(core.X - e), Math.Abs(core.Y - n)));
            }
            Check("setup", maxDiff < 1e-9, $"Core alignment S matches Civil 3D PointLocation at 4 station/offset pairs, max difference {maxDiff:E1}");

            // What the LAN template would provide: the configured layers and table style.
            foreach (string layer in new[] { Cfg.BoundaryLayer, Cfg.ImpactOutlineLayer, Cfg.MatchMarks.Layer, Report.SegmentTable.Style.Layer, Report.MasterTable.Style.Layer, Report.Hcfcd.Style.Layer }.Distinct())
                EnsureLayer(tr, layer);
            foreach (string style in new[] { Report.SegmentTable.Style.TableStyle, Report.MasterTable.Style.TableStyle, Report.Hcfcd.Style.TableStyle, Report.SegmentTable.Style.TableStyle + CheckSettings.RestyleSuffix }.Distinct())
                EnsureTableStyle(tr, style);
            tr.Commit();
        }
        Say($"setup drawing now has layers {Cfg.BoundaryLayer}, {Cfg.ImpactOutlineLayer}, {Cfg.MatchMarks.Layer}, {Report.SegmentTable.Style.Layer} and table styles {Report.SegmentTable.Style.TableStyle}, {Report.SegmentTable.Style.TableStyle + CheckSettings.RestyleSuffix} (standing in for the LAN template)");

        string pluginDir = Path.GetDirectoryName(typeof(ModelWriter).Assembly.Location)!;
        var t = HcfcdTemplate.Load(Report.Hcfcd.TemplatePath, pluginDir);
        string from = HcfcdTemplate.ResolvePath(Report.Hcfcd.TemplatePath, pluginDir);
        if (!t.Succeeded)
        {
            string repo = Environment.GetEnvironmentVariable("SHEETVOL_REPO") ?? throw new InvalidOperationException("HCFCD template not staged and SHEETVOL_REPO not set.");
            from = Path.Combine(repo, CheckSettings.RepoTemplatePath);
            Say($"setup HCFCD template not in the staged plugin folder ({HcfcdTemplate.ResolvePath(Report.Hcfcd.TemplatePath, pluginDir)}); loading the shipped file from the repo");
            t = HcfcdTemplate.Load(from, "");
        }
        _template = t.Value;
        Say($"setup HCFCD template '{_template.Definition.Name}' from {from}: {_template.Definition.Rows.Count} rows, placeholder={_template.Definition.Placeholder}");
    }

    private static void EnsureLayer(Transaction tr, string name)
    {
        var lt = (LayerTable)tr.GetObject(_db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(name)) return;
        lt.UpgradeOpen();
        var rec = new LayerTableRecord { Name = name };
        lt.Add(rec);
        tr.AddNewlyCreatedDBObject(rec, true);
    }

    private static void EnsureTableStyle(Transaction tr, string name)
    {
        var dict = (DBDictionary)tr.GetObject(_db.TableStyleDictionaryId, OpenMode.ForRead);
        if (dict.Contains(name)) return;
        dict.UpgradeOpen();
        var ts = new TableStyle();          // Name throws eOwnerNotSet until the dictionary owns the style; the key names it
        dict.SetAt(name, ts);
        tr.AddNewlyCreatedDBObject(ts, true);
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>One regen of a run: read the store, apply the plan, write the record, commit (or abort).</summary>
    private static ApplyOutcome Regen(RunDefinition run, ModelWritePlan plan, IReadOnlyList<SourceRef>? sources = null, bool commit = true,
        ModelWritersConfig? cfg = null, ReportConfig? report = null)
    {
        var events = new EventLog(_db);
        try
        {
            using var tr = _db.TransactionManager.StartTransaction();
            var read = RunStore.Read(tr, _db, run.Name, report ?? Report);
            var ctx = new ModelWriteContext(tr, _db, run.Name, cfg ?? Cfg, report ?? Report, Units);
            var result = ModelWriter.Apply(ctx, plan, read);
            RunStoreWriteResult? store = null;
            if (result.Succeeded && commit)
            {
                var others = read.Record?.Objects.Where(o => !ModelWriteResult.Roles.Contains(o.Role)) ?? [];
                var record = new RunRecord(run, result.Objects.Concat(others).ToList(), sources ?? read.Record?.Sources ?? [],
                    read.Record?.Orphans ?? [], read.Record?.Fingerprint);
                store = RunStore.Write(tr, _db, record, DateTime.UtcNow, report ?? Report);
                tr.Commit();
            }
            else tr.Abort();
            return new ApplyOutcome(result, store, events, read);
        }
        finally
        {
            events.Dispose();
        }
    }

    private static void UserEdit(string what, Action<Transaction> edit)
    {
        using var tr = _db.TransactionManager.StartTransaction();
        edit(tr);
        tr.Commit();
        Say($"user edit: {what}");
    }

    private static T Open<T>(Transaction tr, string handle) where T : DBObject
        => (T)tr.GetObject(WritersModel.Handles.Resolve(_db, handle), OpenMode.ForRead);

    private static bool Alive(string handle) => !WritersModel.Handles.Resolve(_db, handle).IsNull;

    private static RunDefinition Def(string name, IReadOnlyList<SegmentDef> segments, IReadOnlyList<AnalysisDef> analyses,
        ReportPreset preset = ReportPreset.Standard, AreaUnit areaUnit = AreaUnit.SquareFeet)
        => new(name, FixtureSpec.Names.AlignmentS, SegmentDriver.Stations, "check", null, -100, 100, analyses, [], segments,
               new Dictionary<Guid, SegmentOverrides>(), new ViewOptions(), preset, areaUnit);

    private static Guid G(int run, int n) => new($"00000000-0000-0000-{run:0000}-{n:000000000000}");

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Problems(ApplyOutcome o, params string[] codes)
        => string.Join(" | ", o.Result.Problems.Where(p => codes.Length == 0 || codes.Contains(p.Code)).Select(p => $"{p.Code} {p.Severity}: {p.Message}"));

    /// <summary>Plain text of a table row, cells joined with " | ".</summary>
    private static string RowText(Table t, int r) => string.Join(" | ", Enumerable.Range(0, t.Columns.Count).Select(c => TableRenderer.Plain(t, r, c)));

    /// <summary>The first row whose label column starts with <paramref name="label"/>, or -1.</summary>
    private static int FindRow(Table t, string label, int labelColumn = 0)
    {
        for (int r = 0; r < t.Rows.Count; r++)
            if (TableRenderer.Plain(t, r, labelColumn).Trim().StartsWith(label, StringComparison.OrdinalIgnoreCase)) return r;
        return -1;
    }

    /// <summary>Every entity carrying a SheetVol ownership tag, by handle, with a digest of its content (T20).</summary>
    private static SortedDictionary<string, string> Snapshot()
    {
        var snap = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using var tr = _db.TransactionManager.StartTransaction();
        var bt = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId btrId in bt)
        {
            var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
            if (!btr.IsLayout) continue;
            foreach (ObjectId id in btr)
            {
                var obj = tr.GetObject(id, OpenMode.ForRead);
                if (OwnerTag.Read(tr, obj, Cfg.OwnerTagKey) is null) continue;
                snap[obj.Handle.ToString()] = obj switch
                {
                    Polyline pl => "PL " + string.Join(";", Enumerable.Range(0, pl.NumberOfVertices).Select(i => $"{pl.GetPoint2dAt(i)}/{pl.GetBulgeAt(i):R}")) + " " + pl.Layer,
                    Line ln => $"LN {ln.StartPoint} {ln.EndPoint} {ln.Layer}",
                    MText mt => $"MT {mt.Contents} {mt.Location} {mt.Direction} {mt.TextHeight:R}",
                    Table t => $"TB {t.Position} {t.Rows.Count}x{t.Columns.Count} " + string.Join("/", Enumerable.Range(0, t.Rows.Count).Select(r => RowText(t, r))),
                    _ => obj.GetType().Name,
                };
            }
        }
        var nod = (DBDictionary)tr.GetObject(_db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (nod.Contains(Report.Persistence.DictionaryName))
            foreach (DBDictionaryEntry e in (DBDictionary)tr.GetObject(nod.GetAt(Report.Persistence.DictionaryName), OpenMode.ForRead))
                if (tr.GetObject(e.Value, OpenMode.ForRead) is Xrecord xr)
                    snap["store:" + e.Key] = string.Join("|", (xr.Data?.AsArray() ?? []).Select(v => $"{v.TypeCode}={v.Value}"));
        tr.Commit();
        return snap;
    }

    private static ObjectId AlignmentIdByName(Transaction tr, string name)
    {
        var civ = CivilDocument.GetCivilDocument(_db);
        foreach (ObjectId id in civ.GetAlignmentIds())
            if (tr.GetObject(id, OpenMode.ForRead) is CivAlignment al && al.Name == name) return id;
        return ObjectId.Null;
    }
}

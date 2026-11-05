// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4 (SVSELFTEST driver).
//
// In-memory stand-ins for A1-A3 so the regen pipeline runs end to end before they merge
// ("Develops against fakes until A1-A3 merge"). They are deliberately honest about what
// they are:
//   - FakeWorld holds the section 8 fixture world in memory: alignment S as contract
//     geometry, and EG, FG_A, EG_H, FG_H as C5's exact triangle lists. Volumes come from
//     C5's overlay integrator, so the numbers are analytic, not invented.
//   - The fake writers and the fake store keep one Xrecord per owned object and per run in
//     the REAL drawing, inside the pipeline's transaction. Handles are real handles, and an
//     aborted regen really rolls back (T20). They never touch the product's dictionary.
//   - Decorators (FailOnWrite, BiasedVolumes, OutOfDateSurface) wrap ANY factory, fake or
//     real, so the failure tests keep working after the real A1-A3 drop in.
// None of this is product code. EngineBindings.Current points here only after the
// test-only "SVSELFTEST BindFakes", which refuses when real bindings are installed.

using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Civil3D.Session;
using SheetVol.Core;
using SheetVol.Core.Planning;
using SheetVol.Core.Tracking;
using SheetVol.Core.Volumes;
using SheetVol.Core.Volumes.Integrator;
using SheetVol.Core.Volumes.Integrator.Fixtures;
using CoreSurfaces = SheetVol.Core.Volumes.Integrator.Fixtures.FixtureSurfaces;

namespace SheetVol.Civil3D.SelfTest;

/// <summary>Edits the self-test makes to the source world: T12 moves a pyramid apex, T14 lengthens the alignment.</summary>
public interface ISelfTestWorld
{
    /// <summary>T12: moves the apex of the Fixture A pyramid centred at x = <paramref name="centerX"/> to <paramref name="apexZ"/> and rebuilds the surface.</summary>
    void SetPyramidApex(string surfaceName, double centerX, double apexZ);

    /// <summary>T14: extends the straight alignment so it ends at raw station <paramref name="newEndStation"/>.</summary>
    void LengthenAlignment(string alignmentName, double newEndStation);
}

/// <summary>The world edits for the installed (real) adapters. Set by the orchestrator at integration; null means T12 and T14 are reported as not run.</summary>
public static class SelfTestWorlds
{
    /// <summary>World edits against the real drawing objects, or null.</summary>
    public static ISelfTestWorld? Installed { get; set; }
}

// ============================================================================
// The in-memory world
// ============================================================================

/// <summary>Section 8 fixture world in memory.</summary>
public sealed class FakeWorld : ISelfTestWorld
{
    private int _nextHandle = 0xF000;

    /// <summary>Alignments by name.</summary>
    public Dictionary<string, IAlignmentGeometry> Alignments { get; } = new(StringComparer.Ordinal);

    /// <summary>TIN surfaces by name.</summary>
    public Dictionary<string, TriangleListSource> Surfaces { get; } = new(StringComparer.Ordinal);

    /// <summary>Owned volume surfaces created so far (name to handle).</summary>
    public Dictionary<string, string> VolumeSurfaces { get; } = new(StringComparer.Ordinal);

    /// <summary>Fake handles of sources.</summary>
    public Dictionary<string, string> Handles { get; } = new(StringComparer.Ordinal);

    /// <summary>Drawing units: feet.</summary>
    public DrawingUnits Units { get; set; } = new(UnitSystem.Imperial, 1.0);

    /// <summary>The fixture world: alignment S, EG, FG_A, EG_H, FG_H.</summary>
    public static FakeWorld Fixtures()
    {
        var w = new FakeWorld();
        w.Alignments[FixtureSpec.Names.AlignmentS] = new FakeLineAlignment(FixtureSpec.Names.AlignmentS, FixtureSpec.S.StartX, FixtureSpec.S.EndX);
        w.Surfaces[FixtureSpec.Names.EG] = CoreSurfaces.Eg();
        w.Surfaces[FixtureSpec.Names.FgA] = CoreSurfaces.FgA();
        w.Surfaces[FixtureSpec.Names.EgH] = CoreSurfaces.EgH();
        w.Surfaces[FixtureSpec.Names.FgH] = CoreSurfaces.FgH();
        return w;
    }

    /// <summary>A stable fake handle per name.</summary>
    public string HandleOf(string name)
    {
        if (!Handles.TryGetValue(name, out var h)) Handles[name] = h = (_nextHandle++).ToString("X", CultureInfo.InvariantCulture);
        return h;
    }

    /// <inheritdoc />
    public void SetPyramidApex(string surfaceName, double centerX, double apexZ)
    {
        var tris = Surfaces[surfaceName].All.Select(t => new Tri3(Move(t.A), Move(t.B), Move(t.C))).ToList();
        Surfaces[surfaceName] = new TriangleListSource(surfaceName, tris);

        Pt3 Move(Pt3 p) => p.X == centerX && p.Y == 0 && p.Z != CoreSurfaces.EgElevation ? p with { Z = apexZ } : p;
    }

    /// <inheritdoc />
    public void LengthenAlignment(string alignmentName, double newEndStation)
    {
        var a = Alignments[alignmentName];
        Alignments[alignmentName] = new FakeLineAlignment(alignmentName, a.StartStation, newEndStation);
    }
}

/// <summary>
/// A straight alignment along +X from (start, 0) to (end, 0), station = x. Stations display as
/// <c>5+00.00</c>. Contract conventions: negative offset = left = +Y.
/// </summary>
public sealed class FakeLineAlignment : IAlignmentGeometry
{
    /// <summary>Creates the alignment.</summary>
    public FakeLineAlignment(string name, double start, double end)
    {
        Name = name;
        StartStation = start;
        EndStation = end;
        Segments = [new AlignSeg(SegKind.Line, start, end, new Pt(start, 0), new Pt(end, 0), 0, 0)];
    }

    /// <inheritdoc />
    public string Name { get; }
    /// <inheritdoc />
    public double StartStation { get; }
    /// <inheritdoc />
    public double EndStation { get; }
    /// <inheritdoc />
    public IReadOnlyList<AlignSeg> Segments { get; }

    /// <inheritdoc />
    public Pt PointAt(double rawStation, double offset)
    {
        if (rawStation < StartStation - 1e-9 || rawStation > EndStation + 1e-9)
            throw new ArgumentOutOfRangeException(nameof(rawStation), rawStation, $"Station is outside alignment '{Name}'.");
        return new Pt(rawStation, -offset);
    }

    /// <inheritdoc />
    public string FormatStation(double rawStation)
    {
        long cents = (long)Math.Round(Math.Abs(rawStation) * 100, MidpointRounding.AwayFromZero);
        long major = cents / 10000, minor = cents % 10000;
        string sign = rawStation < 0 && cents != 0 ? "-" : "";
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{major}+{minor / 100:00}.{minor % 100:00}");
    }

    /// <inheritdoc />
    public bool TryParseStation(string text, out double rawStation, out string? error)
    {
        error = null;
        string t = text.Trim();
        int plus = t.IndexOf('+');
        if (plus > 0
            && double.TryParse(t[..plus], NumberStyles.Float, CultureInfo.InvariantCulture, out double major)
            && double.TryParse(t[(plus + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double minor)
            && minor < 100)
        {
            rawStation = (major < 0 || t.StartsWith('-') ? -1 : 1) * (Math.Abs(major) * 100 + minor);
            return true;
        }
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out rawStation)) return true;
        error = $"'{text}' is not a station.";
        return false;
    }
}

/// <summary>Bounded volumes from C5's integrator over two triangle lists: analytic on the fixtures.</summary>
public sealed class IntegratorVolumeSource : IVolumeSource
{
    private readonly TriangleListSource _base, _comparison;
    private readonly double _cutBias;

    /// <summary>Creates the source; <paramref name="cutBias"/> is added to every bounded cut (E1012 test).</summary>
    public IntegratorVolumeSource(TriangleListSource baseSurface, TriangleListSource comparison, double cutBias = 0)
    {
        _base = baseSurface;
        _comparison = comparison;
        _cutBias = cutBias;
    }

    /// <inheritdoc />
    public RawVolume SurfaceTotals
    {
        get
        {
            var pts = _base.All.Concat(_comparison.All).SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
            double x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
            var r = OverlayIntegrator.IntegrateBand(_base, _comparison, [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1), new(x0, y0)]);
            return r.Totals is { } t ? new RawVolume(t.Cut, t.Fill) : new RawVolume(0, 0);
        }
    }

    /// <inheritdoc />
    public RawVolume Bounded(IReadOnlyList<Pt> closedPolygon)
    {
        var r = OverlayIntegrator.IntegrateBand(_base, _comparison, closedPolygon);
        return r.Totals is { } t ? new RawVolume(t.Cut + _cutBias, t.Fill) : new RawVolume(0, 0);   // wholly outside: 0/0, as P4
    }

    /// <inheritdoc />
    public double Area2D(IReadOnlyList<Pt> closedPolygon) => OverlayIntegrator.IntegrateBand(_base, _comparison, closedPolygon).Totals?.PlanArea ?? 0;
}

// ============================================================================
// Fake A1
// ============================================================================

/// <summary>
/// Adapters over a <see cref="FakeWorld"/>. Like A1's real adapters, the geometry and volume
/// sources they return work only while the transaction they were opened in is open: each holds an
/// object opened in that transaction and throws once AutoCAD has disposed it (A1 AlignmentAdapter
/// GuardOpen). So the self-test fails on any use of geometry after a commit, as the real thing would.
/// </summary>
public sealed class FakeAdapterFactory(FakeWorld world) : IAdapterFactory
{
    /// <inheritdoc />
    public IDrawingSources Open(Database db, Transaction tr, SheetVolSettings settings) => new Sources(world, new TxLife(tr));

    /// <summary>
    /// The opening transaction's lifetime. MEASURED 2026-11-05: an object wrapper from tr.GetObject is NOT
    /// disposed when its transaction commits, so IsDisposed on it proves nothing. Over = the transaction
    /// was disposed, or it was committed (the active-transaction count fell below its depth).
    /// </summary>
    private sealed class TxLife(Transaction tr)
    {
        private readonly int _depth = tr.TransactionManager.NumberOfActiveTransactions;

        public bool IsOver => tr.IsDisposed || tr.TransactionManager.NumberOfActiveTransactions < _depth;
    }

    private static void Guard(TxLife anchor, string what)
    {
        if (anchor.IsOver)
            throw new InvalidOperationException($"The fake {what} is used after its transaction ended; open a new adapter inside the current transaction.");
    }

    private sealed class GuardedAlignment(IAlignmentGeometry a, TxLife anchor) : IAlignmentGeometry
    {
        public string Name => a.Name;
        public double StartStation => a.StartStation;
        public double EndStation => a.EndStation;
        public IReadOnlyList<AlignSeg> Segments => a.Segments;
        public Pt PointAt(double rawStation, double offset) { Guard(anchor, $"alignment '{a.Name}'"); return a.PointAt(rawStation, offset); }
        public string FormatStation(double rawStation) { Guard(anchor, $"alignment '{a.Name}'"); return a.FormatStation(rawStation); }
        public bool TryParseStation(string text, out double rawStation, out string? error) => a.TryParseStation(text, out rawStation, out error);
    }

    private sealed class GuardedVolumes(IVolumeSource v, TxLife anchor) : IVolumeSource
    {
        public RawVolume SurfaceTotals { get { Guard(anchor, "volume source"); return v.SurfaceTotals; } }
        public RawVolume Bounded(IReadOnlyList<Pt> p) { Guard(anchor, "volume source"); return v.Bounded(p); }
        public double Area2D(IReadOnlyList<Pt> p) { Guard(anchor, "volume source"); return v.Area2D(p); }
    }

    private sealed class Sources(FakeWorld w, TxLife anchor) : IDrawingSources
    {
        public Outcome<DrawingUnits> Units() => Outcome<DrawingUnits>.Ok(w.Units);

        public IReadOnlyList<string> AlignmentNames() => w.Alignments.Keys.Order(StringComparer.Ordinal).ToList();

        public IReadOnlyList<string> TinSurfaceNames() => w.Surfaces.Keys.Order(StringComparer.Ordinal).ToList();

        public Outcome<AlignmentSource> Alignment(string name, string? storedHandle) =>
            w.Alignments.TryGetValue(name, out var a)
                ? Outcome<AlignmentSource>.Ok(new AlignmentSource(new GuardedAlignment(a, anchor), w.HandleOf("alignment:" + name), false))
                : Outcome<AlignmentSource>.Fail(new Problem(PipelineCodes.AlignmentMissing, Severity.Error,
                    $"Alignment '{name}' was not found in the drawing; nothing was read or written.", Fix: "Restore the alignment or point the run at another with SVDEFINE."));

        public Outcome<ManualOutline> ManualOutline(string polylineHandle) =>
            Outcome<ManualOutline>.Fail(new Problem(ProblemCodes.E311, Severity.Error, $"Polyline {polylineHandle} was not found."));

        public Outcome<SurfaceSource> Surface(string name, string? storedHandle, bool rebuildIfOutOfDate) =>
            w.Surfaces.TryGetValue(name, out var s)
                ? Outcome<SurfaceSource>.Ok(new SurfaceSource(name, w.HandleOf("surface:" + name), s, false, false))
                : Outcome<SurfaceSource>.Fail(new Problem("SV-E402", Severity.Error, $"TIN surface '{name}' was not found."));

        public Outcome<OwnedVolumeSurface> EnsureVolumeSurface(OwnedSurfacePlan plan, string? storedHandle)
        {
            bool created = !w.VolumeSurfaces.ContainsKey(plan.Name);
            string handle = w.VolumeSurfaces.TryGetValue(plan.Name, out var h) ? h : w.VolumeSurfaces[plan.Name] = w.HandleOf("volume:" + plan.Name);
            return Outcome<OwnedVolumeSurface>.Ok(new OwnedVolumeSurface(plan.Name, handle,
                new GuardedVolumes(new IntegratorVolumeSource(w.Surfaces[plan.BaseSurface], w.Surfaces[plan.ComparisonSurface]), anchor), created));
        }

        public AnalysisSurfaceState? ReadVolumeSurfaceState(Guid analysisId, OwnedSurfacePlan plan, string? storedHandle) =>
            w.VolumeSurfaces.ContainsKey(plan.Name) && w.Surfaces.ContainsKey(plan.BaseSurface) && w.Surfaces.ContainsKey(plan.ComparisonSurface)
                ? new AnalysisSurfaceState(analysisId, false, new IntegratorVolumeSource(w.Surfaces[plan.BaseSurface], w.Surfaces[plan.ComparisonSurface]).SurfaceTotals)
                : null;
    }
}

// ============================================================================
// Fake A2 / A3: one Xrecord per owned object in the real drawing
// ============================================================================

/// <summary>A fake owned object as stored: role, key text, signature, name (layouts).</summary>
public sealed record FakeObject(string Handle, OwnedKey Key, string Signature, string Name);

/// <summary>Xrecord-backed object table, inside the caller's transaction.</summary>
public sealed class FakeObjectTable(Database db, Transaction tr, string dictionaryName)
{
    /// <summary>Every live fake object.</summary>
    public List<FakeObject> All()
    {
        var dict = Dictionary(create: false);
        var list = new List<FakeObject>();
        if (dict is null) return list;
        foreach (DBDictionaryEntry e in dict)
        {
            if (e.Value.IsErased) continue;
            if (tr.GetObject(e.Value, OpenMode.ForRead) is Xrecord x && Parse(x) is { } o) list.Add(o);
        }
        return list;
    }

    /// <summary>The object at a handle, or null when missing or erased.</summary>
    public FakeObject? Get(string? handle)
    {
        if (handle is null || !long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h)) return null;
        if (!db.TryGetObjectId(new Handle(h), out ObjectId id) || id.IsNull || id.IsErased) return null;
        return tr.GetObject(id, OpenMode.ForRead) is Xrecord x ? Parse(x) : null;
    }

    /// <summary>Creates an object; returns its handle.</summary>
    public string Create(OwnedKey key, string signature, string name)
    {
        var dict = Dictionary(create: true)!;
        var x = new Xrecord { Data = Data(key, signature, name) };
        dict.UpgradeOpen();
        dict.SetAt(Guid.NewGuid().ToString("N"), x);
        tr.AddNewlyCreatedDBObject(x, true);
        return x.Handle.ToString();
    }

    /// <summary>Updates signature and name in place (handle kept).</summary>
    public void Update(string handle, OwnedKey key, string signature, string name)
    {
        var x = Open(handle);
        x.UpgradeOpen();
        x.Data = Data(key, signature, name);
    }

    /// <summary>Erases an object.</summary>
    public void Erase(string handle)
    {
        var x = Open(handle);
        x.UpgradeOpen();
        x.Erase();
    }

    private Xrecord Open(string handle)
    {
        long h = long.Parse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (!db.TryGetObjectId(new Handle(h), out ObjectId id) || id.IsErased) throw new InvalidOperationException($"Fake object {handle} does not exist.");
        return (Xrecord)tr.GetObject(id, OpenMode.ForRead);
    }

    private DBDictionary? Dictionary(bool create)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (nod.Contains(dictionaryName)) return (DBDictionary)tr.GetObject(nod.GetAt(dictionaryName), OpenMode.ForRead);
        if (!create) return null;
        nod.UpgradeOpen();
        var d = new DBDictionary();
        nod.SetAt(dictionaryName, d);
        tr.AddNewlyCreatedDBObject(d, true);
        return d;
    }

    private static ResultBuffer Data(OwnedKey key, string signature, string name) => new(
        new TypedValue((int)DxfCode.Text, key.SegmentId.ToString("D")),
        new TypedValue((int)DxfCode.Text, key.Role.ToString()),
        new TypedValue((int)DxfCode.Text, key.Qualifier.Length == 0 ? "-" : key.Qualifier),
        new TypedValue((int)DxfCode.Text, signature),
        new TypedValue((int)DxfCode.Text, name.Length == 0 ? "-" : name));

    private static FakeObject? Parse(Xrecord x)
    {
        var v = x.Data?.AsArray();
        if (v is null || v.Length < 5) return null;
        string S(int i) => v[i].Value as string ?? "";
        string q = S(2) == "-" ? "" : S(2), n = S(4) == "-" ? "" : S(4);
        return new FakeObject(x.Handle.ToString(), new OwnedKey(Guid.Parse(S(0)), Enum.Parse<OwnedRole>(S(1)), q), S(3), n);
    }
}

/// <summary>Writers and store backed by <see cref="FakeObjectTable"/>; records every call in <see cref="Log"/>.</summary>
public sealed class FakeWriterFactory(TemplateInfo template) : IWriterFactory
{
    /// <summary>Every writer call, in order, as "verb role qualifier handle". Rolled-back calls stay listed: compare drawing state, not the log, for atomicity.</summary>
    public List<string> Log { get; } = [];

    /// <inheritdoc />
    public IModelWriter Model(Database db, Transaction tr, SheetVolSettings settings) =>
        new Writer(new FakeObjectTable(db, tr, settings.SelfTest.FakeObjectDictionary), Log, db, tr, template,
            [OwnedRole.Boundary, OwnedRole.MatchMark, OwnedRole.ModelTable, OwnedRole.MasterTable, OwnedRole.ImpactOutline]);

    /// <inheritdoc />
    public IPaperWriter Paper(Database db, Transaction tr, SheetVolSettings settings) =>
        new Writer(new FakeObjectTable(db, tr, settings.SelfTest.FakeObjectDictionary), Log, db, tr, template,
            [OwnedRole.Layout, OwnedRole.Viewport, OwnedRole.Clip, OwnedRole.PaperTable, OwnedRole.TitleAttributes]);

    /// <inheritdoc />
    public IPersistenceStore Store(Database db, Transaction tr, SheetVolSettings settings) => new FakeStore(db, tr, settings);

    private sealed class Writer(FakeObjectTable table, List<string> log, Database db, Transaction tr, TemplateInfo template, OwnedRole[] roles)
        : IModelWriter, IPaperWriter
    {
        public IReadOnlySet<OwnedRole> Roles { get; } = roles.ToHashSet();

        public IReadOnlyList<Problem> Preflight(IReadOnlyList<OwnedObject> planned) => [];

        public StoredObject Inspect(OwnedKey key, string? handle)
        {
            var o = table.Get(handle);
            return new StoredObject(key, handle, o is not null && o.Key == key, o?.Signature);
        }

        public WriteResult Write(OwnedObject planned, string? existingHandle)
        {
            string name = planned is LayoutObject l ? l.Name : "";
            if (existingHandle is null)
            {
                string h = table.Create(planned.Key, planned.Signature, name);
                log.Add($"create {planned.Key.Role} {planned.Key.Qualifier} {h}");
                return new WriteResult(h, true, []);
            }
            table.Update(existingHandle, planned.Key, planned.Signature, name);
            log.Add($"update {planned.Key.Role} {planned.Key.Qualifier} {existingHandle}");
            return new WriteResult(existingHandle, true, []);
        }

        public void Erase(OwnedKey key, string handle)
        {
            table.Erase(handle);
            log.Add($"erase {key.Role} {key.Qualifier} {handle}");
        }

        public Outcome<TemplateInfo> Template() => Outcome<TemplateInfo>.Ok(template);

        public IReadOnlyList<LayoutRef> Layouts()
        {
            var list = table.All().Where(o => o.Key.Role == OwnedRole.Layout).Select(o => new LayoutRef(o.Name, o.Handle)).ToList();
            // The drawing's real layouts are foreign names for de-duplication (E603).
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict)
            {
                var layout = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                if (!layout.ModelType) list.Add(new LayoutRef(layout.LayoutName, layout.Handle.ToString()));
            }
            return list;
        }

        public void RenameLayout(string handle, string newName)
        {
            var o = table.Get(handle) ?? throw new InvalidOperationException($"Fake layout {handle} does not exist.");
            table.Update(handle, o.Key, OwnedSignatures.OfLayout(newName), newName);
            log.Add($"rename Layout {o.Name} -> {newName} {handle}");
        }

        public void DeleteLayout(string handle)
        {
            table.Erase(handle);
            log.Add($"delete Layout {handle}");
        }
    }
}

/// <summary>Persistence store: one Xrecord per run holding <see cref="RunRecordCodec"/> chunks.</summary>
public sealed class FakeStore(Database db, Transaction tr, SheetVolSettings settings) : IPersistenceStore
{
    private readonly string _dictionary = settings.SelfTest.FakeStoreDictionary;

    /// <inheritdoc />
    public IReadOnlyList<string> RunNames()
    {
        var d = Dictionary(false);
        if (d is null) return [];
        var names = new List<string>();
        foreach (DBDictionaryEntry e in d) if (!e.Value.IsErased) names.Add(e.Key);
        return names.Order(StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc />
    public StoredRunLoad Load(string runName)
    {
        var d = Dictionary(false);
        if (d is null || !d.Contains(runName)) return StoredRunLoad.Missing;
        var x = (Xrecord)tr.GetObject(d.GetAt(runName), OpenMode.ForRead);
        return RunRecordCodec.FromChunks((x.Data?.AsArray() ?? []).Select(v => v.Value as string));
    }

    /// <inheritdoc />
    public void Save(RunRecord record)
    {
        var chunks = RunRecordCodec.ToChunks(record, settings.Report.Persistence);
        var data = new ResultBuffer(chunks.Select(c => new TypedValue((int)DxfCode.Text, c)).ToArray());
        var d = Dictionary(true)!;
        if (d.Contains(record.Run.Name))
        {
            var x = (Xrecord)tr.GetObject(d.GetAt(record.Run.Name), OpenMode.ForWrite);
            x.Data = data;
            return;
        }
        d.UpgradeOpen();
        var nx = new Xrecord { Data = data };
        d.SetAt(record.Run.Name, nx);
        tr.AddNewlyCreatedDBObject(nx, true);
    }

    /// <inheritdoc />
    public void Delete(string runName)
    {
        var d = Dictionary(false);
        if (d is null || !d.Contains(runName)) return;
        var id = d.GetAt(runName);
        d.UpgradeOpen();
        d.Remove(id);
        tr.GetObject(id, OpenMode.ForWrite).Erase();
    }

    /// <summary>Every stored run as (name, joined text), for before/after comparisons.</summary>
    public IReadOnlyList<(string Name, string Text)> Snapshot() =>
        RunNames().Select(n =>
        {
            var x = (Xrecord)tr.GetObject(Dictionary(false)!.GetAt(n), OpenMode.ForRead);
            return (n, string.Concat((x.Data?.AsArray() ?? []).Select(v => v.Value as string)));
        }).ToList();

    private DBDictionary? Dictionary(bool create)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (nod.Contains(_dictionary)) return (DBDictionary)tr.GetObject(nod.GetAt(_dictionary), OpenMode.ForRead);
        if (!create) return null;
        nod.UpgradeOpen();
        var d = new DBDictionary();
        nod.SetAt(_dictionary, d);
        tr.AddNewlyCreatedDBObject(d, true);
        return d;
    }
}

// ============================================================================
// Decorators: work over fake or real factories
// ============================================================================

/// <summary>T20: the model writer refuses the Nth boundary it is asked to create, as if segment N failed mid-apply.</summary>
public sealed class FailOnWriteFactory(IWriterFactory inner, OwnedRole role, int failOnCreateNumber) : IWriterFactory
{
    /// <inheritdoc />
    public IModelWriter Model(Database db, Transaction tr, SheetVolSettings settings) => new Failing(inner.Model(db, tr, settings), role, failOnCreateNumber);
    /// <inheritdoc />
    public IPaperWriter Paper(Database db, Transaction tr, SheetVolSettings settings) => inner.Paper(db, tr, settings);
    /// <inheritdoc />
    public IPersistenceStore Store(Database db, Transaction tr, SheetVolSettings settings) => inner.Store(db, tr, settings);

    private sealed class Failing(IModelWriter w, OwnedRole role, int n) : IModelWriter
    {
        private int _creates;
        public IReadOnlySet<OwnedRole> Roles => w.Roles;
        public IReadOnlyList<Problem> Preflight(IReadOnlyList<OwnedObject> planned) => w.Preflight(planned);
        public StoredObject Inspect(OwnedKey key, string? handle) => w.Inspect(key, handle);
        public void Erase(OwnedKey key, string handle) => w.Erase(key, handle);

        public WriteResult Write(OwnedObject planned, string? existingHandle)
        {
            if (planned.Key.Role == role && existingHandle is null && ++_creates == n)
                return new WriteResult("", false, [new Problem(PipelineCodes.SelfTestFailed, Severity.Error,
                    $"Forced failure writing {role} number {n} (T20).", planned.Key.SegmentId)]);
            return w.Write(planned, existingHandle);
        }
    }
}

/// <summary>E1012: adds a bias to every bounded cut, so the integrator and the volume surface disagree.</summary>
public sealed class BiasedVolumesFactory(IAdapterFactory inner, double cutBias) : IAdapterFactory
{
    /// <inheritdoc />
    public IDrawingSources Open(Database db, Transaction tr, SheetVolSettings settings) => new Biased(inner.Open(db, tr, settings), cutBias);

    private sealed class Biased(IDrawingSources s, double bias) : IDrawingSources
    {
        public Outcome<DrawingUnits> Units() => s.Units();
        public IReadOnlyList<string> AlignmentNames() => s.AlignmentNames();
        public IReadOnlyList<string> TinSurfaceNames() => s.TinSurfaceNames();
        public Outcome<AlignmentSource> Alignment(string name, string? storedHandle) => s.Alignment(name, storedHandle);
        public Outcome<ManualOutline> ManualOutline(string polylineHandle) => s.ManualOutline(polylineHandle);
        public Outcome<SurfaceSource> Surface(string name, string? storedHandle, bool rebuild) => s.Surface(name, storedHandle, rebuild);
        public AnalysisSurfaceState? ReadVolumeSurfaceState(Guid id, OwnedSurfacePlan plan, string? h) => s.ReadVolumeSurfaceState(id, plan, h);

        public Outcome<OwnedVolumeSurface> EnsureVolumeSurface(OwnedSurfacePlan plan, string? storedHandle)
        {
            var o = s.EnsureVolumeSurface(plan, storedHandle);
            return o.Succeeded ? Outcome<OwnedVolumeSurface>.Ok(o.Value with { Volumes = new Bias(o.Value.Volumes, bias) }, o.Problems) : o;
        }
    }

    private sealed class Bias(IVolumeSource v, double bias) : IVolumeSource
    {
        public RawVolume SurfaceTotals => new(v.SurfaceTotals.Cut + bias, v.SurfaceTotals.Fill);
        public RawVolume Bounded(IReadOnlyList<Pt> p) { var r = v.Bounded(p); return new RawVolume(r.Cut + bias, r.Fill); }
        public double Area2D(IReadOnlyList<Pt> p) => v.Area2D(p);
    }
}

/// <summary>E404: reports one surface as out of date. With rebuild allowed it is "rebuilt"; without, the read fails naming it.</summary>
public sealed class OutOfDateSurfaceFactory(IAdapterFactory inner, string surfaceName) : IAdapterFactory
{
    /// <inheritdoc />
    public IDrawingSources Open(Database db, Transaction tr, SheetVolSettings settings) => new Stale(inner.Open(db, tr, settings), surfaceName);

    private sealed class Stale(IDrawingSources s, string name) : IDrawingSources
    {
        public Outcome<DrawingUnits> Units() => s.Units();
        public IReadOnlyList<string> AlignmentNames() => s.AlignmentNames();
        public IReadOnlyList<string> TinSurfaceNames() => s.TinSurfaceNames();
        public Outcome<AlignmentSource> Alignment(string n, string? h) => s.Alignment(n, h);
        public Outcome<ManualOutline> ManualOutline(string h) => s.ManualOutline(h);
        public Outcome<OwnedVolumeSurface> EnsureVolumeSurface(OwnedSurfacePlan plan, string? h) => s.EnsureVolumeSurface(plan, h);
        public AnalysisSurfaceState? ReadVolumeSurfaceState(Guid id, OwnedSurfacePlan plan, string? h) => s.ReadVolumeSurfaceState(id, plan, h);

        public Outcome<SurfaceSource> Surface(string n, string? h, bool rebuild)
        {
            if (!string.Equals(n, name, StringComparison.Ordinal)) return s.Surface(n, h, rebuild);
            if (!rebuild)
                return Outcome<SurfaceSource>.Fail(new Problem(PipelineCodes.SourceOutOfDate, Severity.Error,
                    $"Surface '{n}' is out of date and regen.rebuildSources is off; nothing was computed.", Fix: "Rebuild the surface, or turn regen.rebuildSources on."));
            var o = s.Surface(n, h, rebuild);
            return o.Succeeded ? Outcome<SurfaceSource>.Ok(o.Value with { Rebuilt = true }, o.Problems) : o;
        }
    }
}

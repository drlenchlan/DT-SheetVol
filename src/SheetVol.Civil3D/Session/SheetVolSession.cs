// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4.
//
// The database side of ISheetVolSession (6.14). The dialog never opens database objects:
// every read here takes the document lock and one transaction; RegenerateAsync and
// ExportAsync run on the main thread in command context through ICommandContext.
//
// Editor-side members (Highlight, ZoomTo, PickAlignmentAsync, PickStationAsync) are a
// strategy, IEditorSide. The default, CoreConsoleEditorSide, throws NotSupportedException:
// Core Console has no interactive editor and SheetVol.Civil3D may not reference AcMgd.
// U2 implements IEditorSide in SheetVol.UI/Host and constructs the session with it:
//
//     var session = new SheetVolSession(new UiEditorSide(...));   // U2, UI/Host
//
// The helpers OwnedHandle and StationAt give U2 what it needs to highlight a boundary
// and turn a picked point into a station without touching the database itself.

using System.Runtime.ExceptionServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Planning;
using SheetVol.Core.Tracking;
using SheetVol.Core.Volumes;
using SheetVol.Core.Volumes.Integrator;
using AcadDocument = Autodesk.AutoCAD.ApplicationServices.Document;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace SheetVol.Civil3D.Session;

/// <summary>Editor-side members of <see cref="ISheetVolSession"/>, implemented by U2 in UI/Host (6.14).</summary>
public interface IEditorSide
{
    /// <summary>Highlights a segment's boundary transiently; null clears (E911).</summary>
    void Highlight(Guid? segmentId);

    /// <summary>Zooms to a segment (E911).</summary>
    void ZoomTo(Guid segmentId);

    /// <summary>Picks an alignment in the drawing; null when cancelled.</summary>
    Task<string?> PickAlignmentAsync();

    /// <summary>Picks a point and returns its raw station on the alignment (UCS to WCS first, E217); null when cancelled.</summary>
    Task<double?> PickStationAsync(string alignmentName);
}

/// <summary>Core Console has no interactive editor: every editor member throws <see cref="NotSupportedException"/>.</summary>
public sealed class CoreConsoleEditorSide : IEditorSide
{
    private static NotSupportedException No(string member) =>
        new($"{member} needs the interactive editor; it is implemented by SheetVol.UI (U2) and is not available in Core Console.");

    /// <inheritdoc />
    public void Highlight(Guid? segmentId) => throw No(nameof(Highlight));

    /// <inheritdoc />
    public void ZoomTo(Guid segmentId) => throw No(nameof(ZoomTo));

    /// <inheritdoc />
    public Task<string?> PickAlignmentAsync() => throw No(nameof(PickAlignmentAsync));

    /// <inheritdoc />
    public Task<double?> PickStationAsync(string alignmentName) => throw No(nameof(PickStationAsync));
}

/// <summary>Runs work on the main thread in a document's command context (6.14 "Document rules").</summary>
public interface ICommandContext
{
    /// <summary>Runs <paramref name="work"/> in command context and returns its result.</summary>
    Task<T> RunAsync<T>(Func<T> work);
}

/// <summary>
/// The default command context: already in a command (SVREGEN, SVSELFTEST) runs inline; from
/// application context (the modeless dialog) goes through
/// <c>DocumentCollection.ExecuteInCommandContextAsync</c> (AcCoreMgd).
/// </summary>
public sealed class AutoCadCommandContext : ICommandContext
{
    /// <inheritdoc />
    public async Task<T> RunAsync<T>(Func<T> work)
    {
        var docs = Application.DocumentManager;
        if (!docs.IsApplicationContext) return work();
        T result = default!;
        ExceptionDispatchInfo? error = null;
        await docs.ExecuteInCommandContextAsync(_ =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            return Task.CompletedTask;
        }, null);
        error?.Throw();
        return result;
    }
}

/// <summary>The engine's side of the one door between UI and engine (6.14).</summary>
public sealed class SheetVolSession : ISheetVolSession, IDisposable
{
    private readonly IEditorSide _editor;
    private readonly ICommandContext _context;
    private readonly Func<RegenEngine?> _engine;
    private readonly Dictionary<string, (double Start, double End)> _ranges = new(StringComparer.Ordinal);
    private AcadDocument? _watched;
    private Database? _watchedDb;
    private IntPtr _watchedPtr;
    private IntPtr _cachePtr;
    private DrawingUnits? _units;
    private bool _dirty;
    private bool _disposed;

    /// <summary>Creates the session over the installed engine (<see cref="EngineBindings.Current"/>, settings from the plugin directory).</summary>
    /// <param name="editor">Editor-side implementation (U2); null uses <see cref="CoreConsoleEditorSide"/>.</param>
    /// <param name="context">Command-context runner; null uses <see cref="AutoCadCommandContext"/>.</param>
    public SheetVolSession(IEditorSide? editor = null, ICommandContext? context = null)
        : this(EngineHost.CreateOrNull, editor, context)
    {
    }

    /// <summary>Creates the session over a given engine factory (SVSELFTEST passes fakes).</summary>
    public SheetVolSession(Func<RegenEngine?> engine, IEditorSide? editor = null, ICommandContext? context = null)
    {
        _engine = engine;
        _editor = editor ?? new CoreConsoleEditorSide();
        _context = context ?? new AutoCadCommandContext();
        var docs = Application.DocumentManager;
        docs.DocumentActivated += OnDocumentActivated;
        docs.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
        SessionState.Changed += OnStateChanged;
        Watch(docs.MdiActiveDocument);
    }

    /// <inheritdoc />
    public event EventHandler? SourcesChanged;

    // ---- Lists and drawing --------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyList<string> AlignmentNames => Read(src => src.AlignmentNames(), []);

    /// <inheritdoc />
    public IReadOnlyList<string> TinSurfaceNames => Read(src => src.TinSurfaceNames(), []);

    /// <inheritdoc />
    /// <remarks>
    /// Rev 3: read on every call (nothing cached), so the dialog's re-read on SourcesChanged sees the
    /// drawing as it is; a surface edit or erase raises SourcesChanged at the command boundary like
    /// any surface. SheetVol-owned surfaces are never listed; a surface whose base or comparison is
    /// erased is listed with SourcesIntact false (E422).
    /// </remarks>
    public IReadOnlyList<VolumeSurfaceInfo> VolumeSurfaces => Read<IReadOnlyList<VolumeSurfaceInfo>>(src => src.VolumeSurfaces?.List() ?? [], []);

    /// <inheritdoc />
    public DrawingInfo? ActiveDrawing
    {
        get
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc is null) return null;
            var units = Units(doc);
            return units is null ? null : new DrawingInfo(doc.Name, units.Value.System);
        }
    }

    /// <inheritdoc />
    public bool IsCommandActive
    {
        get
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            return doc is not null && !string.IsNullOrEmpty(doc.CommandInProgress);
        }
    }

    // ---- Runs ---------------------------------------------------------------------------------

    /// <inheritdoc />
    public RunDefinition? LoadRun(string runName) =>
        Read((_, store) => store.Load(runName) is { Record: { } r, CanModify: true } ? r.Run : null, null);

    /// <inheritdoc />
    public ValidationResult Validate(RunDefinition run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var doc = Application.DocumentManager.MdiActiveDocument;
        var engine = _engine();
        if (doc is null || engine is null)
            return new ValidationResult(false, [], [], [new Problem(PipelineCodes.SettingsInvalid, Severity.Error,
                doc is null ? "No drawing is open." : "SheetVol settings are invalid; run SVCONFIG.")]);
        try
        {
            var units = Units(doc);
            if (units is null)
                return new ValidationResult(false, [], [], [new Problem(PipelineCodes.SettingsInvalid, Severity.Error, "The drawing's units could not be read.")]);
            // Geometry lives only inside its transaction (A1), so the planner runs inside it; the
            // ValidationResult it returns is plain data.
            return DrawingRead.Run(doc, engine, (src, _) =>
            {
                var alignment = src.Alignment(run.AlignmentName, null);
                if (!alignment.Succeeded)
                    return new ValidationResult(false, [], [], alignment.Problems);
                var outlines = run.ManualSegments
                    .Select(m => (m.Id, O: src.ManualOutline(m.PolylineHandle)))
                    .Where(t => t.O.Succeeded).ToDictionary(t => t.Id, t => t.O.Value);
                var planner = new Planner(engine.Settings.Planning.ToOptions(engine.Settings.DrawingUnitsPerFoot(units.Value)));
                return planner.Validate(run, alignment.Value.Geometry, outlines);
            });
        }
        catch (NotWiredException ex)
        {
            return new ValidationResult(false, [], [], [new Problem(PipelineCodes.NotWired, Severity.Error, ex.Message)]);
        }
    }

    /// <inheritdoc />
    public async Task<RegenResult> RegenerateAsync(RunDefinition run, IProgress<double> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var doc = Application.DocumentManager.MdiActiveDocument
            ?? throw new InvalidOperationException("No drawing is open.");
        var engine = _engine();
        if (engine is null)
            return Refused(run.Name, SettingsProvider.Current.Problems);
        var outcome = await _context.RunAsync(() => engine.Regenerate(doc, run, RegenMode.Full, progress, ct, watchEscape: true));
        InvalidateCaches();
        SessionState.Record(doc.Database, outcome);
        return outcome.Result;
    }

    /// <inheritdoc />
    public async Task<RegenResult?> ExportAsync(RunDefinition run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var doc = Application.DocumentManager.MdiActiveDocument;
        var engine = _engine();
        if (doc is null || engine is null) return null;

        // The tables show the last regen: export exactly that when it still matches the run.
        var cached = SessionState.Last(doc.Database, run.Name);
        var status = GetRunStatus(run.Name);
        if (cached?.Reconcile is { } rec && !status.IsStale && cached.DefinitionSignature == Fingerprints.DefinitionSignature(run))
        {
            return await _context.RunAsync(() => DrawingRead.Run(doc, engine, (src, _) =>
            {
                var alignment = src.Alignment(cached.Run.AlignmentName, null);
                if (!alignment.Succeeded) return (RegenResult?)null;
                var files = ExportFiles.Write(cached.Run, rec, RegenEngine.DrawingPath(doc), alignment.Value.Geometry.FormatStation, engine.Settings, engine.Clock());
                return cached.Result with { CsvPath = files.CsvPath, JsonPath = files.JsonPath, Problems = [.. cached.Result.Problems, .. files.Problems] };
            }));
        }

        // Otherwise compute volumes read-only, but only for a run that has been regenerated before.
        if (status.LastResult is null && !Read((_, store) => store.Load(run.Name).Record?.Fingerprint is not null, false)) return null;
        var outcome = await _context.RunAsync(() => engine.Regenerate(doc, run, RegenMode.ExportOnly, null, ct, watchEscape: true));
        return outcome.Result;
    }

    /// <inheritdoc />
    public RunStatus GetRunStatus(string runName)
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        var engine = _engine();
        var last = doc is null ? null : SessionState.Last(doc.Database, runName)?.Result;
        if (doc is null || engine is null) return new RunStatus(true, last);
        try
        {
            var report = DrawingRead.Run(doc, engine, (src, store) => RunStatusReader.Read(src, store, runName, engine.Settings));
            return new RunStatus(report.IsStale, last);
        }
        catch (NotWiredException)
        {
            return new RunStatus(true, last);
        }
    }

    // ---- Stations -------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>Without a readable alignment the raw number is returned in invariant text, so the dialog still shows a value; it never invents equation stationing.</remarks>
    public string FormatStation(string alignmentName, double rawStation) =>
        WithAlignment(alignmentName, a => a.FormatStation(rawStation))
        ?? rawStation.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public (double Start, double End)? StationRange(string alignmentName)
    {
        EnsureCacheFor(Application.DocumentManager.MdiActiveDocument);
        if (_ranges.TryGetValue(alignmentName, out var r)) return r;
        var range = WithAlignment(alignmentName, a => new Tuple<double, double>(a.StartStation, a.EndStation));
        if (range is null) return null;
        return _ranges[alignmentName] = (range.Item1, range.Item2);
    }

    /// <summary>For U2's pick: the raw station of a WCS point on the alignment (C5 station locator), or null when it is not on it.</summary>
    public double? StationAt(string alignmentName, Pt worldPoint) =>
        WithAlignment(alignmentName, a =>
        {
            if (!StationLocator.TryCreate(a, worldPoint, out var locator, out _)) return (double?)null;
            var hit = locator.Locate(new Pt(0, 0), a.StartStation, a.EndStation);   // locator frame: world minus origin
            return hit.Station >= a.StartStation && hit.Station <= a.EndStation ? hit.Station : null;
        });

    /// <summary>Runs <paramref name="use"/> on the alignment inside one read transaction (geometry never outlives it); null when unavailable.</summary>
    private T? WithAlignment<T>(string alignmentName, Func<IAlignmentGeometry, T> use) where T : class? =>
        string.IsNullOrEmpty(alignmentName) ? null
        : Read((src, _) => src.Alignment(alignmentName, null) is { Succeeded: true } o ? use(o.Value.Geometry) : null, (T?)null);

    private double? WithAlignment(string alignmentName, Func<IAlignmentGeometry, double?> use) =>
        string.IsNullOrEmpty(alignmentName) ? null
        : Read((src, _) => src.Alignment(alignmentName, null) is { Succeeded: true } o ? use(o.Value.Geometry) : null, (double?)null);

    /// <summary>For U2's highlight and zoom: the handle of an owned object (for example a segment's boundary) from the stored run, or null.</summary>
    public string? OwnedHandle(string runName, Guid segmentId, OwnedRole role) =>
        Read((_, store) => store.Load(runName).Record?.Owned.FirstOrDefault(o => o.Key.SegmentId == segmentId && o.Key.Role == role)?.Handle, null);

    // ---- Editor side (U2) -----------------------------------------------------------------------

    /// <inheritdoc />
    public void Highlight(Guid? segmentId) => _editor.Highlight(segmentId);

    /// <inheritdoc />
    public void ZoomTo(Guid segmentId) => _editor.ZoomTo(segmentId);

    /// <inheritdoc />
    public Task<string?> PickAlignmentAsync() => _editor.PickAlignmentAsync();

    /// <inheritdoc />
    public Task<double?> PickStationAsync(string alignmentName) => _editor.PickStationAsync(alignmentName);

    // ---- Events and caches ------------------------------------------------------------------------

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var docs = Application.DocumentManager;
        docs.DocumentActivated -= OnDocumentActivated;
        docs.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        SessionState.Changed -= OnStateChanged;
        Watch(null);
    }

    // Documents are compared by native database pointer, and handlers are removed from the
    // exact wrapper objects they were added to: managed wrappers are not stable objects.
    private void Watch(AcadDocument? doc)
    {
        IntPtr ptr = doc?.Database.UnmanagedObject ?? IntPtr.Zero;
        if (ptr == _watchedPtr) return;
        if (_watched is not null && _watchedDb is not null)
        {
            _watched.CommandEnded -= OnCommandEnded;
            _watched.CommandCancelled -= OnCommandEnded;
            _watched.CommandFailed -= OnCommandEnded;
            _watchedDb.ObjectAppended -= OnObjectChanged;
            _watchedDb.ObjectErased -= OnObjectErased;
            _watchedDb.ObjectModified -= OnObjectChanged;
        }
        _watched = doc;
        _watchedDb = doc?.Database;
        _watchedPtr = ptr;
        if (doc is not null && _watchedDb is not null)
        {
            doc.CommandEnded += OnCommandEnded;
            doc.CommandCancelled += OnCommandEnded;
            doc.CommandFailed += OnCommandEnded;
            _watchedDb.ObjectAppended += OnObjectChanged;
            _watchedDb.ObjectErased += OnObjectErased;
            _watchedDb.ObjectModified += OnObjectChanged;
        }
    }

    // Handlers only set a flag (6.11): the work happens at the command boundary.
    private void OnObjectChanged(object? sender, ObjectEventArgs e)
    {
        if (e.DBObject is CivAlignment or CivSurface) _dirty = true;
    }

    private void OnObjectErased(object? sender, ObjectErasedEventArgs e)
    {
        if (e.DBObject is CivAlignment or CivSurface) _dirty = true;
    }

    private void OnCommandEnded(object? sender, Autodesk.AutoCAD.ApplicationServices.CommandEventArgs e)
    {
        if (!_dirty) return;
        _dirty = false;
        InvalidateCaches();
        Raise();
    }

    private void OnDocumentActivated(object? sender, Autodesk.AutoCAD.ApplicationServices.DocumentCollectionEventArgs e)
    {
        Watch(e.Document);
        InvalidateCaches();
        Raise();
    }

    private void OnDocumentToBeDestroyed(object? sender, Autodesk.AutoCAD.ApplicationServices.DocumentCollectionEventArgs e)
    {
        if (e.Document is not null && e.Document.Database.UnmanagedObject == _watchedPtr) Watch(null);
        InvalidateCaches();
        Raise();
    }

    private void OnStateChanged(object? sender, Database db)
    {
        InvalidateCaches();
        Raise();
    }

    private void Raise() => SourcesChanged?.Invoke(this, EventArgs.Empty);

    // Only plain values are cached (units, station ranges). Alignment geometry is never kept:
    // A1's adapters are valid only inside the transaction that opened them.
    private void InvalidateCaches()
    {
        _ranges.Clear();
        _units = null;
        _cachePtr = IntPtr.Zero;
    }

    private void EnsureCacheFor(AcadDocument? doc)
    {
        IntPtr ptr = doc?.Database.UnmanagedObject ?? IntPtr.Zero;
        if (ptr == _cachePtr) return;
        InvalidateCaches();
        _cachePtr = ptr;
    }

    private DrawingUnits? Units(AcadDocument doc)
    {
        EnsureCacheFor(doc);
        if (_units is { } u) return u;
        var engine = _engine();
        if (engine is null) return null;
        try
        {
            var o = DrawingRead.Run(doc, engine, (src, _) => src.Units());
            return _units = o.Succeeded ? o.Value : null;
        }
        catch (NotWiredException)
        {
            return null;
        }
    }

    private T Read<T>(Func<IDrawingSources, IPersistenceStore, T> read, T fallback)
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        var engine = _engine();
        if (doc is null || engine is null) return fallback;
        try { return DrawingRead.Run(doc, engine, read); }
        catch (NotWiredException) { return fallback; }
    }

    private T Read<T>(Func<IDrawingSources, T> read, T fallback) => Read((src, _) => read(src), fallback);

    private static RegenResult Refused(string runName, IReadOnlyList<Problem> problems) =>
        new(false, runName, [], problems, [], false, TimeSpan.Zero, null, null);
}

/// <summary>Builds the engine from the effective settings and the installed bindings.</summary>
public static class EngineHost
{
    /// <summary>The engine, or null when the settings file is invalid (every command then refuses with its problems).</summary>
    public static RegenEngine? CreateOrNull() => Create(EngineBindings.Current).Engine;

    /// <summary>The engine over <paramref name="bindings"/> and the settings load that made it.</summary>
    public static (RegenEngine? Engine, SettingsLoad Load) Create(EngineBindings bindings)
    {
        var load = SettingsProvider.Current;
        return load.Settings is null ? (null, load) : (new RegenEngine(bindings, load.Settings, SettingsProvider.PluginDirectory), load);
    }
}

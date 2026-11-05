using System.Globalization;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.Civil.ApplicationServices;
using SheetVol.Core;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcPolyline = Autodesk.AutoCAD.DatabaseServices.Polyline;

namespace SheetVol.UI.Host;

/// <summary>
/// The editor side of <see cref="ISheetVolSession"/> (HANDOFF.md 6.14), as a decorator over
/// the database-side session (A4's, or the design fixture before A4 merges).
/// <list type="bullet">
/// <item>Editor members are implemented here: <see cref="Highlight"/> draws a transient copy
/// of the segment's boundary (no database object is opened or changed), <see cref="ZoomTo"/>
/// sets the view to it, <see cref="PickAlignmentAsync"/> and <see cref="PickStationAsync"/>
/// prompt through <c>Document.Editor</c> in command context.</item>
/// <item>Database members are forwarded on the main thread under a document lock, and
/// <see cref="RegenerateAsync"/> and <see cref="ExportAsync"/> in command context
/// (<see cref="CommandContext"/>).</item>
/// <item><see cref="SourcesChanged"/> is raised for the inner session's own changes and when
/// the active document changes or closes (E901), deferred to the dispatcher so the dialog
/// never rebinds inside an AutoCAD event.</item>
/// <item><see cref="IsCommandActive"/> also reads the active document's command state, and
/// <see cref="CommandStateChanged"/> tells the host when it may have changed (E905).</item>
/// </list>
/// Boundaries for highlight and zoom come from the last <see cref="Validate"/> result, the
/// same geometry the regen draws as the boundary polyline.
/// </summary>
internal sealed class EditorSession : ISheetVolSession, IDisposable
{
    private readonly ISheetVolSession _inner;
    private readonly HostConfig _config;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<Guid, Boundary> _boundaries = [];
    private EventHandler? _sourcesChanged;
    private Document? _watched;
    private AcPolyline? _highlight;
    private IReadOnlyList<string> _lastAlignments = [];
    private IReadOnlyList<string> _lastSurfaces = [];
    private string? _lastReport;
    private int _editorWork;          // editor or engine work in flight through CommandContext (E905)
    private int _regens;              // regens in flight; SourcesChanged waits for them
    private bool _sourcesWanted;
    private bool _sourcesPending;
    private bool _commandStatePending;
    private bool _disposed;

    /// <summary>Wraps <paramref name="inner"/> and starts following the document manager.</summary>
    public EditorSession(ISheetVolSession inner, HostConfig config)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _dispatcher = Dispatcher.CurrentDispatcher;

        _inner.SourcesChanged += OnInnerSourcesChanged;
        var dm = AcApp.DocumentManager;
        dm.DocumentActivated += OnDocumentActivated;
        dm.DocumentToBeDeactivated += OnDocumentToBeDeactivated;
        dm.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
        dm.DocumentDestroyed += OnDocumentDestroyed;
        Watch(dm.MdiActiveDocument);
    }

    /// <summary>Raised (deferred) when the active document's command state may have changed (E905).</summary>
    public event EventHandler? CommandStateChanged;

    // ---- Database side: forwarded under a lock ------------------------------------------
    //
    // The view model calls Validate, FormatStation and the object lists on paths with no catch
    // (an async-void command's continuation, Refresh), where an exception would end the AutoCAD
    // process. Those members therefore never throw: a failure is one SV-E901 line and a value
    // that says so (Validate returns an Error problem) or keeps the last good state (the lists).
    // LoadRun, GetRunStatus and ActiveDrawing are reached only from the dialog's constructor
    // (SVDIALOG catches) and from SourcesChanged, which this class raises inside a catch and
    // never while a regen runs, so they report failures by throwing.

    /// <inheritdoc/>
    public IReadOnlyList<string> AlignmentNames
        => Read(() => _lastAlignments = _inner.AlignmentNames, _ => _lastAlignments);

    /// <inheritdoc/>
    public IReadOnlyList<string> TinSurfaceNames
        => Read(() => _lastSurfaces = _inner.TinSurfaceNames, _ => _lastSurfaces);

    /// <inheritdoc/>
    public DrawingInfo? ActiveDrawing
        => AcApp.DocumentManager.MdiActiveDocument is null ? null : CommandContext.Locked(() => _inner.ActiveDrawing);

    /// <inheritdoc/>
    public RunDefinition? LoadRun(string runName) => CommandContext.Locked(() => _inner.LoadRun(runName));

    /// <inheritdoc/>
    public ValidationResult Validate(RunDefinition run)
    {
        var result = Read(
            () => _inner.Validate(run),
            message => new ValidationResult(false, [], [], [new Problem(HostText.ReadFailedCode, Severity.Error, message)]));
        // Only a plan the dialog adopts replaces the cache. It refuses a candidate that brings a
        // run-level error (a station that does not parse or lies off the alignment) and keeps the
        // previous plan, so a result with a run-level error never replaces the highlight geometry.
        var runLevelError = result.Problems.Any(p => p.SegmentId is null && p.Severity == Severity.Error);
        if (result.Segments.Count == 0 || runLevelError) return result;
        _boundaries.Clear();
        foreach (var boundary in result.Boundaries) _boundaries[boundary.SegmentId] = boundary;
        return result;
    }

    /// <inheritdoc/>
    public string FormatStation(string alignmentName, double rawStation)
        => Read(() => _inner.FormatStation(alignmentName, rawStation),
                _ => rawStation.ToString(_config.FallbackStationPattern, CultureInfo.InvariantCulture));

    /// <inheritdoc/>
    public (double Start, double End)? StationRange(string alignmentName)
        => Read<(double Start, double End)?>(() => _inner.StationRange(alignmentName), _ => null);

    /// <inheritdoc/>
    public RunStatus GetRunStatus(string runName) => CommandContext.Locked(() => _inner.GetRunStatus(runName));

    /// <inheritdoc/>
    public bool IsCommandActive
    {
        get
        {
            if (_editorWork > 0 || _inner.IsCommandActive) return true;
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            return doc is not null && !string.IsNullOrEmpty(doc.CommandInProgress);
        }
    }

    /// <inheritdoc/>
    public async Task<RegenResult> RegenerateAsync(RunDefinition run, IProgress<double> progress, CancellationToken ct)
    {
        _regens++;
        try
        {
            return await Exclusive(() => CommandContext.RunAsync(_ => _inner.RegenerateAsync(run, progress, ct)));
        }
        finally
        {
            _regens--;
            if (_regens == 0 && _sourcesWanted) RaiseSourcesChangedDeferred();
        }
    }

    /// <inheritdoc/>
    public Task<RegenResult?> ExportAsync(RunDefinition run, CancellationToken ct)
        => Exclusive(() => CommandContext.RunAsync(_ => _inner.ExportAsync(run, ct)));

    /// <inheritdoc/>
    public event EventHandler SourcesChanged
    {
        add => _sourcesChanged += value;
        remove => _sourcesChanged -= value;
    }

    // ---- Editor side ----------------------------------------------------------------------

    /// <summary>
    /// Transient highlight of the segment's boundary (E911); null clears it. Transient graphics
    /// touch no database object, so this runs directly on the main thread; it never throws
    /// (a failure is one SV-E911 line on the command line).
    /// </summary>
    public void Highlight(Guid? segmentId)
    {
        try
        {
            ClearHighlight();
            if (segmentId is not Guid id || _disposed) return;
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc is null || !_boundaries.TryGetValue(id, out var boundary)) return;
            var polyline = ToPolyline(boundary);
            if (polyline is null) return;
            try
            {
                polyline.ColorIndex = _config.HighlightColorIndex;
                TransientManager.CurrentTransientManager.AddTransient(polyline, TransientDrawingMode.Highlight, 128, new IntegerCollection());
            }
            catch
            {
                polyline.Dispose();
                throw;
            }
            _highlight = polyline;
            doc.Editor.UpdateScreen();
        }
        catch (Exception ex)
        {
            Report(HostText.HighlightFailedFormat, ex);
        }
    }

    /// <summary>Zooms the view to the segment's boundary, in command context. Never throws; a failure is one SV-E911 line.</summary>
    public async void ZoomTo(Guid segmentId)
    {
        try
        {
            var points = BoundaryPoints(segmentId) ?? throw new InvalidOperationException(HostText.NoBoundaryForSegment);
            await Exclusive(() => CommandContext.RunAsync(doc =>
            {
                Zoom(doc, points);
                return true;
            }));
        }
        catch (Exception ex)
        {
            Report(HostText.ZoomFailedFormat, ex);
        }
    }

    /// <summary>Entity pick filtered to alignments, in command context. Null when the user cancels.</summary>
    /// <exception cref="InvalidOperationException">A command is active or no drawing is open; the dialog shows the message.</exception>
    public Task<string?> PickAlignmentAsync()
        => Exclusive(() =>
        {
            FocusDrawing();
            return CommandContext.RunAsync<string?>(PickAlignment);
        });

    private static string? PickAlignment(Document doc)
    {
        var options = new PromptEntityOptions(HostText.PickAlignmentPrompt);
        options.SetRejectMessage(HostText.PickAlignmentReject);
        options.AddAllowedClass(CivilReads.AlignmentType, false);
        var picked = doc.Editor.GetEntity(options);
        if (picked.Status != PromptStatus.OK) return null;
        using var tr = doc.TransactionManager.StartTransaction();
        var name = CivilReads.Name(tr.GetObject(picked.ObjectId, OpenMode.ForRead));
        tr.Commit();
        return name;
    }

    /// <summary>Point pick (UCS to WCS) converted to a raw station on the named alignment, in command context. Null when the user cancels.</summary>
    /// <exception cref="InvalidOperationException">A command is active, no drawing is open, the alignment is missing, or the point is not beside it.</exception>
    public Task<double?> PickStationAsync(string alignmentName)
    {
        if (string.IsNullOrEmpty(alignmentName)) throw new InvalidOperationException(HostText.PickStationNoAlignment);
        return Exclusive(() =>
        {
            FocusDrawing();
            return CommandContext.RunAsync<double?>(doc => PickStation(doc, alignmentName));
        });
    }

    private double? PickStation(Document doc, string alignmentName)
    {
        var ed = doc.Editor;
        var picked = ed.GetPoint(new PromptPointOptions(string.Format(HostText.PickStationPromptFormat, alignmentName)));
        if (picked.Status != PromptStatus.OK) return null;
        var wcs = picked.Value.TransformBy(ed.CurrentUserCoordinateSystem);
        if (_inner is DesignSession design)
            return design.StationAt(new Pt(wcs.X, wcs.Y))
                   ?? throw new InvalidOperationException(string.Format(HostText.PointNotBesideAlignmentFormat, alignmentName));
        return StationOnAlignment(doc, alignmentName, wcs);
    }

    // ---- Guards -----------------------------------------------------------------------------

    /// <summary>
    /// One piece of editor or engine work at a time (E905): refused while a command runs or while
    /// another piece is in flight (the dialog stays live during a pick prompt, and a second request
    /// would otherwise run inside that prompt). While it runs, <see cref="IsCommandActive"/> is true
    /// and the host re-queries the dialog's command state at start and end.
    /// </summary>
    /// <exception cref="InvalidOperationException">A command or other editor work is active; the dialog shows the message.</exception>
    private async Task<T> Exclusive<T>(Func<Task<T>> work)
    {
        if (IsCommandActive) throw new InvalidOperationException(HostText.CommandActive);
        _editorWork++;
        RaiseCommandStateDeferred();
        try
        {
            return await work();
        }
        finally
        {
            _editorWork--;
            RaiseCommandStateDeferred();
        }
    }

    /// <summary>A synchronous read under the lock that never throws: a failure is one SV-E901 line and the fallback built from its message.</summary>
    private T Read<T>(Func<T> read, Func<string, T> fallback)
    {
        try
        {
            return CommandContext.Locked(read);
        }
        catch (Exception ex)
        {
            var message = string.Format(HostText.ReadFailedMessageFormat, ex.Message);
            ReportLine(HostText.ReadFailedCode + " " + message);
            return fallback(message);
        }
    }

    // ---- Lifetime -----------------------------------------------------------------------------

    /// <summary>Clears the highlight and stops following documents and the inner session.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        ClearHighlight();
        _disposed = true;
        _inner.SourcesChanged -= OnInnerSourcesChanged;
        var dm = AcApp.DocumentManager;
        dm.DocumentActivated -= OnDocumentActivated;
        dm.DocumentToBeDeactivated -= OnDocumentToBeDeactivated;
        dm.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        dm.DocumentDestroyed -= OnDocumentDestroyed;
        Watch(null);
        (_inner as IDisposable)?.Dispose();
    }

    // ---- Document manager events -----------------------------------------------------------

    private void OnInnerSourcesChanged(object? sender, EventArgs e) => RaiseSourcesChangedDeferred();

    private void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
    {
        Watch(e.Document);
        _boundaries.Clear();
        _lastAlignments = [];
        _lastSurfaces = [];
        RaiseSourcesChangedDeferred();
        RaiseCommandStateDeferred();
    }

    private void OnDocumentToBeDeactivated(object? sender, DocumentCollectionEventArgs e) => ClearHighlight();

    private void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
    {
        if (ReferenceEquals(e.Document, AcApp.DocumentManager.MdiActiveDocument)) ClearHighlight();
        if (ReferenceEquals(e.Document, _watched)) Watch(null);
    }

    private void OnDocumentDestroyed(object? sender, DocumentDestroyedEventArgs e)
    {
        _boundaries.Clear();
        RaiseSourcesChangedDeferred();
        RaiseCommandStateDeferred();
    }

    private void Watch(Document? doc)
    {
        if (_watched is not null)
        {
            _watched.CommandWillStart -= OnCommandEvent;
            _watched.CommandEnded -= OnCommandEvent;
            _watched.CommandCancelled -= OnCommandEvent;
            _watched.CommandFailed -= OnCommandEvent;
        }
        _watched = _disposed ? null : doc;
        if (_watched is not null)
        {
            _watched.CommandWillStart += OnCommandEvent;
            _watched.CommandEnded += OnCommandEvent;
            _watched.CommandCancelled += OnCommandEvent;
            _watched.CommandFailed += OnCommandEvent;
        }
    }

    private void OnCommandEvent(object? sender, CommandEventArgs e) => RaiseCommandStateDeferred();

    /// <summary>
    /// Coalesced and deferred: the dialog rebinds after the AutoCAD event returns, when
    /// MdiActiveDocument is current, and inside a catch. Held back while a regen runs and raised
    /// when it ends, so the dialog never rebinds from its own post-regen path, which has no catch.
    /// </summary>
    private void RaiseSourcesChangedDeferred()
    {
        if (_disposed) return;
        if (_regens > 0)
        {
            _sourcesWanted = true;
            return;
        }
        if (_sourcesPending) return;
        _sourcesPending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _sourcesPending = false;
            if (_disposed) return;
            if (_regens > 0)
            {
                _sourcesWanted = true;
                return;
            }
            _sourcesWanted = false;
            try
            {
                _sourcesChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Report(HostText.RebindFailedFormat, ex);
            }
        });
    }

    /// <summary>Deferred so CommandInProgress has its new value when the host re-queries it.</summary>
    private void RaiseCommandStateDeferred()
    {
        if (_commandStatePending || _disposed) return;
        _commandStatePending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _commandStatePending = false;
            if (_disposed) return;
            try
            {
                CommandStateChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Report(HostText.RebindFailedFormat, ex);
            }
        });
    }

    // ---- Geometry helpers ----------------------------------------------------------------------

    private void ClearHighlight()
    {
        var polyline = _highlight;
        _highlight = null;
        if (polyline is null) return;
        try
        {
            TransientManager.CurrentTransientManager.EraseTransient(polyline, new IntegerCollection());
            AcApp.DocumentManager.MdiActiveDocument?.Editor.UpdateScreen();
        }
        catch (Exception ex)
        {
            Report(HostText.HighlightFailedFormat, ex);
        }
        finally
        {
            polyline.Dispose();
        }
    }

    /// <summary>A non-database polyline from the boundary's drawn vertices (true arcs), or from the tessellation.</summary>
    private static AcPolyline? ToPolyline(Boundary boundary)
    {
        var vertices = boundary.Drawn.Count >= 3
            ? boundary.Drawn
            : boundary.Tessellated.Select(p => new PolyVertex(p, 0)).ToList();
        var count = vertices.Count;
        if (count >= 2 && vertices[0].Point == vertices[count - 1].Point) count--;   // closed by the flag, not a repeated vertex
        if (count < 3) return null;
        var polyline = new AcPolyline(count);
        for (var i = 0; i < count; i++)
            polyline.AddVertexAt(i, new Point2d(vertices[i].Point.X, vertices[i].Point.Y), vertices[i].Bulge, 0, 0);
        polyline.Closed = true;
        return polyline;
    }

    private IReadOnlyList<Pt>? BoundaryPoints(Guid segmentId)
    {
        if (!_boundaries.TryGetValue(segmentId, out var boundary)) return null;
        IReadOnlyList<Pt> points = boundary.Tessellated.Count >= 3 ? boundary.Tessellated : boundary.Drawn.Select(v => v.Point).ToList();
        return points.Count >= 3 ? points : null;
    }

    private void Zoom(Document doc, IReadOnlyList<Pt> points)
    {
        if (!doc.Database.TileMode && Convert.ToInt32(AcApp.GetSystemVariable("CVPORT")) == 1)
            throw new InvalidOperationException(HostText.ZoomNeedsModelSpace);

        var ed = doc.Editor;
        using var view = ed.GetCurrentView();
        var wcsToDcs = (Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)
                        * Matrix3d.Displacement(view.Target - Point3d.Origin)
                        * Matrix3d.PlaneToWorld(view.ViewDirection)).Inverse();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in points)
        {
            var d = new Point3d(p.X, p.Y, 0).TransformBy(wcsToDcs);
            minX = Math.Min(minX, d.X);
            minY = Math.Min(minY, d.Y);
            maxX = Math.Max(maxX, d.X);
            maxY = Math.Max(maxY, d.Y);
        }
        var width = Math.Max(maxX - minX, 1e-6) * _config.ZoomMarginFactor;
        var height = Math.Max(maxY - minY, 1e-6) * _config.ZoomMarginFactor;
        var aspect = view.Height > 0 ? view.Width / view.Height : 1;
        if (width / height > aspect) height = width / aspect;
        else width = height * aspect;
        view.CenterPoint = new Point2d((minX + maxX) / 2, (minY + maxY) / 2);
        view.Width = width;
        view.Height = height;
        ed.SetCurrentView(view);
    }

    private static double StationOnAlignment(Document doc, string alignmentName, Point3d wcs)
    {
        var civil = CivilDocument.GetCivilDocument(doc.Database);
        using var tr = doc.TransactionManager.StartTransaction();
        foreach (ObjectId id in civil.GetAlignmentIds())
        {
            var obj = tr.GetObject(id, OpenMode.ForRead);
            if (!CivilReads.IsAlignment(obj) || CivilReads.Name(obj) != alignmentName) continue;
            double station;
            try
            {
                (station, _) = CivilReads.StationOffset(obj, wcs.X, wcs.Y);
            }
            catch (Exception ex) when (ex is Autodesk.Civil.PointNotOnEntityException
                                       || ex.InnerException is Autodesk.Civil.PointNotOnEntityException)
            {
                // The second form covers a binder that wraps the target's exception (CivilReads uses dynamic).
                throw new InvalidOperationException(string.Format(HostText.PointNotBesideAlignmentFormat, alignmentName));
            }
            tr.Commit();
            return station;
        }
        throw new InvalidOperationException(string.Format(HostText.AlignmentNotFoundFormat, alignmentName));
    }

    private static void FocusDrawing()
    {
        try
        {
            AcApp.MainWindow.Focus();
        }
        catch (Exception)
        {
            // Focus is a convenience: the prompt still works after one click in the drawing.
        }
    }

    private void Report(string format, Exception ex) => ReportLine(string.Format(format, ex.Message));

    /// <summary>One line on the command line; the same line twice in a row is written once (a failing read repeats on every refresh).</summary>
    private void ReportLine(string line)
    {
        if (line == _lastReport) return;
        _lastReport = line;
        AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + line);
    }
}

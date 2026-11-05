// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4.
//
// Small pure pieces the regen pipeline composes: the display polygon source C3's view
// math asks for (built with C1's boundary builder, so views see exactly the geometry the
// volumes use), a memoizing volume source (the integrator cross-check and the reconciler
// ask for the same polygon), and the cancel probe (CancellationToken plus Esc).

using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Geometry;
using SheetVol.Core.Views;

namespace SheetVol.Civil3D.Session;

/// <summary>
/// C3's <see cref="IDisplayPolygonSource"/> over C1's <see cref="BoundaryBuilder"/>: the band of one
/// segment between two raw stations with that segment's own offsets (C3 note: "A4 adapts C1's
/// boundary builder").
/// </summary>
public sealed class DisplayPolygonSource : IDisplayPolygonSource
{
    private readonly IAlignmentGeometry _alignment;
    private readonly double _left, _right;
    private readonly GeometryOptions _options;
    private readonly Guid _segmentId;

    /// <summary>Creates the source for one segment.</summary>
    public DisplayPolygonSource(IAlignmentGeometry alignment, SegmentDef segment, GeometryOptions options)
    {
        _alignment = alignment;
        _left = segment.OffsetLeft;
        _right = segment.OffsetRight;
        _options = options;
        _segmentId = segment.Id;
    }

    /// <inheritdoc />
    /// <remarks>
    /// C1's per-segment overload (rev2b): the segment's own offsets, which carry its
    /// <see cref="SegmentOverrides"/> offsets after planning, reach the view polygon exactly as
    /// they reach the volume boundary.
    /// </remarks>
    public IReadOnlyList<PolyVertex> Build(double fromStation, double toStation)
    {
        var def = new SegmentDef(_segmentId, "view", fromStation, toStation, _left, _right);
        var built = BoundaryBuilder.Build(_alignment, [def], _options);
        return built.Boundaries.Count == 1 ? built.Boundaries[0].Drawn : [];
    }
}

/// <summary>
/// The lifetime of the transaction an adapter was opened in (the A1 rule A4 adopted: no geometry
/// outlives its transaction). MEASURED 2026-11-05: an object wrapper from tr.GetObject is NOT
/// disposed when its transaction commits, so IsDisposed on it proves nothing. Over = the
/// transaction was disposed, or it was committed or aborted (the active-transaction count fell
/// below its depth).
/// </summary>
public sealed class TransactionLifetime
{
    private readonly Transaction _tr;
    private readonly int _depth;

    /// <summary>Starts watching <paramref name="tr"/>, which must be the top transaction.</summary>
    public TransactionLifetime(Transaction tr)
    {
        _tr = tr;
        _depth = tr.TransactionManager.NumberOfActiveTransactions;
    }

    /// <summary>True once the transaction has ended.</summary>
    public bool IsOver => _tr.IsDisposed || _tr.TransactionManager.NumberOfActiveTransactions < _depth;

    /// <summary>Throws <see cref="InvalidOperationException"/> naming <paramref name="what"/> once the transaction has ended.</summary>
    public void Guard(string what)
    {
        if (IsOver)
            throw new InvalidOperationException($"The {what} is used after its transaction ended; open a new adapter inside the current transaction.");
    }
}

/// <summary>Alignment geometry that refuses use once its transaction has ended (<see cref="TransactionLifetime"/>).</summary>
public sealed class LifetimeGuardedAlignment(IAlignmentGeometry inner, TransactionLifetime life) : IAlignmentGeometry
{
    /// <inheritdoc />
    public string Name => inner.Name;
    /// <inheritdoc />
    public double StartStation => inner.StartStation;
    /// <inheritdoc />
    public double EndStation => inner.EndStation;
    /// <inheritdoc />
    public IReadOnlyList<AlignSeg> Segments => inner.Segments;

    /// <summary>The wrapped geometry (for adapter-specific reports).</summary>
    public IAlignmentGeometry Inner => inner;

    /// <inheritdoc />
    public Pt PointAt(double rawStation, double offset) { life.Guard($"alignment '{inner.Name}'"); return inner.PointAt(rawStation, offset); }
    /// <inheritdoc />
    public string FormatStation(double rawStation) { life.Guard($"alignment '{inner.Name}'"); return inner.FormatStation(rawStation); }
    /// <inheritdoc />
    public bool TryParseStation(string text, out double rawStation, out string? error) => inner.TryParseStation(text, out rawStation, out error);
}

/// <summary>A volume source that refuses use once its transaction has ended (<see cref="TransactionLifetime"/>).</summary>
public sealed class LifetimeGuardedVolumes(IVolumeSource inner, TransactionLifetime life, string name) : IVolumeSource
{
    /// <inheritdoc />
    public RawVolume SurfaceTotals { get { life.Guard($"volume source '{name}'"); return inner.SurfaceTotals; } }
    /// <inheritdoc />
    public RawVolume Bounded(IReadOnlyList<Pt> closedPolygon) { life.Guard($"volume source '{name}'"); return inner.Bounded(closedPolygon); }
    /// <inheritdoc />
    public double Area2D(IReadOnlyList<Pt> closedPolygon) { life.Guard($"volume source '{name}'"); return inner.Area2D(closedPolygon); }
}

/// <summary>Caches Bounded and Area2D per polygon instance, so the cross-check and the reconciler share one call.</summary>
public sealed class MemoVolumeSource : IVolumeSource
{
    private readonly IVolumeSource _inner;
    private readonly ConditionalWeakTable<IReadOnlyList<Pt>, StrongBox<RawVolume>> _bounded = new();
    private readonly ConditionalWeakTable<IReadOnlyList<Pt>, StrongBox<double>> _area = new();
    private RawVolume? _totals;

    /// <summary>Wraps a source.</summary>
    public MemoVolumeSource(IVolumeSource inner) => _inner = inner;

    /// <inheritdoc />
    public RawVolume SurfaceTotals => _totals ??= _inner.SurfaceTotals;

    /// <inheritdoc />
    public RawVolume Bounded(IReadOnlyList<Pt> closedPolygon) =>
        _bounded.GetValue(closedPolygon, p => new StrongBox<RawVolume>(_inner.Bounded(p))).Value;

    /// <inheritdoc />
    public double Area2D(IReadOnlyList<Pt> closedPolygon) =>
        _area.GetValue(closedPolygon, p => new StrongBox<double>(_inner.Area2D(p))).Value;
}

/// <summary>
/// Cancellation for a regen: the caller's token, plus Esc in full AutoCAD
/// (<see cref="HostApplicationServices.UserBreak"/>, AcDbMgd, Core Console safe). Checked between
/// steps and per segment; a cancel before commit aborts the transaction (E219, E416).
/// </summary>
public sealed class CancelProbe
{
    private readonly CancellationToken _token;
    private readonly bool _watchEscape;

    /// <summary>Creates a probe.</summary>
    public CancelProbe(CancellationToken token, bool watchEscape)
    {
        _token = token;
        _watchEscape = watchEscape;
    }

    /// <summary>Throws <see cref="OperationCanceledException"/> when cancelled or Esc was pressed.</summary>
    public void Check()
    {
        _token.ThrowIfCancellationRequested();
        if (_watchEscape && HostApplicationServices.Current is { } host && host.UserBreak())
            throw new OperationCanceledException("Esc pressed.");
    }
}

/// <summary>Reports progress in [0, 1] through a step range.</summary>
public sealed class ProgressSpan
{
    private readonly IProgress<double>? _progress;
    private double _last = -1;

    /// <summary>Wraps the caller's progress.</summary>
    public ProgressSpan(IProgress<double>? progress) => _progress = progress;

    /// <summary>Reports <paramref name="from"/> + <paramref name="fraction"/> x (<paramref name="to"/> − <paramref name="from"/>).</summary>
    public void Report(double from, double to, double fraction)
    {
        double v = from + Math.Clamp(fraction, 0, 1) * (to - from);
        if (v - _last < 0.005 && v < 1) return;
        _last = v;
        _progress?.Report(v);
    }

    /// <summary>Reports an absolute value.</summary>
    public void At(double value) => Report(value, value, 0);
}

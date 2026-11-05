// A1 adapters. Owner: A1.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Volumes;

namespace SheetVol.Civil3D.Adapters;

/// <summary>
/// <see cref="IVolumeSource"/> over an owned <c>TinVolumeSurface</c>, with every probe finding
/// about the numbers mapped here and nowhere else (HANDOFF.md 6.2 "the adapters absorb every
/// quirk"; P1 to P5, P22):
/// <list type="bullet">
/// <item><b>Sign (P1).</b> <c>SurfaceVolumeInfo.Cut</c> is real cut (base above comparison),
/// <c>Fill</c> real fill, so <c>RawVolume(Cut, Fill)</c> takes them as they are. Civil's
/// <c>Net</c> is Fill − Cut while A5 displays Cut − Fill; the contract carries no net, C2
/// computes Cut − Fill itself, and the only net used here is a difference of two nets (2D area),
/// whose sign does not matter. That is the one place the sign is handled.</item>
/// <item><b>Units (P2).</b> Raw cubic and square drawing units (the Civil drawing unit, never
/// INSUNITS); C2 converts.</item>
/// <item><b>Factors (P3).</b> Bounded results never include the surface's factors and
/// <see cref="SurfaceTotals"/> reads <c>Unadjusted*</c>, so values are raw and C2 applies the
/// factors exactly once (E410).</item>
/// <item><b>Noise.</b> A magnitude below <see cref="AdapterConfig.SignNoiseClamp"/> is returned as 0
/// (P4 measured 5.4e-11 of fill where there is none); a larger negative passes through so C2
/// rejects it (SV-E432) instead of the adapter hiding it.</item>
/// <item><b>Polygons (5.1, P4, P5).</b> Closed, first point repeated, more than 3 points, or
/// <see cref="ArgumentException"/>; winding does not matter.</item>
/// <item><b>Boundary past the surface (E407).</b> No clipping is needed: P4 showed a polygon
/// partly outside counts only the data inside (and wholly outside returns 0/0/0) without an
/// exception, and P22 showed the datum-method area off the edge equals the analytic clipped area
/// to 4e-16. Clipping to the surface border first would only add a second, less exact,
/// computation of the same thing.</item>
/// <item><b>2D area (6.5, P22).</b> <c>|Net(d1) − Net(d2)| / |d2 − d1|</c> from two bounded calls,
/// which P22 confirmed equals the plan area of data inside the polygon to 5.8e-16.</item>
/// </list>
/// The owned surface must have been rebuilt in this regen (<see cref="AnalysisResolver.EnsureOwnedSurface"/>
/// does it; P18). The adapter holds the surface opened in the caller's transaction and is valid
/// only while that transaction is open.
/// </summary>
public sealed class VolumeSourceAdapter : IVolumeSource
{
    private readonly TinVolumeSurface _vol;
    private readonly AdapterConfig _cfg;

    private VolumeSourceAdapter(TinVolumeSurface vol, AdapterConfig cfg, RawVolume totals)
    {
        _vol = vol;
        _cfg = cfg;
        SurfaceTotals = totals;
        Name = vol.Name;
    }

    /// <summary>The owned surface's name.</summary>
    public string Name { get; }

    /// <summary>Number of <c>GetBoundedVolumes</c> calls made so far (two per <see cref="Area2D"/>).</summary>
    public int BoundedCalls { get; private set; }

    /// <summary>Whole-surface raw totals, <c>GetVolumeProperties().Unadjusted*</c>, read when the adapter was opened.</summary>
    public RawVolume SurfaceTotals { get; }

    /// <summary>Opens the owned volume surface for reading. SV-E402 when the id is not a TIN volume surface, SV-E403 when it has no triangles.</summary>
    public static Outcome<VolumeSourceAdapter> Open(Transaction tr, ObjectId ownedSurfaceId, AdapterConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(tr);
        ArgumentNullException.ThrowIfNull(cfg);
        if (ownedSurfaceId.IsNull || ownedSurfaceId.IsErased || tr.GetObject(ownedSurfaceId, OpenMode.ForRead) is not TinVolumeSurface vol)
            return Outcome<VolumeSourceAdapter>.Fail(new Problem(AdapterCodes.NotTinSurface, Severity.Error,
                $"Object {ownedSurfaceId.Handle} is not a TIN volume surface."));
        VolumeSurfaceProperties props;
        try { props = vol.GetVolumeProperties(); }
        catch (InvalidOperationException)
        {
            return Outcome<VolumeSourceAdapter>.Fail(new Problem(AdapterCodes.NoTriangles, Severity.Error,
                $"Volume surface '{vol.Name}' has no triangles, so it has no volume."));
        }
        var totals = new RawVolume(Clean(props.UnadjustedCutVolume, cfg), Clean(props.UnadjustedFillVolume, cfg));
        return Outcome<VolumeSourceAdapter>.Ok(new VolumeSourceAdapter(vol, cfg, totals));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The polygon is not closed, has 3 points or fewer, or has a non-finite coordinate.</exception>
    public RawVolume Bounded(IReadOnlyList<Pt> closedPolygon)
    {
        var info = Call(ToPoints(closedPolygon), null);
        return new RawVolume(Clean(info.Cut), Clean(info.Fill));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">As for <see cref="Bounded"/>.</exception>
    public double Area2D(IReadOnlyList<Pt> closedPolygon)
    {
        var pts = ToPoints(closedPolygon);
        double d1 = _cfg.AreaDatumLow, d2 = _cfg.AreaDatumHigh;
        var low = Call(pts, d1);
        var high = Call(pts, d2);
        return Clean(Math.Abs(low.Net - high.Net) / Math.Abs(d2 - d1));
    }

    /// <summary>One bounded call, raw, for reports: (Cut, Fill, Net) exactly as Civil 3D returns them.</summary>
    public (double Cut, double Fill, double Net) RawBounded(IReadOnlyList<Pt> closedPolygon, double? datum = null)
    {
        var info = Call(ToPoints(closedPolygon), datum);
        return (info.Cut, info.Fill, info.Net);
    }

    private SurfaceVolumeInfo Call(Point3dCollection pts, double? datum)
    {
        if (_vol.IsDisposed)
            throw new InvalidOperationException($"The volume source for '{Name}' is used after its transaction ended; open a new one inside the current transaction.");
        BoundedCalls++;
        return datum is null ? _vol.GetBoundedVolumes(pts) : _vol.GetBoundedVolumes(pts, datum.Value);
    }

    private double Clean(double v) => Clean(v, _cfg);

    private static double Clean(double v, AdapterConfig cfg) => Math.Abs(v) < cfg.SignNoiseClamp ? 0.0 : v;

    /// <summary>The 5.1 polygon rule, checked before Civil 3D sees the points.</summary>
    internal static Point3dCollection ToPoints(IReadOnlyList<Pt> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        if (polygon.Count <= 3)
            throw new ArgumentException($"A bounding polygon needs more than 3 points with the first repeated last; it has {polygon.Count}.", nameof(polygon));
        if (polygon[0] != polygon[^1])
            throw new ArgumentException("A bounding polygon must be closed: its first point must be repeated as its last.", nameof(polygon));
        var pts = new Point3dCollection();
        foreach (var p in polygon)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
                throw new ArgumentException("A bounding polygon has a coordinate that is not a finite number.", nameof(polygon));
            pts.Add(new Point3d(p.X, p.Y, 0.0));
        }
        return pts;
    }
}

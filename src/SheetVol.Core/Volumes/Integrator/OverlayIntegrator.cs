// Overlay integrator and impacts below OHWM (HANDOFF.md 6.15). Owner: C5.
//
// Method (6.15 steps 1 to 7):
//  1. The boundary is validated and translated to a local origin (its bounding-box minimum);
//     every triangle is translated the same way before any area math (E1009).
//  2. Base triangles are indexed on a uniform grid. Each comparison triangle is intersected with
//     each overlapping base triangle (convex against convex, Sutherland-Hodgman). On a piece both
//     surfaces are planar, so depth d = base - comparison is linear.
//  3. The piece is clipped to the boundary: the boundary is ear-clipped once into triangles and the
//     piece is clipped against each triangle it meets (correct for concave boundaries).
//  4. Band totals: plan area, cut where d > 0, fill where d < 0. A piece where d changes sign is
//     split on d = 0 first; each part is integrated exactly (fan triangles, area * mean of vertex d).
//  5. Clip to base < OHWM, then to d > MinCutDepth (both half-plane clips on the linear vertex
//     attributes). What remains adds impacted area, cut volume, and the station interval spanned
//     by its vertices (station extremes of a convex piece inside the band are at its vertices).
//  6. Impacted length = union of those intervals clamped to the segment, gaps shorter than
//     GapTolerance bridged, measured along the alignment (E1005, E1007, E1008).
//  7. QA: cut where base >= OHWM and fill where base < OHWM (E1003, E1004).
// Plus: plan area of base-below-OHWM ground (E1001) and the lateral search-limit test (E1002).
//
// Numerical tolerances (never user-visible thresholds):
//  - snap = 1e-12 * S (S = boundary extent): a point this close to a clip line is on it, so
//    coincident TIN edges never produce slivers;
//  - a piece thinner than snap (2 * area / perimeter) adds its area and volume but no station
//    interval (it can only be a clipping artifact);
//  - plane noise: on the impact path only (steps 5 to 7, ground below OHWM, search limit), a
//    vertex depth within the two triangles' evaluation error bound (SurfaceTriangle.Noise, about
//    1e-12 ft near the origin, 1e-6 ft at state-plane coordinates) is set to exactly 0, and a base
//    elevation within the base triangle's bound of OHWM is set to exactly OHWM. Without this, two
//    surfaces that coincide but are triangulated differently report rounding noise (about 1e-13)
//    as cut, and the impacted length runs the whole segment (measured: T28 gave 500 instead of
//    310 on a rotated Fixture H). Band totals (step 4) are never snapped;
//  - station intervals closer than 1e-9 * segment length merge (StationIntervals).

namespace SheetVol.Core.Volumes.Integrator;

/// <summary>
/// Integrates two TIN surfaces against each other inside a closed boundary. Pure Core, no
/// Civil 3D. Units: drawing units in and out (lengths, square and cubic drawing units).
/// Depth d = base - comparison: cut where d &gt; 0, fill where d &lt; 0.
/// </summary>
public static class OverlayIntegrator
{
    /// <summary>
    /// Band totals inside a closed boundary with no OHWM limit: plan area where both surfaces
    /// exist (the integrator's 2D area), cut and fill (unadjusted, cubic drawing units). This is the
    /// cross-check against GetBoundedVolumes (6.15, T29).
    /// </summary>
    /// <param name="baseSurface">Existing ground (base). Only triangles inside the boundary window are requested.</param>
    /// <param name="comparisonSurface">Design (comparison).</param>
    /// <param name="closedBoundary">Simple polygon, world coordinates, first == last, either orientation, may be concave.</param>
    /// <param name="segmentId">Carried into every <see cref="Problem"/>.</param>
    /// <returns>Totals, or null totals with an Error when the boundary is invalid or a surface has no triangles in the window.</returns>
    public static BandResult IntegrateBand(ITriangleSource baseSurface, ITriangleSource comparisonSurface,
                                           IReadOnlyList<Pt> closedBoundary, Guid? segmentId = null)
    {
        ArgumentNullException.ThrowIfNull(baseSurface);
        ArgumentNullException.ThrowIfNull(comparisonSurface);
        var problems = new List<Problem>();
        var poly = BoundaryPolygon.TryCreate(closedBoundary, segmentId, problems);
        if (poly is null) return new BandResult(segmentId, null, problems);
        var run = new OverlayRun(poly, segmentId, problems);
        if (!run.Load(baseSurface, comparisonSurface)) return new BandResult(segmentId, null, problems);
        run.Overlay(null);
        var totals = run.Totals();
        run.ReportCoverage(totals);
        return new BandResult(segmentId, totals, problems);
    }

    /// <summary>
    /// Band totals plus the HCFCD impact quantities below OHWM for one station segment (6.15, 6.16).
    /// </summary>
    /// <param name="baseSurface">Existing ground (base).</param>
    /// <param name="comparisonSurface">Design (comparison).</param>
    /// <param name="alignment">The run's alignment; stations of impacted pieces are measured along it (E1008).</param>
    /// <param name="segment">Station range (raw, End &gt; Start) and signed offsets (Left &lt; Right) of the segment.</param>
    /// <param name="closedBoundary">The segment's tessellated boundary, world coordinates, first == last.</param>
    /// <param name="ohwm">OHWM default elevation and per-segment overrides (A11, E1010), drawing units.</param>
    /// <param name="material">Material label for the result row (the analysis name, 6.16).</param>
    /// <param name="settings">MinCutDepth and GapTolerance from config (E1007); null = defaults (count everything).</param>
    public static SegmentImpactResult IntegrateSegment(
        ITriangleSource baseSurface, ITriangleSource comparisonSurface,
        IAlignmentGeometry alignment, SegmentDef segment, IReadOnlyList<Pt> closedBoundary,
        OhwmSpec ohwm, string material, IntegratorSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(baseSurface);
        ArgumentNullException.ThrowIfNull(comparisonSurface);
        ArgumentNullException.ThrowIfNull(alignment);
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(ohwm);
        ArgumentNullException.ThrowIfNull(material);
        if (!double.IsFinite(segment.StartStation) || !double.IsFinite(segment.EndStation) || !(segment.EndStation > segment.StartStation))
            throw new ArgumentException("Segment end station must be greater than its start station.", nameof(segment));
        settings ??= IntegratorSettings.Default;

        var problems = new List<Problem>();
        double elev = ImpactQuantities.OhwmFor(ohwm, segment.Id);
        SegmentImpactResult Failed() => new(segment.Id, elev, null, null, double.NaN, problems);

        if (!double.IsFinite(elev))
            problems.Add(new Problem(IntegratorCodes.InvalidOhwm, Severity.Error,
                $"OHWM elevation for this segment is not a finite number ({elev}).", segment.Id, "Enter an OHWM elevation."));
        if (!double.IsFinite(settings.MinCutDepth) || settings.MinCutDepth < 0 ||
            !double.IsFinite(settings.GapTolerance) || settings.GapTolerance < 0)
            problems.Add(new Problem(IntegratorCodes.InvalidSettings, Severity.Error,
                $"MinCutDepth ({settings.MinCutDepth}) and GapTolerance ({settings.GapTolerance}) must be finite and >= 0.",
                segment.Id, "Correct MinCutDepth and GapTolerance in the configuration."));
        if (problems.Count > 0) return Failed();

        var poly = BoundaryPolygon.TryCreate(closedBoundary, segment.Id, problems);
        if (poly is null) return Failed();
        var origin = new Pt(poly.OriginX, poly.OriginY);
        if (!StationLocator.TryCreate(alignment, origin, out var locator, out string? stationError))
        {
            problems.Add(new Problem(IntegratorCodes.StationGeometry, Severity.Error,
                $"Stations cannot be measured along the alignment: {stationError}.", segment.Id,
                "Check that the alignment is solved and its entities are connected."));
            return Failed();
        }

        Pt Local(Pt w) => new(w.X - origin.X, w.Y - origin.Y);
        var lateral = poly.LateralEdges(
            Local(alignment.PointAt(segment.StartStation, segment.OffsetLeft)),
            Local(alignment.PointAt(segment.EndStation, segment.OffsetLeft)),
            Local(alignment.PointAt(segment.EndStation, segment.OffsetRight)),
            Local(alignment.PointAt(segment.StartStation, segment.OffsetRight)));

        var run = new OverlayRun(poly, segment.Id, problems);
        if (!run.Load(baseSurface, comparisonSurface)) return Failed();

        // Station search window: points inside the band project into [Start, End] except near a
        // mitred angle point, where they can sit up to about 4 offsets beyond it.
        double margin = 4.0 * Math.Max(Math.Abs(segment.OffsetLeft), Math.Abs(segment.OffsetRight)) + 1e-6 * poly.Extent;
        var ctx = new ImpactContext(elev, settings.MinCutDepth, locator,
                                    segment.StartStation - margin, segment.EndStation + margin);
        run.Overlay(ctx);
        double ground = run.GroundBelow(elev);
        bool touches = lateral is not null && run.TouchesSearchLimit(lateral, elev);
        var union = StationIntervals.Union(ctx.Intervals, segment.StartStation, segment.EndStation, settings.GapTolerance);
        var totals = run.Totals();
        var impact = new ImpactResult(segment.Id, material,
            union.TotalLength, ctx.ImpactedArea.Value, ctx.CutVolume.Value, union.Intervals,
            ctx.CutAbove.Value, ctx.FillBelow.Value, touches);

        run.ReportCoverage(totals);
        if (lateral is null)
            problems.Add(new Problem(IntegratorCodes.SearchLimit, Severity.Warning,
                "The lateral edges of the boundary could not be matched to the segment's offsets, so the search-limit check did not run.",
                segment.Id, "Check that the boundary was generated from this segment's stations and offsets."));
        if (touches)
            problems.Add(new Problem(IntegratorCodes.SearchLimit, Severity.Warning,
                $"Ground below OHWM {elev:0.00} reaches a lateral edge of the boundary; impacts may continue outside the offsets.",
                segment.Id, "Widen the offsets."));
        if (!(ground > 0))
            problems.Add(new Problem(IntegratorCodes.NoGroundBelowOhwm, Severity.Info,
                $"No existing ground in this segment is below OHWM {elev:0.00}.", segment.Id));
        return new SegmentImpactResult(segment.Id, elev, totals, impact, ground, problems);
    }
}

/// <summary>Per-segment impact state threaded through the overlay loop.</summary>
internal sealed class ImpactContext
{
    public readonly double Ohwm, MinCutDepth, WindowFrom, WindowTo;
    public readonly StationLocator Locator;
    public NeumaierSum ImpactedArea, CutVolume, CutAbove, FillBelow;
    public readonly List<(double From, double To)> Intervals = [];

    public ImpactContext(double ohwm, double minCutDepth, StationLocator locator, double windowFrom, double windowTo)
    {
        Ohwm = ohwm; MinCutDepth = minCutDepth; Locator = locator; WindowFrom = windowFrom; WindowTo = windowTo;
    }
}

/// <summary>One integration over one boundary: loaded triangles, indexes, scratch buffers, accumulators.</summary>
internal sealed class OverlayRun
{
    private readonly BoundaryPolygon _poly;
    private readonly Guid? _segmentId;
    private readonly List<Problem> _problems;
    private SurfaceTriangle[] _base = [], _comp = [];
    private GridIndex _baseGrid = null!, _bndGrid = null!;
    private readonly List<int> _cand = [], _cand2 = [];
    private readonly PieceVertex[] _t0 = new PieceVertex[PieceMath.MaxVertices], _t1 = new PieceVertex[PieceMath.MaxVertices],
                                   _t2 = new PieceVertex[PieceMath.MaxVertices], _t3 = new PieceVertex[PieceMath.MaxVertices],
                                   _t4 = new PieceVertex[PieceMath.MaxVertices], _s0 = new PieceVertex[PieceMath.MaxVertices],
                                   _s1 = new PieceVertex[PieceMath.MaxVertices], _s2 = new PieceVertex[PieceMath.MaxVertices],
                                   _s3 = new PieceVertex[PieceMath.MaxVertices], _s4 = new PieceVertex[PieceMath.MaxVertices];
    private NeumaierSum _plan, _cut, _fill;

    public OverlayRun(BoundaryPolygon poly, Guid? segmentId, List<Problem> problems)
    {
        _poly = poly; _segmentId = segmentId; _problems = problems;
    }

    /// <summary>Requests both surfaces' triangles for the boundary window only, translates them, builds the indexes.</summary>
    public bool Load(ITriangleSource baseSrc, ITriangleSource compSrc)
    {
        double pad = 1e-9 * _poly.Extent;
        var wmin = new Pt(_poly.OriginX - pad, _poly.OriginY - pad);
        var wmax = new Pt(_poly.OriginX + _poly.MaxX + pad, _poly.OriginY + _poly.MaxY + pad);
        bool okB = LoadOne(baseSrc, "base", wmin, wmax, out _base, out int degB);
        bool okC = LoadOne(compSrc, "comparison", wmin, wmax, out _comp, out int degC);
        if (!okB || !okC) return false;
        if (degB + degC > 0)
            _problems.Add(new Problem(IntegratorCodes.DegenerateTriangles, Severity.Info,
                $"Ignored {degB} base and {degC} comparison triangles with no plan area (vertical or sliver).", _segmentId));

        var boxes = new (double, double, double, double)[_base.Length];
        for (int i = 0; i < _base.Length; i++) boxes[i] = (_base[i].Plan.MinX, _base[i].Plan.MinY, _base[i].Plan.MaxX, _base[i].Plan.MaxY);
        _baseGrid = new GridIndex(boxes, 0, 0, _poly.MaxX, _poly.MaxY);
        var tris = _poly.Triangles;
        var tboxes = new (double, double, double, double)[tris.Length];
        for (int i = 0; i < tris.Length; i++) tboxes[i] = (tris[i].MinX, tris[i].MinY, tris[i].MaxX, tris[i].MaxY);
        _bndGrid = new GridIndex(tboxes, 0, 0, _poly.MaxX, _poly.MaxY);
        return true;
    }

    private bool LoadOne(ITriangleSource src, string role, Pt wmin, Pt wmax, out SurfaceTriangle[] tris, out int degenerate)
    {
        var list = new List<SurfaceTriangle>();
        int returned = 0;
        degenerate = 0;
        tris = [];
        double s = _poly.Snap, ox = _poly.OriginX, oy = _poly.OriginY;
        double world = Math.Max(Math.Max(Math.Abs(wmin.X), Math.Abs(wmax.X)), Math.Max(Math.Abs(wmin.Y), Math.Abs(wmax.Y)));
        foreach (var t in src.Triangles(wmin, wmax))
        {
            returned++;
            if (!Finite(t.A) || !Finite(t.B) || !Finite(t.C))
            {
                _problems.Add(new Problem(IntegratorCodes.EmptySurface, Severity.Error,
                    $"The {role} surface returned a triangle with a non-finite coordinate. No volume was computed.", _segmentId,
                    "Rebuild the surface."));
                return false;
            }
            if (!SurfaceTriangle.TryCreate(t, ox, oy, world, out var st, out bool deg))
            {
                if (deg) degenerate++;
                continue;
            }
            var p = st.Plan;
            if (p.MaxX < -s || p.MinX > _poly.MaxX + s || p.MaxY < -s || p.MinY > _poly.MaxY + s) continue;
            list.Add(st);
        }
        if (returned == 0)
        {
            _problems.Add(new Problem(IntegratorCodes.EmptySurface, Severity.Error,
                $"The {role} surface has no triangles inside this segment's window (empty surface, or no data here). No volume was computed.",
                _segmentId, "Check the surface, or move the segment onto the surface data."));
            return false;
        }
        tris = [.. list];
        return true;
    }

    private static bool Finite(Pt3 p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

    private static bool Disjoint(in ClipTriangle a, double minX, double minY, double maxX, double maxY, double s)
        => a.MaxX < minX - s || a.MinX > maxX + s || a.MaxY < minY - s || a.MinY > maxY + s;

    /// <summary>Steps 2 to 5 and 7: every comparison/base piece inside the boundary.</summary>
    public void Overlay(ImpactContext? ctx)
    {
        double snap = _poly.Snap;
        var bt = _poly.Triangles;
        foreach (ref readonly var c in _comp.AsSpan())
        {
            var cp = c.Plan;
            _baseGrid.Query(cp.MinX, cp.MinY, cp.MaxX, cp.MaxY, _cand);
            foreach (int bi in _cand)
            {
                ref readonly var b = ref _base[bi];
                if (Disjoint(b.Plan, cp.MinX, cp.MinY, cp.MaxX, cp.MaxY, snap)) continue;
                _t0[0] = new PieceVertex(cp.X0, cp.Y0);
                _t0[1] = new PieceVertex(cp.X1, cp.Y1);
                _t0[2] = new PieceVertex(cp.X2, cp.Y2);
                var piece = b.Plan.Clip(_t0.AsSpan(0, 3), _t1, _t2, snap);
                if (piece.Length < 3) continue;

                double pminX = double.PositiveInfinity, pminY = double.PositiveInfinity;
                double pmaxX = double.NegativeInfinity, pmaxY = double.NegativeInfinity;
                foreach (var v in piece)
                {
                    pminX = Math.Min(pminX, v.X); pmaxX = Math.Max(pmaxX, v.X);
                    pminY = Math.Min(pminY, v.Y); pmaxY = Math.Max(pmaxY, v.Y);
                }
                _bndGrid.Query(pminX, pminY, pmaxX, pmaxY, _cand2);
                foreach (int ti in _cand2)
                {
                    ref readonly var t = ref bt[ti];
                    if (Disjoint(t, pminX, pminY, pmaxX, pmaxY, snap)) continue;
                    var q = t.Clip(piece, _t3, _t4, snap);
                    if (q.Length < 3) continue;
                    for (int k = 0; k < q.Length; k++)
                    {
                        double zb = b.Eval(q[k].X, q[k].Y);
                        q[k].Zb = zb;
                        q[k].D = zb - c.Eval(q[k].X, q[k].Y);
                    }
                    ProcessPiece(q, ctx, b.Noise + c.Noise, b.Noise);
                }
            }
        }
    }

    /// <param name="q">Piece with Zb and D at its vertices.</param>
    /// <param name="ctx">Impact state, or null for band totals only.</param>
    /// <param name="depthNoise">Evaluation error bound on D (base plus comparison triangle), drawing units.</param>
    /// <param name="baseNoise">Evaluation error bound on Zb, drawing units.</param>
    private void ProcessPiece(ReadOnlySpan<PieceVertex> q, ImpactContext? ctx, double depthNoise, double baseNoise)
    {
        PieceMath.Integrate(q, out double area, out double vol);
        if (!(area > 0)) return;
        _plan.Add(area);

        double minD = double.PositiveInfinity, maxD = double.NegativeInfinity;
        foreach (var v in q)
        {
            minD = Math.Min(minD, v.D); maxD = Math.Max(maxD, v.D);
        }
        if (minD >= 0) _cut.Add(vol);
        else if (maxD <= 0) _fill.Add(-vol);
        else
        {
            _cut.Add(PieceMath.PositivePart(q, _s0));
            _fill.Add(PieceMath.NegativePart(q, _s0));
        }
        if (ctx is null) return;

        // Impact path: snap plane noise (see the header) on a copy, then clip.
        double ohwm = ctx.Ohwm;
        double minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;
        for (int k = 0; k < q.Length; k++)
        {
            var v = q[k];
            if (Math.Abs(v.D) <= depthNoise) v.D = 0;
            if (Math.Abs(v.Zb - ohwm) <= baseNoise) v.Zb = ohwm;
            minZ = Math.Min(minZ, v.Zb); maxZ = Math.Max(maxZ, v.Zb);
            _s4[k] = v;
        }
        q = _s4.AsSpan(0, q.Length);
        ReadOnlySpan<PieceVertex> below, above;
        if (maxZ < ohwm) { below = q; above = default; }
        else if (minZ >= ohwm) { below = default; above = q; }
        else
        {
            int nb = PieceMath.ClipScalar(q, _s1, PieceAttr.Zb, ohwm, keepAbove: false, strict: true);
            int na = PieceMath.ClipScalar(q, _s2, PieceAttr.Zb, ohwm, keepAbove: true, strict: false);
            below = _s1.AsSpan(0, nb);
            above = _s2.AsSpan(0, na);
        }
        if (above.Length >= 3) ctx.CutAbove.Add(PieceMath.PositivePart(above, _s0));
        if (below.Length < 3) return;

        ctx.FillBelow.Add(PieceMath.NegativePart(below, _s0));
        int ni = PieceMath.ClipScalar(below, _s3, PieceAttr.D, ctx.MinCutDepth, keepAbove: true, strict: true);
        if (ni < 3) return;
        var imp = _s3.AsSpan(0, ni);
        PieceMath.Integrate(imp, out double ia, out double iv);
        if (!(ia > 0)) return;
        ctx.ImpactedArea.Add(ia);
        ctx.CutVolume.Add(iv);
        if (!(2.0 * ia / PieceMath.Perimeter(imp) > _poly.Snap)) return;   // clipping artifact: no interval

        double sMin = double.PositiveInfinity, sMax = double.NegativeInfinity;
        foreach (var v in imp)
        {
            double st = ctx.Locator.Locate(new Pt(v.X, v.Y), ctx.WindowFrom, ctx.WindowTo).Station;
            sMin = Math.Min(sMin, st); sMax = Math.Max(sMax, st);
        }
        ctx.Intervals.Add((sMin, sMax));
    }

    /// <summary>Plan area inside the boundary where the base exists and is strictly below <paramref name="ohwm"/>, square drawing units (E1001).</summary>
    public double GroundBelow(double ohwm)
    {
        double snap = _poly.Snap;
        var bt = _poly.Triangles;
        var sum = new NeumaierSum();
        foreach (ref readonly var b in _base.AsSpan())
        {
            var bp = b.Plan;
            _bndGrid.Query(bp.MinX, bp.MinY, bp.MaxX, bp.MaxY, _cand2);
            foreach (int ti in _cand2)
            {
                ref readonly var t = ref bt[ti];
                if (Disjoint(t, bp.MinX, bp.MinY, bp.MaxX, bp.MaxY, snap)) continue;
                _t0[0] = new PieceVertex(bp.X0, bp.Y0);
                _t0[1] = new PieceVertex(bp.X1, bp.Y1);
                _t0[2] = new PieceVertex(bp.X2, bp.Y2);
                var q = t.Clip(_t0.AsSpan(0, 3), _t3, _t4, snap);
                if (q.Length < 3) continue;
                double minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;
                for (int k = 0; k < q.Length; k++)
                {
                    double zb = b.Eval(q[k].X, q[k].Y);
                    q[k].Zb = Math.Abs(zb - ohwm) <= b.Noise ? ohwm : zb;
                    minZ = Math.Min(minZ, q[k].Zb); maxZ = Math.Max(maxZ, q[k].Zb);
                }
                if (minZ >= ohwm) continue;
                double area;
                if (maxZ < ohwm) PieceMath.Integrate(q, out area, out _);
                else
                {
                    int n = PieceMath.ClipScalar(q, _s1, PieceAttr.Zb, ohwm, keepAbove: false, strict: true);
                    PieceMath.Integrate(_s1.AsSpan(0, n), out area, out _);
                }
                if (area > 0) sum.Add(area);
            }
        }
        return sum.Value;
    }

    /// <summary>True when the base surface is strictly below <paramref name="ohwm"/> anywhere along a lateral edge (E1002).</summary>
    public bool TouchesSearchLimit(List<(double Ax, double Ay, double Bx, double By)> edges, double ohwm)
    {
        double snap = _poly.Snap;
        foreach (var (ax, ay, bx, by) in edges)
        {
            _baseGrid.Query(Math.Min(ax, bx) - snap, Math.Min(ay, by) - snap, Math.Max(ax, bx) + snap, Math.Max(ay, by) + snap, _cand);
            foreach (int bi in _cand)
            {
                ref readonly var b = ref _base[bi];
                if (!ClipSegment(b.Plan, ax, ay, bx, by, snap, out double t0, out double t1) || !(t1 > t0)) continue;
                double z0 = b.Eval(ax + t0 * (bx - ax), ay + t0 * (by - ay));
                double z1 = b.Eval(ax + t1 * (bx - ax), ay + t1 * (by - ay));
                if (Math.Min(z0, z1) < ohwm - b.Noise) return true;
            }
        }
        return false;
    }

    /// <summary>Parametric clip of segment A-B to a triangle grown by <paramref name="snap"/> (Cyrus-Beck).</summary>
    private static bool ClipSegment(in ClipTriangle t, double ax, double ay, double bx, double by, double snap, out double t0, out double t1)
    {
        t0 = 0; t1 = 1;
        return Edge(t.N0x, t.N0y, t.C0, ax, ay, bx, by, snap, ref t0, ref t1)
            && Edge(t.N1x, t.N1y, t.C1, ax, ay, bx, by, snap, ref t0, ref t1)
            && Edge(t.N2x, t.N2y, t.C2, ax, ay, bx, by, snap, ref t0, ref t1);
    }

    private static bool Edge(double nx, double ny, double c, double ax, double ay, double bx, double by, double snap, ref double t0, ref double t1)
    {
        double sa = nx * ax + ny * ay - c + snap, sb = nx * bx + ny * by - c + snap;
        if (sa < 0 && sb < 0) return false;
        if (sa < 0) t0 = Math.Max(t0, sa / (sa - sb));
        else if (sb < 0) t1 = Math.Min(t1, sa / (sa - sb));
        return t1 >= t0;
    }

    public BandTotals Totals() => new(_poly.Area, _plan.Value, _cut.Value, _fill.Value);

    public void ReportCoverage(BandTotals totals)
    {
        if (!(totals.PlanArea > 0))
            _problems.Add(new Problem(IntegratorCodes.NoOverlap, Severity.Warning,
                "The base and comparison surfaces do not overlap inside this boundary: volumes are 0 and coverage is 0%.",
                _segmentId, "Check the surfaces and the segment location."));
        else if (totals.Coverage < 1 - 1e-9)
            _problems.Add(new Problem(IntegratorCodes.PartialCoverage, Severity.Warning,
                $"The surfaces cover {totals.Coverage * 100:0.####}% of the boundary; the uncovered part contributes nothing.",
                _segmentId, "Extend the surfaces or narrow the offsets."));
    }
}

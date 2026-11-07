// Overlay integrator and impacts (HANDOFF.md 6.15). Owner: C5.
//
// Rev 3 (NOW.md X5): OHWM is a surface, never an elevation. Under the HCFCD preset the analysis's
// base IS the OHWM surface, so impact cut is simply d = base - comparison > MinCutDepth. The
// base < OHWM clip, the OHWM-elevation snap and the step 7 QA accumulators are gone.
//
// Method (6.15 steps 1 to 6):
//  1. The boundary is validated and translated to a local origin (its bounding-box minimum);
//     every triangle is translated the same way before any area math (E1009).
//  2. Base triangles are indexed on a uniform grid. Each comparison triangle is intersected with
//     each overlapping base triangle (convex against convex, Sutherland-Hodgman). On a piece both
//     surfaces are planar, so depth d = base - comparison is linear.
//  3. The piece is clipped to the boundary: the boundary is ear-clipped once into triangles and the
//     piece is clipped against each triangle it meets (correct for concave boundaries).
//  4. Band totals: plan area, cut where d > 0, fill where d < 0. A piece where d changes sign is
//     split on d = 0 first; each part is integrated exactly (fan triangles, area * mean of vertex d).
//  5. Clip to d > MinCutDepth (half-plane clip on the linear vertex depth). What remains adds
//     impacted area, cut volume, and the station interval spanned by its vertices (station
//     extremes of a convex piece inside the band are at its vertices).
//  6. Impacted length = union of those intervals clamped to the segment, gaps shorter than
//     GapTolerance bridged, measured along the alignment (E1005, E1007, E1008).
// Plus: plan area where the base surface exists inside the boundary (BaseArea), E1001 when no cut
// is counted, the lateral search-limit test (E1002): a cut piece from step 5 has an edge on a
// lateral edge of the boundary, and (rev 3, D13) the impact outline: every step 5 piece's plan
// polygon goes to OutlineBuilder, whose union by edge cancellation is ImpactResult.Outlines.
//
// Depth source (rev 3, contracts ITriangleSource note): for a volume-surface analysis the adapter
// hands ONE triangle source whose Z is already d = base - comparison. Steps 2 and 3 reduce to
// clipping those triangles to the boundary; steps 4 to 6 and the outline are shared. The band
// plan area and BaseArea are then the plan area of the depth triangles inside the boundary.
//
// Numerical tolerances (never user-visible thresholds):
//  - snap = 1e-12 * S (S = boundary extent): a point this close to a clip line is on it, so
//    coincident TIN edges never produce slivers;
//  - a piece thinner than snap (2 * area / perimeter) adds its area and volume but no station
//    interval, no search-limit hit and no outline edges (it can only be a clipping artifact);
//  - outline edges match within 2 * snap (the search-limit tolerance), see OutlineBuilder;
//  - plane noise: on the impact path only (step 5 and the search limit), a vertex depth within the
//    two triangles' evaluation error bound (SurfaceTriangle.Noise, about 1e-12 ft near the origin,
//    1e-6 ft at state-plane coordinates) is set to exactly 0. Without this, two surfaces that
//    coincide but are triangulated differently report rounding noise (about 1e-13) as cut, and the
//    impacted length runs the whole segment (measured pre-rev-3: T28 gave 500 instead of 310 on a
//    rotated Fixture H). Band totals (step 4) are never snapped;
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
    /// Band totals inside a closed boundary: plan area where both surfaces
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
        return Band(run => run.Load(baseSurface, comparisonSurface), closedBoundary, segmentId);
    }

    /// <summary>
    /// Band totals inside a closed boundary from ONE depth triangle source (rev 3, a volume-surface
    /// analysis): each triangle's Z is already the depth d = base - comparison (d &gt; 0 cut). Plan
    /// area is the plan area of the depth triangles inside the boundary; cut and fill as for the pair.
    /// </summary>
    /// <param name="depth">Depth triangles (the volume surface's TIN, Z normalised to d by the adapter).</param>
    /// <param name="closedBoundary">Simple polygon, world coordinates, first == last, either orientation, may be concave.</param>
    /// <param name="segmentId">Carried into every <see cref="Problem"/>.</param>
    /// <returns>Totals, or null totals with an Error when the boundary is invalid or the source has no triangles in the window.</returns>
    public static BandResult IntegrateBand(ITriangleSource depth, IReadOnlyList<Pt> closedBoundary, Guid? segmentId = null)
    {
        ArgumentNullException.ThrowIfNull(depth);
        return Band(run => run.LoadDepth(depth), closedBoundary, segmentId);
    }

    private static BandResult Band(Func<OverlayRun, bool> load, IReadOnlyList<Pt> closedBoundary, Guid? segmentId)
    {
        var problems = new List<Problem>();
        var poly = BoundaryPolygon.TryCreate(closedBoundary, segmentId, problems);
        if (poly is null) return new BandResult(segmentId, null, problems);
        var run = new OverlayRun(poly, segmentId, problems);
        if (!load(run)) return new BandResult(segmentId, null, problems);
        run.Overlay(null);
        var totals = run.Totals();
        run.ReportCoverage(totals);
        return new BandResult(segmentId, totals, problems);
    }

    /// <summary>
    /// Band totals plus the HCFCD impact quantities for one station segment (6.15, 6.16). Rev 3:
    /// impact cut is where the comparison is below the base by more than MinCutDepth; under the
    /// HCFCD preset the base is the OHWM surface.
    /// </summary>
    /// <param name="baseSurface">Base (the OHWM surface under the HCFCD preset).</param>
    /// <param name="comparisonSurface">Comparison (proposed).</param>
    /// <param name="alignment">The run's alignment; stations of impacted pieces are measured along it (E1008).</param>
    /// <param name="segment">Station range (raw, End &gt; Start) and signed offsets (Left &lt; Right) of the segment.</param>
    /// <param name="closedBoundary">The segment's tessellated boundary, world coordinates, first == last.</param>
    /// <param name="material">Material label for the result row (the analysis name, 6.16).</param>
    /// <param name="settings">MinCutDepth and GapTolerance from config (E1007); null = defaults (count everything).</param>
    public static SegmentImpactResult IntegrateSegment(
        ITriangleSource baseSurface, ITriangleSource comparisonSurface,
        IAlignmentGeometry alignment, SegmentDef segment, IReadOnlyList<Pt> closedBoundary,
        string material, IntegratorSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(baseSurface);
        ArgumentNullException.ThrowIfNull(comparisonSurface);
        return Segment(run => run.Load(baseSurface, comparisonSurface), alignment, segment, closedBoundary, material, settings);
    }

    /// <summary>
    /// Band totals plus the HCFCD impact quantities for one station segment from ONE depth triangle
    /// source (rev 3, a volume-surface analysis): each triangle's Z is already d = base - comparison
    /// (d &gt; 0 cut). Steps 2 and 3 reduce to clipping those triangles to the boundary; everything
    /// from step 4 on, the outline included, is the pair path's. <see cref="SegmentImpactResult.BaseArea"/>
    /// is the plan area of the depth triangles inside the boundary (a volume surface exists only where
    /// both of its surfaces do, so the base's own extent is not visible through it).
    /// </summary>
    /// <param name="depth">Depth triangles (the volume surface's TIN, Z normalised to d by the adapter).</param>
    /// <param name="alignment">The run's alignment; stations of impacted pieces are measured along it (E1008).</param>
    /// <param name="segment">Station range (raw, End &gt; Start) and signed offsets (Left &lt; Right) of the segment.</param>
    /// <param name="closedBoundary">The segment's tessellated boundary, world coordinates, first == last.</param>
    /// <param name="material">Material label for the result row (the analysis name, 6.16).</param>
    /// <param name="settings">MinCutDepth and GapTolerance from config (E1007); null = defaults (count everything).</param>
    public static SegmentImpactResult IntegrateSegment(
        ITriangleSource depth, IAlignmentGeometry alignment, SegmentDef segment, IReadOnlyList<Pt> closedBoundary,
        string material, IntegratorSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(depth);
        return Segment(run => run.LoadDepth(depth), alignment, segment, closedBoundary, material, settings);
    }

    private static SegmentImpactResult Segment(Func<OverlayRun, bool> load,
        IAlignmentGeometry alignment, SegmentDef segment, IReadOnlyList<Pt> closedBoundary,
        string material, IntegratorSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(material);
        if (!double.IsFinite(segment.StartStation) || !double.IsFinite(segment.EndStation) || !(segment.EndStation > segment.StartStation))
            throw new ArgumentException("Segment end station must be greater than its start station.", nameof(segment));
        settings ??= IntegratorSettings.Default;

        var problems = new List<Problem>();
        SegmentImpactResult Failed() => new(segment.Id, null, null, double.NaN, problems);

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
        if (!load(run)) return Failed();

        // Station search window: points inside the band project into [Start, End] except near a
        // mitred angle point, where they can sit up to about 4 offsets beyond it.
        double margin = 4.0 * Math.Max(Math.Abs(segment.OffsetLeft), Math.Abs(segment.OffsetRight)) + 1e-6 * poly.Extent;
        var ctx = new ImpactContext(settings.MinCutDepth, locator,
                                    segment.StartStation - margin, segment.EndStation + margin, lateral,
                                    new OutlineBuilder(2.0 * poly.Snap));
        run.Overlay(ctx);
        double baseArea = run.BaseArea();
        bool touches = ctx.TouchesLateral;
        var union = StationIntervals.Union(ctx.Intervals, segment.StartStation, segment.EndStation, settings.GapTolerance);
        var totals = run.Totals();
        var impact = new ImpactResult(segment.Id, material,
            union.TotalLength, ctx.ImpactedArea.Value, ctx.CutVolume.Value, union.Intervals,
            ctx.Outline.Build(poly.OriginX, poly.OriginY),   // rev 3 (D13): union of the step 5 pieces
            touches);

        run.ReportCoverage(totals);
        if (lateral is null)
            problems.Add(new Problem(IntegratorCodes.SearchLimit, Severity.Warning,
                "The lateral edges of the boundary could not be matched to the segment's offsets, so the search-limit check did not run.",
                segment.Id, "Check that the boundary was generated from this segment's stations and offsets."));
        if (touches)
            problems.Add(new Problem(IntegratorCodes.SearchLimit, Severity.Warning,
                "Cut reaches a lateral edge of the boundary; impacts may continue outside the offsets.",
                segment.Id, "Widen the offsets."));
        if (!(impact.ImpactedArea > 0))
            problems.Add(new Problem(IntegratorCodes.NoCut, Severity.Info,
                "No cut in this segment: the comparison surface is nowhere below the base surface, or the two do not overlap.", segment.Id));
        return new SegmentImpactResult(segment.Id, totals, impact, baseArea, problems);
    }
}

/// <summary>Per-segment impact state threaded through the overlay loop.</summary>
internal sealed class ImpactContext
{
    public readonly double MinCutDepth, WindowFrom, WindowTo;
    public readonly StationLocator Locator;
    public readonly List<(double Ax, double Ay, double Bx, double By)>? Lateral;
    public NeumaierSum ImpactedArea, CutVolume;
    public bool TouchesLateral;
    public readonly List<(double From, double To)> Intervals = [];
    public readonly OutlineBuilder Outline;

    public ImpactContext(double minCutDepth, StationLocator locator, double windowFrom, double windowTo,
                         List<(double Ax, double Ay, double Bx, double By)>? lateral, OutlineBuilder outline)
    {
        MinCutDepth = minCutDepth; Locator = locator; WindowFrom = windowFrom; WindowTo = windowTo; Lateral = lateral;
        Outline = outline;
    }
}

/// <summary>
/// One integration over one boundary: loaded triangles, indexes, scratch buffers, accumulators.
/// Pair mode (<see cref="Load"/>) overlays base and comparison; depth mode (<see cref="LoadDepth"/>)
/// holds the depth triangles in the base slot and clips them to the boundary directly.
/// </summary>
internal sealed class OverlayRun
{
    private readonly BoundaryPolygon _poly;
    private readonly Guid? _segmentId;
    private readonly List<Problem> _problems;
    private bool _depth;
    private SurfaceTriangle[] _base = [], _comp = [];
    private GridIndex _baseGrid = null!, _bndGrid = null!;
    private readonly List<int> _cand = [], _cand2 = [];
    private readonly PieceVertex[] _t0 = new PieceVertex[PieceMath.MaxVertices], _t1 = new PieceVertex[PieceMath.MaxVertices],
                                   _t2 = new PieceVertex[PieceMath.MaxVertices], _t3 = new PieceVertex[PieceMath.MaxVertices],
                                   _t4 = new PieceVertex[PieceMath.MaxVertices], _s0 = new PieceVertex[PieceMath.MaxVertices],
                                   _s3 = new PieceVertex[PieceMath.MaxVertices], _s4 = new PieceVertex[PieceMath.MaxVertices];
    private NeumaierSum _plan, _cut, _fill;

    public OverlayRun(BoundaryPolygon poly, Guid? segmentId, List<Problem> problems)
    {
        _poly = poly; _segmentId = segmentId; _problems = problems;
    }

    /// <summary>Requests both surfaces' triangles for the boundary window only, translates them, builds the indexes.</summary>
    public bool Load(ITriangleSource baseSrc, ITriangleSource compSrc)
    {
        var (wmin, wmax) = Window();
        bool okB = LoadOne(baseSrc, "base", wmin, wmax, out _base, out int degB);
        bool okC = LoadOne(compSrc, "comparison", wmin, wmax, out _comp, out int degC);
        if (!okB || !okC) return false;
        if (degB + degC > 0)
            _problems.Add(new Problem(IntegratorCodes.DegenerateTriangles, Severity.Info,
                $"Ignored {degB} base and {degC} comparison triangles with no plan area (vertical or sliver).", _segmentId));

        var boxes = new (double, double, double, double)[_base.Length];
        for (int i = 0; i < _base.Length; i++) boxes[i] = (_base[i].Plan.MinX, _base[i].Plan.MinY, _base[i].Plan.MaxX, _base[i].Plan.MaxY);
        _baseGrid = new GridIndex(boxes, 0, 0, _poly.MaxX, _poly.MaxY);
        BuildBoundaryGrid();
        return true;
    }

    /// <summary>Depth mode: requests the depth triangles (Z = d) for the boundary window only, translates them, indexes the boundary.</summary>
    public bool LoadDepth(ITriangleSource depthSrc)
    {
        var (wmin, wmax) = Window();
        if (!LoadOne(depthSrc, "volume", wmin, wmax, out _base, out int deg)) return false;
        if (deg > 0)
            _problems.Add(new Problem(IntegratorCodes.DegenerateTriangles, Severity.Info,
                $"Ignored {deg} volume surface triangles with no plan area (vertical or sliver).", _segmentId));
        _depth = true;
        BuildBoundaryGrid();
        return true;
    }

    private (Pt Min, Pt Max) Window()
    {
        double pad = 1e-9 * _poly.Extent;
        return (new Pt(_poly.OriginX - pad, _poly.OriginY - pad), new Pt(_poly.OriginX + _poly.MaxX + pad, _poly.OriginY + _poly.MaxY + pad));
    }

    private void BuildBoundaryGrid()
    {
        var tris = _poly.Triangles;
        var tboxes = new (double, double, double, double)[tris.Length];
        for (int i = 0; i < tris.Length; i++) tboxes[i] = (tris[i].MinX, tris[i].MinY, tris[i].MaxX, tris[i].MaxY);
        _bndGrid = new GridIndex(tboxes, 0, 0, _poly.MaxX, _poly.MaxY);
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

    /// <summary>Steps 2 to 5: every comparison/base piece inside the boundary (depth mode: every depth triangle's part inside it).</summary>
    public void Overlay(ImpactContext? ctx)
    {
        double snap = _poly.Snap;
        var bt = _poly.Triangles;
        if (_depth)
        {
            foreach (ref readonly var d in _base.AsSpan())
            {
                var dp = d.Plan;
                _bndGrid.Query(dp.MinX, dp.MinY, dp.MaxX, dp.MaxY, _cand2);
                foreach (int ti in _cand2)
                {
                    ref readonly var t = ref bt[ti];
                    if (Disjoint(t, dp.MinX, dp.MinY, dp.MaxX, dp.MaxY, snap)) continue;
                    _t0[0] = new PieceVertex(dp.X0, dp.Y0);
                    _t0[1] = new PieceVertex(dp.X1, dp.Y1);
                    _t0[2] = new PieceVertex(dp.X2, dp.Y2);
                    var q = t.Clip(_t0.AsSpan(0, 3), _t3, _t4, snap);
                    if (q.Length < 3) continue;
                    for (int k = 0; k < q.Length; k++) q[k].D = d.Eval(q[k].X, q[k].Y);
                    ProcessPiece(q, ctx, d.Noise);
                }
            }
            return;
        }
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
                        q[k].D = b.Eval(q[k].X, q[k].Y) - c.Eval(q[k].X, q[k].Y);
                    ProcessPiece(q, ctx, b.Noise + c.Noise);
                }
            }
        }
    }

    /// <param name="q">Piece with D at its vertices.</param>
    /// <param name="ctx">Impact state, or null for band totals only.</param>
    /// <param name="depthNoise">Evaluation error bound on D (base plus comparison triangle), drawing units.</param>
    private void ProcessPiece(ReadOnlySpan<PieceVertex> q, ImpactContext? ctx, double depthNoise)
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

        // Impact path: snap plane noise (see the header) on a copy, then clip to d > MinCutDepth.
        for (int k = 0; k < q.Length; k++)
        {
            var v = q[k];
            if (Math.Abs(v.D) <= depthNoise) v.D = 0;
            _s4[k] = v;
        }
        q = _s4.AsSpan(0, q.Length);
        int ni = PieceMath.ClipScalar(q, _s3, PieceAttr.D, ctx.MinCutDepth, keepAbove: true, strict: true);
        if (ni < 3) return;
        var imp = _s3.AsSpan(0, ni);
        PieceMath.Integrate(imp, out double ia, out double iv);
        if (!(ia > 0)) return;
        ctx.ImpactedArea.Add(ia);
        ctx.CutVolume.Add(iv);
        if (!(2.0 * ia / PieceMath.Perimeter(imp) > _poly.Snap)) return;   // clipping artifact: no interval, no outline edges
        ctx.Outline.AddPiece(imp);

        double sMin = double.PositiveInfinity, sMax = double.NegativeInfinity;
        foreach (var v in imp)
        {
            double st = ctx.Locator.Locate(new Pt(v.X, v.Y), ctx.WindowFrom, ctx.WindowTo).Station;
            sMin = Math.Min(sMin, st); sMax = Math.Max(sMax, st);
        }
        ctx.Intervals.Add((sMin, sMax));
        if (!ctx.TouchesLateral && ctx.Lateral is not null && OnLateralEdge(imp, ctx.Lateral, ctx.MinCutDepth))
            ctx.TouchesLateral = true;
    }

    /// <summary>
    /// True when an edge of the cut piece, longer than snap and deeper than <paramref name="minCutDepth"/>
    /// at one end at least, lies on a lateral edge of the boundary (both its end points within 2 * snap
    /// of that edge): the cut reaches the search limit (E1002). An edge where the cut has died out to
    /// the threshold along the boundary (depth equal to it at both ends) does not count.
    /// </summary>
    private bool OnLateralEdge(ReadOnlySpan<PieceVertex> imp, List<(double Ax, double Ay, double Bx, double By)> lateral, double minCutDepth)
    {
        double tol = 2.0 * _poly.Snap;
        for (int k = 0; k < imp.Length; k++)
        {
            var p = imp[k];
            var r = imp[(k + 1) % imp.Length];
            if (!(Math.Max(p.D, r.D) > minCutDepth)) continue;
            if (!(Math.Sqrt((r.X - p.X) * (r.X - p.X) + (r.Y - p.Y) * (r.Y - p.Y)) > _poly.Snap)) continue;
            foreach (var (ax, ay, bx, by) in lateral)
                if (DistanceToSegment(p.X, p.Y, ax, ay, bx, by) <= tol && DistanceToSegment(r.X, r.Y, ax, ay, bx, by) <= tol)
                    return true;
        }
        return false;
    }

    private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        double t = len2 > 0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0.0, 1.0) : 0.0;
        double ex = ax + t * dx - px, ey = ay + t * dy - py;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    /// <summary>Plan area inside the boundary where the base surface exists (depth mode: where the depth triangles exist), square drawing units.</summary>
    public double BaseArea()
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
                PieceMath.Integrate(q, out double area, out _);
                if (area > 0) sum.Add(area);
            }
        }
        return sum.Value;
    }

    public BandTotals Totals() => new(_poly.Area, _plan.Value, _cut.Value, _fill.Value);

    public void ReportCoverage(BandTotals totals)
    {
        if (!(totals.PlanArea > 0))
            _problems.Add(new Problem(IntegratorCodes.NoOverlap, Severity.Warning,
                _depth ? "The volume surface has no data inside this boundary: volumes are 0 and coverage is 0%."
                       : "The base and comparison surfaces do not overlap inside this boundary: volumes are 0 and coverage is 0%.",
                _segmentId, "Check the surfaces and the segment location."));
        else if (totals.Coverage < 1 - 1e-9)
            _problems.Add(new Problem(IntegratorCodes.PartialCoverage, Severity.Warning,
                $"The surfaces cover {totals.Coverage * 100:0.####}% of the boundary; the uncovered part contributes nothing.",
                _segmentId, "Extend the surfaces or narrow the offsets."));
    }
}

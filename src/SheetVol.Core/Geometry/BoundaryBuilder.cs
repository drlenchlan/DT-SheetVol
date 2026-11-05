using SheetVol.Core.Planning;

namespace SheetVol.Core.Geometry;

/// <summary>
/// One piece of the whole-band polygon: a maximal run of contiguous built station segments
/// that share the same offsets. Its lateral vertices come from the same global station list
/// and the same offset points as the segments, so the sum of the segment areas equals the
/// piece area up to rounding (partition check, 6.5).
/// <para><b>What "band" means when offsets vary.</b> With per-segment offset overrides (rev 2)
/// the band is the union of the segment polygons. It is split into one piece per stretch of
/// equal offsets; two pieces meet on the shared cut line of the width change (see
/// <see cref="BoundaryBuilder"/>) and never overlap, so the band area and volume are the sums
/// over its pieces. The partition check compares the sum of the segments with the sum of the
/// pieces, and the outside-limits row subtracts the sum of the pieces from the surface totals.
/// Where every segment of a stretch has offsets of its own, each piece there is one segment,
/// and the check on that stretch only confirms that each segment was counted once.</para>
/// </summary>
/// <param name="StartStation">Raw station where the piece starts.</param>
/// <param name="EndStation">Raw station where the piece ends.</param>
/// <param name="SegmentIds">Segments the piece covers, in station order.</param>
/// <param name="Tessellated">Closed ring, first == last, counter-clockwise, WCS drawing units.</param>
public sealed record BandPiece(double StartStation, double EndStation, IReadOnlyList<Guid> SegmentIds, IReadOnlyList<Pt> Tessellated);

/// <summary>Output of <see cref="BoundaryBuilder.Build"/>.</summary>
/// <param name="Boundaries">One per segment that passed every check, in station order.</param>
/// <param name="Band">Whole-band pieces over the built segments.</param>
/// <param name="Stations">The global sorted station list (raw) every vertex was computed at.</param>
/// <param name="Problems">E201, E202, E204, E205, E214 problems; segment problems carry the segment ID.</param>
public sealed record BoundaryBuildResult(
    IReadOnlyList<Boundary> Boundaries,
    IReadOnlyList<BandPiece> Band,
    IReadOnlyList<double> Stations,
    IReadOnlyList<Problem> Problems);

/// <summary>
/// Boundary generation per HANDOFF.md 6.4. Conventions: raw stations; offsets signed,
/// negative left of the direction of travel; WCS drawing units. Rings run counter-clockwise:
/// the right offset line forward, the cut line at the end, the left offset line backward,
/// the cut line at the start.
/// <para><b>Per-segment offsets (rev 2).</b> Each segment is built with its own
/// <see cref="SegmentDef.OffsetLeft"/> and <see cref="SegmentDef.OffsetRight"/>. All offset
/// points at one station lie on one straight cut line through the alignment point: the
/// normal on lines, arcs and spirals, the mitre bisector at an angle point. At a break
/// between segments of different widths the shared cut line is the straight piece of that
/// line between the wider pair of offset points, from the smaller of the two left offsets to
/// the larger of the two right offsets. Each segment's ring runs along it between its own two
/// offset points, so the narrower segment's lateral edge meets the cut line at its own offset
/// and turns onto it. Both rings take their vertices at the break from the same station node
/// (one evaluation per offset), so they touch along their common part of the cut line,
/// overlap nowhere and leave no gap between them; the step in the band's outer edge is a
/// straight piece of the cut line.</para>
/// <para>One global station list serves every segment (6.4 step 1). An arc or spiral element is
/// subdivided for the largest offset of any segment that overlaps it, so a narrower
/// neighbour on the same element is tessellated finer than it needs, never coarser.</para>
/// </summary>
public static class BoundaryBuilder
{
    private sealed record Fault(string Code, Func<SegmentDef, (string Message, string Fix)> Describe);

    private readonly record struct Node(double Station, bool Hard, int Junction);

    /// <summary>
    /// Builds every segment with one pair of offsets. Same result as
    /// <see cref="Build(IAlignmentGeometry, IReadOnlyList{SegmentDef}, GeometryOptions)"/> when
    /// every segment carries these offsets, which this overload checks.
    /// </summary>
    /// <param name="alignment">Alignment geometry.</param>
    /// <param name="segments">Station segments sorted by station, not overlapping, inside the alignment, all with these offsets.</param>
    /// <param name="offsetLeft">Signed left offset, drawing units.</param>
    /// <param name="offsetRight">Signed right offset, drawing units; greater than the left.</param>
    /// <param name="options">Tolerances.</param>
    /// <exception cref="ArgumentException">Inputs break the stated preconditions (a caller defect).</exception>
    public static BoundaryBuildResult Build(IAlignmentGeometry alignment, IReadOnlyList<SegmentDef> segments, double offsetLeft, double offsetRight, GeometryOptions options)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (!double.IsFinite(offsetLeft) || !double.IsFinite(offsetRight) || !(offsetLeft < offsetRight))
            throw new ArgumentException("Offsets must be finite with left < right; the planner rejects them first (E210).");
        foreach (var s in segments)
            if (s.OffsetLeft != offsetLeft || s.OffsetRight != offsetRight)
                throw new ArgumentException($"Segment {s.Label} carries offsets ({s.OffsetLeft}, {s.OffsetRight}); the run builds ({offsetLeft}, {offsetRight}).");
        return Build(alignment, segments, options);
    }

    /// <summary>
    /// Builds and validates the boundary of every segment, each with its own offsets, and the
    /// whole-band pieces.
    /// </summary>
    /// <param name="alignment">Alignment geometry.</param>
    /// <param name="segments">Station segments sorted by station, not overlapping, inside the alignment, each with finite offsets, left less than right.</param>
    /// <param name="options">Tolerances.</param>
    /// <exception cref="ArgumentException">Inputs break the stated preconditions (a caller defect).</exception>
    public static BoundaryBuildResult Build(IAlignmentGeometry alignment, IReadOnlyList<SegmentDef> segments, GeometryOptions options)
    {
        CheckArguments(alignment, segments, options);
        var problems = new List<Problem>();
        if (segments.Count == 0) return new BoundaryBuildResult([], [], [], problems);

        double tol = options.StationTolerance;
        var model = AlignmentModel.Create(alignment, s => MaxAbsOffsetAt(segments, s, tol), options, problems);
        if (model is null) return new BoundaryBuildResult([], [], [], problems);

        double runStart = segments[0].StartStation, runEnd = segments[^1].EndStation;
        var junctionFaults = JunctionFaults(model, runStart, runEnd, options);
        var elementFaults = new Dictionary<(int Element, double Left, double Right), List<Fault>>();

        var nodes = BuildNodes(model, segments, runStart, runEnd, options);
        var lines = new OffsetLines(model, nodes);
        var stations = nodes.Select(n => n.Station).ToArray();

        var boundaries = new List<Boundary>();
        var built = new bool[segments.Count];
        var ranges = new (int A, int B)[segments.Count];
        for (int k = 0; k < segments.Count; k++)
        {
            var seg = segments[k];
            double[] offsets = [seg.OffsetLeft, seg.OffsetRight];
            int ia = IndexOf(stations, seg.StartStation, tol), ib = IndexOf(stations, seg.EndStation, tol);
            ranges[k] = (ia, ib);

            var faults = new List<Fault>();
            for (int e = 0; e < model.Count; e++)
            {
                if (!(model.Segs[e].StartStation < seg.EndStation - tol && model.Segs[e].EndStation > seg.StartStation + tol)) continue;
                var key = (e, seg.OffsetLeft, seg.OffsetRight);
                if (!elementFaults.TryGetValue(key, out var found))
                    elementFaults[key] = found = ElementFaults(model, e, offsets, options);
                faults.AddRange(found);
            }
            for (int j = 0; j < model.Count - 1; j++)
            {
                double sj = model.JunctionStation(j);
                if (sj >= seg.StartStation - tol && sj <= seg.EndStation + tol) faults.AddRange(junctionFaults[j]);
            }
            if (faults.Count == 0) faults.AddRange(MitreFolds(model, nodes, ia, ib, offsets, options));
            if (faults.Count > 0)
            {
                foreach (var f in faults)
                {
                    var (msg, fix) = f.Describe(seg);
                    problems.Add(new Problem(f.Code, Severity.Error, msg, seg.Id, fix));
                }
                continue;
            }

            var ring = Ring(lines, ia, ib, seg.OffsetLeft, seg.OffsetRight);
            string? reason = BoundaryValidator.Check(ring, requireCounterClockwise: true, minDistinctVertices: 4);
            if (reason is not null)
            {
                problems.Add(new Problem(ProblemCodes.E202, Severity.Error,
                    $"{Fmt.Segment(alignment, seg, tol)}: {reason}. No volume is computed on it.", seg.Id,
                    "Move the break away from the nearby angle point or curve, or reduce the offsets, so the boundary does not fold over itself."));
                continue;
            }

            boundaries.Add(new Boundary(seg.Id, Drawn(model, nodes, lines, ia, ib, seg.OffsetLeft, seg.OffsetRight), ring));
            built[k] = true;
        }

        // One piece per maximal run of contiguous built segments with equal offsets (see BandPiece).
        var band = new List<BandPiece>();
        for (int k = 0; k < segments.Count; k++)
        {
            if (!built[k]) continue;
            int first = k;
            while (k + 1 < segments.Count && built[k + 1] && segments[k + 1].StartStation == segments[k].EndStation
                   && segments[k + 1].OffsetLeft == segments[k].OffsetLeft && segments[k + 1].OffsetRight == segments[k].OffsetRight) k++;
            band.Add(new BandPiece(segments[first].StartStation, segments[k].EndStation,
                segments.Skip(first).Take(k - first + 1).Select(s => s.Id).ToArray(),
                Ring(lines, ranges[first].A, ranges[k].B, segments[first].OffsetLeft, segments[first].OffsetRight)));
        }

        return new BoundaryBuildResult(boundaries, band, stations, problems);
    }

    /// <summary>Largest offset magnitude of the segments whose range contains the station, 0 when none does.</summary>
    private static double MaxAbsOffsetAt(IReadOnlyList<SegmentDef> segments, double s, double tol)
    {
        double max = 0;
        foreach (var g in segments)
            if (s >= g.StartStation - tol && s <= g.EndStation + tol)
                max = Math.Max(max, Math.Max(Math.Abs(g.OffsetLeft), Math.Abs(g.OffsetRight)));
        return max;
    }

    /// <summary>Every offset (left and right) of the segments that overlap the station range by more than the tolerance.</summary>
    private static double[] OffsetsOver(IReadOnlyList<SegmentDef> segments, double from, double to, double tol) =>
        segments.Where(g => g.StartStation < to - tol && g.EndStation > from + tol)
                .SelectMany(g => new[] { g.OffsetLeft, g.OffsetRight })
                .ToArray();

    private static void CheckArguments(IAlignmentGeometry alignment, IReadOnlyList<SegmentDef> segments, GeometryOptions options)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(options);
        double tol = options.StationTolerance;
        for (int k = 0; k < segments.Count; k++)
        {
            var s = segments[k];
            if (!double.IsFinite(s.OffsetLeft) || !double.IsFinite(s.OffsetRight) || !(s.OffsetLeft < s.OffsetRight))
                throw new ArgumentException($"Segment {s.Label} has offsets ({s.OffsetLeft}, {s.OffsetRight}); they must be finite with left < right, and the planner rejects them first (E210).");
            if (!(s.EndStation > s.StartStation))
                throw new ArgumentException($"Segment {s.Label} has End <= Start.");
            if (s.StartStation < alignment.StartStation - tol || s.EndStation > alignment.EndStation + tol)
                throw new ArgumentException($"Segment {s.Label} lies outside the alignment; the planner marks such segments invalid (E305, E211).");
            if (k > 0 && s.StartStation < segments[k - 1].EndStation - tol)
                throw new ArgumentException($"Segment {s.Label} overlaps or precedes the previous segment.");
        }
    }

    /// <summary>E201 (offset reaches a curve or spiral center) and E204 (analytic cross-check) for element <paramref name="i"/> under one pair of offsets.</summary>
    private static List<Fault> ElementFaults(AlignmentModel m, int i, double[] offsets, GeometryOptions opt)
    {
        var faults = new List<Fault>();
        double tol = opt.StationTolerance;
        var a = m.Source;
        var g = m.Segs[i];

        bool inside = false;
        if (g.Kind != SegKind.Line)
        {
            foreach (double o in offsets)
            {
                foreach (double k in new[] { m.K1[i], m.K2[i] })
                {
                    if (1 + k * o > 0) continue;
                    inside = true;
                    string side = o < 0 ? "left" : "right";
                    double radius = 1 / Math.Abs(k);
                    string what = g.Kind == SegKind.Arc
                        ? $"the curve at {Fmt.Station(a, g.StartStation, tol)} (radius {Fmt.Num(radius)})"
                        : $"the spiral at {Fmt.Station(a, g.StartStation, tol)}, which tightens to radius {Fmt.Num(radius)}";
                    faults.Add(new Fault(ProblemCodes.E201, s => (
                        $"{Fmt.Segment(a, s, tol)}: the {side} offset {Fmt.Num(Math.Abs(o))} reaches the center of {what}. The largest usable {side} offset there is less than {Fmt.Num(radius)}.",
                        $"Use a {side} offset smaller than {Fmt.Num(radius)}.")));
                    break;
                }
                if (inside) break;
            }
        }

        if (inside || g.Kind == SegKind.Spiral) return faults;

        // Analytic cross-check (E204): closed-form offset points against the alignment's own.
        double mid = 0.5 * (g.StartStation + g.EndStation);
        double worst = 0;
        foreach (double o in offsets)
        {
            var p = m.Eval(i, mid, o);
            var q = a.PointAt(mid, o);
            worst = Math.Max(worst, Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y)));
        }
        if (worst > opt.MidOrdinate)
        {
            string kind = g.Kind.ToString().ToLowerInvariant();
            faults.Add(new Fault(ProblemCodes.E204, s => (
                $"{Fmt.Segment(a, s, tol)}: the alignment data check failed on the {kind} at {Fmt.Station(a, g.StartStation, tol)}: its computed offset point differs from the alignment's own point by {Fmt.Num(worst)}.",
                "This points to an alignment adapter defect, not a drafting error. Report it with the drawing.")));
        }
        return faults;
    }

    /// <summary>E205 per junction inside the run: angle points above the mitre limit and non-tangent curve joins.</summary>
    private static List<Fault>[] JunctionFaults(AlignmentModel m, double runStart, double runEnd, GeometryOptions opt)
    {
        var faults = new List<Fault>[Math.Max(0, m.Count - 1)];
        double tol = opt.StationTolerance, limit = opt.MitreLimitDeg * Math.PI / 180;
        var a = m.Source;
        for (int j = 0; j < faults.Length; j++)
        {
            faults[j] = [];
            double sj = m.JunctionStation(j), d = m.Deflection[j];
            if (sj < runStart - tol || sj > runEnd + tol) continue;
            string at = Fmt.Station(a, sj, tol);
            if (m.Kinds[j] == JunctionKind.AnglePoint && Math.Abs(d) > limit)
            {
                faults[j].Add(new Fault(ProblemCodes.E205, s => (
                    $"{Fmt.Segment(a, s, tol)}: the angle point at {at} deflects {Fmt.Deg(Math.Abs(d))} degrees, above the mitre limit of {Fmt.Deg(limit)} degrees.",
                    "Add a curve at the angle point, or raise MitreLimitDeg if the sharp join is intended.")));
            }
            else if (m.Kinds[j] == JunctionKind.NonTangentCurve)
            {
                faults[j].Add(new Fault(ProblemCodes.E205, s => (
                    $"{Fmt.Segment(a, s, tol)}: the alignment turns {Fmt.Deg(Math.Abs(d))} degrees at {at} where a curve or spiral joins without tangency. Only tangent-to-tangent angle points are mitred.",
                    "Make the curve tangent to its neighbours, or put a short tangent between them.")));
            }
        }
        return faults;
    }

    /// <summary>
    /// Inside a mitre the two offset lines cross before the angle point: the mitre point
    /// projects onto the incoming tangent at AP + o tan(d/2) and onto the outgoing tangent at
    /// AP - o tan(d/2). Along every tangent between two consecutive stations of the segment the
    /// projected positions must still advance, or the offset line folds back past a cut line
    /// (a break, an element end or another angle point too close to the angle point). Such a
    /// ring can stay simple while it overlaps its neighbour, so it is caught here (E205).
    /// </summary>
    private static List<Fault> MitreFolds(AlignmentModel m, List<Node> nodes, int ia, int ib, double[] offsets, GeometryOptions opt)
    {
        var faults = new List<Fault>();
        double tol = opt.StationTolerance;
        var a = m.Source;
        for (int i = ia; i < ib; i++)
        {
            double s0 = nodes[i].Station, s1 = nodes[i + 1].Station;
            int e = m.ElementAt(0.5 * (s0 + s1));
            if (m.Segs[e].Kind != SegKind.Line) continue;
            int jStart = nodes[i].Junction == e - 1 && e > 0 && m.Kinds[e - 1] == JunctionKind.AnglePoint ? e - 1 : -1;
            int jEnd = nodes[i + 1].Junction == e && e < m.Count - 1 && m.Kinds[e] == JunctionKind.AnglePoint ? e : -1;
            if (jStart < 0 && jEnd < 0) continue;
            foreach (double o in offsets)
            {
                double p0 = jStart >= 0 ? s0 - o * Math.Tan(m.Deflection[jStart] / 2) : s0;
                double p1 = jEnd >= 0 ? s1 + o * Math.Tan(m.Deflection[jEnd] / 2) : s1;
                if (p1 - p0 > tol) continue;
                double need = (s1 - s0) - (p1 - p0);
                string side = o < 0 ? "left" : "right";
                string ap = string.Join(" and ", new[] { jStart, jEnd }.Where(j => j >= 0).Select(j => Fmt.Station(a, m.JunctionStation(j), tol)));
                faults.Add(new Fault(ProblemCodes.E205, s => (
                    $"{Fmt.Segment(a, s, tol)}: the inside {side} offset line at the angle point at {ap} folds back past the cut at {Fmt.Station(a, jEnd >= 0 ? s0 : s1, tol)}. The straight from {Fmt.Station(a, s0, tol)} to {Fmt.Station(a, s1, tol)} is {Fmt.Num(s1 - s0)} long; the mitre needs more than {Fmt.Num(need)}.",
                    "Move the break farther from the angle point or exactly onto it, reduce the inside offset, or add a curve at the angle point.")));
                break;
            }
        }
        return faults;
    }

    /// <summary>
    /// The global station list for the run (6.4 step 1): breaks and element ends are hard;
    /// arc and spiral subdivisions are soft and yield to a hard station within tolerance. Each
    /// element is subdivided for every offset of the segments that overlap it.
    /// </summary>
    private static List<Node> BuildNodes(AlignmentModel m, IReadOnlyList<SegmentDef> segments, double runStart, double runEnd, GeometryOptions opt)
    {
        double tol = opt.StationTolerance;
        var raw = new List<(double S, bool Hard)>();
        foreach (var s in segments)
        {
            raw.Add((s.StartStation, true));
            raw.Add((s.EndStation, true));
        }
        for (int i = 0; i < m.Count; i++)
        {
            var g = m.Segs[i];
            if (g.EndStation <= runStart || g.StartStation >= runEnd) continue;
            if (g.StartStation > runStart) raw.Add((g.StartStation, true));
            if (g.EndStation < runEnd) raw.Add((g.EndStation, true));

            int pieces = Subdivisions(m, i, OffsetsOver(segments, g.StartStation, g.EndStation, tol), opt);
            double len = g.EndStation - g.StartStation;
            for (int k = 1; k < pieces; k++)
            {
                double s = g.StartStation + len * k / pieces;
                if (s > runStart && s < runEnd) raw.Add((s, false));
            }
        }

        raw.Sort((x, y) => x.S.CompareTo(y.S));
        var merged = new List<(double S, bool Hard)>();
        foreach (var r in raw)
        {
            if (merged.Count > 0 && r.S - merged[^1].S <= tol)
            {
                var last = merged[^1];
                if (r.Hard && !last.Hard) merged[^1] = r;
                continue;
            }
            merged.Add(r);
        }
        return merged.Select(r => new Node(r.S, r.Hard, m.JunctionAt(r.S, tol))).ToList();
    }

    /// <summary>
    /// Number of equal station steps for element i so the mid-ordinate on the largest offset
    /// radius among <paramref name="offsets"/> stays within tolerance; 1 when no offset applies.
    /// </summary>
    private static int Subdivisions(AlignmentModel m, int i, double[] offsets, GeometryOptions opt)
    {
        var g = m.Segs[i];
        double len = g.EndStation - g.StartStation;
        if (offsets.Length == 0) return 1;
        if (g.Kind == SegKind.Arc)
        {
            double rMax = offsets.Max(o => m.OffsetRadius(i, o));
            double sweep = len / g.Radius;
            return Math.Max(1, (int)Math.Ceiling(sweep / Polygon.ArcStep(rMax, opt.MidOrdinate) - 1e-9));
        }
        if (g.Kind == SegKind.Spiral)
        {
            // Sagitta of a station step h on the offset curve is about h^2 k (1 + k o) / 8. Bound it with
            // the largest end curvature and the largest offset magnitude, then cap the step at a quarter turn.
            double k = Math.Max(Math.Abs(m.K1[i]), Math.Abs(m.K2[i]));
            if (k <= 0) return 1;
            double factor = k * (1 + k * offsets.Max(Math.Abs));
            double h = Math.Min(Math.Sqrt(8 * opt.MidOrdinate / factor), Math.PI / 2 / k);
            return Math.Max(1, (int)Math.Ceiling(len / h - 1e-9));
        }
        return 1;
    }

    private static int IndexOf(double[] stations, double s, double tol)
    {
        int i = Array.BinarySearch(stations, s);
        if (i >= 0) return i;
        i = ~i;
        if (i < stations.Length && Math.Abs(stations[i] - s) <= tol) return i;
        if (i > 0 && Math.Abs(stations[i - 1] - s) <= tol) return i - 1;
        throw new InvalidOperationException($"Station {s} is missing from the global station list.");
    }

    /// <summary>
    /// Offset points at the station nodes, computed once per offset value and node and shared
    /// by every segment and band piece that uses them: two neighbours with the same offset get
    /// the identical point at their common break.
    /// </summary>
    private sealed class OffsetLines(AlignmentModel model, List<Node> nodes)
    {
        private readonly Dictionary<double, Pt?[]> _lines = [];

        public Pt At(int node, double offset)
        {
            if (!_lines.TryGetValue(offset, out var line)) _lines[offset] = line = new Pt?[nodes.Count];
            return line[node] ??= model.Vertex(nodes[node].Station, nodes[node].Junction, offset);
        }
    }

    private static Pt[] Ring(OffsetLines lines, int ia, int ib, double offsetLeft, double offsetRight)
    {
        var ring = new Pt[2 * (ib - ia + 1) + 1];
        int n = 0;
        for (int i = ia; i <= ib; i++) ring[n++] = lines.At(i, offsetRight);
        for (int i = ib; i >= ia; i--) ring[n++] = lines.At(i, offsetLeft);
        ring[n] = ring[0];
        return ring;
    }

    /// <summary>
    /// Polyline vertices with bulges: hard stations everywhere, every station inside a
    /// spiral, and one bulge per arc piece between consecutive kept stations (concentric arc
    /// of radius R plus or minus the offset).
    /// </summary>
    private static PolyVertex[] Drawn(AlignmentModel m, List<Node> nodes, OffsetLines lines, int ia, int ib, double offsetLeft, double offsetRight)
    {
        var keep = new List<int>();
        for (int i = ia; i <= ib; i++)
        {
            if (i == ia || i == ib || nodes[i].Hard || m.Segs[m.ElementAt(nodes[i].Station)].Kind == SegKind.Spiral)
                keep.Add(i);
        }

        // Bulge of the right line traversed forward between keep[t] and keep[t+1].
        var bulge = new double[keep.Count - 1];
        for (int t = 0; t < bulge.Length; t++)
        {
            double s0 = nodes[keep[t]].Station, s1 = nodes[keep[t + 1]].Station;
            int e = m.ElementAt(0.5 * (s0 + s1));
            var g = m.Segs[e];
            if (g.Kind != SegKind.Arc) continue;
            double sweep = (s1 - s0) / g.Radius;
            bulge[t] = (g.Clockwise ? -1 : 1) * Math.Tan(sweep / 4);
        }

        var drawn = new List<PolyVertex>(2 * keep.Count);
        for (int t = 0; t < keep.Count; t++)
            drawn.Add(new PolyVertex(lines.At(keep[t], offsetRight), t < bulge.Length ? bulge[t] : 0));
        for (int t = keep.Count - 1; t >= 0; t--)
            drawn.Add(new PolyVertex(lines.At(keep[t], offsetLeft), t > 0 ? -bulge[t - 1] : 0));
        return drawn.ToArray();
    }
}

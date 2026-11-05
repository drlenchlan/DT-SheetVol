using SheetVol.Core.Planning;

namespace SheetVol.Core.Geometry;

/// <summary>
/// One piece of the whole-band polygon: a maximal run of contiguous built station segments.
/// Its lateral vertices come from the same global station list as the segments, so the sum
/// of the segment areas equals the band area up to rounding (partition check, 6.5).
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
/// </summary>
public static class BoundaryBuilder
{
    private sealed record Fault(string Code, Func<SegmentDef, (string Message, string Fix)> Describe);

    private readonly record struct Node(double Station, bool Hard, int Junction);

    /// <summary>
    /// Builds and validates the boundary of every segment and the whole-band pieces.
    /// </summary>
    /// <param name="alignment">Alignment geometry.</param>
    /// <param name="segments">Station segments sorted by station, not overlapping, inside the alignment, all with these offsets.</param>
    /// <param name="offsetLeft">Signed left offset, drawing units.</param>
    /// <param name="offsetRight">Signed right offset, drawing units; greater than the left.</param>
    /// <param name="options">Tolerances.</param>
    /// <exception cref="ArgumentException">Inputs break the stated preconditions (a caller defect).</exception>
    public static BoundaryBuildResult Build(IAlignmentGeometry alignment, IReadOnlyList<SegmentDef> segments, double offsetLeft, double offsetRight, GeometryOptions options)
    {
        CheckArguments(alignment, segments, offsetLeft, offsetRight, options);
        var problems = new List<Problem>();
        if (segments.Count == 0) return new BoundaryBuildResult([], [], [], problems);

        double tol = options.StationTolerance;
        var model = AlignmentModel.Create(alignment, Math.Max(Math.Abs(offsetLeft), Math.Abs(offsetRight)), options, problems);
        if (model is null) return new BoundaryBuildResult([], [], [], problems);

        double runStart = segments[0].StartStation, runEnd = segments[^1].EndStation;
        double[] offsets = [offsetLeft, offsetRight];

        var elementFaults = ElementFaults(model, offsets, runStart, runEnd, options);
        var junctionFaults = JunctionFaults(model, offsets, runStart, runEnd, options);

        var nodes = BuildNodes(model, segments, offsets, runStart, runEnd, options);
        int count = nodes.Count;
        var left = new Pt[count];
        var right = new Pt[count];
        for (int i = 0; i < count; i++)
        {
            left[i] = model.Vertex(nodes[i].Station, nodes[i].Junction, offsetLeft);
            right[i] = model.Vertex(nodes[i].Station, nodes[i].Junction, offsetRight);
        }
        var stations = nodes.Select(n => n.Station).ToArray();

        var boundaries = new List<Boundary>();
        var built = new bool[segments.Count];
        var ranges = new (int A, int B)[segments.Count];
        for (int k = 0; k < segments.Count; k++)
        {
            var seg = segments[k];
            int ia = IndexOf(stations, seg.StartStation, tol), ib = IndexOf(stations, seg.EndStation, tol);
            ranges[k] = (ia, ib);

            var faults = new List<Fault>();
            for (int e = 0; e < model.Count; e++)
                if (model.Segs[e].StartStation < seg.EndStation - tol && model.Segs[e].EndStation > seg.StartStation + tol)
                    faults.AddRange(elementFaults[e]);
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

            var ring = Ring(right, left, ia, ib);
            string? reason = BoundaryValidator.Check(ring, requireCounterClockwise: true, minDistinctVertices: 4);
            if (reason is not null)
            {
                problems.Add(new Problem(ProblemCodes.E202, Severity.Error,
                    $"{Fmt.Segment(alignment, seg, tol)}: {reason}. No volume is computed on it.", seg.Id,
                    "Move the break away from the nearby angle point or curve, or reduce the offsets, so the boundary does not fold over itself."));
                continue;
            }

            boundaries.Add(new Boundary(seg.Id, Drawn(model, nodes, right, left, ia, ib), ring));
            built[k] = true;
        }

        var band = new List<BandPiece>();
        for (int k = 0; k < segments.Count; k++)
        {
            if (!built[k]) continue;
            int first = k;
            while (k + 1 < segments.Count && built[k + 1] && segments[k + 1].StartStation == segments[k].EndStation) k++;
            band.Add(new BandPiece(segments[first].StartStation, segments[k].EndStation,
                segments.Skip(first).Take(k - first + 1).Select(s => s.Id).ToArray(),
                Ring(right, left, ranges[first].A, ranges[k].B)));
        }

        return new BoundaryBuildResult(boundaries, band, stations, problems);
    }

    private static void CheckArguments(IAlignmentGeometry alignment, IReadOnlyList<SegmentDef> segments, double offsetLeft, double offsetRight, GeometryOptions options)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(offsetLeft) || !double.IsFinite(offsetRight) || !(offsetLeft < offsetRight))
            throw new ArgumentException("Offsets must be finite with left < right; the planner rejects them first (E210).");
        double tol = options.StationTolerance;
        for (int k = 0; k < segments.Count; k++)
        {
            var s = segments[k];
            if (s.OffsetLeft != offsetLeft || s.OffsetRight != offsetRight)
                throw new ArgumentException($"Segment {s.Label} carries offsets ({s.OffsetLeft}, {s.OffsetRight}); the run builds ({offsetLeft}, {offsetRight}).");
            if (!(s.EndStation > s.StartStation))
                throw new ArgumentException($"Segment {s.Label} has End <= Start.");
            if (s.StartStation < alignment.StartStation - tol || s.EndStation > alignment.EndStation + tol)
                throw new ArgumentException($"Segment {s.Label} lies outside the alignment; the planner marks such segments invalid (E305, E211).");
            if (k > 0 && s.StartStation < segments[k - 1].EndStation - tol)
                throw new ArgumentException($"Segment {s.Label} overlaps or precedes the previous segment.");
        }
    }

    private static List<Fault>[] ElementFaults(AlignmentModel m, double[] offsets, double runStart, double runEnd, GeometryOptions opt)
    {
        var faults = new List<Fault>[m.Count];
        double tol = opt.StationTolerance;
        var a = m.Source;
        for (int i = 0; i < m.Count; i++)
        {
            faults[i] = [];
            var g = m.Segs[i];
            if (g.EndStation <= runStart + tol || g.StartStation >= runEnd - tol) continue;

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
                        faults[i].Add(new Fault(ProblemCodes.E201, s => (
                            $"{Fmt.Segment(a, s, tol)}: the {side} offset {Fmt.Num(Math.Abs(o))} reaches the center of {what}. The largest usable {side} offset there is less than {Fmt.Num(radius)}.",
                            $"Use a {side} offset smaller than {Fmt.Num(radius)}.")));
                        break;
                    }
                    if (inside) break;
                }
            }

            if (inside || g.Kind == SegKind.Spiral) continue;

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
                faults[i].Add(new Fault(ProblemCodes.E204, s => (
                    $"{Fmt.Segment(a, s, tol)}: the alignment data check failed on the {kind} at {Fmt.Station(a, g.StartStation, tol)}: its computed offset point differs from the alignment's own point by {Fmt.Num(worst)}.",
                    "This points to an alignment adapter defect, not a drafting error. Report it with the drawing.")));
            }
        }
        return faults;
    }

    private static List<Fault>[] JunctionFaults(AlignmentModel m, double[] offsets, double runStart, double runEnd, GeometryOptions opt)
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
    /// arc and spiral subdivisions are soft and yield to a hard station within tolerance.
    /// </summary>
    private static List<Node> BuildNodes(AlignmentModel m, IReadOnlyList<SegmentDef> segments, double[] offsets, double runStart, double runEnd, GeometryOptions opt)
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

            int pieces = Subdivisions(m, i, offsets, opt);
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

    /// <summary>Number of equal station steps for element i so the mid-ordinate on the larger offset radius stays within tolerance.</summary>
    private static int Subdivisions(AlignmentModel m, int i, double[] offsets, GeometryOptions opt)
    {
        var g = m.Segs[i];
        double len = g.EndStation - g.StartStation;
        if (g.Kind == SegKind.Arc)
        {
            double rMax = Math.Max(m.OffsetRadius(i, offsets[0]), m.OffsetRadius(i, offsets[1]));
            double sweep = len / g.Radius;
            return Math.Max(1, (int)Math.Ceiling(sweep / Polygon.ArcStep(rMax, opt.MidOrdinate) - 1e-9));
        }
        if (g.Kind == SegKind.Spiral)
        {
            // Sagitta of a station step h on the offset curve is about h^2 k (1 + k o) / 8. Bound it with
            // the largest end curvature and the largest offset magnitude, then cap the step at a quarter turn.
            double k = Math.Max(Math.Abs(m.K1[i]), Math.Abs(m.K2[i]));
            if (k <= 0) return 1;
            double factor = k * (1 + k * Math.Max(Math.Abs(offsets[0]), Math.Abs(offsets[1])));
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

    private static Pt[] Ring(Pt[] right, Pt[] left, int ia, int ib)
    {
        var ring = new Pt[2 * (ib - ia + 1) + 1];
        int n = 0;
        for (int i = ia; i <= ib; i++) ring[n++] = right[i];
        for (int i = ib; i >= ia; i--) ring[n++] = left[i];
        ring[n] = right[ia];
        return ring;
    }

    /// <summary>
    /// Polyline vertices with bulges: hard stations everywhere, every station inside a
    /// spiral, and one bulge per arc piece between consecutive kept stations (concentric arc
    /// of radius R plus or minus the offset).
    /// </summary>
    private static PolyVertex[] Drawn(AlignmentModel m, List<Node> nodes, Pt[] right, Pt[] left, int ia, int ib)
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
            drawn.Add(new PolyVertex(right[keep[t]], t < bulge.Length ? bulge[t] : 0));
        for (int t = keep.Count - 1; t >= 0; t--)
            drawn.Add(new PolyVertex(left[keep[t]], t > 0 ? -bulge[t - 1] : 0));
        return drawn.ToArray();
    }
}

// Impact outlines by edge cancellation (HANDOFF.md 6.15 rev 3, NOW.md D13). Owner: C5. Internal.
//
// The step 5 pieces (convex, counter-clockwise, local coordinates) tile the impacted area. Every
// piece contributes its directed edges; an edge shared by two pieces appears once in each
// direction and is interior, so the pair cancels. What remains is the boundary of the union,
// with the impacted area on the left of every edge, chained into closed rings: outer rings come
// out counter-clockwise and holes clockwise, so the signed ring areas sum to the impacted area.
//
// Matching is within a tolerance (2 x the boundary snap, the search-limit test's tolerance):
//  1. Weld: every piece vertex within the tolerance of an earlier one takes that vertex's id
//     (hash grid with the tolerance as its cell). An edge whose ends weld together is dropped.
//  2. Cancel: an edge (a, b) cancels one (b, a).
//  3. T-junctions: an edge that survived and has a surviving edge's end vertex on it (within the
//     tolerance, strictly between its ends) is split there, then step 2 runs again. This is the
//     case where a sliver piece thinner than snap was left out (it contributes no edges) or the
//     clip snap left a vertex just off a shared edge.
//  4. Chain: from any unused edge, follow out-edges until back at the start. Every operation above
//     keeps in-degree equal to out-degree at every vertex, so every walk closes. Where a vertex
//     has several unused out-edges (two parts touching at a point) the walk takes the sharpest
//     left turn, which keeps each part its own ring.
//  5. Tidy: a vertex within the tolerance of the chord of its neighbours (and between them) is
//     dropped, so a straight edge built from many pieces is one edge. A ring thinner than the
//     tolerance (area <= tolerance x perimeter) is dropped. Each ring starts at its lowest-x
//     (then lowest-y) vertex; outer rings come first by descending area, then holes.

namespace SheetVol.Core.Volumes.Integrator;

/// <summary>Collects the step 5 pieces of one segment and builds their union's rings.</summary>
internal sealed class OutlineBuilder
{
    private readonly double _tol;
    private readonly List<double> _vx = [], _vy = [];
    private readonly Dictionary<(long, long), List<int>> _grid = [];
    private readonly List<(int A, int B)> _edges = [];

    /// <param name="tolerance">Weld and match tolerance, local drawing units, &gt; 0.</param>
    public OutlineBuilder(double tolerance)
    {
        if (!(tolerance > 0) || !double.IsFinite(tolerance)) throw new ArgumentOutOfRangeException(nameof(tolerance));
        _tol = tolerance;
    }

    /// <summary>Adds the directed edges of one counter-clockwise convex piece (local coordinates).</summary>
    public void AddPiece(ReadOnlySpan<PieceVertex> piece)
    {
        int n = piece.Length;
        if (n < 3) return;
        Span<int> ids = stackalloc int[n];
        for (int k = 0; k < n; k++) ids[k] = Weld(piece[k].X, piece[k].Y);
        for (int k = 0; k < n; k++)
        {
            int a = ids[k], b = ids[k + 1 == n ? 0 : k + 1];
            if (a != b) _edges.Add((a, b));
        }
    }

    /// <summary>
    /// The rings, translated back to world coordinates by (<paramref name="originX"/>,
    /// <paramref name="originY"/>), each closed (first == last). Empty when no piece was added.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Pt>> Build(double originX, double originY)
    {
        if (_edges.Count == 0) return [];
        var edges = Cancel(_edges);
        for (int round = 0; round < 8 && edges.Count > 0; round++)
        {
            var split = SplitTJunctions(edges);
            if (split.Count == edges.Count) break;
            edges = Cancel(split);
        }

        var rings = new List<(List<(double X, double Y)> Ring, double Area)>();
        foreach (var ids in Chain(edges))
        {
            var pts = Tidy(ids);
            if (pts.Count < 3) continue;
            double area = SignedArea(pts), perim = Perimeter(pts);
            if (!(Math.Abs(area) > _tol * perim)) continue;
            rings.Add((StartAtLowest(pts), area));
        }
        rings.Sort((p, q) =>
        {
            bool ph = p.Area < 0, qh = q.Area < 0;
            if (ph != qh) return ph ? 1 : -1;
            return Math.Abs(q.Area).CompareTo(Math.Abs(p.Area));
        });

        var result = new List<IReadOnlyList<Pt>>(rings.Count);
        foreach (var (ring, _) in rings)
        {
            var w = new List<Pt>(ring.Count + 1);
            foreach (var (x, y) in ring) w.Add(new Pt(x + originX, y + originY));
            w.Add(w[0]);
            result.Add(w);
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------
    // 1. Weld
    // ---------------------------------------------------------------------------------------

    private int Weld(double x, double y)
    {
        long cx = (long)Math.Floor(x / _tol), cy = (long)Math.Floor(y / _tol);
        int best = -1;
        double bestD = double.PositiveInfinity;
        for (long i = cx - 1; i <= cx + 1; i++)
            for (long j = cy - 1; j <= cy + 1; j++)
            {
                if (!_grid.TryGetValue((i, j), out var list)) continue;
                foreach (int v in list)
                {
                    double dx = _vx[v] - x, dy = _vy[v] - y, d = Math.Sqrt(dx * dx + dy * dy);
                    if (d <= _tol && d < bestD) { bestD = d; best = v; }
                }
            }
        if (best >= 0) return best;
        int id = _vx.Count;
        _vx.Add(x); _vy.Add(y);
        if (!_grid.TryGetValue((cx, cy), out var cell)) _grid[(cx, cy)] = cell = [];
        cell.Add(id);
        return id;
    }

    // ---------------------------------------------------------------------------------------
    // 2. Cancel
    // ---------------------------------------------------------------------------------------

    private static List<(int A, int B)> Cancel(List<(int A, int B)> edges)
    {
        var count = new Dictionary<(int, int), int>(edges.Count);
        foreach (var e in edges)
        {
            var rev = (e.B, e.A);
            if (count.TryGetValue(rev, out int c) && c > 0) count[rev] = c - 1;
            else count[e] = count.GetValueOrDefault(e) + 1;
        }
        var kept = new List<(int A, int B)>();
        foreach (var e in edges)
            if (count.TryGetValue(e, out int c) && c > 0)
            {
                kept.Add(e);
                count[e] = c - 1;
            }
        return kept;
    }

    // ---------------------------------------------------------------------------------------
    // 3. T-junctions
    // ---------------------------------------------------------------------------------------

    private List<(int A, int B)> SplitTJunctions(List<(int A, int B)> edges)
    {
        var verts = new HashSet<int>();
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var (a, b) in edges)
        {
            verts.Add(a); verts.Add(b);
        }
        foreach (int v in verts)
        {
            minX = Math.Min(minX, _vx[v]); maxX = Math.Max(maxX, _vx[v]);
            minY = Math.Min(minY, _vy[v]); maxY = Math.Max(maxY, _vy[v]);
        }
        var boxes = new (double, double, double, double)[edges.Count];
        for (int k = 0; k < edges.Count; k++)
        {
            var (a, b) = edges[k];
            boxes[k] = (Math.Min(_vx[a], _vx[b]) - _tol, Math.Min(_vy[a], _vy[b]) - _tol,
                        Math.Max(_vx[a], _vx[b]) + _tol, Math.Max(_vy[a], _vy[b]) + _tol);
        }
        var index = new GridIndex(boxes, minX, minY, maxX, maxY);
        var cand = new List<int>();
        Dictionary<int, List<(double T, int V)>>? splits = null;
        foreach (int v in verts)
        {
            double px = _vx[v], py = _vy[v];
            index.Query(px - _tol, py - _tol, px + _tol, py + _tol, cand);
            foreach (int k in cand)
            {
                var (a, b) = edges[k];
                if (v == a || v == b) continue;
                double ex = _vx[b] - _vx[a], ey = _vy[b] - _vy[a], len2 = ex * ex + ey * ey;
                if (!(len2 > 0)) continue;
                double t = ((px - _vx[a]) * ex + (py - _vy[a]) * ey) / len2;
                if (!(t > 0 && t < 1)) continue;
                double qx = _vx[a] + t * ex - px, qy = _vy[a] + t * ey - py;
                if (Math.Sqrt(qx * qx + qy * qy) > _tol) continue;
                splits ??= [];
                if (!splits.TryGetValue(k, out var list)) splits[k] = list = [];
                list.Add((t, v));
            }
        }
        if (splits is null) return edges;
        var result = new List<(int A, int B)>(edges.Count + splits.Count);
        for (int k = 0; k < edges.Count; k++)
        {
            if (!splits.TryGetValue(k, out var list)) { result.Add(edges[k]); continue; }
            list.Sort((p, q) => p.T.CompareTo(q.T));
            int from = edges[k].A;
            foreach (var (_, v) in list)
            {
                if (v != from) result.Add((from, v));
                from = v;
            }
            if (from != edges[k].B) result.Add((from, edges[k].B));
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------
    // 4. Chain
    // ---------------------------------------------------------------------------------------

    private List<List<int>> Chain(List<(int A, int B)> edges)
    {
        var outgoing = new Dictionary<int, List<int>>();
        for (int k = 0; k < edges.Count; k++)
        {
            if (!outgoing.TryGetValue(edges[k].A, out var list)) outgoing[edges[k].A] = list = [];
            list.Add(k);
        }
        var used = new bool[edges.Count];
        var rings = new List<List<int>>();
        for (int s = 0; s < edges.Count; s++)
        {
            if (used[s]) continue;
            used[s] = true;
            int start = edges[s].A, prev = start, cur = edges[s].B;
            var ring = new List<int> { start };
            while (cur != start)
            {
                ring.Add(cur);
                int next = -1;
                double bestTurn = double.NegativeInfinity;
                double inX = _vx[cur] - _vx[prev], inY = _vy[cur] - _vy[prev];
                foreach (int k in outgoing[cur])
                {
                    if (used[k]) continue;
                    int b = edges[k].B;
                    double outX = _vx[b] - _vx[cur], outY = _vy[b] - _vy[cur];
                    double turn = Math.Atan2(inX * outY - inY * outX, inX * outX + inY * outY);
                    if (turn > bestTurn) { bestTurn = turn; next = k; }
                }
                if (next < 0) throw new InvalidOperationException("Impact outline chaining found an unbalanced vertex.");
                used[next] = true;
                prev = cur;
                cur = edges[next].B;
            }
            rings.Add(ring);
        }
        return rings;
    }

    // ---------------------------------------------------------------------------------------
    // 5. Tidy
    // ---------------------------------------------------------------------------------------

    private List<(double X, double Y)> Tidy(List<int> ids)
    {
        var p = new List<(double X, double Y)>(ids.Count);
        foreach (int id in ids)
        {
            p.Add((_vx[id], _vy[id]));
            while (p.Count >= 3 && Straight(p[^3], p[^2], p[^1])) p.RemoveAt(p.Count - 2);
        }
        while (p.Count >= 3)
        {
            if (Straight(p[^2], p[^1], p[0])) { p.RemoveAt(p.Count - 1); continue; }
            if (Straight(p[^1], p[0], p[1])) { p.RemoveAt(0); continue; }
            break;
        }
        return p;
    }

    /// <summary>True when <paramref name="v"/> lies within the tolerance of chord u-w and between u and w.</summary>
    private bool Straight((double X, double Y) u, (double X, double Y) v, (double X, double Y) w)
    {
        double cx = w.X - u.X, cy = w.Y - u.Y, len = Math.Sqrt(cx * cx + cy * cy);
        if (!(len > 0)) return false;
        double dist = Math.Abs(cx * (v.Y - u.Y) - cy * (v.X - u.X)) / len;
        if (dist > _tol) return false;
        return (v.X - u.X) * cx + (v.Y - u.Y) * cy > 0 && (v.X - w.X) * -cx + (v.Y - w.Y) * -cy > 0;
    }

    private static List<(double X, double Y)> StartAtLowest(List<(double X, double Y)> p)
    {
        int lo = 0;
        for (int i = 1; i < p.Count; i++)
            if (p[i].X < p[lo].X || (p[i].X == p[lo].X && p[i].Y < p[lo].Y)) lo = i;
        if (lo == 0) return p;
        var r = new List<(double X, double Y)>(p.Count);
        for (int i = 0; i < p.Count; i++) r.Add(p[(lo + i) % p.Count]);
        return r;
    }

    private static double SignedArea(List<(double X, double Y)> p)
    {
        double x0 = p[0].X, y0 = p[0].Y, a2 = 0;
        for (int i = 1; i + 1 < p.Count; i++)
            a2 += (p[i].X - x0) * (p[i + 1].Y - y0) - (p[i].Y - y0) * (p[i + 1].X - x0);
        return 0.5 * a2;
    }

    private static double Perimeter(List<(double X, double Y)> p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var (ax, ay) = p[i];
            var (bx, by) = p[i + 1 == p.Count ? 0 : i + 1];
            s += Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        }
        return s;
    }
}

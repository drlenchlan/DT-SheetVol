// Boundary preparation for the integrator (6.15 steps 1 and 3). Owner: C5. Internal.
//
// Choice for clipping against a possibly concave boundary: the boundary is ear-clipped once
// into counter-clockwise triangles, and every convex piece is clipped against each triangle it
// meets with Sutherland-Hodgman. Sutherland-Hodgman is only correct against a convex window,
// which a triangle always is; the triangles partition the boundary, so the clipped parts sum to
// exactly the piece inside the boundary. Ear clipping is O(n^2) on the vertex count, which is a
// few hundred per segment boundary.

namespace SheetVol.Core.Volumes.Integrator;

/// <summary>
/// A validated simple polygon in local coordinates (translated so its bounding-box minimum is the
/// origin, E1009), counter-clockwise, with its ear triangulation.
/// </summary>
internal sealed class BoundaryPolygon
{
    /// <summary>Snap distance relative to the boundary extent: points closer than this to a line are on it.</summary>
    public const double RelativeSnap = 1e-12;

    /// <summary>World coordinates of the local origin (the boundary bounding-box minimum).</summary>
    public double OriginX { get; }
    public double OriginY { get; }

    /// <summary>Largest bounding-box side, drawing units (&gt; 0).</summary>
    public double Extent { get; }

    /// <summary>Snap distance in drawing units: RelativeSnap * Extent.</summary>
    public double Snap { get; }

    /// <summary>Local vertices, counter-clockwise, consecutive duplicates removed, no closing repeat.</summary>
    public double[] X { get; }
    public double[] Y { get; }

    /// <summary>Plan area of the polygon, square drawing units, &gt; 0.</summary>
    public double Area { get; }

    /// <summary>Local bounding box (MinX = MinY = 0).</summary>
    public double MaxX { get; }
    public double MaxY { get; }

    /// <summary>Ear triangles, counter-clockwise; their areas sum to <see cref="Area"/>.</summary>
    public ClipTriangle[] Triangles { get; }

    private BoundaryPolygon(double ox, double oy, double extent, double[] x, double[] y, double area, double maxX, double maxY, ClipTriangle[] tris)
    {
        OriginX = ox; OriginY = oy; Extent = extent; Snap = RelativeSnap * extent;
        X = x; Y = y; Area = area; MaxX = maxX; MaxY = maxY; Triangles = tris;
    }

    /// <summary>
    /// Validates and prepares a closed boundary (first == last, world coordinates). On failure adds
    /// one Error (<see cref="IntegratorCodes.InvalidBoundary"/>) and returns null: no number is ever
    /// computed on an invalid boundary (6.4 step 6, E202).
    /// </summary>
    public static BoundaryPolygon? TryCreate(IReadOnlyList<Pt>? closed, Guid? segmentId, List<Problem> problems)
    {
        BoundaryPolygon? Fail(string why)
        {
            problems.Add(new Problem(IntegratorCodes.InvalidBoundary, Severity.Error,
                $"Boundary is invalid: {why}. No volume was computed.", segmentId,
                "Fix the segment definition or the boundary polyline so it is a closed, simple polygon."));
            return null;
        }

        if (closed is null || closed.Count < 4)
            return Fail("it needs at least three distinct vertices plus the closing vertex");

        double gminX = double.PositiveInfinity, gminY = double.PositiveInfinity;
        double gmaxX = double.NegativeInfinity, gmaxY = double.NegativeInfinity;
        foreach (var p in closed)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return Fail("a vertex has a non-finite coordinate");
            gminX = Math.Min(gminX, p.X); gmaxX = Math.Max(gmaxX, p.X);
            gminY = Math.Min(gminY, p.Y); gmaxY = Math.Max(gmaxY, p.Y);
        }
        double extent = Math.Max(gmaxX - gminX, gmaxY - gminY);
        if (!(extent > 0)) return Fail("all vertices coincide");
        double snap = RelativeSnap * extent;

        var first = closed[0];
        var last = closed[^1];
        if (Math.Sqrt((first.X - last.X) * (first.X - last.X) + (first.Y - last.Y) * (first.Y - last.Y)) > 1e-9 * extent)
            return Fail("it is not closed (the last vertex must repeat the first)");

        // Translate to the local origin first (E1009), then drop consecutive duplicates.
        var xs = new List<double>(closed.Count);
        var ys = new List<double>(closed.Count);
        for (int i = 0; i < closed.Count; i++)
        {
            double x = closed[i].X - gminX, y = closed[i].Y - gminY;
            if (xs.Count > 0 && Dist(x, y, xs[^1], ys[^1]) <= snap) continue;
            xs.Add(x); ys.Add(y);
        }
        while (xs.Count > 1 && Dist(xs[^1], ys[^1], xs[0], ys[0]) <= snap)
        {
            xs.RemoveAt(xs.Count - 1); ys.RemoveAt(ys.Count - 1);
        }
        int n = xs.Count;
        if (n < 3) return Fail("it has fewer than three distinct vertices");

        double a2 = 0, perim = 0;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            a2 += (xs[i] - xs[0]) * (ys[j] - ys[0]) - (ys[i] - ys[0]) * (xs[j] - xs[0]);
            perim += Dist(xs[i], ys[i], xs[j], ys[j]);
        }
        double area = 0.5 * Math.Abs(a2);
        if (!(area > snap * perim)) return Fail("its area is zero");
        if (a2 < 0) { xs.Reverse(); ys.Reverse(); }

        double[] x0 = [.. xs], y0 = [.. ys];
        if (!IsSimple(x0, y0, snap, out int badI, out int badJ))
        {
            return Fail($"it intersects or touches itself near ({x0[badI] + gminX:0.###}, {y0[badI] + gminY:0.###}) " +
                        $"and ({x0[badJ] + gminX:0.###}, {y0[badJ] + gminY:0.###})");
        }

        var tris = EarClip(x0, y0, snap);
        if (tris is null) return Fail("it could not be triangulated");
        double sum = 0;
        foreach (var t in tris) sum += t.Area;
        if (Math.Abs(sum - area) > 1e-9 * area + n * snap * extent)
            return Fail($"its triangulation does not reproduce its area ({sum} vs {area})");

        return new BoundaryPolygon(gminX, gminY, extent, x0, y0, area, gmaxX - gminX, gmaxY - gminY, [.. tris]);
    }

    private static double Dist(double ax, double ay, double bx, double by)
        => Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));

    // --------------------------------------------------------------------------------------
    // Simplicity: no two non-adjacent edges within snap of each other, no adjacent edges that
    // fold back on each other. O(n^2) with a bounding-box reject.
    // --------------------------------------------------------------------------------------

    private static bool IsSimple(double[] x, double[] y, double snap, out int badI, out int badJ)
    {
        int n = x.Length;
        badI = badJ = 0;
        for (int i = 0; i < n; i++)
        {
            int i1 = (i + 1) % n, i2 = (i + 2) % n;
            // Adjacent edges (i,i1) and (i1,i2): reject a fold-back (spike).
            double ex = x[i1] - x[i], ey = y[i1] - y[i], fx = x[i2] - x[i1], fy = y[i2] - y[i1];
            double cross = ex * fy - ey * fx, dot = ex * fx + ey * fy;
            double lf = Math.Sqrt(fx * fx + fy * fy);
            if (dot < 0 && Math.Abs(cross) <= snap * Math.Sqrt(ex * ex + ey * ey) + snap * lf)
            {
                badI = i1; badJ = i2;
                return false;
            }
        }
        if (n == 3) return true;
        for (int i = 0; i < n; i++)
        {
            int i1 = (i + 1) % n;
            double aminX = Math.Min(x[i], x[i1]) - snap, amaxX = Math.Max(x[i], x[i1]) + snap;
            double aminY = Math.Min(y[i], y[i1]) - snap, amaxY = Math.Max(y[i], y[i1]) + snap;
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue;     // adjacent through the closing edge
                int j1 = (j + 1) % n;
                if (Math.Max(x[j], x[j1]) < aminX || Math.Min(x[j], x[j1]) > amaxX ||
                    Math.Max(y[j], y[j1]) < aminY || Math.Min(y[j], y[j1]) > amaxY) continue;
                if (SegmentsWithin(x[i], y[i], x[i1], y[i1], x[j], y[j], x[j1], y[j1], snap))
                {
                    badI = i; badJ = j;
                    return false;
                }
            }
        }
        return true;
    }

    private static bool SegmentsWithin(double ax, double ay, double bx, double by,
                                       double cx, double cy, double dx, double dy, double tol)
    {
        double d1 = Orient(cx, cy, dx, dy, ax, ay), d2 = Orient(cx, cy, dx, dy, bx, by);
        double d3 = Orient(ax, ay, bx, by, cx, cy), d4 = Orient(ax, ay, bx, by, dx, dy);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
        return PointSegDist(ax, ay, cx, cy, dx, dy) <= tol || PointSegDist(bx, by, cx, cy, dx, dy) <= tol
            || PointSegDist(cx, cy, ax, ay, bx, by) <= tol || PointSegDist(dx, dy, ax, ay, bx, by) <= tol;
    }

    private static double Orient(double ax, double ay, double bx, double by, double px, double py)
        => (bx - ax) * (py - ay) - (by - ay) * (px - ax);

    private static double PointSegDist(double px, double py, double ax, double ay, double bx, double by)
    {
        double ex = bx - ax, ey = by - ay;
        double l2 = ex * ex + ey * ey;
        double t = l2 > 0 ? Math.Clamp(((px - ax) * ex + (py - ay) * ey) / l2, 0, 1) : 0;
        return Dist(px, py, ax + t * ex, ay + t * ey);
    }

    // --------------------------------------------------------------------------------------
    // Ear clipping on a counter-clockwise simple polygon. A vertex within snap of the chord of
    // its neighbours (and between them) is dropped without a triangle: it adds zero area. An ear
    // is a convex vertex whose triangle contains no other remaining vertex (inside or within snap
    // of an edge). Returns null when no ear can be found (numerically hopeless input).
    // --------------------------------------------------------------------------------------

    private static List<ClipTriangle>? EarClip(double[] x, double[] y, double snap)
    {
        int n = x.Length;
        var next = new int[n];
        var prev = new int[n];
        for (int i = 0; i < n; i++) { next[i] = (i + 1) % n; prev[i] = (i - 1 + n) % n; }
        var tris = new List<ClipTriangle>(n);
        int remaining = n, cur = 0, stall = 0;
        while (remaining > 3)
        {
            int p = prev[cur], q = next[cur];
            double chord = Dist(x[p], y[p], x[q], y[q]);
            // (cur - p) x (q - cur): > 0 means cur is a convex (left-turn) vertex of the CCW polygon.
            // Its magnitude over the chord length is the distance of cur from the chord p-q.
            double convexCross = (x[cur] - x[p]) * (y[q] - y[cur]) - (y[cur] - y[p]) * (x[q] - x[cur]);
            double distFromChord = chord > 0 ? Math.Abs(convexCross) / chord : Dist(x[cur], y[cur], x[p], y[p]);
            bool between = (x[cur] - x[p]) * (x[q] - x[p]) + (y[cur] - y[p]) * (y[q] - y[p]) > 0
                        && (x[cur] - x[q]) * (x[p] - x[q]) + (y[cur] - y[q]) * (y[p] - y[q]) > 0;
            if (distFromChord <= snap && between)
            {
                Unlink(cur, next, prev); remaining--; cur = q; stall = 0;
                continue;
            }
            if (convexCross > 0 && distFromChord > snap && IsEar(p, cur, q, x, y, next, snap))
            {
                tris.Add(new ClipTriangle(x[p], y[p], x[cur], y[cur], x[q], y[q]));
                Unlink(cur, next, prev); remaining--; cur = q; stall = 0;
                continue;
            }
            cur = q;
            if (++stall > remaining) return null;
        }
        {
            int p = prev[cur], q = next[cur];
            var last = new ClipTriangle(x[p], y[p], x[cur], y[cur], x[q], y[q]);
            if (last.Area > 0) tris.Add(last);
        }
        return tris;
    }

    private static void Unlink(int i, int[] next, int[] prev)
    {
        next[prev[i]] = next[i];
        prev[next[i]] = prev[i];
    }

    private static bool IsEar(int p, int c, int q, double[] x, double[] y, int[] next, double snap)
    {
        var t = new ClipTriangle(x[p], y[p], x[c], y[c], x[q], y[q]);
        for (int k = next[q]; k != p; k = next[k])
        {
            double px = x[k], py = y[k];
            if (px < t.MinX - snap || px > t.MaxX + snap || py < t.MinY - snap || py > t.MaxY + snap) continue;
            if (t.N0x * px + t.N0y * py - t.C0 >= -snap &&
                t.N1x * px + t.N1y * py - t.C1 >= -snap &&
                t.N2x * px + t.N2y * py - t.C2 >= -snap)
                return false;
        }
        return true;
    }

    // --------------------------------------------------------------------------------------
    // Lateral edges for the search-limit check (E1002).
    // --------------------------------------------------------------------------------------

    /// <summary>
    /// Finds the boundary vertices nearest to the four corner points (start-left, end-left,
    /// end-right, start-right, all local) and returns the edges of the two lateral chains
    /// (start-left to end-left and end-right to start-right), excluding the two cut-line chains.
    /// Returns null when the four corners are not distinct or are not in cyclic order.
    /// </summary>
    public List<(double Ax, double Ay, double Bx, double By)>? LateralEdges(Pt startLeft, Pt endLeft, Pt endRight, Pt startRight)
    {
        int sl = Nearest(startLeft), el = Nearest(endLeft), er = Nearest(endRight), sr = Nearest(startRight);
        if (sl == el || sl == er || sl == sr || el == er || el == sr || er == sr) return null;
        int n = X.Length;
        int FirstHit(int from, int a, int b, int c)
        {
            for (int k = (from + 1) % n; k != from; k = (k + 1) % n)
                if (k == a || k == b || k == c) return k;
            return -1;
        }
        var chains = new List<(int From, int To)>();
        int h1 = FirstHit(sl, el, er, sr);
        if (h1 == el)
        {
            if (FirstHit(el, sl, er, sr) != er || FirstHit(er, sl, el, sr) != sr) return null;
            chains.Add((sl, el)); chains.Add((er, sr));
        }
        else if (h1 == sr)
        {
            if (FirstHit(sr, sl, el, er) != er || FirstHit(er, sl, el, sr) != el) return null;
            chains.Add((sr, er)); chains.Add((el, sl));
        }
        else return null;

        var edges = new List<(double, double, double, double)>();
        foreach (var (from, to) in chains)
            for (int k = from; k != to; k = (k + 1) % n)
            {
                int k1 = (k + 1) % n;
                edges.Add((X[k], Y[k], X[k1], Y[k1]));
            }
        return edges;
    }

    private int Nearest(Pt p)
    {
        int best = 0;
        double bestD = double.PositiveInfinity;
        for (int i = 0; i < X.Length; i++)
        {
            double d = (X[i] - p.X) * (X[i] - p.X) + (Y[i] - p.Y) * (Y[i] - p.Y);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }
}

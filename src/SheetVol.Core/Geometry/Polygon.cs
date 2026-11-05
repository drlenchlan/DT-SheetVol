namespace SheetVol.Core.Geometry;

/// <summary>
/// Plane polygon utilities on rings of <see cref="Pt"/> in drawing units (WCS). A ring may be
/// given open or closed (first == last). Every routine translates to a local origin (the
/// ring's first vertex) before doing arithmetic, so state-plane coordinates keep precision.
/// Signed areas are positive for counter-clockwise rings.
/// </summary>
public static class Polygon
{
    /// <summary>Signed shoelace area, square drawing units; positive when counter-clockwise.</summary>
    public static double SignedArea(IReadOnlyList<Pt> ring)
    {
        int n = ring.Count;
        if (n < 3) return 0;
        double ox = ring[0].X, oy = ring[0].Y, sum = 0;
        for (int i = 0; i < n; i++)
        {
            Pt p = ring[i], q = ring[(i + 1) % n];
            sum += (p.X - ox) * (q.Y - oy) - (q.X - ox) * (p.Y - oy);
        }
        return 0.5 * sum;
    }

    /// <summary>Absolute area, square drawing units.</summary>
    public static double Area(IReadOnlyList<Pt> ring) => Math.Abs(SignedArea(ring));

    /// <summary>True when the ring has at least two points and its first point equals its last exactly.</summary>
    public static bool IsClosed(IReadOnlyList<Pt> ring) => ring.Count >= 2 && ring[0] == ring[ring.Count - 1];

    /// <summary>Number of distinct points in the ring, the closing repeat not counted.</summary>
    public static int DistinctVertexCount(IReadOnlyList<Pt> ring) => new HashSet<Pt>(ring).Count;

    /// <summary>Axis-aligned bounds of the ring.</summary>
    public static (Pt Min, Pt Max) Bounds(IReadOnlyList<Pt> ring)
    {
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var p in ring)
        {
            x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y);
            x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
        }
        return (new Pt(x0, y0), new Pt(x1, y1));
    }

    /// <summary>
    /// Finds a crossing or touching between two non-adjacent edges of the ring. Collinear
    /// overlap and a vertex lying on another edge count as self-intersection.
    /// </summary>
    /// <param name="ring">Open or closed ring.</param>
    /// <param name="at">WCS point of the first intersection found.</param>
    public static bool TryFindSelfIntersection(IReadOnlyList<Pt> ring, out Pt at)
    {
        at = default;
        var pts = OpenLocal(ring, ring.Count > 0 ? ring[0] : default, out double extent);
        int m = pts.Length;
        if (m < 3) return false;
        double eps = 1e-11 * Math.Max(1.0, extent);

        var order = new int[m];
        var minX = new double[m];
        for (int i = 0; i < m; i++)
        {
            order[i] = i;
            minX[i] = Math.Min(pts[i].X, pts[(i + 1) % m].X);
        }
        Array.Sort((double[])minX.Clone(), order);

        for (int oi = 0; oi < m; oi++)
        {
            int i = order[oi];
            Pt a = pts[i], b = pts[(i + 1) % m];
            double maxX = Math.Max(a.X, b.X);
            double aMinY = Math.Min(a.Y, b.Y), aMaxY = Math.Max(a.Y, b.Y);
            for (int oj = oi + 1; oj < m; oj++)
            {
                int j = order[oj];
                if (minX[j] > maxX + eps) break;
                if (j == (i + 1) % m || i == (j + 1) % m) continue;
                Pt c = pts[j], d = pts[(j + 1) % m];
                if (Math.Max(aMinY, Math.Min(c.Y, d.Y)) > Math.Min(aMaxY, Math.Max(c.Y, d.Y)) + eps) continue;
                if (SegmentsIntersect(a, b, c, d, eps, out Pt hit))
                {
                    at = new Pt(hit.X + ring[0].X, hit.Y + ring[0].Y);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Area of the intersection of two simple polygons, square drawing units. Both rings are
    /// triangulated (ear clipping) and every pair of triangles with overlapping bounds is
    /// clipped (Sutherland-Hodgman, convex against convex).
    /// </summary>
    /// <exception cref="InvalidOperationException">A ring is not simple and cannot be triangulated.</exception>
    public static double IntersectionArea(IReadOnlyList<Pt> a, IReadOnlyList<Pt> b)
    {
        var (amin, amax) = Bounds(a);
        var (bmin, bmax) = Bounds(b);
        if (amin.X > bmax.X || bmin.X > amax.X || amin.Y > bmax.Y || bmin.Y > amax.Y) return 0;

        Pt origin = a[0];
        var ta = Triangulate(CounterClockwise(OpenLocal(a, origin, out _)));
        var tb = Triangulate(CounterClockwise(OpenLocal(b, origin, out _)));
        var la = new Pt(amin.X - origin.X, amin.Y - origin.Y);
        var ha = new Pt(amax.X - origin.X, amax.Y - origin.Y);
        var lb = new Pt(bmin.X - origin.X, bmin.Y - origin.Y);
        var hb = new Pt(bmax.X - origin.X, bmax.Y - origin.Y);
        var boxA = ta.Where(t => Overlaps(TriBounds(t), (lb, hb))).Select(t => (Tri: t, Box: TriBounds(t))).ToList();
        var boxB = tb.Where(t => Overlaps(TriBounds(t), (la, ha))).Select(t => (Tri: t, Box: TriBounds(t))).ToList();

        double sum = 0;
        foreach (var x in boxA)
            foreach (var y in boxB)
                if (Overlaps(x.Box, y.Box))
                    sum += ClipArea(x.Tri, y.Tri);
        return sum;
    }

    /// <summary>
    /// Ring of points from polyline vertices with AutoCAD bulges: each arc edge is subdivided
    /// so its mid-ordinate is at most <paramref name="midOrdinate"/>. Original vertices are
    /// kept exactly. The result ends with its first point when <paramref name="closed"/>.
    /// </summary>
    public static IReadOnlyList<Pt> TessellateBulges(IReadOnlyList<PolyVertex> vertices, bool closed, double midOrdinate)
    {
        var result = new List<Pt>();
        int n = vertices.Count;
        if (n == 0) return result;
        int edges = closed ? n : n - 1;
        for (int i = 0; i < edges; i++)
        {
            Pt p = vertices[i].Point, q = vertices[(i + 1) % n].Point;
            double bulge = vertices[i].Bulge;
            result.Add(p);
            if (bulge == 0 || p == q) continue;

            double theta = 4 * Math.Atan(bulge);                      // signed included angle, CCW positive
            double dx = q.X - p.X, dy = q.Y - p.Y, c = Math.Sqrt(dx * dx + dy * dy);
            double r = c / (2 * Math.Abs(Math.Sin(theta / 2)));
            double h = c * (1 - bulge * bulge) / (4 * bulge);        // chord midpoint to center, along the chord's left normal
            double cx = (p.X + q.X) / 2 - dy / c * h, cy = (p.Y + q.Y) / 2 + dx / c * h;
            double a0 = Math.Atan2(p.Y - cy, p.X - cx);
            int k = Math.Max(1, (int)Math.Ceiling(Math.Abs(theta) / ArcStep(r, midOrdinate) - 1e-9));
            for (int j = 1; j < k; j++)
            {
                double ang = a0 + theta * j / k;
                result.Add(new Pt(cx + r * Math.Cos(ang), cy + r * Math.Sin(ang)));
            }
        }
        result.Add(closed ? vertices[0].Point : vertices[n - 1].Point);
        return result;
    }

    /// <summary>Largest central angle, radians, whose chord on radius <paramref name="radius"/> keeps the mid-ordinate within <paramref name="midOrdinate"/>. Capped at a quarter turn.</summary>
    internal static double ArcStep(double radius, double midOrdinate)
    {
        if (!(radius > 0)) return Math.PI / 2;
        double ratio = 1 - midOrdinate / radius;
        if (ratio <= 0) return Math.PI / 2;
        return Math.Min(Math.PI / 2, 2 * Math.Acos(ratio));
    }

    // ------------------------------------------------------------------------------------

    /// <summary>Ring translated by <paramref name="origin"/>, closing repeat and consecutive duplicates removed.</summary>
    private static Pt[] OpenLocal(IReadOnlyList<Pt> ring, Pt origin, out double extent)
    {
        var list = new List<Pt>(ring.Count);
        foreach (var p in ring)
        {
            var q = new Pt(p.X - origin.X, p.Y - origin.Y);
            if (list.Count == 0 || list[^1] != q) list.Add(q);
        }
        while (list.Count > 1 && list[0] == list[^1]) list.RemoveAt(list.Count - 1);
        extent = 0;
        if (list.Count > 0)
        {
            var (mn, mx) = Bounds(list);
            extent = Math.Max(mx.X - mn.X, mx.Y - mn.Y);
        }
        return list.ToArray();
    }

    private static Pt[] CounterClockwise(Pt[] pts)
    {
        if (SignedArea(pts) < 0) Array.Reverse(pts);
        return pts;
    }

    private static int Side(Pt a, Pt b, Pt p, double eps)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len == 0) return 0;
        double dist = (dx * (p.Y - a.Y) - dy * (p.X - a.X)) / len;
        return dist > eps ? 1 : dist < -eps ? -1 : 0;
    }

    private static bool Within(Pt a, Pt b, Pt p, double eps) =>
        p.X >= Math.Min(a.X, b.X) - eps && p.X <= Math.Max(a.X, b.X) + eps &&
        p.Y >= Math.Min(a.Y, b.Y) - eps && p.Y <= Math.Max(a.Y, b.Y) + eps;

    private static bool SegmentsIntersect(Pt a, Pt b, Pt c, Pt d, double eps, out Pt hit)
    {
        hit = default;
        int d1 = Side(a, b, c, eps), d2 = Side(a, b, d, eps), d3 = Side(c, d, a, eps), d4 = Side(c, d, b, eps);
        if (d1 * d2 < 0 && d3 * d4 < 0)
        {
            double rx = b.X - a.X, ry = b.Y - a.Y, sx = d.X - c.X, sy = d.Y - c.Y;
            double t = ((c.X - a.X) * sy - (c.Y - a.Y) * sx) / (rx * sy - ry * sx);
            hit = new Pt(a.X + t * rx, a.Y + t * ry);
            return true;
        }
        if (d1 == 0 && Within(a, b, c, eps)) { hit = c; return true; }
        if (d2 == 0 && Within(a, b, d, eps)) { hit = d; return true; }
        if (d3 == 0 && Within(c, d, a, eps)) { hit = a; return true; }
        if (d4 == 0 && Within(c, d, b, eps)) { hit = b; return true; }
        return false;
    }

    private static double Cross(Pt o, Pt a, Pt b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    private static double Dist(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Ear clipping of a counter-clockwise simple ring (no closing repeat). Collinear vertices are dropped.</summary>
    internal static List<Pt[]> Triangulate(Pt[] ccw)
    {
        var tris = new List<Pt[]>();
        if (ccw.Length < 3) return tris;
        var (mn, mx) = Bounds(ccw);
        double ext = Math.Max(mx.X - mn.X, mx.Y - mn.Y);
        double insideTol = 1e-13 * ext * ext;

        var idx = new List<int>(Enumerable.Range(0, ccw.Length));
        int k = 0, sinceProgress = 0;
        while (idx.Count > 3)
        {
            int n = idx.Count;
            k %= n;
            if (sinceProgress > n)
                throw new InvalidOperationException("Polygon could not be triangulated: it is not simple or is numerically degenerate.");

            Pt p = ccw[idx[(k + n - 1) % n]], c = ccw[idx[k]], q = ccw[idx[(k + 1) % n]];
            double cross = Cross(p, c, q);
            if (Math.Abs(cross) <= 1e-12 * Dist(p, c) * Dist(c, q))
            {
                idx.RemoveAt(k);                    // collinear: contributes no area
                sinceProgress = 0;
                continue;
            }
            if (cross > 0 && IsEar(ccw, idx, k, p, c, q, insideTol))
            {
                tris.Add([p, c, q]);
                idx.RemoveAt(k);
                sinceProgress = 0;
                continue;
            }
            k++;
            sinceProgress++;
        }
        if (idx.Count == 3)
        {
            Pt p = ccw[idx[0]], c = ccw[idx[1]], q = ccw[idx[2]];
            if (Cross(p, c, q) > 0) tris.Add([p, c, q]);
        }
        return tris;
    }

    private static bool IsEar(Pt[] pts, List<int> idx, int k, Pt p, Pt c, Pt q, double tol)
    {
        int n = idx.Count;
        int ip = idx[(k + n - 1) % n], ic = idx[k], iq = idx[(k + 1) % n];
        foreach (int j in idx)
        {
            if (j == ip || j == ic || j == iq) continue;
            Pt x = pts[j];
            if (Cross(p, c, x) >= -tol && Cross(c, q, x) >= -tol && Cross(q, p, x) >= -tol) return false;
        }
        return true;
    }

    private static (Pt Min, Pt Max) TriBounds(Pt[] t) =>
        (new Pt(Math.Min(t[0].X, Math.Min(t[1].X, t[2].X)), Math.Min(t[0].Y, Math.Min(t[1].Y, t[2].Y))),
         new Pt(Math.Max(t[0].X, Math.Max(t[1].X, t[2].X)), Math.Max(t[0].Y, Math.Max(t[1].Y, t[2].Y))));

    private static bool Overlaps((Pt Min, Pt Max) a, (Pt Min, Pt Max) b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y;

    /// <summary>Area of a counter-clockwise triangle clipped by a counter-clockwise convex polygon.</summary>
    private static double ClipArea(Pt[] subject, Pt[] clip)
    {
        var output = new List<Pt>(subject);
        for (int e = 0; e < clip.Length && output.Count > 0; e++)
        {
            Pt c1 = clip[e], c2 = clip[(e + 1) % clip.Length];
            var input = output;
            output = new List<Pt>(input.Count + 2);
            for (int i = 0; i < input.Count; i++)
            {
                Pt cur = input[i], prev = input[(i + input.Count - 1) % input.Count];
                double dc = Cross(c1, c2, cur), dp = Cross(c1, c2, prev);
                bool curIn = dc >= 0, prevIn = dp >= 0;
                if (curIn)
                {
                    if (!prevIn) output.Add(Lerp(prev, cur, dp / (dp - dc)));
                    output.Add(cur);
                }
                else if (prevIn)
                {
                    output.Add(Lerp(prev, cur, dp / (dp - dc)));
                }
            }
        }
        return output.Count < 3 ? 0 : Math.Max(0, SignedArea(output));
    }

    private static Pt Lerp(Pt a, Pt b, double t) => new(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));
}

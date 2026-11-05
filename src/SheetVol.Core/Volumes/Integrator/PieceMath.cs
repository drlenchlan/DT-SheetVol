// Low-level geometry for the overlay integrator: convex polygon pieces, half-plane clips,
// exact integration of a linear field, triangle planes. Owner: C5. Internal.
//
// All coordinates here are LOCAL (translated to the boundary's origin, E1009).

namespace SheetVol.Core.Volumes.Integrator;

/// <summary>
/// One vertex of a convex piece. X, Y are local plan coordinates (drawing units). Zb is the base
/// surface elevation and D the depth base - comparison at that vertex, both drawing units. On a
/// piece both surfaces are planar, so Zb and D are linear over it and linear interpolation along
/// an edge is exact.
/// </summary>
internal struct PieceVertex
{
    public double X, Y, Zb, D;

    public PieceVertex(double x, double y)
    {
        X = x; Y = y; Zb = 0; D = 0;
    }

    public static PieceVertex Lerp(in PieceVertex p, in PieceVertex q, double t) => new()
    {
        X = p.X + t * (q.X - p.X),
        Y = p.Y + t * (q.Y - p.Y),
        Zb = p.Zb + t * (q.Zb - p.Zb),
        D = p.D + t * (q.D - p.D),
    };
}

/// <summary>Compensated (Neumaier) sum so many small pieces add up to 1e-15 relative, not n * 1e-16.</summary>
internal struct NeumaierSum
{
    private double _sum, _c;

    public void Add(double x)
    {
        double t = _sum + x;
        if (Math.Abs(_sum) >= Math.Abs(x)) _c += (_sum - t) + x;
        else _c += (x - t) + _sum;
        _sum = t;
    }

    public readonly double Value => _sum + _c;
}

/// <summary>Which vertex attribute a scalar clip acts on.</summary>
internal enum PieceAttr { Zb, D }

internal static class PieceMath
{
    /// <summary>
    /// Upper bound on piece vertex count. A triangle clipped by k half-planes has at most 3 + k
    /// vertices; the deepest chain here is 3 (base) + 3 (boundary) + 3 scalar clips = 12.
    /// </summary>
    public const int MaxVertices = 32;

    /// <summary>
    /// Sutherland-Hodgman clip of a convex polygon against the half-plane nx*x + ny*y - c &gt;= 0.
    /// Signed values within <paramref name="snap"/> of zero are treated as exactly on the line, so a
    /// vertex that lies on the line in exact arithmetic never spawns a sliver. Orientation is
    /// preserved. Returns the vertex count written to <paramref name="dst"/> (0 when nothing of
    /// positive extent remains).
    /// </summary>
    public static int ClipLine(ReadOnlySpan<PieceVertex> src, Span<PieceVertex> dst, double nx, double ny, double c, double snap)
    {
        int n = src.Length;
        if (n == 0) return 0;
        if (n >= MaxVertices) throw new InvalidOperationException("Integrator piece exceeded its vertex bound.");
        Span<double> s = stackalloc double[n];
        bool anyPos = false, anyNeg = false;
        for (int i = 0; i < n; i++)
        {
            double v = nx * src[i].X + ny * src[i].Y - c;
            if (Math.Abs(v) <= snap) v = 0;
            s[i] = v;
            if (v > 0) anyPos = true;
            else if (v < 0) anyNeg = true;
        }
        if (!anyPos) return 0;                  // all on or outside: nothing of positive area inside
        if (!anyNeg) { src.CopyTo(dst); return n; }
        return Emit(src, s, dst, pin: null, level: 0);
    }

    /// <summary>
    /// Clip on a vertex attribute: keeps f &gt; 0 (strict) or f &gt;= 0 (closed), where
    /// f = attr - level when <paramref name="keepAbove"/>, else f = level - attr. New vertices on
    /// the clip line get the attribute set to exactly <paramref name="level"/>.
    /// Strict and closed differ only when every vertex has f == 0 (the piece lies flat on the
    /// level): strict returns nothing, closed returns the whole piece.
    /// </summary>
    public static int ClipScalar(ReadOnlySpan<PieceVertex> src, Span<PieceVertex> dst, PieceAttr attr, double level, bool keepAbove, bool strict)
    {
        int n = src.Length;
        if (n == 0) return 0;
        if (n >= MaxVertices) throw new InvalidOperationException("Integrator piece exceeded its vertex bound.");
        Span<double> s = stackalloc double[n];
        bool anyPos = false, anyNeg = false;
        for (int i = 0; i < n; i++)
        {
            double a = attr == PieceAttr.D ? src[i].D : src[i].Zb;
            double v = keepAbove ? a - level : level - a;
            s[i] = v;
            if (v > 0) anyPos = true;
            else if (v < 0) anyNeg = true;
        }
        if (strict && !anyPos) return 0;
        if (!anyNeg) { src.CopyTo(dst); return n; }
        if (!anyPos) return 0;
        return Emit(src, s, dst, attr, level);
    }

    /// <summary>
    /// Writes the kept vertices (s &gt;= 0) and the strict crossings in order. When
    /// <paramref name="pin"/> is set, each crossing gets that attribute set to exactly
    /// <paramref name="level"/> (interpolation would leave a rounding residue).
    /// </summary>
    private static int Emit(ReadOnlySpan<PieceVertex> src, ReadOnlySpan<double> s, Span<PieceVertex> dst, PieceAttr? pin, double level)
    {
        int n = src.Length, m = 0;
        for (int i = 0; i < n; i++)
        {
            int j = i + 1 == n ? 0 : i + 1;
            double si = s[i], sj = s[j];
            if (si >= 0) dst[m++] = src[i];
            if ((si > 0 && sj < 0) || (si < 0 && sj > 0))
            {
                double t = si / (si - sj);
                var v = PieceVertex.Lerp(src[i], src[j], t);
                if (pin == PieceAttr.D) v.D = level;
                else if (pin == PieceAttr.Zb) v.Zb = level;
                dst[m++] = v;
            }
        }
        return m;
    }

    /// <summary>
    /// Exact plan area and exact integral of the linear field D over a convex polygon, by fan
    /// triangulation from vertex 0: each triangle contributes area * (D0 + Di + Di+1) / 3, which is
    /// exact for a linear field (no centroid sampling error). Area in square drawing units,
    /// integral in cubic drawing units. Counter-clockwise input gives positive values.
    /// </summary>
    public static void Integrate(ReadOnlySpan<PieceVertex> p, out double area, out double integralD)
    {
        area = 0; integralD = 0;
        int n = p.Length;
        if (n < 3) return;
        double x0 = p[0].X, y0 = p[0].Y, d0 = p[0].D;
        double a2 = 0, v6 = 0;
        for (int i = 1; i + 1 < n; i++)
        {
            double ax = p[i].X - x0, ay = p[i].Y - y0;
            double bx = p[i + 1].X - x0, by = p[i + 1].Y - y0;
            double cr = ax * by - ay * bx;
            a2 += cr;
            v6 += cr * (d0 + p[i].D + p[i + 1].D);
        }
        area = 0.5 * a2;
        integralD = v6 / 6.0;
    }

    /// <summary>Perimeter of a polygon, drawing units.</summary>
    public static double Perimeter(ReadOnlySpan<PieceVertex> p)
    {
        double sum = 0;
        for (int i = 0; i < p.Length; i++)
        {
            int j = i + 1 == p.Length ? 0 : i + 1;
            sum += Math.Sqrt((p[j].X - p[i].X) * (p[j].X - p[i].X) + (p[j].Y - p[i].Y) * (p[j].Y - p[i].Y));
        }
        return sum;
    }

    /// <summary>Integral of max(D, 0) over the polygon (cut volume), cubic drawing units, &gt;= 0.</summary>
    public static double PositivePart(ReadOnlySpan<PieceVertex> p, Span<PieceVertex> scratch)
    {
        if (p.Length < 3) return 0;
        bool anyPos = false, anyNeg = false;
        foreach (var v in p) { if (v.D > 0) anyPos = true; else if (v.D < 0) anyNeg = true; }
        if (!anyPos) return 0;
        if (!anyNeg) { Integrate(p, out _, out double all); return Math.Max(all, 0); }
        int m = ClipScalar(p, scratch, PieceAttr.D, 0.0, keepAbove: true, strict: false);
        Integrate(scratch[..m], out _, out double vol);
        return Math.Max(vol, 0);
    }

    /// <summary>Integral of max(-D, 0) over the polygon (fill volume), cubic drawing units, &gt;= 0.</summary>
    public static double NegativePart(ReadOnlySpan<PieceVertex> p, Span<PieceVertex> scratch)
    {
        if (p.Length < 3) return 0;
        bool anyPos = false, anyNeg = false;
        foreach (var v in p) { if (v.D > 0) anyPos = true; else if (v.D < 0) anyNeg = true; }
        if (!anyNeg) return 0;
        if (!anyPos) { Integrate(p, out _, out double all); return Math.Max(-all, 0); }
        int m = ClipScalar(p, scratch, PieceAttr.D, 0.0, keepAbove: false, strict: false);
        Integrate(scratch[..m], out _, out double vol);
        return Math.Max(-vol, 0);
    }
}

/// <summary>
/// A counter-clockwise plan triangle used only as a clip window (boundary ear triangles).
/// Inside is where Nk.x * x + Nk.y * y - Ck &gt;= 0 for all three edges (unit left normals).
/// </summary>
internal readonly struct ClipTriangle
{
    public readonly double X0, Y0, X1, Y1, X2, Y2;
    public readonly double MinX, MinY, MaxX, MaxY;
    public readonly double N0x, N0y, C0, N1x, N1y, C1, N2x, N2y, C2;
    public readonly double Area;

    public ClipTriangle(double x0, double y0, double x1, double y1, double x2, double y2)
    {
        X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
        MinX = Math.Min(x0, Math.Min(x1, x2)); MaxX = Math.Max(x0, Math.Max(x1, x2));
        MinY = Math.Min(y0, Math.Min(y1, y2)); MaxY = Math.Max(y0, Math.Max(y1, y2));
        Area = 0.5 * ((x1 - x0) * (y2 - y0) - (y1 - y0) * (x2 - x0));
        EdgeNormal(x0, y0, x1, y1, out N0x, out N0y, out C0);
        EdgeNormal(x1, y1, x2, y2, out N1x, out N1y, out C1);
        EdgeNormal(x2, y2, x0, y0, out N2x, out N2y, out C2);
    }

    internal static void EdgeNormal(double ax, double ay, double bx, double by, out double nx, out double ny, out double c)
    {
        double ex = bx - ax, ey = by - ay;
        double len = Math.Sqrt(ex * ex + ey * ey);
        nx = -ey / len; ny = ex / len;
        c = nx * ax + ny * ay;
    }

    /// <summary>Clips a convex polygon to this triangle. Ping-pongs between the two buffers; returns the result span (inside one of them).</summary>
    public Span<PieceVertex> Clip(ReadOnlySpan<PieceVertex> src, Span<PieceVertex> a, Span<PieceVertex> b, double snap)
    {
        int n = PieceMath.ClipLine(src, a, N0x, N0y, C0, snap);
        if (n < 3) return Span<PieceVertex>.Empty;
        n = PieceMath.ClipLine(a[..n], b, N1x, N1y, C1, snap);
        if (n < 3) return Span<PieceVertex>.Empty;
        n = PieceMath.ClipLine(b[..n], a, N2x, N2y, C2, snap);
        if (n < 3) return Span<PieceVertex>.Empty;
        return a[..n];
    }
}

/// <summary>
/// One surface triangle in local coordinates: counter-clockwise plan vertices, bounding box,
/// edge half-planes for clipping, and its plane z(x, y).
/// The plane is computed from the three vertices sorted by (x, y), so two surfaces that carry the
/// same triangle (in any vertex order) evaluate to bitwise-identical elevations and depth is
/// exactly zero there.
/// </summary>
internal readonly struct SurfaceTriangle
{
    private const double MachineEpsilon = 2.220446049250313e-16;   // 2^-52

    public readonly ClipTriangle Plan;
    private readonly double _rx, _ry, _rz, _gx, _gy;

    /// <summary>
    /// Forward error bound on <see cref="Eval"/>, drawing units:
    /// 64 * eps * (max |z| + kappa * |grad z| * (L + W)), where kappa = L^2 / (2 * plan area) is the
    /// triangle's conditioning, L its longest plan edge and W the magnitude of the world
    /// coordinates (input quantization at state-plane scale). Two triangles that lie in the same
    /// plane in exact arithmetic evaluate within the sum of their bounds of each other.
    /// </summary>
    public readonly double Noise;

    private SurfaceTriangle(ClipTriangle plan, double rx, double ry, double rz, double gx, double gy, double noise)
    {
        Plan = plan; _rx = rx; _ry = ry; _rz = rz; _gx = gx; _gy = gy; Noise = noise;
    }

    /// <summary>Elevation of the triangle's plane at local (x, y), drawing units.</summary>
    public double Eval(double x, double y) => _rz + _gx * (x - _rx) + _gy * (y - _ry);

    /// <summary>
    /// Builds a local triangle from a world triangle. Returns false with <paramref name="degenerate"/>
    /// set when the plan area is at most 1e-12 of the squared longest edge (a vertical or sliver
    /// triangle, whose plane is undefined); its plan area is negligible by the same bound.
    /// </summary>
    /// <param name="t">World triangle.</param>
    /// <param name="ox">Local origin x (world).</param>
    /// <param name="oy">Local origin y (world).</param>
    /// <param name="worldScale">Largest |world coordinate| in the window, for <see cref="Noise"/>.</param>
    /// <param name="tri">The local triangle.</param>
    /// <param name="degenerate">True when rejected for having no plan area.</param>
    public static bool TryCreate(in Tri3 t, double ox, double oy, double worldScale, out SurfaceTriangle tri, out bool degenerate)
    {
        double x0 = t.A.X - ox, y0 = t.A.Y - oy, z0 = t.A.Z;
        double x1 = t.B.X - ox, y1 = t.B.Y - oy, z1 = t.B.Z;
        double x2 = t.C.X - ox, y2 = t.C.Y - oy, z2 = t.C.Z;
        double det = (x1 - x0) * (y2 - y0) - (y1 - y0) * (x2 - x0);
        double l01 = (x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0);
        double l12 = (x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1);
        double l20 = (x0 - x2) * (x0 - x2) + (y0 - y2) * (y0 - y2);
        double lmax2 = Math.Max(l01, Math.Max(l12, l20));
        if (!(Math.Abs(det) > 1e-12 * lmax2))
        {
            tri = default; degenerate = true;
            return false;
        }
        degenerate = false;
        if (det < 0)
        {
            (x1, x2) = (x2, x1); (y1, y2) = (y2, y1); (z1, z2) = (z2, z1);
        }
        var plan = new ClipTriangle(x0, y0, x1, y1, x2, y2);

        // Canonical plane: vertices sorted by (x, y), reference = the first.
        Span<(double X, double Y, double Z)> v = [(x0, y0, z0), (x1, y1, z1), (x2, y2, z2)];
        Sort3(v);
        double e1x = v[1].X - v[0].X, e1y = v[1].Y - v[0].Y, e1z = v[1].Z - v[0].Z;
        double e2x = v[2].X - v[0].X, e2y = v[2].Y - v[0].Y, e2z = v[2].Z - v[0].Z;
        double d = e1x * e2y - e1y * e2x;
        double gx = (e1z * e2y - e2z * e1y) / d;
        double gy = (e2z * e1x - e1z * e2x) / d;
        double lmax = Math.Sqrt(lmax2);
        double kappa = lmax2 / Math.Abs(det);
        double maxZ = Math.Max(Math.Abs(z0), Math.Max(Math.Abs(z1), Math.Abs(z2)));
        double noise = 64 * MachineEpsilon * (maxZ + kappa * Math.Sqrt(gx * gx + gy * gy) * (lmax + worldScale));
        tri = new SurfaceTriangle(plan, v[0].X, v[0].Y, v[0].Z, gx, gy, noise);
        return true;
    }

    private static void Sort3(Span<(double X, double Y, double Z)> v)
    {
        if (Less(v[1], v[0])) (v[0], v[1]) = (v[1], v[0]);
        if (Less(v[2], v[1])) (v[1], v[2]) = (v[2], v[1]);
        if (Less(v[1], v[0])) (v[0], v[1]) = (v[1], v[0]);
    }

    private static bool Less((double X, double Y, double Z) a, (double X, double Y, double Z) b)
        => a.X < b.X || (a.X == b.X && a.Y < b.Y);
}

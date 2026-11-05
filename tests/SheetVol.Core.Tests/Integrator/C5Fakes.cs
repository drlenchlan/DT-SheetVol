// Minimal fakes for the C5 integrator tests. C1 builds the shared alignment fakes in parallel;
// the orchestrator consolidates at merge. Everything here is prefixed C5.

using System.Globalization;

namespace SheetVol.Core.Tests.Integrator;

/// <summary>
/// An alignment made of lines, circular arcs and clothoid spirals, with exact PointAt on lines and
/// arcs and numerically integrated PointAt on spirals (composite Simpson, step at most 0.25).
/// Curvature sign: positive turns left (counter-clockwise), negative turns right (clockwise).
/// Offsets: negative = left, positive = right (HANDOFF.md 6.2).
/// </summary>
internal sealed class C5Alignment : IAlignmentGeometry
{
    private sealed record Piece(SegKind Kind, double S0, double Length, double X0, double Y0, double Dir0, double K0, double K1);

    private readonly Piece[] _pieces;

    public string Name { get; }
    public double StartStation => _pieces[0].S0;
    public double EndStation => _pieces[^1].S0 + _pieces[^1].Length;
    public IReadOnlyList<AlignSeg> Segments { get; }

    private C5Alignment(string name, Piece[] pieces)
    {
        Name = name;
        _pieces = pieces;
        var segs = new List<AlignSeg>();
        foreach (var p in pieces)
        {
            var start = new Pt(p.X0, p.Y0);
            Center(p, p.Length, out double ex, out double ey, out double edir);
            Pt? center = null;
            double radius = 0;
            bool cw = false;
            if (p.Kind == SegKind.Arc)
            {
                radius = 1.0 / Math.Abs(p.K0);
                // Center is on the left for a left turn: start + (1/k) * left normal.
                center = new Pt(p.X0 - Math.Sin(p.Dir0) / p.K0, p.Y0 + Math.Cos(p.Dir0) / p.K0);
                cw = p.K0 < 0;
            }
            segs.Add(new AlignSeg(p.Kind, p.S0, p.S0 + p.Length, start, new Pt(ex, ey), p.Dir0, edir, center, radius, cw));
        }
        Segments = segs;
    }

    private static double Theta(Piece p, double u) => p.Dir0 + p.K0 * u + (p.K1 - p.K0) * u * u / (2 * p.Length);

    private static void Center(Piece p, double u, out double x, out double y, out double dir)
    {
        dir = Theta(p, u);
        switch (p.Kind)
        {
            case SegKind.Line:
                x = p.X0 + u * Math.Cos(p.Dir0);
                y = p.Y0 + u * Math.Sin(p.Dir0);
                return;
            case SegKind.Arc:
            {
                double r = 1.0 / p.K0;   // signed
                double cx = p.X0 - Math.Sin(p.Dir0) * r, cy = p.Y0 + Math.Cos(p.Dir0) * r;
                x = cx + Math.Sin(dir) * r;
                y = cy - Math.Cos(dir) * r;
                return;
            }
            default:
            {
                int n = 2 * Math.Max(4, (int)Math.Ceiling(u / 0.5));
                double h = u / n, sx = 0, sy = 0;
                for (int i = 0; i <= n; i++)
                {
                    double w = i == 0 || i == n ? 1 : (i % 2 == 1 ? 4 : 2);
                    double t = Theta(p, i * h);
                    sx += w * Math.Cos(t); sy += w * Math.Sin(t);
                }
                x = p.X0 + sx * h / 3; y = p.Y0 + sy * h / 3;
                return;
            }
        }
    }

    public Pt PointAt(double rawStation, double offset)
    {
        var p = _pieces[^1];
        foreach (var q in _pieces)
            if (rawStation <= q.S0 + q.Length) { p = q; break; }
        double u = Math.Clamp(rawStation - p.S0, 0, p.Length);
        Center(p, u, out double x, out double y, out double dir);
        // Right normal of direction dir is (sin dir, -cos dir).
        return new Pt(x + offset * Math.Sin(dir), y - offset * Math.Cos(dir));
    }

    public string FormatStation(double rawStation) => rawStation.ToString("0.00", CultureInfo.InvariantCulture);

    public bool TryParseStation(string text, out double rawStation, out string? error)
    {
        error = null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out rawStation)) return true;
        error = "not a number";
        return false;
    }

    public sealed class Builder
    {
        private readonly string _name;
        private readonly List<Piece> _pieces = [];
        private double _x, _y, _dir, _s;

        public Builder(string name, double x0 = 0, double y0 = 0, double dir0 = 0, double station0 = 0)
        {
            _name = name; _x = x0; _y = y0; _dir = dir0; _s = station0;
        }

        private Builder Add(SegKind kind, double length, double k0, double k1)
        {
            var p = new Piece(kind, _s, length, _x, _y, _dir, k0, k1);
            _pieces.Add(p);
            Center(p, length, out _x, out _y, out _dir);
            _s += length;
            return this;
        }

        public Builder Line(double length) => Add(SegKind.Line, length, 0, 0);

        /// <summary>Arc of the given radius and length; clockwise = turns right.</summary>
        public Builder Arc(double radius, double length, bool clockwise)
        {
            double k = (clockwise ? -1 : 1) / radius;
            return Add(SegKind.Arc, length, k, k);
        }

        /// <summary>Clothoid from signed curvature k0 to k1.</summary>
        public Builder Spiral(double length, double k0, double k1) => Add(SegKind.Spiral, length, k0, k1);

        public C5Alignment Build() => new(_name, [.. _pieces]);
    }

    /// <summary>Alignment S: straight from (0,0) to (2000,0), station 0 at x = 0, left = +y (section 8).</summary>
    public static C5Alignment S(double ox = 0, double oy = 0) => new Builder("S", ox, oy).Line(2000).Build();

    /// <summary>Alignment C: tangent 300 east, arc R = 300 clockwise, delta 60 degrees (length 100*pi), tangent 300 (section 8).</summary>
    public static C5Alignment C(double ox = 0, double oy = 0)
        => new Builder("C", ox, oy).Line(300).Arc(300, 100 * Math.PI, clockwise: true).Line(300).Build();

    /// <summary>Alignment SP: tangent 200, spiral 150 (0 to 1/400 right), arc R 400 right length 200, spiral 150 back to 0, tangent 200.</summary>
    public static C5Alignment SP()
        => new Builder("SP").Line(200).Spiral(150, 0, -1.0 / 400).Arc(400, 200, clockwise: true).Spiral(150, -1.0 / 400, 0).Line(200).Build();
}

/// <summary>Tessellated band boundaries for the tests (C1 owns the real boundary builder).</summary>
internal static class C5Boundaries
{
    /// <summary>
    /// Closed band from station s0 to s1 between signed offsets offL &lt; offR: the left offset line
    /// forward, then the right offset line back, first == last. Stations: s0, every segment end
    /// inside, s1, and on arcs and spirals a vertex at most every <paramref name="curveStep"/> along
    /// the alignment.
    /// </summary>
    public static IReadOnlyList<Pt> Band(IAlignmentGeometry al, double s0, double s1, double offL, double offR, double curveStep = 2.0)
    {
        var st = new List<double> { s0 };
        foreach (var g in al.Segments)
        {
            double a = Math.Max(g.StartStation, s0), b = Math.Min(g.EndStation, s1);
            if (b <= a) continue;
            if (g.Kind != SegKind.Line)
            {
                int n = Math.Max(1, (int)Math.Ceiling((b - a) / curveStep));
                for (int i = 1; i < n; i++) st.Add(a + (b - a) * i / n);
            }
            if (b < s1) st.Add(b);
        }
        st.Add(s1);
        st.Sort();
        var pts = new List<Pt>();
        foreach (double s in st) pts.Add(al.PointAt(s, offL));
        for (int i = st.Count - 1; i >= 0; i--) pts.Add(al.PointAt(st[i], offR));
        pts.Add(pts[0]);
        return pts;
    }

    /// <summary>Axis-aligned rectangle, closed, counter-clockwise.</summary>
    public static IReadOnlyList<Pt> Rect(double x0, double y0, double x1, double y1)
        => [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1), new(x0, y0)];

    /// <summary>Shoelace area of a closed polygon, computed relative to its first vertex.</summary>
    public static double Area(IReadOnlyList<Pt> closed)
    {
        double a2 = 0, x0 = closed[0].X, y0 = closed[0].Y;
        for (int i = 0; i + 1 < closed.Count; i++)
            a2 += (closed[i].X - x0) * (closed[i + 1].Y - y0) - (closed[i].Y - y0) * (closed[i + 1].X - x0);
        return Math.Abs(0.5 * a2);
    }

    public static IReadOnlyList<Pt> Shift(IReadOnlyList<Pt> pts, double dx, double dy) => [.. pts.Select(p => new Pt(p.X + dx, p.Y + dy))];
}

/// <summary>Wraps a source and moves it by (dx, dy) (state-plane test, E1009).</summary>
internal sealed class C5ShiftedSource(ITriangleSource inner, double dx, double dy) : ITriangleSource
{
    public IEnumerable<Tri3> Triangles(Pt windowMin, Pt windowMax)
    {
        foreach (var t in inner.Triangles(new Pt(windowMin.X - dx, windowMin.Y - dy), new Pt(windowMax.X - dx, windowMax.Y - dy)))
            yield return new Tri3(Move(t.A), Move(t.B), Move(t.C));
    }

    private Pt3 Move(Pt3 p) => new(p.X + dx, p.Y + dy, p.Z);
}

/// <summary>Wraps a source, rotates it by <c>a</c> radians about the origin, then moves it by (dx, dy). Elevations unchanged.</summary>
internal sealed class C5RotatedSource(ITriangleSource inner, double a, double dx, double dy) : ITriangleSource
{
    private readonly double _c = Math.Cos(a), _s = Math.Sin(a);

    public IEnumerable<Tri3> Triangles(Pt windowMin, Pt windowMax)
    {
        // Inverse-rotate the window corners and request their bounding box.
        Span<Pt> corners = [new(windowMin.X, windowMin.Y), new(windowMax.X, windowMin.Y), new(windowMax.X, windowMax.Y), new(windowMin.X, windowMax.Y)];
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var p in corners)
        {
            double u = p.X - dx, v = p.Y - dy;
            double x = _c * u + _s * v, y = -_s * u + _c * v;
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
        }
        return inner.Triangles(new Pt(x0, y0), new Pt(x1, y1)).Select(t => new Tri3(Move(t.A), Move(t.B), Move(t.C)));
    }

    private Pt3 Move(Pt3 p) => new(dx + _c * p.X - _s * p.Y, dy + _s * p.X + _c * p.Y, p.Z);
}

/// <summary>Wraps a source and scales x, y and z by k (unit-agnostic test, E1020).</summary>
internal sealed class C5ScaledSource(ITriangleSource inner, double k) : ITriangleSource
{
    public IEnumerable<Tri3> Triangles(Pt windowMin, Pt windowMax)
    {
        foreach (var t in inner.Triangles(new Pt(windowMin.X / k, windowMin.Y / k), new Pt(windowMax.X / k, windowMax.Y / k)))
            yield return new Tri3(Scale(t.A), Scale(t.B), Scale(t.C));
    }

    private Pt3 Scale(Pt3 p) => new(p.X * k, p.Y * k, p.Z * k);
}

/// <summary>Keeps only the triangles whose centroid satisfies a predicate (a surface with a hole or a partial extent, E1011).</summary>
internal sealed class C5FilteredSource(ITriangleSource inner, Func<double, double, bool> keep) : ITriangleSource
{
    public IEnumerable<Tri3> Triangles(Pt windowMin, Pt windowMax)
        => inner.Triangles(windowMin, windowMax).Where(t => keep((t.A.X + t.B.X + t.C.X) / 3, (t.A.Y + t.B.Y + t.C.Y) / 3));
}

/// <summary>A source with no triangles (E403).</summary>
internal sealed class C5EmptySource : ITriangleSource
{
    public IEnumerable<Tri3> Triangles(Pt windowMin, Pt windowMax) => [];
}

/// <summary>Shared constants and helpers.</summary>
internal static class C5
{
    public const double CubicFeetPerCubicYard = 27.0;

    public static SegmentDef Seg(double s0, double s1, double offL = -100, double offR = 100)
        => new(Guid.NewGuid(), $"{s0}-{s1}", s0, s1, offL, offR);

    public static OhwmSpec Ohwm(double elevation, IReadOnlyDictionary<Guid, double>? bySegment = null)
        => new(elevation, bySegment ?? new Dictionary<Guid, double>());

    /// <summary>Asserts |actual - expected| &lt;= rel * max(|expected|, floor).</summary>
    public static void Rel(double expected, double actual, double rel, double floor = 1.0, string? what = null)
    {
        double tol = rel * Math.Max(Math.Abs(expected), floor);
        Assert.True(Math.Abs(actual - expected) <= tol,
            $"{what ?? "value"}: expected {expected:R}, got {actual:R} (diff {actual - expected:E3}, tol {tol:E3})");
    }
}

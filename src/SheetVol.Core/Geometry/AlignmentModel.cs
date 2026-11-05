using SheetVol.Core.Planning;

namespace SheetVol.Core.Geometry;

internal enum JunctionKind
{
    /// <summary>Directions agree within tolerance; one offset point serves both elements.</summary>
    Tangent,
    /// <summary>Two lines meet at a change of direction; offsets join by mitre.</summary>
    AnglePoint,
    /// <summary>A curve or spiral joins with a change of direction above tolerance.</summary>
    NonTangentCurve,
}

/// <summary>
/// Read-only evaluator over <see cref="IAlignmentGeometry.Segments"/>: offset points on lines
/// and arcs in closed form, spirals through <see cref="IAlignmentGeometry.PointAt"/>, mitre
/// points at angle points, and the signed curvature at each element end (counter-clockwise
/// positive). Spiral end curvatures follow the clothoid model: taken from the neighbouring
/// line or arc where there is one, otherwise solved from the direction change
/// (k1 + k2) / 2 * L = delta.
/// </summary>
internal sealed class AlignmentModel
{
    public IAlignmentGeometry Source { get; }
    public AlignSeg[] Segs { get; }
    public double[] K1 { get; }
    public double[] K2 { get; }
    public double[] Deflection { get; }
    public JunctionKind[] Kinds { get; }

    private readonly double[] _ux, _uy, _phi0, _sgn;
    private readonly double[] _starts;

    private AlignmentModel(IAlignmentGeometry source, AlignSeg[] segs, Func<double, double> maxAbsOffsetAt, GeometryOptions opt)
    {
        Source = source;
        Segs = segs;
        int n = segs.Length;
        _ux = new double[n]; _uy = new double[n]; _phi0 = new double[n]; _sgn = new double[n];
        K1 = new double[n]; K2 = new double[n];
        _starts = segs.Select(s => s.StartStation).ToArray();

        for (int i = 0; i < n; i++)
        {
            var g = segs[i];
            switch (g.Kind)
            {
                case SegKind.Line:
                    double dx = g.End.X - g.Start.X, dy = g.End.Y - g.Start.Y, len = Math.Sqrt(dx * dx + dy * dy);
                    _ux[i] = dx / len; _uy[i] = dy / len;
                    break;
                case SegKind.Arc:
                    var c = g.Center!.Value;
                    _phi0[i] = Math.Atan2(g.Start.Y - c.Y, g.Start.X - c.X);
                    _sgn[i] = g.Clockwise ? -1 : 1;
                    K1[i] = K2[i] = _sgn[i] / g.Radius;
                    break;
            }
        }

        for (int i = 0; i < n; i++)
        {
            if (segs[i].Kind != SegKind.Spiral) continue;
            double length = segs[i].EndStation - segs[i].StartStation;
            double delta = Angles.Normalize(segs[i].EndDir - segs[i].StartDir);
            double? k1 = i > 0 ? K2[i - 1] : null;
            double? k2 = i + 1 < n && segs[i + 1].Kind != SegKind.Spiral ? K1[i + 1] : null;
            if (k1 is null && k2 is null) k1 = 0;
            K1[i] = k1 ?? 2 * delta / length - k2!.Value;
            K2[i] = k2 ?? 2 * delta / length - k1!.Value;
        }

        Deflection = new double[Math.Max(0, n - 1)];
        Kinds = new JunctionKind[Math.Max(0, n - 1)];
        for (int j = 0; j < n - 1; j++)
        {
            double d = Angles.Normalize(StartDirection(j + 1) - EndDirection(j));
            Deflection[j] = d;
            if (segs[j].Kind == SegKind.Line && segs[j + 1].Kind == SegKind.Line)
                Kinds[j] = JunctionKind.AnglePoint;
            else
                Kinds[j] = Math.Abs(d) * maxAbsOffsetAt(segs[j].EndStation) <= opt.MidOrdinate ? JunctionKind.Tangent : JunctionKind.NonTangentCurve;
        }
    }

    /// <summary>
    /// Builds the model, or returns null after adding E214 problems when the geometry has no
    /// elements, a zero-length element, an arc without center or radius, or a gap.
    /// </summary>
    /// <param name="a">The alignment.</param>
    /// <param name="maxAbsOffsetAt">
    /// Largest offset magnitude in use at a junction, given the junction's raw station: the
    /// widest of the segments whose range contains it, 0 where none does. A curve join whose
    /// kink times that offset stays within the mid-ordinate is treated as tangent; per-segment
    /// offsets (rev 2) mean the answer differs from junction to junction.
    /// </param>
    /// <param name="opt">Tolerances.</param>
    /// <param name="problems">Receives E214 problems.</param>
    public static AlignmentModel? Create(IAlignmentGeometry a, Func<double, double> maxAbsOffsetAt, GeometryOptions opt, List<Problem> problems)
    {
        var segs = a.Segments.ToArray();
        int before = problems.Count;
        double tol = opt.StationTolerance, gap = opt.MidOrdinate;
        if (segs.Length == 0)
        {
            problems.Add(new Problem(ProblemCodes.E214, Severity.Error,
                $"Alignment '{a.Name}' has no connected geometry.", null,
                "Check that the alignment has entities and that they are solved and connected."));
            return null;
        }

        for (int i = 0; i < segs.Length; i++)
        {
            var g = segs[i];
            string at = Fmt.Station(a, g.StartStation, tol);
            if (!(g.EndStation - g.StartStation > tol) || !double.IsFinite(g.StartStation) || !double.IsFinite(g.EndStation))
                problems.Add(Gap(a, $"the {g.Kind.ToString().ToLowerInvariant()} at {at} has no length"));
            else if (g.Kind == SegKind.Line && Distance(g.Start, g.End) <= 0)
                problems.Add(Gap(a, $"the line at {at} has coincident end points"));
            else if (g.Kind == SegKind.Arc && (g.Center is null || !(g.Radius > 0) || !double.IsFinite(g.Radius)))
                problems.Add(Gap(a, $"the arc at {at} has no center or radius"));

            if (i > 0)
            {
                var p = segs[i - 1];
                if (Math.Abs(g.StartStation - p.EndStation) > gap || Distance(g.Start, p.End) > gap)
                    problems.Add(Gap(a, $"there is a gap at station {at} (previous element ends at {Fmt.Station(a, p.EndStation, tol)}, {Fmt.Num(Distance(g.Start, p.End))} apart)"));
            }
        }
        if (Math.Abs(segs[0].StartStation - a.StartStation) > gap || Math.Abs(segs[^1].EndStation - a.EndStation) > gap)
            problems.Add(Gap(a, $"its entities cover {Fmt.Num(segs[0].StartStation)} to {Fmt.Num(segs[^1].EndStation)} but the alignment runs {Fmt.Num(a.StartStation)} to {Fmt.Num(a.EndStation)}"));

        return problems.Count > before ? null : new AlignmentModel(a, segs, maxAbsOffsetAt, opt);
    }

    private static Problem Gap(IAlignmentGeometry a, string what) =>
        new(ProblemCodes.E214, Severity.Error, $"Alignment '{a.Name}' cannot be used: {what}.", null,
            "Fix the alignment so its entities are solved and connected end to end, then validate again.");

    public int Count => Segs.Length;

    public double JunctionStation(int j) => Segs[j].EndStation;

    /// <summary>Index of the element containing the raw station; the last element owns the end station.</summary>
    public int ElementAt(double s)
    {
        int i = Array.BinarySearch(_starts, s);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, Segs.Length - 1);
    }

    /// <summary>Junction index j (between elements j and j+1) at the station, or -1.</summary>
    public int JunctionAt(double s, double tol)
    {
        int i = ElementAt(s);
        if (i > 0 && Math.Abs(s - Segs[i - 1].EndStation) <= tol) return i - 1;
        if (i < Segs.Length - 1 && Math.Abs(s - Segs[i].EndStation) <= tol) return i;
        return -1;
    }

    /// <summary>Signed offset point at a raw station inside element <paramref name="i"/>.</summary>
    public Pt Eval(int i, double s, double o)
    {
        var g = Segs[i];
        switch (g.Kind)
        {
            case SegKind.Line:
            {
                double t = (s - g.StartStation) / (g.EndStation - g.StartStation);
                double x = g.Start.X + t * (g.End.X - g.Start.X), y = g.Start.Y + t * (g.End.Y - g.Start.Y);
                return new Pt(x + o * _uy[i], y - o * _ux[i]);   // right normal of (ux, uy) is (uy, -ux)
            }
            case SegKind.Arc:
            {
                var c = g.Center!.Value;
                double phi = _phi0[i] + _sgn[i] * (s - g.StartStation) / g.Radius;
                double r = g.Radius * (1 + K1[i] * o);         // R - o on a clockwise arc, R + o on a counter-clockwise arc
                return new Pt(c.X + r * Math.Cos(phi), c.Y + r * Math.Sin(phi));
            }
            default:
                return Source.PointAt(s, o);
        }
    }

    /// <summary>
    /// Offset point at a station node: a mitre point at an angle point, the following
    /// element's start at any other junction, otherwise the containing element.
    /// </summary>
    public Pt Vertex(double s, int junction, double o)
    {
        if (junction >= 0)
            return Kinds[junction] == JunctionKind.AnglePoint ? Mitre(junction, o) : Eval(junction + 1, s, o);
        return Eval(ElementAt(s), s, o);
    }

    /// <summary>Intersection of the two offset lines at angle point j (both elements are lines).</summary>
    public Pt Mitre(int j, double o)
    {
        var p = Segs[j + 1].Start;
        double n1x = _uy[j], n1y = -_ux[j], n2x = _uy[j + 1], n2y = -_ux[j + 1];
        double den = 1 + n1x * n2x + n1y * n2y;
        if (den < 1e-12) return new Pt(p.X + o * n2x, p.Y + o * n2y);   // reversal; such a junction is reported as E205 and never built
        return new Pt(p.X + o * (n1x + n2x) / den, p.Y + o * (n1y + n2y) / den);
    }

    /// <summary>Radius of the arc offset by <paramref name="o"/>, drawing units; zero or negative means the offset passes the center.</summary>
    public double OffsetRadius(int i, double o) => Segs[i].Radius * (1 + K1[i] * o);

    private double StartDirection(int i) => Segs[i].Kind == SegKind.Line ? Math.Atan2(_uy[i], _ux[i]) : Segs[i].StartDir;

    private double EndDirection(int i) => Segs[i].Kind == SegKind.Line ? Math.Atan2(_uy[i], _ux[i]) : Segs[i].EndDir;

    private static double Distance(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>Angle helpers, radians.</summary>
internal static class Angles
{
    /// <summary>Angle wrapped to (-pi, pi].</summary>
    public static double Normalize(double a)
    {
        a %= 2 * Math.PI;
        if (a <= -Math.PI) a += 2 * Math.PI;
        else if (a > Math.PI) a -= 2 * Math.PI;
        return a;
    }
}

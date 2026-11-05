using System.Globalization;
using System.Text.RegularExpressions;

namespace SheetVol.Core.Tests.Fakes;

/// <summary>
/// Station equation for <see cref="FakeAlignment"/>: from <paramref name="RawStation"/> onward
/// the displayed station is raw + (<paramref name="StationAhead"/> - <paramref name="RawStation"/>).
/// StationAhead above the back station leaves a gap of stations that do not exist; below it,
/// some displayed stations occur twice and are ambiguous.
/// </summary>
public sealed record StationEquation(double RawStation, double StationAhead);

/// <summary>
/// In-memory <see cref="IAlignmentGeometry"/> built from an <see cref="AlignSeg"/> list.
/// <para>Lines and arcs are evaluated in closed form. Spirals are clothoids (curvature linear
/// in length): the end curvatures come from the neighbouring line or arc, the missing one
/// solved from the direction change (k1 + k2) / 2 * L = EndDir - StartDir. Points on a spiral
/// are the clothoid integral evaluated by composite 5-point Gauss-Legendre quadrature on
/// panels of at most 2 drawing units; the error is below 1e-9 for the spirals used here. The
/// constructor checks every spiral's integrated end against its <see cref="AlignSeg.End"/>.</para>
/// <para>Offsets are signed, negative left of the direction of travel. Stations display as
/// <c>5+00.00</c> with <paramref name="stationBase"/> (100) per major division. A station
/// outside the alignment is parsed by extrapolation so the planner can report it, and
/// <see cref="PointAt"/> outside the alignment throws.</para>
/// </summary>
public sealed partial class FakeAlignment : IAlignmentGeometry
{
    private readonly AlignSeg[] _segs;
    private readonly double[] _k1, _k2;
    private readonly StationEquation[] _equations;
    private readonly double _base;

    [GeneratedRegex(@"^([+-]?)(\d+)\+(\d+(?:\.\d*)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex PlusForm();

    /// <param name="name">Alignment name.</param>
    /// <param name="segments">Elements in order. Contiguity is not enforced, so a test can build a broken alignment.</param>
    /// <param name="equations">Station equations sorted by raw station, or null.</param>
    /// <param name="stationBase">Displayed units per major station division: 100 for feet, 1000 for metric.</param>
    public FakeAlignment(string name, IReadOnlyList<AlignSeg> segments, IReadOnlyList<StationEquation>? equations = null, double stationBase = 100)
    {
        if (segments.Count == 0) throw new ArgumentException("An alignment needs at least one element.", nameof(segments));
        Name = name;
        _segs = segments.ToArray();
        _equations = (equations ?? []).OrderBy(e => e.RawStation).ToArray();
        _base = stationBase;
        _k1 = new double[_segs.Length];
        _k2 = new double[_segs.Length];
        for (int i = 0; i < _segs.Length; i++)
        {
            var g = _segs[i];
            if (g.Kind == SegKind.Arc) _k1[i] = _k2[i] = (g.Clockwise ? -1 : 1) / g.Radius;
        }
        for (int i = 0; i < _segs.Length; i++)
        {
            var g = _segs[i];
            if (g.Kind != SegKind.Spiral) continue;
            double length = g.EndStation - g.StartStation;
            double delta = Wrap(g.EndDir - g.StartDir);
            double? k1 = i > 0 ? _k2[i - 1] : null;
            double? k2 = i + 1 < _segs.Length && _segs[i + 1].Kind != SegKind.Spiral ? _k1[i + 1] : null;
            if (k1 is null && k2 is null) k1 = 0;
            _k1[i] = k1 ?? 2 * delta / length - k2!.Value;
            _k2[i] = k2 ?? 2 * delta / length - k1!.Value;
            var end = Clothoid.Point(g.Start, g.StartDir, _k1[i], _k2[i], length, length);
            if (Dist(end, g.End) > 1e-6)
                throw new ArgumentException($"Spiral {i} does not close: the clothoid ends {Dist(end, g.End):E3} from AlignSeg.End.", nameof(segments));
        }
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public double StartStation => _segs[0].StartStation;

    /// <inheritdoc />
    public double EndStation => _segs[^1].EndStation;

    /// <inheritdoc />
    public IReadOnlyList<AlignSeg> Segments => _segs;

    /// <inheritdoc />
    public Pt PointAt(double rawStation, double offset)
    {
        var (p, dir) = Centerline(rawStation);
        return new Pt(p.X + offset * Math.Sin(dir), p.Y - offset * Math.Cos(dir));
    }

    /// <summary>Direction of travel at a raw station, radians counter-clockwise from +X. Test helper, not part of the contract.</summary>
    public double DirectionAt(double rawStation) => Centerline(rawStation).Dir;

    /// <inheritdoc />
    public string FormatStation(double rawStation)
    {
        double d = Displayed(rawStation);
        long cents = (long)Math.Round(Math.Abs(d) * 100, MidpointRounding.AwayFromZero);
        long baseCents = (long)Math.Round(_base * 100);
        long major = cents / baseCents, minor = cents % baseCents;
        int digits = ((long)_base - 1).ToString(CultureInfo.InvariantCulture).Length;
        string sign = d < 0 && cents != 0 ? "-" : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{sign}{major}+{(minor / 100).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')}.{minor % 100:00}");
    }

    /// <inheritdoc />
    public bool TryParseStation(string text, out double rawStation, out string? error)
    {
        rawStation = 0;
        error = null;
        string t = text.Trim();
        double displayed;
        var m = PlusForm().Match(t);
        if (m.Success)
        {
            double major = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            double minor = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (minor >= _base)
            {
                error = $"'{text}': the part after + must be less than {_base}.";
                return false;
            }
            displayed = (m.Groups[1].Value == "-" ? -1 : 1) * (major * _base + minor);
        }
        else if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out displayed))
        {
            error = $"'{text}' is not a station.";
            return false;
        }

        const double tol = 1e-9;
        var candidates = new List<double>();
        for (int i = 0; i <= _equations.Length; i++)
        {
            var (lo, hi, shift) = Region(i);
            double raw = displayed - shift;
            if (raw >= lo - tol && raw <= hi + tol && !candidates.Any(c => Math.Abs(c - raw) <= tol)) candidates.Add(raw);
        }
        if (candidates.Count == 1)
        {
            rawStation = candidates[0];
            return true;
        }
        if (candidates.Count > 1)
        {
            error = $"Station {text} occurs {candidates.Count} times because of a station equation: raw "
                + string.Join(" or raw ", candidates.Select(c => c.ToString("0.00", CultureInfo.InvariantCulture))) + ".";
            return false;
        }
        var (firstLo, _, firstShift) = Region(0);
        var (_, lastHi, lastShift) = Region(_equations.Length);
        if (displayed < firstLo + firstShift) { rawStation = displayed - firstShift; return true; }
        if (displayed > lastHi + lastShift) { rawStation = displayed - lastShift; return true; }
        error = $"Station {text} does not exist: it falls in the gap of a station equation.";
        return false;
    }

    private (double Lo, double Hi, double Shift) Region(int i)
    {
        double lo = i == 0 ? StartStation : _equations[i - 1].RawStation;
        double hi = i == _equations.Length ? EndStation : _equations[i].RawStation;
        double shift = i == 0 ? 0 : _equations[i - 1].StationAhead - _equations[i - 1].RawStation;
        return (lo, hi, shift);
    }

    private double Displayed(double raw)
    {
        double shift = 0;
        foreach (var e in _equations)
            if (raw >= e.RawStation) shift = e.StationAhead - e.RawStation;
        return raw + shift;
    }

    private (Pt P, double Dir) Centerline(double s)
    {
        const double tol = 1e-9;
        if (s < StartStation - tol || s > EndStation + tol)
            throw new ArgumentOutOfRangeException(nameof(s), s, $"Station is outside alignment '{Name}' ({StartStation} to {EndStation}).");
        int i = _segs.Length - 1;
        while (i > 0 && _segs[i].StartStation > s) i--;
        var g = _segs[i];
        double t = s - g.StartStation;
        switch (g.Kind)
        {
            case SegKind.Line:
            {
                double len = g.EndStation - g.StartStation;
                double f = t / len;
                return (new Pt(g.Start.X + f * (g.End.X - g.Start.X), g.Start.Y + f * (g.End.Y - g.Start.Y)),
                        Math.Atan2(g.End.Y - g.Start.Y, g.End.X - g.Start.X));
            }
            case SegKind.Arc:
            {
                var c = g.Center!.Value;
                double sgn = g.Clockwise ? -1 : 1;
                double phi = Math.Atan2(g.Start.Y - c.Y, g.Start.X - c.X) + sgn * t / g.Radius;
                return (new Pt(c.X + g.Radius * Math.Cos(phi), c.Y + g.Radius * Math.Sin(phi)), phi + sgn * Math.PI / 2);
            }
            default:
            {
                double length = g.EndStation - g.StartStation;
                return (Clothoid.Point(g.Start, g.StartDir, _k1[i], _k2[i], length, t), Clothoid.Direction(g.StartDir, _k1[i], _k2[i], length, t));
            }
        }
    }

    internal static double Wrap(double a)
    {
        a %= 2 * Math.PI;
        if (a <= -Math.PI) a += 2 * Math.PI;
        else if (a > Math.PI) a -= 2 * Math.PI;
        return a;
    }

    private static double Dist(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>
/// Clothoid with curvature varying linearly from k1 to k2 over its length (counter-clockwise
/// positive). Points by composite 5-point Gauss-Legendre quadrature of (cos, sin) of the
/// direction, panels of at most 2 units.
/// </summary>
public static class Clothoid
{
    private static readonly double[] X = [0, -0.5384693101056831, 0.5384693101056831, -0.9061798459386640, 0.9061798459386640];
    private static readonly double[] W = [0.5688888888888889, 0.4786286704993665, 0.4786286704993665, 0.2369268850561891, 0.2369268850561891];

    /// <summary>Direction, radians, at distance <paramref name="t"/> from the start.</summary>
    public static double Direction(double dir0, double k1, double k2, double length, double t) =>
        dir0 + k1 * t + (k2 - k1) * t * t / (2 * length);

    /// <summary>Point at distance <paramref name="t"/> from <paramref name="start"/>.</summary>
    public static Pt Point(Pt start, double dir0, double k1, double k2, double length, double t)
    {
        if (t == 0) return start;
        int panels = Math.Max(1, (int)Math.Ceiling(t / 2));
        double h = t / panels, x = 0, y = 0;
        for (int p = 0; p < panels; p++)
        {
            double mid = (p + 0.5) * h;
            for (int q = 0; q < X.Length; q++)
            {
                double u = mid + 0.5 * h * X[q];
                double th = Direction(dir0, k1, k2, length, u);
                x += W[q] * Math.Cos(th);
                y += W[q] * Math.Sin(th);
            }
        }
        return new Pt(start.X + 0.5 * h * x, start.Y + 0.5 * h * y);
    }
}

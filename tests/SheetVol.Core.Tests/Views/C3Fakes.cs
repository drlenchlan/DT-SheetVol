// Minimal test fakes for the C3 suites (views and tracking). C1 builds the shared fakes in
// parallel; the orchestrator consolidates at merge, hence the C3 prefix.

using System.Globalization;
using SheetVol.Core.Views;

namespace SheetVol.Core.Tests.Views;

/// <summary>One piece of a <see cref="C3Alignment"/>: a line (Radius 0) or a circular arc.</summary>
internal readonly record struct C3Piece(double Length, double Radius = 0, bool Clockwise = false);

/// <summary>
/// Line-and-arc alignment with exact analytic geometry. Offsets follow the contract:
/// negative = left of travel, positive = right.
/// </summary>
internal sealed class C3Alignment : IAlignmentGeometry
{
    private readonly List<AlignSeg> _segments = [];

    private C3Alignment(string name, double startStation) { Name = name; StartStation = startStation; EndStation = startStation; }

    public string Name { get; }
    public double StartStation { get; }
    public double EndStation { get; private set; }
    public IReadOnlyList<AlignSeg> Segments => _segments;

    /// <summary>Straight alignment from <paramref name="start"/> along <paramref name="bearing"/> (radians CCW from +X).</summary>
    public static C3Alignment Straight(Pt start, double bearing, double length, double startStation = 0, string name = "S")
        => Build(name, start, bearing, startStation, new C3Piece(length));

    public static C3Alignment Build(string name, Pt start, double bearing, double startStation, params C3Piece[] pieces)
    {
        var a = new C3Alignment(name, startStation);
        Pt p = start;
        double dir = bearing, s = startStation;
        foreach (C3Piece piece in pieces)
        {
            if (piece.Radius == 0)
            {
                var end = new Pt(p.X + piece.Length * Math.Cos(dir), p.Y + piece.Length * Math.Sin(dir));
                a._segments.Add(new AlignSeg(SegKind.Line, s, s + piece.Length, p, end, dir, dir));
                p = end;
            }
            else
            {
                double sign = piece.Clockwise ? -1 : 1;
                // Center lies to the left of travel for a counter-clockwise arc, to the right for clockwise.
                var center = new Pt(p.X - sign * piece.Radius * Math.Sin(dir), p.Y + sign * piece.Radius * Math.Cos(dir));
                double phi0 = Math.Atan2(p.Y - center.Y, p.X - center.X);
                double phi1 = phi0 + sign * piece.Length / piece.Radius;
                var end = new Pt(center.X + piece.Radius * Math.Cos(phi1), center.Y + piece.Radius * Math.Sin(phi1));
                double endDir = dir + sign * piece.Length / piece.Radius;
                a._segments.Add(new AlignSeg(SegKind.Arc, s, s + piece.Length, p, end, dir, endDir, center, piece.Radius, piece.Clockwise));
                p = end;
                dir = endDir;
            }
            s += piece.Length;
        }
        a.EndStation = s;
        return a;
    }

    public Pt PointAt(double rawStation, double offset)
    {
        const double slack = 1e-9;
        if (rawStation < StartStation - slack || rawStation > EndStation + slack)
            throw new ArgumentOutOfRangeException(nameof(rawStation), rawStation, $"Outside {StartStation}..{EndStation}.");
        AlignSeg seg = _segments.First(g => rawStation <= g.EndStation + slack);
        double t = rawStation - seg.StartStation;
        if (seg.Kind == SegKind.Line)
        {
            double c = Math.Cos(seg.StartDir), s = Math.Sin(seg.StartDir);
            // Right of travel is (sin, -cos).
            return new Pt(seg.Start.X + t * c + offset * s, seg.Start.Y + t * s - offset * c);
        }
        Pt center = seg.Center!.Value;
        double sign = seg.Clockwise ? -1 : 1;
        double phi0 = Math.Atan2(seg.Start.Y - center.Y, seg.Start.X - center.X);
        double phi = phi0 + sign * t / seg.Radius;
        double r = seg.Radius + sign * offset;   // right is away from the center on a counter-clockwise arc
        return new Pt(center.X + r * Math.Cos(phi), center.Y + r * Math.Sin(phi));
    }

    public string FormatStation(double rawStation)
    {
        double hundreds = Math.Floor(rawStation / 100);
        double rest = rawStation - hundreds * 100;
        return string.Create(CultureInfo.InvariantCulture, $"{hundreds:0}+{rest:00.00}");
    }

    public bool TryParseStation(string text, out double rawStation, out string? error)
    {
        bool ok = double.TryParse(text.Replace("+", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out rawStation);
        error = ok ? null : $"Not a station: {text}";
        return ok;
    }
}

/// <summary>
/// Offset-band polygon builder for a <see cref="C3Alignment"/>: left offset line forward,
/// right offset line back, arcs as bulges, implicit closure (no repeated first vertex).
/// </summary>
internal sealed class C3BandSource(C3Alignment alignment, double offsetLeft, double offsetRight) : IDisplayPolygonSource
{
    public int Calls { get; private set; }

    public IReadOnlyList<PolyVertex> Build(double fromStation, double toStation)
    {
        Calls++;
        const double slack = 1e-9;
        var stations = new List<double> { fromStation };
        foreach (AlignSeg seg in alignment.Segments)
            if (seg.EndStation > fromStation + slack && seg.EndStation < toStation - slack) stations.Add(seg.EndStation);
        stations.Add(toStation);

        var bulges = new double[stations.Count - 1];
        for (int k = 0; k < bulges.Length; k++)
        {
            double mid = (stations[k] + stations[k + 1]) / 2;
            AlignSeg seg = alignment.Segments.First(g => mid <= g.EndStation);
            bulges[k] = seg.Kind == SegKind.Arc
                ? Math.Tan((seg.Clockwise ? -1 : 1) * (stations[k + 1] - stations[k]) / seg.Radius / 4)
                : 0;
        }

        var vertices = new List<PolyVertex>();
        for (int k = 0; k < stations.Count; k++)
            vertices.Add(new PolyVertex(alignment.PointAt(stations[k], offsetLeft), k < bulges.Length ? bulges[k] : 0));
        for (int k = stations.Count - 1; k >= 0; k--)
            vertices.Add(new PolyVertex(alignment.PointAt(stations[k], offsetRight), k > 0 ? -bulges[k - 1] : 0));
        return vertices;
    }
}

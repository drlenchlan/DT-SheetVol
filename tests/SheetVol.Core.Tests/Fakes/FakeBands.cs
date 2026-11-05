using SheetVol.Core.Views;

namespace SheetVol.Core.Tests.Fakes;

/// <summary>
/// Hand-built band polygons for tests that need a boundary without running the planner
/// (moved here from the C5 integrator suite, where they were <c>C5Boundaries</c>). They are not
/// <see cref="SheetVol.Core.Geometry.BoundaryBuilder"/>'s rings and do not stand in for them:
/// the left offset line runs forward and the right one back (clockwise where
/// BoundaryBuilder is counter-clockwise), curves get a vertex every <c>curveStep</c> along the
/// alignment instead of the mid-ordinate rule, and an angle point gets no mitre.
/// </summary>
public static class FakeBands
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

    /// <summary>The polygon moved by (dx, dy).</summary>
    public static IReadOnlyList<Pt> Shift(IReadOnlyList<Pt> pts, double dx, double dy) => [.. pts.Select(p => new Pt(p.X + dx, p.Y + dy))];
}

/// <summary>
/// Display polygon source for the view tests (moved here from the C3 suite, where it was
/// <c>C3BandSource</c>); the pipeline adapts BoundaryBuilder to <see cref="IDisplayPolygonSource"/>
/// instead. Differs from <see cref="SheetVol.Core.Boundary.Drawn"/>: the left offset line runs
/// forward and the right one back (clockwise), with vertices at the two stations and every
/// element end between them only, arcs as bulges, and no repeated first vertex.
/// </summary>
/// <param name="alignment">Any alignment; offsets are evaluated with its <see cref="IAlignmentGeometry.PointAt"/>.</param>
/// <param name="offsetLeft">Signed left offset.</param>
/// <param name="offsetRight">Signed right offset.</param>
public sealed class FakeBandSource(IAlignmentGeometry alignment, double offsetLeft, double offsetRight) : IDisplayPolygonSource
{
    /// <summary>Number of <see cref="Build"/> calls.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
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

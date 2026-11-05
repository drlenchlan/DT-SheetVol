using SheetVol.Core.Tests.Fakes;

namespace SheetVol.Core.Tests.Geometry;

/// <summary>
/// Exact band areas for test assertions. Between two offset curves of a tangent-continuous
/// alignment the area is w * L + (dTheta / 2) * (oR^2 - oL^2), with dTheta the signed
/// (counter-clockwise positive) direction change over the stretch; a mitred angle point of
/// deflection d adds tan(d / 2) * (oR^2 - oL^2), half of it when a bisector cut sits on it.
/// </summary>
internal static class Analytic
{
    public static double BandArea(FakeAlignment a, double from, double to, double oL, double oR)
    {
        const double tol = 1e-9;
        double turn = 0, mitre = 0;
        var segs = a.Segments;
        for (int i = 0; i < segs.Count; i++)
        {
            var g = segs[i];
            if (g.EndStation <= from + tol || g.StartStation >= to - tol) continue;
            double t0 = from > g.StartStation + tol ? a.DirectionAt(from) : g.StartDir;
            double t1 = to < g.EndStation - tol ? a.DirectionAt(to) : g.EndDir;
            turn += FakeAlignment.Wrap(t1 - t0);
        }
        for (int j = 0; j + 1 < segs.Count; j++)
        {
            double sj = segs[j].EndStation;
            if (sj < from - tol || sj > to + tol) continue;
            if (segs[j].Kind != SegKind.Line || segs[j + 1].Kind != SegKind.Line) continue;
            double d = FakeAlignment.Wrap(segs[j + 1].StartDir - segs[j].EndDir);
            double weight = sj > from + tol && sj < to - tol ? 1 : 0.5;
            mitre += weight * Math.Tan(d / 2);
        }
        return (oR - oL) * (to - from) + (turn / 2 + mitre) * (oR * oR - oL * oL);
    }

    /// <summary>Perimeter of a ring; used to bound tessellation error (mid-ordinate times perimeter).</summary>
    public static double Perimeter(IReadOnlyList<Pt> ring)
    {
        double sum = 0;
        for (int i = 0; i + 1 < ring.Count; i++)
            sum += Math.Sqrt(Math.Pow(ring[i + 1].X - ring[i].X, 2) + Math.Pow(ring[i + 1].Y - ring[i].Y, 2));
        return sum;
    }

    public static double Dist(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

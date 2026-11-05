namespace SheetVol.Core.Views;

/// <summary>Axis-aligned extents in one coordinate frame. Units are those of the points measured.</summary>
public readonly record struct Extents(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>MaxX − MinX.</summary>
    public double Width => MaxX - MinX;

    /// <summary>MaxY − MinY.</summary>
    public double Height => MaxY - MinY;

    /// <summary>Midpoint of the extents.</summary>
    public Pt Center => new((MinX + MaxX) / 2, (MinY + MaxY) / 2);
}

/// <summary>
/// Exact geometry of closed polylines whose vertices carry AutoCAD bulges
/// (<see cref="PolyVertex.Bulge"/> = tan(included angle / 4) of the arc to the next vertex,
/// positive counter-clockwise). The polygon closes from the last vertex back to the first;
/// an explicit closing vertex equal to the first is accepted and contributes no segment.
/// </summary>
public static class BulgePolyline
{
    /// <summary>Center of the arc from <paramref name="a"/> to <paramref name="b"/> with the given bulge (non-zero).</summary>
    public static Pt ArcCenter(Pt a, Pt b, double bulge)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        double c = Math.Sqrt(vx * vx + vy * vy);
        // Distance from the chord midpoint to the center along the chord's left normal:
        // (c / 2) / tan(theta / 2) with theta = 4 atan(bulge), which simplifies to c (1 − b²) / (4 b).
        double h = (1 - bulge * bulge) / (4 * bulge);
        return new Pt((a.X + b.X) / 2 - vy * h, (a.Y + b.Y) / 2 + vx * h);
    }

    /// <summary>Radius of the arc from <paramref name="a"/> to <paramref name="b"/> with the given bulge (non-zero), same units as the points.</summary>
    public static double ArcRadius(Pt a, Pt b, double bulge)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        double c = Math.Sqrt(vx * vx + vy * vy);
        return c * (1 + bulge * bulge) / (4 * Math.Abs(bulge));
    }

    /// <summary>
    /// Exact axis-aligned extents of the closed polyline, arcs included: an arc adds every
    /// axis-extreme point (angles 0, π/2, π, 3π/2 about its center) that lies inside its sweep.
    /// </summary>
    public static Extents Bounds(IReadOnlyList<PolyVertex> polygon, ViewMathConfig config)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        ArgumentNullException.ThrowIfNull(config);
        if (polygon.Count == 0) throw new ArgumentException("Polygon has no vertices.", nameof(polygon));

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

        void Include(Pt p)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }

        int n = polygon.Count;
        for (int i = 0; i < n; i++)
        {
            Pt a = polygon[i].Point;
            Pt b = polygon[(i + 1) % n].Point;
            double bulge = polygon[i].Bulge;
            Include(a);
            if (Distance(a, b) <= config.DuplicateVertexTolerance || Math.Abs(bulge) <= config.StraightBulgeTolerance)
                continue;

            Pt center = ArcCenter(a, b, bulge);
            double radius = ArcRadius(a, b, bulge);
            double start = Math.Atan2(a.Y - center.Y, a.X - center.X);
            double sweep = 4 * Math.Atan(bulge);          // signed, positive counter-clockwise
            for (int k = 0; k < 4; k++)
            {
                double phi = k * Math.PI / 2;
                double travelled = sweep > 0 ? Mod2Pi(phi - start) : Mod2Pi(start - phi);
                if (travelled <= Math.Abs(sweep))
                    Include(new Pt(center.X + radius * Math.Cos(phi), center.Y + radius * Math.Sin(phi)));
            }
        }

        return new Extents(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Applies an orientation-preserving similarity (rotation, translation, positive uniform
    /// scale) to every vertex. Bulges are invariant under such a map, so arcs stay arcs.
    /// A mirror or non-uniform scale would invalidate the bulges; do not pass one.
    /// </summary>
    public static IReadOnlyList<PolyVertex> Map(IReadOnlyList<PolyVertex> polygon, Func<Pt, Pt> similarity)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        ArgumentNullException.ThrowIfNull(similarity);
        var result = new PolyVertex[polygon.Count];
        for (int i = 0; i < polygon.Count; i++)
            result[i] = new PolyVertex(similarity(polygon[i].Point), polygon[i].Bulge);
        return result;
    }

    /// <summary>Euclidean distance, same units as the points.</summary>
    public static double Distance(Pt a, Pt b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Mod2Pi(double radians)
    {
        double r = radians % Math.Tau;
        return r < 0 ? r + Math.Tau : r;
    }
}

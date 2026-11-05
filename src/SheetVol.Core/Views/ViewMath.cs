namespace SheetVol.Core.Views;

/// <summary>
/// The oriented bounding box of a display polygon in the view's rotated frame.
/// </summary>
/// <param name="ModelCenter">Center of the box in WCS model coordinates, drawing units. This is the view target.</param>
/// <param name="Width">Extent along paper X (along the sheet), drawing units.</param>
/// <param name="Height">Extent along paper Y, drawing units.</param>
/// <param name="TwistRadians">The rotation the box was measured in, radians counter-clockwise.</param>
public readonly record struct OrientedBox(Pt ModelCenter, double Width, double Height, double TwistRadians);

/// <summary>
/// Pure view math for HANDOFF.md 6.6. Conventions: model points are WCS drawing units;
/// angles are radians counter-clockwise from +X; the twist is the counter-clockwise rotation
/// that takes a model direction to its paper direction, so
/// <c>paper = Rotate(model − ModelCenter, twist) / ModelUnitsPerPaperUnit</c>, with paper
/// coordinates relative to the viewport center. Whether AutoCAD's viewport TwistAngle uses
/// the same sign is probe P12's question; the paper writer absorbs any difference.
/// </summary>
public static class ViewMath
{
    /// <summary>Normalizes an angle to (−π, π], radians. Negative zero becomes zero.</summary>
    public static double NormalizeAngle(double radians)
    {
        double r = radians % Math.Tau;
        if (r <= -Math.PI) r += Math.Tau;
        else if (r > Math.PI) r -= Math.Tau;
        return r == 0 ? 0 : r;
    }

    /// <summary>Rotates a point about the origin by <paramref name="radians"/> counter-clockwise.</summary>
    public static Pt Rotate(Pt p, double radians)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new Pt(p.X * c - p.Y * s, p.X * s + p.Y * c);
    }

    /// <summary>
    /// Direction of travel of the alignment at a raw station, radians counter-clockwise from +X,
    /// measured on the alignment itself through <see cref="IAlignmentGeometry.PointAt"/> by a
    /// central difference of half-step <see cref="ViewMathConfig.TangentStep"/>, clamped to the alignment.
    /// </summary>
    public static double TangentDirection(IAlignmentGeometry alignment, double rawStation, ViewMathConfig config)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        ArgumentNullException.ThrowIfNull(config);
        double s0 = Math.Max(alignment.StartStation, rawStation - config.TangentStep);
        double s1 = Math.Min(alignment.EndStation, rawStation + config.TangentStep);
        if (!(s1 > s0))
            throw new ArgumentOutOfRangeException(nameof(rawStation), rawStation, "No alignment length around this station to measure a tangent on.");
        Pt p0 = alignment.PointAt(s0, 0);
        Pt p1 = alignment.PointAt(s1, 0);
        return Math.Atan2(p1.Y - p0.Y, p1.X - p0.X);
    }

    /// <summary>
    /// The view twist for a segment (6.6, A3), radians in (−π, π].
    /// <see cref="ViewOrientation.North"/>: 0. <see cref="ViewOrientation.AlongAlignment"/>:
    /// −bearing of the chord from the alignment point at <paramref name="startStation"/> to the
    /// one at <paramref name="endStation"/>, so stationing reads left to right on paper. When
    /// the chord is no longer than <see cref="ViewMathConfig.CoincidentChordTolerance"/> the
    /// tangent at mid-station replaces the chord.
    /// </summary>
    public static double Twist(IAlignmentGeometry alignment, double startStation, double endStation,
                               ViewOrientation orientation, ViewMathConfig config)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        ArgumentNullException.ThrowIfNull(config);
        if (orientation == ViewOrientation.North) return 0;

        Pt a = alignment.PointAt(startStation, 0);
        Pt b = alignment.PointAt(endStation, 0);
        double bearing = BulgePolyline.Distance(a, b) <= config.CoincidentChordTolerance
            ? TangentDirection(alignment, (startStation + endStation) / 2, config)
            : Math.Atan2(b.Y - a.Y, b.X - a.X);
        return NormalizeAngle(-bearing);
    }

    /// <summary>
    /// Oriented bounding box of a closed bulge polygon in the frame rotated by
    /// <paramref name="twistRadians"/>, arcs measured exactly. With twist 0 this is the
    /// axis-aligned box (North mode, E610). Rotation happens about the polygon's first vertex
    /// so state-plane coordinates keep their precision.
    /// </summary>
    public static OrientedBox Box(IReadOnlyList<PolyVertex> polygon, double twistRadians, ViewMathConfig config)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        if (polygon.Count == 0) throw new ArgumentException("Polygon has no vertices.", nameof(polygon));
        Pt origin = polygon[0].Point;
        var rotated = BulgePolyline.Map(polygon, p => Rotate(new Pt(p.X - origin.X, p.Y - origin.Y), twistRadians));
        Extents e = BulgePolyline.Bounds(rotated, config);
        Pt local = Rotate(e.Center, -twistRadians);
        return new OrientedBox(new Pt(local.X + origin.X, local.Y + origin.Y), e.Width, e.Height, twistRadians);
    }

    /// <summary>
    /// Maps a model point to paper coordinates relative to the viewport center:
    /// <c>Rotate(model − modelCenter, twist) / modelUnitsPerPaperUnit</c>, paper units.
    /// </summary>
    public static Pt ToPaper(Pt model, Pt modelCenter, double twistRadians, double modelUnitsPerPaperUnit)
    {
        Pt r = Rotate(new Pt(model.X - modelCenter.X, model.Y - modelCenter.Y), twistRadians);
        return new Pt(r.X / modelUnitsPerPaperUnit, r.Y / modelUnitsPerPaperUnit);
    }

    /// <summary>
    /// Maps a closed bulge polygon to paper coordinates relative to the viewport center.
    /// Rotation and positive uniform scale preserve bulges, so arcs stay arcs (6.6 clip).
    /// </summary>
    public static IReadOnlyList<PolyVertex> ToPaper(IReadOnlyList<PolyVertex> polygon, Pt modelCenter,
                                                    double twistRadians, double modelUnitsPerPaperUnit)
        => BulgePolyline.Map(polygon, p => ToPaper(p, modelCenter, twistRadians, modelUnitsPerPaperUnit));

    /// <summary>True when the box, at the given scale, fits a viewport of the given paper size within the tolerance.</summary>
    public static bool Fits(OrientedBox box, double modelUnitsPerPaperUnit, double paperWidth, double paperHeight, ViewMathConfig config)
        => box.Width / modelUnitsPerPaperUnit <= paperWidth + config.FitTolerance
        && box.Height / modelUnitsPerPaperUnit <= paperHeight + config.FitTolerance;
}

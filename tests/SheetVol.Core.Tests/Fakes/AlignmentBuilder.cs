namespace SheetVol.Core.Tests.Fakes;

/// <summary>
/// Builds a tangent-continuous <see cref="FakeAlignment"/> element by element, in WCS
/// drawing units, directions in radians counter-clockwise from +X. <see cref="Angle"/> turns
/// the direction without an element, making a tangent-to-tangent angle point.
/// </summary>
public sealed class AlignmentBuilder
{
    private readonly List<AlignSeg> _segs = [];
    private Pt _p;
    private double _dir;
    private double _sta;

    /// <param name="start">Point at the start station.</param>
    /// <param name="direction">Initial direction of travel, radians.</param>
    /// <param name="startStation">Raw station at <paramref name="start"/>.</param>
    public AlignmentBuilder(Pt start, double direction, double startStation = 0)
    {
        _p = start;
        _dir = direction;
        _sta = startStation;
    }

    /// <summary>Appends a tangent of the given length.</summary>
    public AlignmentBuilder Line(double length)
    {
        var end = new Pt(_p.X + length * Math.Cos(_dir), _p.Y + length * Math.Sin(_dir));
        _segs.Add(new AlignSeg(SegKind.Line, _sta, _sta + length, _p, end, _dir, _dir));
        Advance(end, _dir, length);
        return this;
    }

    /// <summary>Appends a circular arc turning <paramref name="delta"/> radians (positive) in the given sense.</summary>
    public AlignmentBuilder Arc(double radius, double delta, bool clockwise)
    {
        double sgn = clockwise ? -1 : 1;
        var center = new Pt(_p.X - sgn * radius * Math.Sin(_dir), _p.Y + sgn * radius * Math.Cos(_dir));
        double phi0 = Math.Atan2(_p.Y - center.Y, _p.X - center.X), phi1 = phi0 + sgn * delta;
        var end = new Pt(center.X + radius * Math.Cos(phi1), center.Y + radius * Math.Sin(phi1));
        double endDir = _dir + sgn * delta, length = radius * delta;
        _segs.Add(new AlignSeg(SegKind.Arc, _sta, _sta + length, _p, end, _dir, endDir, center, radius, clockwise));
        Advance(end, endDir, length);
        return this;
    }

    /// <summary>
    /// Appends a clothoid from radius <paramref name="radiusIn"/> to <paramref name="radiusOut"/>;
    /// use <see cref="double.PositiveInfinity"/> for a tangent end.
    /// </summary>
    public AlignmentBuilder Spiral(double length, double radiusIn, double radiusOut, bool clockwise)
    {
        double sgn = clockwise ? -1 : 1;
        double k1 = sgn / radiusIn, k2 = sgn / radiusOut;
        var end = Clothoid.Point(_p, _dir, k1, k2, length, length);
        double endDir = Clothoid.Direction(_dir, k1, k2, length, length);
        _segs.Add(new AlignSeg(SegKind.Spiral, _sta, _sta + length, _p, end, _dir, endDir));
        Advance(end, endDir, length);
        return this;
    }

    /// <summary>Turns the direction by <paramref name="deflection"/> radians (counter-clockwise positive) with no curve.</summary>
    public AlignmentBuilder Angle(double deflection)
    {
        _dir += deflection;
        return this;
    }

    /// <summary>Creates the alignment.</summary>
    public FakeAlignment Build(string name, IReadOnlyList<StationEquation>? equations = null, double stationBase = 100) =>
        new(name, _segs, equations, stationBase);

    private void Advance(Pt end, double dir, double length)
    {
        _p = end;
        _dir = dir;
        _sta += length;
    }
}

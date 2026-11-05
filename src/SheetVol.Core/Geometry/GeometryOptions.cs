namespace SheetVol.Core.Geometry;

/// <summary>
/// Tolerances for boundary generation (HANDOFF.md 6.4). Lengths are drawing units, areas
/// square drawing units, angles degrees. Build with <see cref="ForUnitScale"/> so every
/// length default scales with the drawing's linear unit (E216); nothing here assumes feet.
/// </summary>
public sealed record GeometryOptions
{
    /// <summary>Default mid-ordinate in feet (HANDOFF.md 6.4 step 1).</summary>
    public const double DefaultMidOrdinateFeet = 0.01;

    /// <summary>Default station merge tolerance in feet (E206).</summary>
    public const double DefaultStationToleranceFeet = 1e-6;

    /// <summary>Default mitre limit in degrees (HANDOFF.md 6.4 step 5).</summary>
    public const double DefaultMitreLimitDeg = 150;

    /// <summary>
    /// Largest distance, drawing units, between a tessellation chord and the true offset curve
    /// at its middle. Arcs and spirals are subdivided to meet it on the larger offset radius.
    /// </summary>
    public required double MidOrdinate { get; init; }

    /// <summary>
    /// Largest tangent-to-tangent deflection, degrees, that is joined by a mitre. A larger
    /// deflection is an error (E205).
    /// </summary>
    public required double MitreLimitDeg { get; init; }

    /// <summary>Stations closer than this, drawing units, are one station (E206).</summary>
    public required double StationTolerance { get; init; }

    /// <summary>
    /// Intersection area, square drawing units, at or below which two boundaries are taken to
    /// touch rather than overlap (E215, E312).
    /// </summary>
    public required double OverlapAreaTolerance { get; init; }

    /// <summary>
    /// Defaults for a drawing whose linear unit is <paramref name="drawingUnitsPerFoot"/>
    /// drawing units per foot: 1 for feet, 0.3048 for metres, 304.8 for millimetres.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Scale is not a positive finite number.</exception>
    public static GeometryOptions ForUnitScale(double drawingUnitsPerFoot)
    {
        if (!double.IsFinite(drawingUnitsPerFoot) || drawingUnitsPerFoot <= 0)
            throw new ArgumentOutOfRangeException(nameof(drawingUnitsPerFoot), drawingUnitsPerFoot, "Unit scale must be a positive finite number of drawing units per foot.");

        double mid = DefaultMidOrdinateFeet * drawingUnitsPerFoot;
        return new GeometryOptions
        {
            MidOrdinate = mid,
            MitreLimitDeg = DefaultMitreLimitDeg,
            StationTolerance = DefaultStationToleranceFeet * drawingUnitsPerFoot,
            OverlapAreaTolerance = mid * mid,
        };
    }
}

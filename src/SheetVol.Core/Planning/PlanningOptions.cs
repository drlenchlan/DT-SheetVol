using SheetVol.Core.Geometry;

namespace SheetVol.Core.Planning;

/// <summary>
/// Settings for segment planning (HANDOFF.md 6.3) plus the boundary tolerances (6.4).
/// Lengths are drawing units. Build with <see cref="ForUnitScale"/>; the session supplies
/// the scale from the drawing's units, so nothing is guessed (E216).
/// </summary>
public sealed record PlanningOptions
{
    /// <summary>Boundary generation tolerances.</summary>
    public required GeometryOptions Geometry { get; init; }

    /// <summary>
    /// A remainder left by <c>A..B@L</c> shorter than this, drawing units, is merged into the
    /// previous segment; otherwise it is kept as its own segment. Reported either way (E302).
    /// Default 0: every remainder is kept.
    /// </summary>
    public required double MinSegmentLength { get; init; }

    /// <summary>
    /// Composite format for derived segment labels; <c>{0}</c> is the 1-based position among
    /// the run's station segments. Labels are derived, never identity (6.3). Default <c>{0}</c>.
    /// </summary>
    public required string SegmentLabelFormat { get; init; }

    /// <summary>Most breaks one specification may produce before it is rejected. Default 10,000.</summary>
    public required int MaxBreaks { get; init; }

    /// <summary>Default <see cref="SegmentLabelFormat"/>.</summary>
    public const string DefaultSegmentLabelFormat = "{0}";

    /// <summary>Default <see cref="MaxBreaks"/>.</summary>
    public const int DefaultMaxBreaks = 10_000;

    /// <summary>
    /// Defaults for a drawing with <paramref name="drawingUnitsPerFoot"/> drawing units per
    /// foot (1 for feet, 0.3048 for metres).
    /// </summary>
    public static PlanningOptions ForUnitScale(double drawingUnitsPerFoot) => new()
    {
        Geometry = GeometryOptions.ForUnitScale(drawingUnitsPerFoot),
        MinSegmentLength = 0,
        SegmentLabelFormat = DefaultSegmentLabelFormat,
        MaxBreaks = DefaultMaxBreaks,
    };
}

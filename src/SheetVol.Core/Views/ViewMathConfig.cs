namespace SheetVol.Core.Views;

/// <summary>
/// Tunable numbers for the view math (HANDOFF.md 6.6). Every value is a placeholder
/// default, not a LAN standard; the pipeline loads the real values from the run config.
/// Units are stated per property: drawing units are WCS model units, paper units are the
/// layout's units, angles are radians counter-clockwise from +X.
/// </summary>
public sealed record ViewMathConfig
{
    /// <summary>Slack allowed when a required paper size is compared with the template viewport, paper units.</summary>
    public double FitTolerance { get; init; } = 1e-9;

    /// <summary>
    /// Chord length at or below which a segment's start and end count as the same point,
    /// drawing units. The twist then follows the alignment tangent at mid-station (6.6).
    /// </summary>
    public double CoincidentChordTolerance { get; init; } = 1e-6;

    /// <summary>
    /// Half-step of the central difference that measures the alignment tangent through
    /// <see cref="IAlignmentGeometry.PointAt"/>, raw station units. A symmetric chord is
    /// parallel to the tangent on lines and circular arcs, so the result is exact there.
    /// </summary>
    public double TangentStep { get; init; } = 1e-3;

    /// <summary>Absolute bulge at or below which a polyline segment is treated as straight, dimensionless.</summary>
    public double StraightBulgeTolerance { get; init; } = 1e-12;

    /// <summary>Two consecutive vertices closer than this are one point (an explicit closing vertex), drawing units.</summary>
    public double DuplicateVertexTolerance { get; init; } = 1e-9;

    /// <summary>The maximum-fitting-length bisection stops when its bracket is this narrow, raw station units.</summary>
    public double LengthSearchTolerance { get; init; } = 1e-3;

    /// <summary>Iteration cap of the maximum-fitting-length bisection.</summary>
    public int MaxLengthSearchIterations { get; init; } = 200;

    /// <summary>
    /// False (the default, D1): the <see cref="ClipMode.Boundary"/> clip is the segment
    /// boundary only, so what the viewport shows is what was counted.
    /// <see cref="ViewOptions.ViewOverlap"/> still enlarges the fitted view; the overlap is
    /// visible with <see cref="ClipMode.None"/>. True: the clip is the display polygon (the
    /// boundary extended by the overlap). The fit test always uses the display polygon,
    /// which contains the boundary, so either clip fits the viewport (E607).
    /// </summary>
    public bool ClipIncludesOverlap { get; init; } = false;

    /// <summary>Invariant-culture .NET format string for numbers printed in problem messages.</summary>
    public string MessageNumberFormat { get; init; } = "0.00";
}

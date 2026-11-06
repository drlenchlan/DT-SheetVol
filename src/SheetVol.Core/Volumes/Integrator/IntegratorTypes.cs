// Result and settings types for the overlay integrator (HANDOFF.md 6.15). Owner: C5.
//
// Units everywhere in this folder: plain drawing units in and out. Lengths and stations
// in drawing length units, areas in square drawing units, volumes in cubic drawing units.
// No unit conversion, no factors (C2 applies factors and display units).
//
// Sign convention: depth d = base - comparison. d > 0 is cut (comparison below base),
// d < 0 is fill. Cut and fill volumes are reported as non-negative magnitudes.

namespace SheetVol.Core.Volumes.Integrator;

/// <summary>
/// Config thresholds for the impact quantities (E1007). The defaults count everything.
/// </summary>
/// <param name="MinCutDepth">Cut shallower than or equal to this depth (drawing units) is not
/// counted as impacted. Must be finite and &gt;= 0. Default 0: any cut counts.</param>
/// <param name="GapTolerance">Gaps between impacted station intervals shorter than this
/// (drawing units, measured along the alignment) are bridged. Must be finite and &gt;= 0.
/// Default 0: no bridging beyond intervals that touch.</param>
public sealed record IntegratorSettings(double MinCutDepth = 0.0, double GapTolerance = 0.0)
{
    /// <summary>MinCutDepth 0, GapTolerance 0.</summary>
    public static IntegratorSettings Default { get; } = new();
}

/// <summary>
/// Band totals of one closed boundary from the triangle overlay (6.15 step 4).
/// </summary>
/// <param name="BoundaryArea">Plan area of the boundary polygon itself, square drawing units, &gt; 0.</param>
/// <param name="PlanArea">Plan area inside the boundary where both surfaces have triangles,
/// square drawing units. This is the integrator's 2D area (6.5).</param>
/// <param name="Cut">Volume where base is above comparison (d &gt; 0), cubic drawing units, &gt;= 0, unadjusted.</param>
/// <param name="Fill">Volume where base is below comparison (d &lt; 0), cubic drawing units, &gt;= 0, unadjusted.</param>
public sealed record BandTotals(double BoundaryArea, double PlanArea, double Cut, double Fill)
{
    /// <summary>PlanArea / BoundaryArea, 0..1 (E1011). Can exceed 1 only by rounding (about 1e-15).</summary>
    public double Coverage => PlanArea / BoundaryArea;
}

/// <summary>
/// Output of <see cref="OverlayIntegrator.IntegrateBand"/>. When <see cref="Totals"/> is null the
/// run failed and <see cref="Problems"/> holds at least one Error; no number is reported.
/// </summary>
public sealed record BandResult(Guid? SegmentId, BandTotals? Totals, IReadOnlyList<Problem> Problems)
{
    /// <summary>True when totals were computed. Warnings (coverage, no overlap) may still be present.</summary>
    public bool Succeeded => Totals is not null;
}

/// <summary>
/// Output of <see cref="OverlayIntegrator.IntegrateSegment"/>: band totals plus the HCFCD impact
/// quantities (rev 3: cut where the comparison is below the base, which is the OHWM surface under
/// the HCFCD preset). When <see cref="Band"/> or <see cref="Impact"/> is null the run failed,
/// <see cref="Problems"/> holds at least one Error, and <see cref="BaseArea"/> is NaN.
/// </summary>
/// <param name="SegmentId">The segment this result belongs to.</param>
/// <param name="Band">Band totals.</param>
/// <param name="Impact">Rows 6 to 9 quantities (6.15 steps 5 and 6).</param>
/// <param name="BaseArea">Plan area inside the boundary where the base surface exists, square drawing
/// units, independent of the comparison surface.</param>
/// <param name="Problems">Errors, warnings and notes, each with a stable SV-Exxx code (see <see cref="IntegratorCodes"/>).</param>
public sealed record SegmentImpactResult(
    Guid SegmentId,
    BandTotals? Band,
    ImpactResult? Impact,
    double BaseArea,
    IReadOnlyList<Problem> Problems)
{
    /// <summary>True when band totals and impact quantities were computed.</summary>
    public bool Succeeded => Band is not null && Impact is not null;

    /// <summary>False when the base surface has no data inside the segment's boundary.</summary>
    public bool HasBase => BaseArea > 0;

    /// <summary>
    /// Cut volume / impacted length from unrounded values (E1019), cubic drawing units per drawing unit.
    /// Null when the impacted length is 0 (shown as N/A) or the run failed.
    /// </summary>
    public double? AveragePerLength => Impact is null ? null : ImpactQuantities.Average(Impact);
}

/// <summary>Derived impact quantities.</summary>
public static class ImpactQuantities
{
    /// <summary>
    /// HCFCD row 9 second value: cut volume / impacted length, from the unrounded values (E1019).
    /// Cubic drawing units per drawing unit. Null when the impacted length is 0 (N/A).
    /// </summary>
    public static double? Average(ImpactResult impact)
    {
        ArgumentNullException.ThrowIfNull(impact);
        return impact.ImpactedLength > 0 ? impact.CutVolume / impact.ImpactedLength : null;
    }
}

/// <summary>
/// Stable problem codes raised by the integrator. Each code is the SV- prefix plus the HANDOFF.md
/// section 7 row it implements, so a message can be traced to its requirement.
/// </summary>
public static class IntegratorCodes
{
    /// <summary>Error: boundary not closed, fewer than three distinct vertices, non-finite, zero area, self-intersecting, or not triangulable (E202). No number is computed.</summary>
    public const string InvalidBoundary = "SV-E202";

    /// <summary>Error: a surface returned no triangles inside the segment window (E403), or a triangle with non-finite coordinates. No number is computed.</summary>
    public const string EmptySurface = "SV-E403";

    /// <summary>Note: degenerate (vertical, zero plan area) triangles were ignored (E409, triangulation noise).</summary>
    public const string DegenerateTriangles = "SV-E409";

    /// <summary>Warning: both surfaces have triangles in the window but none overlap inside the boundary; volumes 0, coverage 0 (E405).</summary>
    public const string NoOverlap = "SV-E405";

    /// <summary>Warning: either surface is missing over part of the boundary; that part contributes nothing (E1011).</summary>
    public const string PartialCoverage = "SV-E1011";

    /// <summary>Note: no cut in the segment; the comparison is nowhere below the base (the OHWM surface under the HCFCD preset), or the two do not overlap (E1001, rev 3).</summary>
    public const string NoCut = "SV-E1001";

    /// <summary>Warning: cut reaches a lateral edge of the boundary; widen the offsets (E1002, rev 3). Also raised when the lateral edges could not be identified and the check did not run.</summary>
    public const string SearchLimit = "SV-E1002";

    /// <summary>Error: MinCutDepth or GapTolerance negative or not finite (E1007).</summary>
    public const string InvalidSettings = "SV-E1007";

    /// <summary>Error: station-of-point could not be set up from the alignment geometry (E1008).</summary>
    public const string StationGeometry = "SV-E1008";
}

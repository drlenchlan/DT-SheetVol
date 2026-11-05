namespace SheetVol.Core.Planning;

/// <summary>
/// Stable problem codes emitted by planning and boundary generation (HANDOFF.md 6.13,
/// sections 7.2 and 7.3). Each constant is the string carried by <see cref="Problem.Code"/>;
/// the suffix is the row ID in HANDOFF.md section 7.
/// </summary>
public static class ProblemCodes
{
    /// <summary>Offset on the inside of a curve or spiral reaches its center.</summary>
    public const string E201 = "SV-E201";
    /// <summary>Boundary is not a valid simple polygon (self-intersection, no area, too few vertices).</summary>
    public const string E202 = "SV-E202";
    /// <summary>Alignment data disagrees with the alignment's own station-offset points (analytic cross-check).</summary>
    public const string E204 = "SV-E204";
    /// <summary>Angle point above the mitre limit, or a non-tangent join at a curve.</summary>
    public const string E205 = "SV-E205";
    /// <summary>A typed station cannot be resolved on the alignment (station equations, ambiguity).</summary>
    public const string E208 = "SV-E208";
    /// <summary>Offsets rejected: left not less than right, zero width, or not a number.</summary>
    public const string E210 = "SV-E210";
    /// <summary>Alignment shortened: a known break or segment now lies beyond its end.</summary>
    public const string E211 = "SV-E211";
    /// <summary>Alignment geometry has gaps, zero-length or unsolved entities.</summary>
    public const string E214 = "SV-E214";
    /// <summary>Two station-segment boundaries overlap.</summary>
    public const string E215 = "SV-E215";
    /// <summary>
    /// Break specification does not parse, or the run produces no segment. This is not a
    /// section 7 row; it was added by work package C1 and is reported to the orchestrator.
    /// </summary>
    public const string E300 = "SV-E300";
    /// <summary>A range left a short remainder (kept or merged).</summary>
    public const string E302 = "SV-E302";
    /// <summary>A skip gap: no segment between two stations.</summary>
    public const string E303 = "SV-E303";
    /// <summary>Overlapping station ranges in the break specification.</summary>
    public const string E304 = "SV-E304";
    /// <summary>A break lies outside the alignment.</summary>
    public const string E305 = "SV-E305";
    /// <summary>Breaks were given out of order or more than once; they were sorted and de-duplicated.</summary>
    public const string E306 = "SV-E306";
    /// <summary>A known segment no longer exists (break removed); its outputs become orphans.</summary>
    public const string E308 = "SV-E308";
    /// <summary>A manual polyline segment is missing, open or not a simple polygon.</summary>
    public const string E311 = "SV-E311";
    /// <summary>A manual segment overlaps another segment.</summary>
    public const string E312 = "SV-E312";
}

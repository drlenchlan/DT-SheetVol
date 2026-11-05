namespace SheetVol.Core.Volumes;

/// <summary>
/// Stable problem codes raised by Core.Volumes (HANDOFF.md 6.13). A code that matches an
/// edge-case row in section 7.4 carries that row's number; codes from 430 up have no row.
/// Codes never change meaning once released.
/// </summary>
public static class VolumeCodes
{
    /// <summary>Error. Base and comparison name the same surface (E401).</summary>
    public const string SameSurface = "SV-E401";

    /// <summary>Warning. No surface data inside the boundary: volumes 0, coverage 0 % (E405).</summary>
    public const string NoSurfaceData = "SV-E405";

    /// <summary>Error. A cut or fill factor is not a finite number above zero (E410).</summary>
    public const string InvalidFactor = "SV-E410";

    /// <summary>Warning. Two analyses share a base/comparison pair but not their factors; the shared owned surface can carry only one set (E411).</summary>
    public const string SharedPairFactorMismatch = "SV-E411";

    /// <summary>Warning. Surface data covers less than the whole boundary (E406, E419).</summary>
    public const string PartialCoverage = "SV-E419";

    /// <summary>Error. The segments do not sum to the whole-band volume within tolerance (6.5 partition check).</summary>
    public const string PartitionMismatch = "SV-E430";

    /// <summary>Warning. The partition check did not run; the message states why (E215, or a segment error).</summary>
    public const string PartitionSkipped = "SV-E431";

    /// <summary>Error. A volume or area from the volume source is negative, NaN or infinite.</summary>
    public const string InvalidSourceValue = "SV-E432";

    /// <summary>Error. The area unit does not belong to the drawing's unit system (SF/AC imperial, m²/ha metric).</summary>
    public const string AreaUnitMismatch = "SV-E433";

    /// <summary>Error. The drawing unit scale is not a finite number above zero.</summary>
    public const string InvalidDrawingUnits = "SV-E434";

    /// <summary>Error. The two datums for the 2D-area calculation are equal or not finite.</summary>
    public const string InvalidDatumPair = "SV-E435";

    /// <summary>Error. A segment has no boundary, so no volume can be computed for it.</summary>
    public const string MissingBoundary = "SV-E436";

    /// <summary>Error. A boundary's plan area is zero, negative or not finite.</summary>
    public const string InvalidBoundaryArea = "SV-E437";

    /// <summary>Error. The 2D area reported inside a boundary exceeds the boundary's own area beyond tolerance.</summary>
    public const string CoverageExceedsBoundary = "SV-E438";

    /// <summary>Error. The band volume exceeds the whole-surface total beyond tolerance, so the outside-limits row would be negative.</summary>
    public const string OutsideLimitsNegative = "SV-E439";

    /// <summary>Error. An analysis definition is incomplete: empty name or surface name.</summary>
    public const string InvalidAnalysis = "SV-E440";

    /// <summary>Error. Two analyses in one run carry the same id.</summary>
    public const string DuplicateAnalysisId = "SV-E441";
}

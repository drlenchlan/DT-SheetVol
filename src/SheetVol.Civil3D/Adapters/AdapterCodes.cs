// A1 adapters (HANDOFF.md section 10, Wave 2). Owner: A1.

using SheetVol.Core;
using SheetVol.Core.Planning;
using SheetVol.Core.Volumes;

namespace SheetVol.Civil3D.Adapters;

/// <summary>
/// Stable problem codes raised by the adapters (HANDOFF.md 6.13). A code that matches a
/// section 7 row carries that row's number; codes already defined in Core are reused, never
/// redefined. New codes without a row (E442 to E446) sit in the SV-E4xx range the registry
/// gives to C2 and A1, above C2's E430 to E441.
/// </summary>
public static class AdapterCodes
{
    /// <summary>Error. The run's alignment is erased, broken or not in the drawing (E213).</summary>
    public const string AlignmentNotFound = "SV-E213";

    /// <summary>Error. The alignment has disconnected, unsolved or zero-length entities; the message lists the gap station (E214).</summary>
    public const string AlignmentGap = ProblemCodes.E214;

    /// <summary>Error. An analytic offset point disagrees with <c>Alignment.PointLocation</c> beyond tolerance, or an arc's direction does not match its geometry (E204).</summary>
    public const string AlignmentCrossCheck = ProblemCodes.E204;

    /// <summary>Error. Base and comparison resolve to the same surface (E401). Same code as C2's <see cref="VolumeCodes.SameSurface"/>.</summary>
    public const string SameSurface = VolumeCodes.SameSurface;

    /// <summary>Error. A grid surface, a volume surface or another non-TIN object was picked as base or comparison (E402).</summary>
    public const string NotTinSurface = "SV-E402";

    /// <summary>Error. A base, comparison or owned surface has no triangles (E403). Also C5's code for an empty triangle window.</summary>
    public const string NoTriangles = "SV-E403";

    /// <summary>Error when a source surface is out of date and not rebuilt, or a reference is stale; Info when a native source was rebuilt (E404).</summary>
    public const string SourceOutOfDate = "SV-E404";

    /// <summary>Info or Warning. The owned volume surface was recreated: erased, renamed or re-sourced by a user, or its name was taken and a suffix was used (E412).</summary>
    public const string OwnedSurfaceRecreated = "SV-E412";

    /// <summary>Error. A base or comparison surface is found neither by name nor by its stored handle.</summary>
    public const string SourceNotFound = "SV-E442";

    /// <summary>Warning. The configured owned-surface style is not in the drawing; Civil 3D's default style was used and the surface may display.</summary>
    public const string OwnedStyleMissing = "SV-E443";

    /// <summary>Warning. The configured owned-surface layer is not in the drawing; the surface stays on the layer Civil 3D gave it.</summary>
    public const string OwnedLayerMissing = "SV-E444";

    /// <summary>Warning. A source surface was not found by name but by its stored handle: it was renamed. The run should store the new name.</summary>
    public const string SourceRenamed = "SV-E445";

    /// <summary>Error. The adapter settings are invalid (a key missing, a tolerance not positive, equal datums).</summary>
    public const string ConfigInvalid = "SV-E446";

    /// <summary>Warning. A source was matched by name while its stored handle is gone or points elsewhere: it was re-referenced (E803).</summary>
    public const string SourceHandleChanged = "SV-E803";

    /// <summary>Warning. Triangles were read from a data-shortcut reference or corridor surface, which probe P23 could not verify headless (E1021, stop condition 12.9 open).</summary>
    public const string TriangleAccessUnverified = "SV-E1021";

    /// <summary>Error. The Civil 3D drawing unit is neither Feet nor Meters, or the Civil document is missing. Same code as C2's <see cref="VolumeCodes.InvalidDrawingUnits"/>.</summary>
    public const string DrawingUnits = VolumeCodes.InvalidDrawingUnits;

    /// <summary>A step of the SVADAPTERS headless check failed at its boundary.</summary>
    public const string CheckStepFailed = "SV-E195";

    /// <summary>Error. An analysis names a user TIN volume surface as its source (rev 3, E423). Refused until the rev3-adapters package resolves such sources.</summary>
    public const string VolumeSurfaceSource = "SV-E423";

    /// <summary>Rev 3 adoption: the single Error raised for any analysis with <see cref="AnalysisDef.VolumeSurface"/> set. The rev3-adapters package replaces it.</summary>
    public static Problem VolumeSurfaceNotYet()
        => new(VolumeSurfaceSource, Severity.Error, "Volume-surface sources arrive with rev3-adapters.",
               Fix: "Use a base and comparison surface pair for this analysis.");
}

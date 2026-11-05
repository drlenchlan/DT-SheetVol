using SheetVol.Core.Geometry;

namespace SheetVol.Core.Planning;

/// <summary>
/// Geometry of a manual segment's polyline as read by the adapter (6.3): vertices in WCS
/// drawing units with AutoCAD bulges, and the polyline's Closed flag. A polyline whose last
/// vertex repeats its first counts as closed.
/// </summary>
public sealed record ManualOutline(IReadOnlyList<PolyVertex> Vertices, bool Closed);

/// <summary>
/// Full output of <see cref="Planner.Plan"/>. <see cref="ToValidationResult"/> reduces it to
/// the frozen <see cref="ValidationResult"/>; the regen pipeline uses the rest (band pieces
/// for the partition check and the outside-limits row, overlaps, dropped GUIDs).
/// </summary>
/// <param name="Segments">Station segments in station order, invalid ones included. Manual segments are not listed: they have no station range.</param>
/// <param name="Boundaries">One per valid station segment in station order, then one per valid manual segment.</param>
/// <param name="Band">Whole-band pieces over the valid station segments.</param>
/// <param name="Overlaps">Pairs of overlapping boundaries (E215, E312).</param>
/// <param name="DroppedSegmentIds">Known segments with no successor; their outputs become orphans.</param>
/// <param name="Stations">The global station list, raw.</param>
/// <param name="PartitionCheckApplicable">True when the partition check of 6.5 applies: at least one band piece and no two station boundaries overlap.</param>
/// <param name="PartitionCheckSkipReason">Plain-words reason when the check does not apply, for the master-table footnote.</param>
/// <param name="Problems">Every problem, run-level first.</param>
public sealed record PlanResult(
    IReadOnlyList<SegmentDef> Segments,
    IReadOnlyList<Boundary> Boundaries,
    IReadOnlyList<BandPiece> Band,
    IReadOnlyList<Overlap> Overlaps,
    IReadOnlyList<Guid> DroppedSegmentIds,
    IReadOnlyList<double> Stations,
    bool PartitionCheckApplicable,
    string? PartitionCheckSkipReason,
    IReadOnlyList<Problem> Problems)
{
    /// <summary>True when no problem has <see cref="Severity.Error"/>.</summary>
    public bool IsValid => Problems.All(p => p.Severity != Severity.Error);

    /// <summary>The frozen dialog-facing result (6.14).</summary>
    public ValidationResult ToValidationResult() => new(IsValid, Segments, Boundaries, Problems);
}

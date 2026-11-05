using System.Globalization;

namespace SheetVol.Core.Volumes;

/// <summary>One analysis to reconcile: its definition, the adapter's volume source for its owned surface, and its impacts.</summary>
/// <param name="Analysis">The analysis definition, carrying the cut and fill factors.</param>
/// <param name="Source">Bounded volumes and 2D area for this analysis's base/comparison pair (raw cubic and square drawing units).</param>
/// <param name="ImpactsBySegment">HcfcdSegment preset only: the integrator's result per segment id for this analysis.</param>
public sealed record AnalysisInput(
    AnalysisDef Analysis,
    IVolumeSource Source,
    IReadOnlyDictionary<Guid, ImpactResult>? ImpactsBySegment = null);

/// <summary>Everything the reconciler needs for one run (HANDOFF.md 6.10 step 3).</summary>
/// <param name="Segments">Planned segments in table order.</param>
/// <param name="Boundaries">One per segment, matched by <see cref="Boundary.SegmentId"/>. Volumes use <see cref="Boundary.Tessellated"/>.</param>
/// <param name="BandPolygons">
/// The whole-band polygon(s) for the partition check, closed, from the same lateral vertex list
/// as the segments (6.4). One per contiguous station run: skip gaps are excluded so their
/// volume lands in the outside-limits row (E303).
/// </param>
/// <param name="Analyses">Analyses in run order.</param>
/// <param name="Units">Drawing units from the adapter. Never inferred.</param>
/// <param name="PartitionSkipReason">Set when boundaries overlap (E215, E312): the check is skipped with this reason.</param>
public sealed record ReconcileRequest(
    IReadOnlyList<SegmentDef> Segments,
    IReadOnlyList<Boundary> Boundaries,
    IReadOnlyList<IReadOnlyList<Pt>> BandPolygons,
    IReadOnlyList<AnalysisInput> Analyses,
    DrawingUnits Units,
    string? PartitionSkipReason = null);

/// <summary>The reconciliation of one analysis.</summary>
/// <param name="Analysis">The analysis definition.</param>
/// <param name="Rows">One per segment, in segment order. Error rows carry NaN in every number.</param>
/// <param name="SurfaceTotals">Whole-surface raw totals from the source.</param>
/// <param name="Partition">Partition check result.</param>
/// <param name="OutsideLimits">Outside-limits row; null when it could not be computed.</param>
public sealed record AnalysisReconciliation(
    AnalysisDef Analysis,
    IReadOnlyList<SegmentResult> Rows,
    RawVolume SurfaceTotals,
    PartitionResult Partition,
    OutsideLimitsResult? OutsideLimits);

/// <summary>
/// The reconciled run. When <see cref="Succeeded"/> is false nothing may be written or
/// displayed as a number (6.10): table and export builders refuse such a result.
/// </summary>
/// <param name="Succeeded">True when no problem is an error.</param>
/// <param name="Units">Drawing units used for every conversion.</param>
/// <param name="Segments">Segments in table order.</param>
/// <param name="Analyses">One entry per analysis, in run order. Empty when the run failed before volumes.</param>
/// <param name="Problems">Every problem in the run, including those attached to rows.</param>
public sealed record ReconcileResult(
    bool Succeeded,
    DrawingUnits Units,
    IReadOnlyList<SegmentDef> Segments,
    IReadOnlyList<AnalysisReconciliation> Analyses,
    IReadOnlyList<Problem> Problems)
{
    /// <summary>All rows, analysis by analysis, for <see cref="RegenResult.Segments"/>.</summary>
    public IReadOnlyList<SegmentResult> AllRows => Analyses.SelectMany(a => a.Rows).ToList();

    /// <summary>True only when every analysis ran the partition check and passed. Skipped is not passed.</summary>
    public bool PartitionCheckPassed => Analyses.Count > 0 && Analyses.All(a => a.Partition.Status == PartitionStatus.Passed);
}

/// <summary>
/// Turns raw bounded volumes into reported quantities (HANDOFF.md 6.5): per segment and
/// analysis, <c>Bounded(Tessellated)</c>, then factors (exactly once, E410), then unit
/// conversion. Runs the partition check and the outside-limits row. Pure: reads the sources,
/// writes nothing.
/// </summary>
public static class Reconciler
{
    /// <summary>Reconciles one run.</summary>
    public static ReconcileResult Reconcile(ReconcileRequest request, VolumeConfig config)
    {
        var unitsOutcome = QuantityUnits.Create(request.Units, config);
        if (!unitsOutcome.Succeeded)
            return new ReconcileResult(false, request.Units, request.Segments, [], unitsOutcome.Problems);
        var units = unitsOutcome.Value;

        var defProblems = OwnedVolumeSurfaces.Validate(request.Analyses.Select(a => a.Analysis).ToList());
        if (defProblems.Any(p => p.Severity == Severity.Error))
            return new ReconcileResult(false, request.Units, request.Segments, [], defProblems);

        var runProblems = new List<Problem>(defProblems);
        var geometry = SegmentGeometry(request, runProblems);
        var tol = config.Tolerances;
        var analyses = new List<AnalysisReconciliation>();

        foreach (var input in request.Analyses)
        {
            var a = input.Analysis;
            var rows = new List<SegmentResult>();

            foreach (var seg in request.Segments)
            {
                var (tess, boundaryArea, geomProblem) = geometry[seg.Id];
                if (geomProblem is not null)
                {
                    rows.Add(ErrorRow(seg, a.Id, [geomProblem]));
                    continue;
                }

                RawVolume raw = input.Source.Bounded(tess!);
                double area = input.Source.Area2D(tess!);
                if (!PlanArea.IsValidRaw(raw) || !double.IsFinite(area) || area < 0)
                {
                    var p = new Problem(VolumeCodes.InvalidSourceValue, Severity.Error,
                        Inv($"Sheet {seg.Label}, {a.Name}: the volume source returned cut {raw.Cut:R}, fill {raw.Fill:R}, area {area:R}; all must be non-negative numbers."),
                        seg.Id, "Rebuild the surfaces and regenerate; report this if it repeats.");
                    runProblems.Add(p);
                    rows.Add(ErrorRow(seg, a.Id, [p]));
                    continue;
                }

                double coverage = area / boundaryArea;
                if (coverage > 1 + tol.CoverageExcessTolerance)
                {
                    var p = new Problem(VolumeCodes.CoverageExceedsBoundary, Severity.Error,
                        Inv($"Sheet {seg.Label}, {a.Name}: 2D area {area:R} exceeds the boundary area {boundaryArea:R}."),
                        seg.Id);
                    runProblems.Add(p);
                    rows.Add(ErrorRow(seg, a.Id, [p]));
                    continue;
                }

                var notes = new List<Problem>();
                if (area == 0)
                    notes.Add(new Problem(VolumeCodes.NoSurfaceData, Severity.Warning,
                        $"Sheet {seg.Label}, {a.Name}: no surface data inside the boundary; volumes are 0 and coverage is 0 %.",
                        seg.Id, "Check that both surfaces reach this segment."));
                else if (coverage < 1 - tol.CoverageFlagTolerance)
                    notes.Add(new Problem(VolumeCodes.PartialCoverage, Severity.Warning,
                        Inv($"Sheet {seg.Label}, {a.Name}: surface data covers {coverage * 100:0.###} % of the boundary."),
                        seg.Id, "Holes, hide boundaries or a surface edge inside the segment."));
                runProblems.AddRange(notes);

                double cut = units.ToVolumeUnit(raw.Cut) * a.CutFactor;     // factors applied here and nowhere else (E410)
                double fill = units.ToVolumeUnit(raw.Fill) * a.FillFactor;
                ImpactResult? impact = null;
                input.ImpactsBySegment?.TryGetValue(seg.Id, out impact);

                rows.Add(new SegmentResult(
                    seg.Id, seg.Label, seg.StartStation, seg.EndStation, a.Id,
                    area, coverage, raw, cut, fill, cut - fill,
                    impact, SegmentStatus.Current, notes));
            }

            RawVolume surfaceTotals = input.Source.SurfaceTotals;
            var bandPieces = request.BandPolygons.Select(input.Source.Bounded).ToList();
            RawVolume band = PartitionCheck.Sum(bandPieces);
            bool sourceTotalsValid = PlanArea.IsValidRaw(surfaceTotals) && bandPieces.All(PlanArea.IsValidRaw);
            if (!sourceTotalsValid)
                runProblems.Add(new Problem(VolumeCodes.InvalidSourceValue, Severity.Error,
                    Inv($"{a.Name}: surface totals (cut {surfaceTotals.Cut:R}, fill {surfaceTotals.Fill:R}) or a band volume (sum cut {band.Cut:R}, fill {band.Fill:R}) is not a pair of non-negative numbers.")));

            PartitionResult partition;
            if (rows.Any(r => r.Status == SegmentStatus.Error) || !sourceTotalsValid)
                partition = new PartitionResult(PartitionStatus.Skipped, new RawVolume(double.NaN, double.NaN), band, 0,
                    "Partition check not run: a segment or the band has no valid volume.");
            else
                partition = PartitionCheck.Run(rows.Select(r => r.Raw).ToList(), band, units, request.PartitionSkipReason);

            if (partition.Status == PartitionStatus.Skipped)
                runProblems.Add(new Problem(VolumeCodes.PartitionSkipped, Severity.Warning, $"{a.Name}: {partition.Note}"));

            if (partition.Status == PartitionStatus.Failed)
            {
                var p = new Problem(VolumeCodes.PartitionMismatch, Severity.Error, $"{a.Name}: {partition.Note}",
                    Fix: "This is a defect in boundary generation; nothing was written. Report it with the drawing.");
                runProblems.Add(p);
                rows = request.Segments.Zip(rows, (s, r) => ErrorRow(s, a.Id, [.. r.Problems, p])).ToList();
            }

            OutsideLimitsResult? outside = null;
            if (sourceTotalsValid)
            {
                var o = PartitionCheck.OutsideLimits(surfaceTotals, band, units);
                if (o.Succeeded) outside = o.Value;
                else runProblems.AddRange(o.Problems.Select(p => p with { Message = $"{a.Name}: {p.Message}" }));
            }

            analyses.Add(new AnalysisReconciliation(a, rows, surfaceTotals, partition, outside));
        }

        bool ok = !runProblems.Any(p => p.Severity == Severity.Error);
        return new ReconcileResult(ok, request.Units, request.Segments, analyses, runProblems);
    }

    /// <summary>True when the row carries a coverage flag (E405 or E419).</summary>
    public static bool IsCoverageFlagged(SegmentResult row)
        => row.Problems.Any(p => p.Code is VolumeCodes.NoSurfaceData or VolumeCodes.PartialCoverage);

    private static Dictionary<Guid, (IReadOnlyList<Pt>? Tess, double Area, Problem? Problem)> SegmentGeometry(
        ReconcileRequest request, List<Problem> runProblems)
    {
        var byId = new Dictionary<Guid, Boundary>();
        var duplicated = new HashSet<Guid>();
        foreach (var b in request.Boundaries)
            if (!byId.TryAdd(b.SegmentId, b)) duplicated.Add(b.SegmentId);

        var result = new Dictionary<Guid, (IReadOnlyList<Pt>?, double, Problem?)>();
        foreach (var seg in request.Segments)
        {
            Problem? p = null;
            double area = 0;
            IReadOnlyList<Pt>? tess = null;
            if (duplicated.Contains(seg.Id))
                p = new Problem(VolumeCodes.MissingBoundary, Severity.Error,
                    $"Sheet {seg.Label}: more than one boundary supplied; which one is meant is ambiguous.", seg.Id);
            else if (!byId.TryGetValue(seg.Id, out var b) || b.Tessellated.Count < 4)
                p = new Problem(VolumeCodes.MissingBoundary, Severity.Error,
                    $"Sheet {seg.Label}: no valid boundary, so no volume.", seg.Id, "Fix the segment's boundary problem first.");
            else
            {
                tess = b.Tessellated;
                area = PlanArea.Polygon(tess);
                if (!double.IsFinite(area) || area <= 0)
                    p = new Problem(VolumeCodes.InvalidBoundaryArea, Severity.Error,
                        Inv($"Sheet {seg.Label}: boundary area {area:R} is not positive."), seg.Id);
            }
            if (p is not null) runProblems.Add(p);
            result[seg.Id] = (tess, area, p);
        }
        return result;
    }

    private static SegmentResult ErrorRow(SegmentDef seg, Guid analysisId, IReadOnlyList<Problem> problems)
        => new(seg.Id, seg.Label, seg.StartStation, seg.EndStation, analysisId,
               double.NaN, double.NaN, new RawVolume(double.NaN, double.NaN),
               double.NaN, double.NaN, double.NaN,
               null, SegmentStatus.Error, problems);

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

using SheetVol.Core.Volumes;

namespace SheetVol.Core.Output;

/// <summary>Volumes in an export: raw cubic drawing units, and run-unit values after factors.</summary>
/// <param name="RawCut">Cut, cubic drawing units, unadjusted.</param>
/// <param name="RawFill">Fill, cubic drawing units, unadjusted.</param>
/// <param name="Cut">Cut in the run's volume unit, after the cut factor.</param>
/// <param name="Fill">Fill in the run's volume unit, after the fill factor.</param>
/// <param name="Net">Cut − Fill (A5).</param>
public sealed record ExportVolumes(double RawCut, double RawFill, double Cut, double Fill, double Net);

/// <summary>
/// Totals for one analysis. Unrounded values are the true totals (A6); the displayed-row sums
/// are what the master table's TOTAL row shows.
/// </summary>
/// <param name="Area2DRaw">Sum of 2D areas, square drawing units.</param>
/// <param name="Area2D">Sum of 2D areas in the run's area unit, unrounded.</param>
/// <param name="Area2DDisplayedRowsSum">Sum of the rounded area rows, as on the TOTAL row.</param>
/// <param name="Cut">Sum of cut, run volume unit, unrounded.</param>
/// <param name="Fill">Sum of fill, run volume unit, unrounded.</param>
/// <param name="Net">Sum of net, run volume unit, unrounded.</param>
/// <param name="CutDisplayedRowsSum">Sum of the rounded cut rows.</param>
/// <param name="FillDisplayedRowsSum">Sum of the rounded fill rows.</param>
/// <param name="NetDisplayedRowsSum">Sum of the rounded net rows.</param>
public sealed record ExportTotals(
    double Area2DRaw, double Area2D, decimal Area2DDisplayedRowsSum,
    double Cut, double Fill, double Net,
    decimal CutDisplayedRowsSum, decimal FillDisplayedRowsSum, decimal NetDisplayedRowsSum);

/// <summary>One analysis in an export.</summary>
/// <param name="Id">Analysis id.</param>
/// <param name="Name">Analysis name.</param>
/// <param name="BaseSurface">Base surface name.</param>
/// <param name="ComparisonSurface">Comparison surface name.</param>
/// <param name="CutFactor">Cut factor applied once to every cut value.</param>
/// <param name="FillFactor">Fill factor applied once to every fill value.</param>
/// <param name="SurfaceTotals">Whole-surface totals.</param>
/// <param name="Band">Whole-band volume.</param>
/// <param name="OutsideLimits">Surface totals − band, always carried even when within tolerance.</param>
/// <param name="OutsideLimitsShown">True when the master table shows the outside-limits row.</param>
/// <param name="PartitionStatus">Passed or Skipped (a failed run is never exported).</param>
/// <param name="PartitionNote">Why the check was skipped; null when it passed.</param>
/// <param name="PartitionTolerance">Tolerance used, cubic drawing units.</param>
/// <param name="Totals">Totals for this analysis.</param>
public sealed record ExportAnalysis(
    Guid Id, string Name, string BaseSurface, string ComparisonSurface, double CutFactor, double FillFactor,
    ExportVolumes SurfaceTotals, ExportVolumes Band, ExportVolumes OutsideLimits, bool OutsideLimitsShown,
    string PartitionStatus, string? PartitionNote, double PartitionTolerance, ExportTotals Totals);

/// <summary>A raw station interval with cut below OHWM (6.15).</summary>
/// <param name="From">Start, raw station.</param>
/// <param name="To">End, raw station.</param>
public sealed record ExportInterval(double From, double To);

/// <summary>
/// Impact quantities below OHWM for one segment and material (6.15). Raw fields are drawing
/// units; the others are in the run's units. Cut volume and average (CSV and JSON
/// <c>impact_cut_volume</c>, <c>impact_average</c>) are the in-place (bank), UNFACTORED cut
/// when <see cref="HcfcdConfig.ImpactVolumesUseCutFactor"/> is off, the default (D7); they take
/// the cut factor only when it is on. The QA fields are unit-converted only.
/// </summary>
/// <param name="Material">Material (analysis) name.</param>
/// <param name="ImpactedLengthRaw">Drawing length units.</param>
/// <param name="ImpactedLength">Feet or metres.</param>
/// <param name="ImpactedAreaRaw">Square drawing units.</param>
/// <param name="ImpactedArea">In the run's area unit, unrounded.</param>
/// <param name="CutVolumeRaw">Cubic drawing units, unadjusted.</param>
/// <param name="CutVolume">Run volume unit; unfactored unless <see cref="HcfcdConfig.ImpactVolumesUseCutFactor"/> is on.</param>
/// <param name="Average">Cut volume ÷ impacted length from unrounded values (E1019); null when the length is 0.</param>
/// <param name="Intervals">Raw station intervals with cut below OHWM (E1005, E1006).</param>
/// <param name="CutAboveOhwmRaw">QA: cut where existing ground is above OHWM, cubic drawing units (E1003).</param>
/// <param name="CutAboveOhwm">QA, run volume unit, no factor.</param>
/// <param name="FillBelowOhwmRaw">QA: fill below OHWM, cubic drawing units (E1004).</param>
/// <param name="FillBelowOhwm">QA, run volume unit, no factor.</param>
/// <param name="TouchesSearchLimit">Ground below OHWM reaches the lateral search limit (E1002).</param>
public sealed record ExportImpact(
    string Material,
    double ImpactedLengthRaw, double ImpactedLength,
    double ImpactedAreaRaw, double ImpactedArea,
    double CutVolumeRaw, double CutVolume, double? Average,
    IReadOnlyList<ExportInterval> Intervals,
    double CutAboveOhwmRaw, double CutAboveOhwm,
    double FillBelowOhwmRaw, double FillBelowOhwm,
    bool TouchesSearchLimit);

/// <summary>One segment under one analysis.</summary>
/// <param name="SegmentId">Segment id.</param>
/// <param name="Label">Sheet label.</param>
/// <param name="StartStation">Raw start station.</param>
/// <param name="EndStation">Raw end station.</param>
/// <param name="StartStationText">Start station as the alignment formats it; null when no formatter was given.</param>
/// <param name="EndStationText">End station as the alignment formats it.</param>
/// <param name="AnalysisId">Analysis id.</param>
/// <param name="Analysis">Analysis name.</param>
/// <param name="RawCut">Cubic drawing units, unadjusted.</param>
/// <param name="RawFill">Cubic drawing units, unadjusted.</param>
/// <param name="Cut">Run volume unit, after the cut factor, unrounded (raw value kept even below the zero tolerance, E409).</param>
/// <param name="Fill">Run volume unit, after the fill factor, unrounded.</param>
/// <param name="Net">Cut − Fill, unrounded.</param>
/// <param name="Area2DRaw">Square drawing units (6.8: always exported).</param>
/// <param name="Area2D">In <paramref name="AreaUnit"/>, unrounded.</param>
/// <param name="AreaUnit">Label of the display unit.</param>
/// <param name="Coverage">Area2D ÷ boundary area, 0..1.</param>
/// <param name="CoverageFlag">True when coverage is flagged (E405, E419).</param>
/// <param name="Status">Segment status.</param>
/// <param name="Impact">HcfcdSegment preset only; zeros with a null average for a station segment with no impact result (SV-E827, D10).</param>
public sealed record ExportRow(
    Guid SegmentId, string Label, double StartStation, double EndStation, string? StartStationText, string? EndStationText,
    Guid AnalysisId, string Analysis,
    double RawCut, double RawFill, double Cut, double Fill, double Net,
    double Area2DRaw, double Area2D, string AreaUnit, double Coverage, bool CoverageFlag, string Status,
    ExportImpact? Impact);

/// <summary>A problem as exported.</summary>
public sealed record ExportProblem(string Code, string Severity, string Message, Guid? SegmentId, string? Fix);

/// <summary>
/// The export document (6.8): the source for both the CSV and the JSON file. Every number is
/// unrounded; writers use the invariant culture (E508).
/// </summary>
/// <param name="SchemaVersion">Export schema version, <see cref="ExportBuilder.SchemaVersion"/>.</param>
/// <param name="RunName">Run name.</param>
/// <param name="AlignmentName">Alignment name.</param>
/// <param name="GeneratedAt">When the export was produced; supplied by the caller.</param>
/// <param name="DrawingPath">Drawing the export came from; null when unknown.</param>
/// <param name="UnitSystem">Imperial or Metric.</param>
/// <param name="BaseUnitsPerDrawingUnit">Feet or metres per drawing unit, as supplied by the adapter.</param>
/// <param name="VolumeUnit">Volume unit label.</param>
/// <param name="AreaUnit">Area unit label.</param>
/// <param name="LengthUnit">Length unit label.</param>
/// <param name="PartitionCheckPassed">True when every analysis passed the partition check.</param>
/// <param name="Analyses">Per-analysis totals and checks.</param>
/// <param name="Rows">Segment rows, analysis by analysis.</param>
/// <param name="Problems">Warnings and notes from the run.</param>
public sealed record ExportDocument(
    int SchemaVersion, string RunName, string AlignmentName, DateTimeOffset GeneratedAt, string? DrawingPath,
    string UnitSystem, double BaseUnitsPerDrawingUnit, string VolumeUnit, string AreaUnit, string LengthUnit,
    bool PartitionCheckPassed,
    IReadOnlyList<ExportAnalysis> Analyses,
    IReadOnlyList<ExportRow> Rows,
    IReadOnlyList<ExportProblem> Problems);

/// <summary>Builds the export document from a reconciled run.</summary>
public static class ExportBuilder
{
    /// <summary>Export schema version. Bumped whenever a field changes meaning or is removed.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// Builds the document. Refuses a run that did not reconcile (SV-E535) and impact values
    /// that are not finite (SV-E432); a failed run is never exported.
    /// </summary>
    /// <param name="run">The run definition (name, alignment, area unit).</param>
    /// <param name="result">The reconciled run.</param>
    /// <param name="generatedAt">Timestamp to record.</param>
    /// <param name="drawingPath">Drawing path to record, or null.</param>
    /// <param name="formatStation">Station formatter for the text columns, or null to leave them out.</param>
    /// <param name="config">Validated config.</param>
    public static Outcome<ExportDocument> Build(
        RunDefinition run, ReconcileResult result, DateTimeOffset generatedAt, string? drawingPath,
        Func<double, string>? formatStation, ReportConfig config)
    {
        var pre = TablePreconditions.Check(result, run.AreaUnit, config);
        if (!pre.Succeeded) return Outcome<ExportDocument>.Fail(pre.Problems);
        var fmt = pre.Value;
        var units = fmt.Units;
        var unit = run.AreaUnit;
        string areaLabel = units.AreaLabel(unit);
        double useCutFactor(double f) => config.Hcfcd.ImpactVolumesUseCutFactor ? f : 1.0;

        var analyses = new List<ExportAnalysis>();
        var rows = new List<ExportRow>();
        foreach (var a in result.Analyses)
        {
            var def = a.Analysis;
            ExportVolumes Vol(RawVolume raw)
            {
                double c = units.ToVolumeUnit(raw.Cut) * def.CutFactor, f = units.ToVolumeUnit(raw.Fill) * def.FillFactor;
                return new ExportVolumes(raw.Cut, raw.Fill, c, f, c - f);
            }

            foreach (var r in a.Rows)
            {
                ExportImpact? impact = null;
                // D10/D18: an HCFCD station segment with no impact result (SV-E827) exports zero impact, never blank.
                var source = r.Impact ?? (run.Preset == ReportPreset.HcfcdSegment && double.IsFinite(r.StartStation)
                    ? new ImpactResult(r.SegmentId, def.Name, 0, 0, 0, [], 0, 0, false) : null);
                if (source is { } i)
                {
                    bool finite = new[] { i.ImpactedLength, i.ImpactedArea, i.CutVolume, i.CutAboveOhwm, i.FillBelowOhwm }.All(double.IsFinite)
                                  && i.Intervals.All(iv => double.IsFinite(iv.From) && double.IsFinite(iv.To));
                    if (!finite)
                        return Outcome<ExportDocument>.Fail(new Problem(VolumeCodes.InvalidSourceValue, Severity.Error,
                            $"Sheet {r.Label}, {def.Name}: an impact value is not a finite number; nothing exported.", r.SegmentId));
                    double length = units.ToLength(i.ImpactedLength);
                    double volume = units.ToVolumeUnit(i.CutVolume) * useCutFactor(def.CutFactor);
                    impact = new ExportImpact(
                        i.Material,
                        i.ImpactedLength, length,
                        i.ImpactedArea, units.ToArea(i.ImpactedArea, unit),
                        i.CutVolume, volume, length > 0 ? volume / length : null,
                        i.Intervals.Select(iv => new ExportInterval(iv.From, iv.To)).ToList(),
                        i.CutAboveOhwm, units.ToVolumeUnit(i.CutAboveOhwm),
                        i.FillBelowOhwm, units.ToVolumeUnit(i.FillBelowOhwm),
                        i.TouchesSearchLimit);
                }
                rows.Add(new ExportRow(
                    r.SegmentId, r.Label, r.StartStation, r.EndStation,
                    formatStation?.Invoke(r.StartStation), formatStation?.Invoke(r.EndStation),
                    def.Id, def.Name,
                    r.Raw.Cut, r.Raw.Fill, r.Cut, r.Fill, r.Net,
                    r.Area2D, units.ToArea(r.Area2D, unit), areaLabel, r.Coverage, Reconciler.IsCoverageFlagged(r), r.Status.ToString(),
                    impact));
            }

            var areaCol = fmt.AreaColumn(a.Rows.Select(r => units.ToArea(r.Area2D, unit)).ToList(), unit);
            var cutCol = fmt.VolumeColumn(a.Rows.Select(r => r.Cut).ToList());
            var fillCol = fmt.VolumeColumn(a.Rows.Select(r => r.Fill).ToList());
            var netCol = fmt.VolumeColumn(a.Rows.Select(r => r.Net).ToList());
            var totals = new ExportTotals(
                a.Rows.Sum(r => r.Area2D), areaCol.UnroundedTotal, areaCol.RowsTotal,
                cutCol.UnroundedTotal, fillCol.UnroundedTotal, netCol.UnroundedTotal,
                cutCol.RowsTotal, fillCol.RowsTotal, netCol.RowsTotal);

            if (a.OutsideLimits is not { } outside)
                return Outcome<ExportDocument>.Fail(new Problem(OutputCodes.ResultNotReconciled, Severity.Error,
                    $"{def.Name}: the outside-limits row was not computed; nothing exported."));
            analyses.Add(new ExportAnalysis(
                def.Id, def.Name, def.BaseSurface, def.ComparisonSurface, def.CutFactor, def.FillFactor,
                Vol(a.SurfaceTotals), Vol(a.Partition.Band), Vol(outside.Difference), outside.Shown,
                a.Partition.Status.ToString(), a.Partition.Note, a.Partition.Tolerance, totals));
        }

        var problems = result.Problems.Select(p => new ExportProblem(p.Code, p.Severity.ToString(), p.Message, p.SegmentId, p.Fix)).ToList();
        return Outcome<ExportDocument>.Ok(new ExportDocument(
            SchemaVersion, run.Name, run.AlignmentName, generatedAt, drawingPath,
            result.Units.System.ToString(), result.Units.BaseUnitsPerDrawingUnit,
            units.VolumeLabel, areaLabel, units.LengthLabel,
            result.PartitionCheckPassed, analyses, rows, problems));
    }
}

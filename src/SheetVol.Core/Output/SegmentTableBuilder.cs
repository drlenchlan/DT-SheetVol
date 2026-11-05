using SheetVol.Core.Volumes;

namespace SheetVol.Core.Output;

/// <summary>
/// The per-sheet table of HANDOFF.md 6.8: a title, then per analysis a header row and the CUT,
/// FILL and NET rows, an optional AREA row, and a factor footer for any analysis whose factors
/// are not both 1 (E410). Values come from <see cref="SegmentResult"/>, already factored and
/// converted; this builder only rounds and formats, so factors apply exactly once.
/// </summary>
public static class SegmentTableBuilder
{
    /// <summary>Columns in the per-sheet table: label and value.</summary>
    public const int ColumnCount = 2;

    /// <summary>
    /// Builds the table for one segment. Refuses a result that did not reconcile (SV-E535) and
    /// an unknown segment (SV-E536).
    /// </summary>
    /// <param name="result">The reconciled run.</param>
    /// <param name="segmentId">The segment the table reports.</param>
    /// <param name="areaUnit">The run's area unit, for the optional AREA row.</param>
    /// <param name="formatStation">Station text for the title, normally <see cref="IAlignmentGeometry.FormatStation"/>.</param>
    /// <param name="config">Validated config.</param>
    /// <param name="outOfDate">Appends the out-of-date marker to the title (1.2 L2, E418).</param>
    public static Outcome<TableModel> Build(
        ReconcileResult result, Guid segmentId, AreaUnit areaUnit,
        Func<double, string> formatStation, ReportConfig config, bool outOfDate = false)
    {
        var pre = TablePreconditions.Check(result, areaUnit, config);
        if (!pre.Succeeded) return Outcome<TableModel>.Fail(pre.Problems);
        var fmt = pre.Value;

        var seg = result.Segments.FirstOrDefault(s => s.Id == segmentId);
        if (seg is null)
            return Outcome<TableModel>.Fail(new Problem(OutputCodes.UnknownSegment, Severity.Error,
                $"No segment {segmentId} in run result.", segmentId));

        var cfg = config.SegmentTable;
        var t = new TableBuilder(ColumnCount);
        string title = string.Format(fmt.Culture, cfg.TitleTemplate, formatStation(seg.StartStation), formatStation(seg.EndStation));
        t.SpanRow(RowKind.Title, TablePreconditions.WithMarker(title, outOfDate, config), segmentId: seg.Id);

        foreach (var a in result.Analyses)
        {
            var def = a.Analysis;
            var row = a.Rows.Single(r => r.SegmentId == seg.Id);
            t.Row(RowKind.AnalysisHeader,
                [string.Format(fmt.Culture, cfg.AnalysisHeaderTemplate, def.Name, def.BaseSurface, def.ComparisonSurface)],
                def.Id, seg.Id);
            t.Row(RowKind.Cut, [cfg.CutLabel, fmt.VolumeCell(fmt.RoundVolume(row.Cut))], def.Id, seg.Id);
            t.Row(RowKind.Fill, [cfg.FillLabel, fmt.VolumeCell(fmt.RoundVolume(row.Fill))], def.Id, seg.Id);
            t.Row(RowKind.Net, [cfg.NetLabel, fmt.NetCell(fmt.RoundVolume(row.Net))], def.Id, seg.Id);
            if (cfg.AreaInSegmentTable)
                t.Row(RowKind.Area, [cfg.AreaLabel, fmt.AreaCell(fmt.RoundArea(row.Area2D, areaUnit), areaUnit)], def.Id, seg.Id);
        }

        foreach (var a in result.Analyses.Where(a => a.Analysis.CutFactor != 1.0 || a.Analysis.FillFactor != 1.0))
            t.SpanRow(RowKind.Footer,
                string.Format(fmt.Culture, cfg.FactorFooterTemplate, a.Analysis.Name, fmt.Factor(a.Analysis.CutFactor), fmt.Factor(a.Analysis.FillFactor)),
                a.Analysis.Id, seg.Id);

        return Outcome<TableModel>.Ok(t.Build(TableKind.Segment, seg.Id, cfg.Style));
    }
}

/// <summary>Checks shared by the table and export builders.</summary>
internal static class TablePreconditions
{
    /// <summary>Refuses a failed result, a bad config or an area unit from the other unit system; otherwise returns a formatter.</summary>
    public static Outcome<QuantityFormatter> Check(ReconcileResult result, AreaUnit areaUnit, ReportConfig config)
    {
        if (!result.Succeeded)
            return Outcome<QuantityFormatter>.Fail(new Problem(OutputCodes.ResultNotReconciled, Severity.Error,
                "The run did not reconcile, so no table or export is produced. See the run's problems."));
        var configProblems = config.Validate();
        if (configProblems.Count > 0) return Outcome<QuantityFormatter>.Fail(configProblems);
        var units = QuantityUnits.Create(result.Units, config.Volumes);
        if (!units.Succeeded) return Outcome<QuantityFormatter>.Fail(units.Problems);
        if (units.Value.CheckAreaUnit(areaUnit) is { } mismatch) return Outcome<QuantityFormatter>.Fail(mismatch);
        return Outcome<QuantityFormatter>.Ok(new QuantityFormatter(units.Value, config));
    }

    /// <summary>Appends the out-of-date marker when asked.</summary>
    public static string WithMarker(string title, bool outOfDate, ReportConfig config)
        => outOfDate ? title + " " + config.Formats.OutOfDateMarker : title;
}

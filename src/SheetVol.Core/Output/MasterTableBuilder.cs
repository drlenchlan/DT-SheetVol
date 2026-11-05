using SheetVol.Core.Volumes;

namespace SheetVol.Core.Output;

/// <summary>
/// The master table of HANDOFF.md 6.8. Per analysis: a header row, the column header
/// <c>SHEET | BEGIN STA | END STA | 2D AREA (unit) | CUT | FILL | NET</c>, one row per sheet,
/// the OUTSIDE LIMITS row when above tolerance, and the TOTAL row. Footer: surfaces and factors
/// per analysis (E410), coverage flags (E405, E419), the unrounded-total footnote when a total
/// row differs from the rounded true total (A6, E413, E420), and the partition-skip note (E215).
/// </summary>
public static class MasterTableBuilder
{
    /// <summary>Columns in the master table.</summary>
    public const int ColumnCount = 7;

    /// <summary>
    /// Builds the master table. Refuses a result that did not reconcile (SV-E535) and an area
    /// unit from the other unit system (SV-E433). Switching <paramref name="areaUnit"/> reformats
    /// from the stored raw area and never recomputes volumes (E418).
    /// </summary>
    /// <param name="result">The reconciled run.</param>
    /// <param name="areaUnit">The run's area unit.</param>
    /// <param name="formatStation">Station text, normally <see cref="IAlignmentGeometry.FormatStation"/>.</param>
    /// <param name="config">Validated config.</param>
    /// <param name="outOfDate">Appends the out-of-date marker to the title.</param>
    public static Outcome<TableModel> Build(
        ReconcileResult result, AreaUnit areaUnit, Func<double, string> formatStation, ReportConfig config, bool outOfDate = false)
    {
        var pre = TablePreconditions.Check(result, areaUnit, config);
        if (!pre.Succeeded) return Outcome<TableModel>.Fail(pre.Problems);
        var fmt = pre.Value;
        var units = fmt.Units;
        var cfg = config.MasterTable;
        var t = new TableBuilder(ColumnCount);
        var footer = new List<(string Text, Guid? AnalysisId)>();
        var coverageLines = new List<(string Text, Guid AnalysisId, Guid SegmentId)>();
        var footnotes = new List<(string Text, Guid AnalysisId)>();
        string areaLabel = units.AreaLabel(areaUnit);
        string areaHeader = string.Format(fmt.Culture, cfg.AreaHeaderTemplate, areaLabel);

        t.SpanRow(RowKind.Title, TablePreconditions.WithMarker(cfg.Title, outOfDate, config));

        foreach (var a in result.Analyses)
        {
            var def = a.Analysis;
            t.SpanRow(RowKind.AnalysisHeader,
                string.Format(fmt.Culture, cfg.AnalysisHeaderTemplate, def.Name, def.BaseSurface, def.ComparisonSurface), def.Id);
            t.Row(RowKind.ColumnHeader,
                [cfg.SheetHeader, cfg.BeginStationHeader, cfg.EndStationHeader, areaHeader, cfg.CutHeader, cfg.FillHeader, cfg.NetHeader],
                def.Id);

            var rows = a.Rows;
            var area = fmt.AreaColumn(rows.Select(r => units.ToArea(r.Area2D, areaUnit)).ToList(), areaUnit);
            var cut = fmt.VolumeColumn(rows.Select(r => r.Cut).ToList());
            var fill = fmt.VolumeColumn(rows.Select(r => r.Fill).ToList());
            var net = fmt.VolumeColumn(rows.Select(r => r.Net).ToList());

            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                bool flagged = Reconciler.IsCoverageFlagged(r);
                t.Row(RowKind.Segment,
                [
                    flagged ? r.Label + cfg.CoverageFlagMarker : r.Label,
                    formatStation(r.StartStation), formatStation(r.EndStation),
                    fmt.AreaNumber(area.Rows[i], areaUnit),
                    fmt.VolumeCell(cut.Rows[i]), fmt.VolumeCell(fill.Rows[i]), fmt.NetCell(net.Rows[i]),
                ], def.Id, r.SegmentId);
                if (flagged)
                    coverageLines.Add((string.Format(fmt.Culture, cfg.CoverageFooterTemplate,
                        cfg.CoverageFlagMarker, r.Label, def.Name, fmt.CoveragePercent(r.Coverage, flagged: true)), def.Id, r.SegmentId));
            }

            decimal cutTotal = cut.RowsTotal, fillTotal = fill.RowsTotal, netTotal = net.RowsTotal;
            double cutRaw = cut.UnroundedTotal, fillRaw = fill.UnroundedTotal, netRaw = net.UnroundedTotal;

            if (a.OutsideLimits is { Shown: true } o)
            {
                double oCut = units.ToVolumeUnit(o.Difference.Cut) * def.CutFactor;
                double oFill = units.ToVolumeUnit(o.Difference.Fill) * def.FillFactor;
                decimal oCutR = fmt.RoundVolume(oCut), oFillR = fmt.RoundVolume(oFill), oNetR = fmt.RoundVolume(oCut - oFill);
                t.Row(RowKind.OutsideLimits,
                    [cfg.OutsideLimitsLabel, "", "", "", fmt.VolumeCell(oCutR), fmt.VolumeCell(oFillR), fmt.NetCell(oNetR)], def.Id);
                if (cfg.TotalIncludesOutsideLimits)
                {
                    cutTotal += oCutR; fillTotal += oFillR; netTotal += oNetR;
                    cutRaw += oCut; fillRaw += oFill; netRaw += oCut - oFill;
                }
            }

            t.Row(RowKind.Total,
            [
                cfg.TotalLabel, "", "",
                fmt.AreaNumber(area.RowsTotal, areaUnit),
                fmt.VolumeCell(cutTotal), fmt.VolumeCell(fillTotal), fmt.NetCell(netTotal),
            ], def.Id);

            var items = new List<string>();
            if (area.TotalDiffers)
                items.Add(string.Format(fmt.Culture, cfg.UnroundedItemTemplate, areaHeader,
                    fmt.FootnoteValue(area.UnroundedTotal, config.AreaDecimals(areaUnit), areaLabel)));
            if (cutTotal != fmt.RoundVolume(cutRaw))
                items.Add(string.Format(fmt.Culture, cfg.UnroundedItemTemplate, cfg.CutHeader,
                    fmt.FootnoteValue(cutRaw, config.Formats.VolumeDecimals, units.VolumeLabel)));
            if (fillTotal != fmt.RoundVolume(fillRaw))
                items.Add(string.Format(fmt.Culture, cfg.UnroundedItemTemplate, cfg.FillHeader,
                    fmt.FootnoteValue(fillRaw, config.Formats.VolumeDecimals, units.VolumeLabel)));
            if (netTotal != fmt.RoundVolume(netRaw))
                items.Add(string.Format(fmt.Culture, cfg.UnroundedItemTemplate, cfg.NetHeader, fmt.FootnoteNet(netRaw)));
            if (items.Count > 0)
                footnotes.Add((string.Format(fmt.Culture, cfg.UnroundedFootnoteTemplate, def.Name,
                    string.Join(cfg.UnroundedItemSeparator, items)), def.Id));

            footer.Add((string.Format(fmt.Culture, cfg.SurfacesFooterTemplate,
                def.Name, def.BaseSurface, def.ComparisonSurface, fmt.Factor(def.CutFactor), fmt.Factor(def.FillFactor)), def.Id));
            if (a.Partition.Status == PartitionStatus.Skipped)
                footnotes.Add((string.Format(fmt.Culture, cfg.PartitionSkippedFootnoteTemplate, def.Name, a.Partition.Note), def.Id));
        }

        foreach (var (text, id) in footer) t.SpanRow(RowKind.Footer, text, id);
        foreach (var (text, id, seg) in coverageLines) t.SpanRow(RowKind.Footer, text, id, seg);
        foreach (var (text, id) in footnotes) t.SpanRow(RowKind.Footer, text, id);

        return Outcome<TableModel>.Ok(t.Build(TableKind.Master, null, cfg.Style));
    }
}

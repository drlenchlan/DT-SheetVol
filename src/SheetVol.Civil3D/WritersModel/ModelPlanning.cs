// A2 model writers: pure helpers that turn planned segments, boundaries and impact results
// into writer specs. No drawing access. Owner: A2.

using System.Globalization;
using SheetVol.Core;
using SheetVol.Core.Geometry;

namespace SheetVol.Civil3D.WritersModel;

/// <summary>Match marks at every break shared by two consecutive segments (6.8).</summary>
public static class MatchMarkPlanner
{
    /// <summary>
    /// One mark at the end of each segment whose next segment starts at the same station (within
    /// <paramref name="stationTolerance"/>), pointing to the next sheet, and one at the start of
    /// that next segment pointing back. A skip gap or the alignment ends get no mark. Points come
    /// from the alignment at the segment's own offsets; the text names the displayed station and
    /// the other segment's label. Empty when marks are disabled in config.
    /// </summary>
    /// <param name="segments">Station segments in station order.</param>
    /// <param name="alignment">The run's alignment.</param>
    /// <param name="scaleFor">View scale (model units per paper unit) per segment, honouring per-segment overrides.</param>
    /// <param name="config">A2 config.</param>
    /// <param name="stationTolerance">Stations closer than this are one break (C1's StationTolerance).</param>
    /// <param name="culture">Culture for the text template (C2's table culture).</param>
    public static IReadOnlyList<MatchMarkSpec> Plan(
        IReadOnlyList<SegmentDef> segments, IAlignmentGeometry alignment, Func<Guid, double> scaleFor,
        ModelWritersConfig config, double stationTolerance, CultureInfo culture)
    {
        var marks = new List<MatchMarkSpec>();
        if (!config.MatchMarks.Enabled) return marks;
        for (int i = 0; i + 1 < segments.Count; i++)
        {
            SegmentDef a = segments[i], b = segments[i + 1];
            if (Math.Abs(a.EndStation - b.StartStation) > stationTolerance) continue;
            marks.Add(Mark(a, MatchEnd.End, a.EndStation, b.Label, alignment, scaleFor(a.Id), config, culture));
            marks.Add(Mark(b, MatchEnd.Start, b.StartStation, a.Label, alignment, scaleFor(b.Id), config, culture));
        }
        return marks;
    }

    private static MatchMarkSpec Mark(SegmentDef s, MatchEnd end, double station, string otherLabel, IAlignmentGeometry alignment,
        double scale, ModelWritersConfig config, CultureInfo culture)
    {
        Pt left = alignment.PointAt(station, s.OffsetLeft), right = alignment.PointAt(station, s.OffsetRight);
        double step = (s.EndStation - s.StartStation) * config.MatchMarks.DirectionSampleFraction;
        Pt here = alignment.PointAt(station, 0);
        Pt toward = alignment.PointAt(end == MatchEnd.Start ? station + step : station - step, 0);
        double dx = toward.X - here.X, dy = toward.Y - here.Y, len = Math.Sqrt(dx * dx + dy * dy);
        var inside = len > 0 ? new Pt(dx / len, dy / len) : new Pt(0, 0);
        string text = string.Format(culture, config.MatchMarks.TextTemplate, alignment.FormatStation(station), otherLabel);
        return new MatchMarkSpec(s.Id, end, left, right, inside, text, scale);
    }
}

/// <summary>
/// The impact outline (6.15, rev 3 D13): the rings of <see cref="ImpactResult.Outlines"/>, the true
/// plan outline of the area counted in <see cref="ImpactResult.ImpactedArea"/> (C5's edge
/// cancellation over its step-5 pieces), drawn as-is. Nothing is approximated here: the pre-rev-3
/// full-width station band is gone, and an impact result with no rings draws nothing, so the
/// writer erases any outline that segment still owns (E308).
/// </summary>
public static class ImpactOutlines
{
    /// <summary>
    /// One spec per ring of every impact result of <paramref name="segmentId"/>, keyed by
    /// (segment, analysis, ring index), in the order given. Results for another segment are
    /// ignored. The points are passed through unchanged (bulge 0); closure is checked by the
    /// writer (SV-E541), never repaired.
    /// </summary>
    /// <param name="segmentId">The station segment.</param>
    /// <param name="impacts">Each analysis's impact result for the segment, with the analysis id (an <see cref="ImpactResult"/> names its material, not its analysis).</param>
    public static IReadOnlyList<ImpactOutlineSpec> Build(Guid segmentId, IEnumerable<(Guid AnalysisId, ImpactResult Impact)> impacts)
    {
        var specs = new List<ImpactOutlineSpec>();
        foreach (var (analysisId, impact) in impacts)
        {
            if (impact.SegmentId != segmentId) continue;
            for (int ring = 0; ring < impact.Outlines.Count; ring++)
                specs.Add(new ImpactOutlineSpec(segmentId, analysisId, ring, impact.Outlines[ring].Select(p => new PolyVertex(p, 0)).ToList()));
        }
        return specs;
    }
}

/// <summary>Where model-space tables go when first created.</summary>
public static class ModelTablePlacement
{
    /// <summary>A per-sheet table at its segment: bottom-left corner on the boundary's top-left, one gap above it.</summary>
    public static TablePlacement AtSegment(Boundary boundary, double modelUnitsPerPaperUnit, ModelWritersConfig config)
    {
        var pts = boundary.Tessellated.Count > 0 ? boundary.Tessellated : boundary.Drawn.Select(v => v.Point).ToList();
        double minX = pts.Min(p => p.X), maxY = pts.Max(p => p.Y);
        return TablePlacement.InModel(new Pt(minX, maxY + config.Tables.PlacementGap * modelUnitsPerPaperUnit), AnchorCorner.BottomLeft, modelUnitsPerPaperUnit);
    }

    /// <summary>The master table in model space: top-right corner one gap left of the run's top-left.</summary>
    public static TablePlacement MasterInModel(IEnumerable<Boundary> boundaries, double modelUnitsPerPaperUnit, ModelWritersConfig config)
    {
        var pts = boundaries.SelectMany(b => b.Tessellated.Count > 0 ? b.Tessellated : b.Drawn.Select(v => v.Point).ToList()).ToList();
        if (pts.Count == 0) throw new ArgumentException("The master table needs at least one boundary to sit beside.", nameof(boundaries));
        double minX = pts.Min(p => p.X), maxY = pts.Max(p => p.Y);
        return TablePlacement.InModel(new Pt(minX - config.Tables.PlacementGap * modelUnitsPerPaperUnit, maxY), AnchorCorner.TopRight, modelUnitsPerPaperUnit);
    }
}

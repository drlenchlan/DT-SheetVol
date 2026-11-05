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
/// Outlines of the impacted area below OHWM (6.15) from the integrator's station intervals.
/// The frozen <see cref="ImpactResult"/> carries the stations that have cut below OHWM, not the
/// plan polygon of the impacted pieces, so each outline is the segment's band between the
/// interval's stations (C1's boundary builder, true arcs kept), drawn across the full offset width.
/// </summary>
public static class ImpactOutlines
{
    /// <summary>
    /// Outlines for one segment from the union of every material's intervals, clamped to the
    /// segment, touching intervals merged. Builder problems come back as warnings and skip the
    /// outline; they never fail the run.
    /// </summary>
    public static (IReadOnlyList<ImpactOutlineSpec> Outlines, IReadOnlyList<Problem> Problems) Build(
        IAlignmentGeometry alignment, SegmentDef segment, IEnumerable<ImpactResult> impacts, GeometryOptions options)
    {
        double tol = options.StationTolerance;
        var intervals = impacts.Where(i => i.SegmentId == segment.Id).SelectMany(i => i.Intervals)
            .Select(iv => (From: Math.Max(Math.Min(iv.From, iv.To), segment.StartStation), To: Math.Min(Math.Max(iv.From, iv.To), segment.EndStation)))
            .Where(iv => iv.To - iv.From > tol)
            .OrderBy(iv => iv.From).ToList();
        var merged = new List<(double From, double To)>();
        foreach (var iv in intervals)
        {
            if (merged.Count > 0 && iv.From <= merged[^1].To + tol) merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, iv.To));
            else merged.Add(iv);
        }
        if (merged.Count == 0) return ([], []);

        var defs = merged.Select((iv, k) => new SegmentDef(Deterministic(segment.Id, k), segment.Label, iv.From, iv.To, segment.OffsetLeft, segment.OffsetRight)).ToList();
        var built = BoundaryBuilder.Build(alignment, defs, segment.OffsetLeft, segment.OffsetRight, options);
        var problems = built.Problems.Select(p => p with
        {
            Severity = p.Severity == Severity.Error ? Severity.Warning : p.Severity,
            SegmentId = segment.Id,
            Message = "Impacted-area outline not drawn: " + p.Message,
        }).ToList();
        var specs = new List<ImpactOutlineSpec>();
        for (int k = 0; k < defs.Count; k++)
        {
            var b = built.Boundaries.FirstOrDefault(x => x.SegmentId == defs[k].Id);
            if (b is not null) specs.Add(new ImpactOutlineSpec(segment.Id, specs.Count, b.Drawn));
        }
        return (specs, problems);
    }

    private static Guid Deterministic(Guid segment, int k)
    {
        var bytes = segment.ToByteArray();
        bytes[15] ^= (byte)(k + 1);
        bytes[14] ^= (byte)((k + 1) >> 8);
        return new Guid(bytes);
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

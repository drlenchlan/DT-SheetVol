using System.Globalization;
using SheetVol.Core.Geometry;

namespace SheetVol.Core.Planning;

/// <summary>One planned station segment.</summary>
/// <param name="Def">The segment; raw stations, the run's offsets.</param>
/// <param name="InRange">False when the segment reaches outside the alignment (E305, E211); such a segment gets no boundary.</param>
/// <param name="Carried">True when the GUID came from <see cref="RunDefinition.KnownSegments"/>.</param>
public sealed record PlannedSegment(SegmentDef Def, bool InRange, bool Carried);

/// <summary>Output of <see cref="SegmentPlanner.Plan"/>.</summary>
/// <param name="Segments">Station segments in station order, out-of-range ones included.</param>
/// <param name="Dropped">GUIDs of known segments with no successor; their outputs become orphans (E308, E211).</param>
/// <param name="Problems">E211, E303, E305, E308 problems.</param>
public sealed record SegmentPlan(IReadOnlyList<PlannedSegment> Segments, IReadOnlyList<Guid> Dropped, IReadOnlyList<Problem> Problems);

/// <summary>
/// Turns parsed breaks into station segments (HANDOFF.md 6.3): one segment per interval
/// between consecutive breaks, none inside a skip range. Identity is carried from the known
/// segments of the last plan:
/// <list type="number">
/// <item>A new interval that starts where a known segment started keeps that GUID. This is
/// the split rule (the left part keeps the GUID) and the merge rule (the merged segment keeps
/// the left GUID).</item>
/// <item>A remaining interval takes the GUID of the unclaimed known segment it overlaps most,
/// so a moved break or a moved START keeps the segment.</item>
/// <item>Anything else is new. Unclaimed known segments are reported as dropped.</item>
/// </list>
/// Intervals inside the alignment claim identities before intervals that reach outside it,
/// so a valid survivor of a shortened alignment keeps its GUID ahead of an invalid stub.
/// Labels are derived from position and are never identity.
/// <para><see cref="RunDefinition.KnownSegments"/> must be the last applied plan (the one the
/// last successful regen wrote). A plan with errors is never applied, so persisting it would
/// overwrite the identities a later, corrected plan needs.</para>
/// </summary>
public static class SegmentPlanner
{
    /// <summary>Plans the station segments of a Stations-driver run.</summary>
    public static SegmentPlan Plan(ParsedBreakSpec parsed, RunDefinition run, IAlignmentGeometry alignment, PlanningOptions options)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(alignment);
        ArgumentNullException.ThrowIfNull(options);
        double tol = options.Geometry.StationTolerance;
        double start = alignment.StartStation, end = alignment.EndStation;
        var problems = new List<Problem>();

        var known = new List<SegmentDef>();
        var seen = new HashSet<Guid>();
        foreach (var k in run.KnownSegments.Where(k => k.SourceKey is null).OrderBy(k => k.StartStation))
        {
            if (seen.Add(k.Id)) { known.Add(k); continue; }
            problems.Add(new Problem(ProblemCodes.E300, Severity.Warning,
                $"The stored segment list repeats segment ID {k.Id} ({Fmt.Station(alignment, k.StartStation, tol)} to {Fmt.Station(alignment, k.EndStation, tol)}); the later copy was ignored.",
                k.Id, "Regenerate the run so the stored segment list is rewritten."));
        }
        var knownBreaks = known.SelectMany(k => new[] { k.StartStation, k.EndStation }).ToList();
        bool WasKnown(double s) => knownBreaks.Any(k => Math.Abs(k - s) <= tol);

        // Breaks outside the alignment.
        foreach (var b in parsed.Breaks)
        {
            if (b.Station >= start - tol && b.Station <= end + tol) continue;
            string sta = Fmt.Station(alignment, b.Station, tol), range = Fmt.Range(alignment, tol);
            problems.Add(WasKnown(b.Station)
                ? new Problem(ProblemCodes.E211, Severity.Error,
                    $"The break at {sta} (from '{b.Source}') is beyond the alignment, which now runs {range}. The segments it bounds are marked invalid.",
                    null, "The alignment was shortened or re-stationed. Remove the break or move it inside the new range; START and END already follow the alignment.")
                : new Problem(ProblemCodes.E305, Severity.Error,
                    $"The break at {sta} (from '{b.Source}') is outside the alignment. Valid range: {range}.",
                    null, $"Remove the break or change it to a station from {range}."));
        }

        // Intervals between consecutive breaks, minus skip ranges.
        var intervals = new List<(double A, double B)>();
        for (int i = 0; i + 1 < parsed.Breaks.Count; i++)
        {
            double a = parsed.Breaks[i].Station, b = parsed.Breaks[i + 1].Station;
            if (parsed.Skips.Any(r => a >= r.Start - tol && b <= r.End + tol)) continue;
            intervals.Add((a, b));
        }
        foreach (var r in parsed.Skips)
            problems.Add(new Problem(ProblemCodes.E303, Severity.Info,
                $"Stations {Fmt.Station(alignment, r.Start, tol)} to {Fmt.Station(alignment, r.End, tol)} are skipped: no segment, viewport or table. Their volume falls under OUTSIDE LIMITS.",
                null, null));

        // Identity: intervals on the alignment first, then those reaching outside it, over what is left.
        bool[] inRanges = intervals.Select(iv => iv.A >= start - tol && iv.B <= end + tol).ToArray();
        var ids = new Guid?[intervals.Count];
        var claimed = new bool[known.Count];
        foreach (bool phase in new[] { true, false })
        {
            var pick = Enumerable.Range(0, intervals.Count).Where(j => inRanges[j] == phase).ToArray();
            var open = Enumerable.Range(0, known.Count).Where(i => !claimed[i]).ToArray();
            var (phaseIds, phaseClaimed) = AssignIdentity(pick.Select(j => intervals[j]).ToArray(), open.Select(i => known[i]).ToArray(), tol);
            for (int p = 0; p < pick.Length; p++) ids[pick[p]] = phaseIds[p];
            for (int q = 0; q < open.Length; q++) claimed[open[q]] |= phaseClaimed[q];
        }

        var segments = new List<PlannedSegment>(intervals.Count);
        for (int j = 0; j < intervals.Count; j++)
        {
            var (a, b) = intervals[j];
            Guid id = ids[j] ?? Guid.NewGuid();
            var from = ids[j] is { } g ? known.First(k => k.Id == g) : null;
            string? layout = run.Overrides.TryGetValue(id, out var ov) && ov.TargetLayout is not null ? ov.TargetLayout : from?.TargetLayout;
            string label = string.Format(CultureInfo.InvariantCulture, options.SegmentLabelFormat, j + 1);
            var def = new SegmentDef(id, label, a, b, run.OffsetLeft, run.OffsetRight, null, layout);
            bool inRange = inRanges[j];
            segments.Add(new PlannedSegment(def, inRange, from is not null));
            if (inRange) continue;

            string what = $"{Fmt.Segment(alignment, def, tol)} reaches outside the alignment ({Fmt.Range(alignment, tol)})";
            problems.Add(from is not null || WasKnown(a) || WasKnown(b)
                ? new Problem(ProblemCodes.E211, Severity.Error, $"{what} after the alignment changed. It is kept, marked invalid, and gets no boundary or volume.", id,
                    "Remove or move the break beyond the alignment end, or restore the alignment length.")
                : new Problem(ProblemCodes.E305, Severity.Error, $"{what}. It gets no boundary or volume.", id,
                    "Change the breaks so every segment lies on the alignment."));
        }

        var dropped = new List<Guid>();
        for (int i = 0; i < known.Count; i++)
        {
            if (claimed[i]) continue;
            var k = known[i];
            dropped.Add(k.Id);
            string range = $"{Fmt.Station(alignment, k.StartStation, tol)} to {Fmt.Station(alignment, k.EndStation, tol)}";
            problems.Add(k.EndStation > end + tol || k.StartStation < start - tol
                ? new Problem(ProblemCodes.E211, Severity.Warning,
                    $"Former segment {k.Label} ({range}) lay beyond the alignment, which now runs {Fmt.Range(alignment, tol)}. It no longer exists: its boundary and tables are removed and its layout becomes an orphan.",
                    k.Id, "Extend the alignment to bring it back, or run SVPURGE later to delete the orphaned layout.")
                : new Problem(ProblemCodes.E308, Severity.Info,
                    $"Former segment {k.Label} ({range}) has no successor in this plan: its boundary and tables are removed and its layout becomes an orphan.",
                    k.Id, null));
        }

        return new SegmentPlan(segments, dropped, problems);
    }

    /// <summary>
    /// Identity carry-over (6.3). Returns, per interval, the known GUID it keeps or null for a
    /// new segment, and per known segment whether it was claimed.
    /// </summary>
    /// <param name="intervals">New intervals, sorted, raw stations.</param>
    /// <param name="known">Known segments sorted by start station.</param>
    /// <param name="tol">Station tolerance, drawing units.</param>
    public static (Guid?[] Ids, bool[] Claimed) AssignIdentity(IReadOnlyList<(double A, double B)> intervals, IReadOnlyList<SegmentDef> known, double tol)
    {
        var ids = new Guid?[intervals.Count];
        var claimed = new bool[known.Count];

        for (int j = 0; j < intervals.Count; j++)
        {
            for (int i = 0; i < known.Count; i++)
            {
                if (claimed[i] || Math.Abs(known[i].StartStation - intervals[j].A) > tol) continue;
                ids[j] = known[i].Id;
                claimed[i] = true;
                break;
            }
        }

        for (int j = 0; j < intervals.Count; j++)
        {
            if (ids[j] is not null) continue;
            int best = -1;
            double bestLength = tol;
            for (int i = 0; i < known.Count; i++)
            {
                if (claimed[i]) continue;
                double overlap = Math.Min(intervals[j].B, known[i].EndStation) - Math.Max(intervals[j].A, known[i].StartStation);
                if (overlap > bestLength) { best = i; bestLength = overlap; }
            }
            if (best < 0) continue;
            ids[j] = known[best].Id;
            claimed[best] = true;
        }
        return (ids, claimed);
    }
}

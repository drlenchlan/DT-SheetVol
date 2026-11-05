// Impacted length: union of station intervals, clamped to the segment, with gap bridging
// (6.15 step 6, E1005, E1007). Owner: C5.

namespace SheetVol.Core.Volumes.Integrator;

/// <summary>Merged station intervals and their total length.</summary>
/// <param name="Intervals">Disjoint, ascending raw-station intervals, each From &lt; To, drawing units.</param>
/// <param name="TotalLength">Sum of To - From over the intervals, drawing units, measured along the alignment.</param>
public sealed record StationUnion(IReadOnlyList<(double From, double To)> Intervals, double TotalLength);

/// <summary>Union of station intervals.</summary>
public static class StationIntervals
{
    /// <summary>
    /// Relative numeric merge tolerance: intervals whose gap is at most this fraction of the
    /// segment length are treated as touching. It absorbs rounding where two pieces share a
    /// vertex computed along different edges (about 1e-13 relative), and is far below any
    /// physical gap.
    /// </summary>
    public const double RelativeMergeTolerance = 1e-9;

    /// <summary>
    /// Clamps each interval to [segmentStart, segmentEnd], drops empty ones, sorts, and merges
    /// intervals that overlap, touch, or are separated by a gap shorter than
    /// <paramref name="gapTolerance"/> ("gaps shorter than GapTolerance are bridged"; a gap
    /// exactly equal to it is kept). Stations in drawing units.
    /// </summary>
    public static StationUnion Union(IEnumerable<(double From, double To)> intervals,
                                     double segmentStart, double segmentEnd, double gapTolerance)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        if (!double.IsFinite(segmentStart) || !double.IsFinite(segmentEnd) || !(segmentEnd > segmentStart))
            throw new ArgumentException("Segment end station must be greater than its start station.");
        if (!double.IsFinite(gapTolerance) || gapTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(gapTolerance), "GapTolerance must be finite and >= 0.");

        double eps = RelativeMergeTolerance * Math.Max(1.0, segmentEnd - segmentStart);
        var list = new List<(double From, double To)>();
        foreach (var (a, b) in intervals)
        {
            double f = Math.Max(Math.Min(a, b), segmentStart);
            double t = Math.Min(Math.Max(a, b), segmentEnd);
            if (t > f) list.Add((f, t));
        }
        list.Sort((p, q) => p.From.CompareTo(q.From));

        var merged = new List<(double From, double To)>();
        double total = 0;
        foreach (var iv in list)
        {
            if (merged.Count > 0)
            {
                var cur = merged[^1];
                double gap = iv.From - cur.To;
                if (gap <= eps || gap < gapTolerance)
                {
                    merged[^1] = (cur.From, Math.Max(cur.To, iv.To));
                    continue;
                }
            }
            merged.Add(iv);
        }
        foreach (var (f, t) in merged) total += t - f;
        return new StationUnion(merged, total);
    }
}

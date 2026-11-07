namespace SheetVol.Core.Planning;

/// <summary>
/// Cleans a whole-band piece's ring before the partition check measures it (6.5, rev 3).
/// <para>The boundary builder takes a band piece's lateral vertices from the global station list,
/// so a piece over several segments carries a vertex at every interior break, and on a tangent
/// that vertex lies on the straight line between its neighbours. MEASURED 2026-11-06 (Civil 3D
/// 2026.2.3, SVSELFTEST Installed, T25): GetBoundedVolumes on the owned volume surface gave fill
/// 708,942.2 on the two-segment band [0, 1000] with straight-through vertices at station 500 on
/// both lateral edges, and 828,000 on the four-vertex polygon over the same range, which is the
/// sum of the two segments. So straight-through and repeated vertices are dropped: the cleaned
/// ring covers the same ground with the same vertex coordinates, minus vertices that change
/// nothing.</para>
/// </summary>
public static class BandRing
{
    /// <summary>
    /// Drops every vertex that repeats its predecessor, or that lies between its neighbours on
    /// the straight line through them, each within <paramref name="tolerance"/>. Repeats until no
    /// vertex qualifies, wrapping around the ring.
    /// </summary>
    /// <param name="ring">Closed ring, first == last.</param>
    /// <param name="tolerance">Largest distance, drawing units, at which two vertices are one and a vertex lies on a line (the planning station tolerance).</param>
    /// <returns>Closed ring, first == last, same orientation, same starting vertex unless that vertex was dropped.</returns>
    /// <exception cref="ArgumentException">The ring is not closed, the tolerance is not a non-negative number, or fewer than three vertices remain (a degenerate band is a planner defect).</exception>
    public static IReadOnlyList<Pt> DropStraightVertices(IReadOnlyList<Pt> ring, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(ring);
        if (!(tolerance >= 0) || !double.IsFinite(tolerance))
            throw new ArgumentException($"Tolerance must be zero or a positive length; it is {tolerance}.", nameof(tolerance));
        if (ring.Count < 4 || ring[0] != ring[^1])
            throw new ArgumentException("The band ring must be closed (first == last) with at least three vertices.", nameof(ring));

        var pts = new List<Pt>(ring.Count - 1);
        for (int i = 0; i < ring.Count - 1; i++) pts.Add(ring[i]);

        bool changed = true;
        while (changed && pts.Count >= 3)
        {
            changed = false;
            for (int i = 0; i < pts.Count && pts.Count >= 3; i++)
            {
                int n = pts.Count;
                Pt prev = pts[(i - 1 + n) % n], cur = pts[i], next = pts[(i + 1) % n];
                if (Distance(prev, cur) <= tolerance || OnLineBetween(prev, cur, next, tolerance))
                {
                    pts.RemoveAt(i);
                    changed = true;
                    i--;
                }
            }
        }

        if (pts.Count < 3)
            throw new ArgumentException($"The band ring collapses to {pts.Count} vertices once repeated and straight-through vertices are dropped; the planner built a band without width.", nameof(ring));
        pts.Add(pts[0]);
        return pts;
    }

    private static double Distance(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>True when <paramref name="cur"/> lies within the tolerance of the line through
    /// <paramref name="prev"/> and <paramref name="next"/> and strictly between them along it, so
    /// the edge prev to next covers the same points as prev to cur to next. A vertex where the
    /// ring turns back on itself is not between its neighbours and is kept for the validator.</summary>
    private static bool OnLineBetween(Pt prev, Pt cur, Pt next, double tolerance)
    {
        double dx = next.X - prev.X, dy = next.Y - prev.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= tolerance) return false;
        double cross = (cur.X - prev.X) * dy - (cur.Y - prev.Y) * dx;
        if (Math.Abs(cross) / len > tolerance) return false;
        double along = ((cur.X - prev.X) * dx + (cur.Y - prev.Y) * dy) / len;
        return along > tolerance && along < len - tolerance;
    }
}

namespace SheetVol.Core.Geometry;

/// <summary>Two boundaries whose interiors overlap.</summary>
/// <param name="First">Segment ID of the earlier boundary in the input order.</param>
/// <param name="Second">Segment ID of the later boundary.</param>
/// <param name="Area">Overlapping plan area, square drawing units.</param>
public sealed record Overlap(Guid First, Guid Second, double Area);

/// <summary>
/// Pairwise overlap test (E215, E312). Every pair whose bounds touch is measured, adjacent
/// segments included: neighbours share a cut line exactly, so their intersection area is
/// zero to rounding and falls under the tolerance, while a band that folds back onto its
/// neighbour is still caught.
/// </summary>
public static class OverlapDetector
{
    /// <summary>Returns every pair whose intersection area exceeds <paramref name="areaTolerance"/> square drawing units.</summary>
    /// <exception cref="InvalidOperationException">A boundary is not a simple polygon; validate boundaries first.</exception>
    public static IReadOnlyList<Overlap> Find(IReadOnlyList<Boundary> boundaries, double areaTolerance)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        var bounds = boundaries.Select(b => Polygon.Bounds(b.Tessellated)).ToArray();
        var result = new List<Overlap>();
        for (int i = 0; i < boundaries.Count; i++)
        {
            for (int j = i + 1; j < boundaries.Count; j++)
            {
                var (a0, a1) = bounds[i];
                var (b0, b1) = bounds[j];
                if (a0.X > b1.X || b0.X > a1.X || a0.Y > b1.Y || b0.Y > a1.Y) continue;
                double area = Polygon.IntersectionArea(boundaries[i].Tessellated, boundaries[j].Tessellated);
                if (area > areaTolerance) result.Add(new Overlap(boundaries[i].SegmentId, boundaries[j].SegmentId, area));
            }
        }
        return result;
    }
}

using System.Globalization;

namespace SheetVol.Core.Geometry;

/// <summary>
/// Boundary checks of HANDOFF.md 6.4 step 6: closed, enough vertices, no self-intersection,
/// positive area. The inner-radius check on arcs is made before a boundary is built (E201).
/// </summary>
public static class BoundaryValidator
{
    /// <summary>
    /// Checks a tessellated ring. Returns null when it passes, otherwise the reason in plain
    /// words (lower case, no trailing period) for use inside a problem message.
    /// </summary>
    /// <param name="tessellated">Ring in WCS drawing units; must be closed (first == last).</param>
    /// <param name="requireCounterClockwise">True for generated boundaries, whose signed area must be positive; false for user polylines, whose area must be non-zero in either direction.</param>
    /// <param name="minDistinctVertices">Smallest number of distinct vertices accepted.</param>
    public static string? Check(IReadOnlyList<Pt> tessellated, bool requireCounterClockwise, int minDistinctVertices)
    {
        ArgumentNullException.ThrowIfNull(tessellated);
        if (tessellated.Count < 4 || !Polygon.IsClosed(tessellated))
            return "the boundary is not closed";
        int distinct = Polygon.DistinctVertexCount(tessellated);
        if (distinct < minDistinctVertices)
            return $"the boundary has {distinct} distinct vertices; at least {minDistinctVertices} are required";
        if (Polygon.TryFindSelfIntersection(tessellated, out var at))
            return string.Create(CultureInfo.InvariantCulture, $"the boundary crosses itself near ({at.X:0.###}, {at.Y:0.###})");
        double area = Polygon.SignedArea(tessellated);
        if (requireCounterClockwise ? !(area > 0) : area == 0)
            return "the boundary has no positive area";
        return null;
    }
}

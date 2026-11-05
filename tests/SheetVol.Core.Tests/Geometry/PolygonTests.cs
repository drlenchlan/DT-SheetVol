using SheetVol.Core.Geometry;

namespace SheetVol.Core.Tests.Geometry;

public class PolygonTests
{
    private static Pt[] Square(double x, double y, double size) =>
        [new(x, y), new(x + size, y), new(x + size, y + size), new(x, y + size), new(x, y)];

    [Fact]
    public void Signed_area_is_positive_counter_clockwise()
    {
        var sq = Square(0, 0, 10);
        Assert.Equal(100, Polygon.SignedArea(sq), 12);
        Assert.Equal(-100, Polygon.SignedArea(sq.Reverse().ToArray()), 12);
    }

    [Fact]
    public void Area_keeps_precision_at_state_plane_coordinates()
    {
        // 3,100,000 E / 13,800,000 N: a 0.5 x 0.25 rectangle must still measure 0.125.
        var r = new Pt[] { new(3_100_000.0, 13_800_000.0), new(3_100_000.5, 13_800_000.0), new(3_100_000.5, 13_800_000.25), new(3_100_000.0, 13_800_000.25) };
        Assert.Equal(0.125, Polygon.SignedArea(r), 9);
    }

    [Fact]
    public void Bow_tie_self_intersects_and_square_does_not()
    {
        var bow = new Pt[] { new(0, 0), new(10, 10), new(10, 0), new(0, 10), new(0, 0) };
        Assert.True(Polygon.TryFindSelfIntersection(bow, out var at));
        Assert.Equal(5, at.X, 9);
        Assert.Equal(5, at.Y, 9);
        Assert.False(Polygon.TryFindSelfIntersection(Square(0, 0, 10), out _));
    }

    [Fact]
    public void Collinear_vertices_are_not_a_self_intersection()
    {
        var ring = new Pt[] { new(0, 0), new(5, 0), new(10, 0), new(10, 5), new(5, 5), new(0, 5), new(0, 0) };
        Assert.False(Polygon.TryFindSelfIntersection(ring, out _));
    }

    [Fact]
    public void A_vertex_touching_another_edge_is_a_self_intersection()
    {
        // The notch vertex (5, 0) lies on the bottom edge.
        var ring = new Pt[] { new(0, 0), new(10, 0), new(10, 10), new(5, 0), new(0, 10), new(0, 0) };
        Assert.True(Polygon.TryFindSelfIntersection(ring, out _));
    }

    [Fact]
    public void Intersection_area_of_squares()
    {
        Assert.Equal(25, Polygon.IntersectionArea(Square(0, 0, 10), Square(5, 5, 10)), 9);
        Assert.Equal(0, Polygon.IntersectionArea(Square(0, 0, 10), Square(20, 0, 10)), 12);
        Assert.Equal(0, Polygon.IntersectionArea(Square(0, 0, 10), Square(10, 0, 10)), 9);   // shared edge
        Assert.Equal(100, Polygon.IntersectionArea(Square(0, 0, 10), Square(-5, -5, 20)), 9);
    }

    [Fact]
    public void Intersection_area_with_a_concave_polygon_and_either_orientation()
    {
        // L-shape: 20 x 20 minus the 10 x 10 upper-right quarter, drawn clockwise.
        var l = new Pt[] { new(0, 0), new(0, 20), new(10, 20), new(10, 10), new(20, 10), new(20, 0), new(0, 0) };
        var sq = Square(5, 5, 10);   // covers 75 of the L and 25 of the missing quarter
        Assert.Equal(75, Polygon.IntersectionArea(l, sq), 9);
        Assert.Equal(75, Polygon.IntersectionArea(sq, l), 9);
    }

    [Fact]
    public void Tessellated_bulges_meet_the_mid_ordinate()
    {
        // Half disc of radius 50: (0,0) -> (100,0) with bulge 1 (counter-clockwise, below the chord), then the chord back.
        var verts = new PolyVertex[] { new(new Pt(0, 0), 1), new(new Pt(100, 0), 0) };
        var ring = Polygon.TessellateBulges(verts, closed: true, midOrdinate: 0.01);
        Assert.True(Polygon.IsClosed(ring));
        double exact = Math.PI * 50 * 50 / 2;
        double area = Polygon.Area(ring);
        Assert.True(area < exact && exact - area < 0.01 * Math.PI * 50, $"area {area} vs {exact}");
        Assert.All(ring, p => Assert.True(p.Y <= 1e-9));   // counter-clockwise from (0,0) to (100,0) bulges to the right of travel: below
        foreach (var p in ring.Skip(1).SkipLast(2))
            Assert.Equal(50, Math.Sqrt((p.X - 50) * (p.X - 50) + p.Y * p.Y), 9);
    }
}

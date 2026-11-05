using SheetVol.Core.Views;

namespace SheetVol.Core.Tests.Views;

/// <summary>Exact bulge geometry used by the oriented box and the clip transform.</summary>
public class BulgePolylineTests
{
    private static readonly ViewMathConfig Config = new();

    [Fact]
    public void Arc_center_and_radius_follow_the_AutoCAD_bulge_convention()
    {
        // Positive bulge = counter-clockwise; a 0.5 bulge on (0,0)-(2,0) has its center above the chord.
        Pt c = BulgePolyline.ArcCenter(new Pt(0, 0), new Pt(2, 0), 0.5);
        Assert.Equal(1, c.X, 1e-12);
        Assert.Equal(0.75, c.Y, 1e-12);
        Assert.Equal(1.25, BulgePolyline.ArcRadius(new Pt(0, 0), new Pt(2, 0), 0.5), 1e-12);
    }

    [Fact]
    public void Bounds_include_arc_extremes_inside_the_sweep()
    {
        // Upper half disc: counter-clockwise semicircle from (1,0) to (-1,0) (bulge 1), closed by the diameter.
        var halfDisc = new[] { new PolyVertex(new Pt(1, 0), 1), new PolyVertex(new Pt(-1, 0), 0) };
        Extents e = BulgePolyline.Bounds(halfDisc, Config);
        Assert.Equal(-1, e.MinX, 1e-12);
        Assert.Equal(1, e.MaxX, 1e-12);
        Assert.Equal(0, e.MinY, 1e-12);
        Assert.Equal(1, e.MaxY, 1e-12);

        // The same chord with a clockwise bulge sweeps below.
        var lowerHalf = new[] { new PolyVertex(new Pt(1, 0), -1), new PolyVertex(new Pt(-1, 0), 0) };
        Extents f = BulgePolyline.Bounds(lowerHalf, Config);
        Assert.Equal(-1, f.MinY, 1e-12);
        Assert.Equal(0, f.MaxY, 1e-12);
    }

    [Fact]
    public void Bounds_ignore_axis_extremes_outside_a_minor_arc()
    {
        // Quarter arc from (1,0) to (0,1) counter-clockwise: bulge tan(pi/8). No extreme lies strictly inside.
        var quarter = new[] { new PolyVertex(new Pt(1, 0), Math.Tan(Math.PI / 8)), new PolyVertex(new Pt(0, 1), 0), new PolyVertex(new Pt(0, 0), 0) };
        Extents e = BulgePolyline.Bounds(quarter, Config);
        Assert.Equal(0, e.MinX, 1e-12);
        Assert.Equal(1, e.MaxX, 1e-12);
        Assert.Equal(0, e.MinY, 1e-12);
        Assert.Equal(1, e.MaxY, 1e-12);
    }

    [Fact]
    public void Explicit_closing_vertex_is_accepted()
    {
        var implicitClose = new[] { new PolyVertex(new Pt(0, 0), 0), new PolyVertex(new Pt(4, 0), 0), new PolyVertex(new Pt(4, 2), 0), new PolyVertex(new Pt(0, 2), 0) };
        var explicitClose = implicitClose.Append(new PolyVertex(new Pt(0, 0), 0)).ToArray();
        Assert.Equal(BulgePolyline.Bounds(implicitClose, Config), BulgePolyline.Bounds(explicitClose, Config));
    }

    [Fact]
    public void Map_keeps_bulges_under_a_similarity()
    {
        var poly = new[] { new PolyVertex(new Pt(1, 0), 1), new PolyVertex(new Pt(-1, 0), 0) };
        var mapped = BulgePolyline.Map(poly, p => ViewMath.Rotate(new Pt(p.X * 2, p.Y * 2), Math.PI / 2));
        Assert.Equal(1, mapped[0].Bulge);
        Assert.Equal(0, mapped[0].Point.X, 1e-12);
        Assert.Equal(2, mapped[0].Point.Y, 1e-12);
        Extents e = BulgePolyline.Bounds(mapped, Config);
        Assert.Equal(-2, e.MinX, 1e-12);                 // rotated half disc now bulges to -X
        Assert.Equal(0, e.MaxX, 1e-12);
    }
}

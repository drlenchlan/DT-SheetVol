// A1 adapters, contract revision 3 (rev3-adapters). Owner: A1.
//
// rev 3 interim: the orchestrator switches the call site to the depth overloads after rev3-int3 merges.
//
// C5's integrator takes a base and a comparison triangle source. A volume-surface analysis has one
// source, the volume surface's TIN normalised to depth d = base - comparison
// (TriangleSourceAdapter.ReadVolumeDepth). Until C5's one-source overloads
// (OverlayIntegrator.IntegrateBand(ITriangleSource depth, ...), IntegrateSegment(ITriangleSource
// depth, ...)) land, the regen pipeline passes the depth source as the base and this flat plane at
// Z = 0 as the comparison: the integrator's d = depth - 0 is the depth exactly, so E1012 and the
// 2D-area check still compare like with like. Delete this file when the call site switches.

using SheetVol.Core;

namespace SheetVol.Civil3D.Adapters;

/// <summary>A flat Z = 0 plane: two triangles covering exactly the requested window (rev 3 interim, see the file header).</summary>
public sealed class ZeroPlaneTriangles : ITriangleSource
{
    /// <summary>The one instance; it holds no state.</summary>
    public static ZeroPlaneTriangles Instance { get; } = new();

    private ZeroPlaneTriangles()
    {
    }

    /// <inheritdoc />
    public IEnumerable<Tri3> Triangles(Pt windowMin, Pt windowMax)
    {
        double x0 = Math.Min(windowMin.X, windowMax.X), x1 = Math.Max(windowMin.X, windowMax.X);
        double y0 = Math.Min(windowMin.Y, windowMax.Y), y1 = Math.Max(windowMin.Y, windowMax.Y);
        if (!(x1 > x0) || !(y1 > y0)) return [];
        var a = new Pt3(x0, y0, 0);
        var b = new Pt3(x1, y0, 0);
        var c = new Pt3(x1, y1, 0);
        var d = new Pt3(x0, y1, 0);
        return [new Tri3(a, b, c), new Tri3(a, c, d)];
    }
}

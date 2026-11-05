// Code-built fixture surfaces from HANDOFF.md section 8 (EG, FG_A, FG_B, FG_D, EG_H, FG_H).
// Owner: C5. Pure Core so the unit suite runs T25 to T29 without Civil 3D, and so the Civil 3D
// fixture builder (FX) can add exactly these triangles to its TIN surfaces.
//
// The expected values in section 8 depend on the surfaces being exactly these shapes. Where a
// grid cell is not planar (only the FG_H transition cells, x = 1300 to 1310, |y| = 10 to 16)
// the diagonal changes the volume; the choice is stated below with its effect on T28.
//
// Coordinates are world coordinates on alignment S (station 0 at x = 0, left = +y), drawing units.

namespace SheetVol.Core.Volumes.Integrator.Fixtures;

/// <summary>How each rectangular grid cell is split into two triangles.</summary>
public enum GridDiagonal
{
    /// <summary>Every cell split from its (xmin, ymin) corner to its (xmax, ymax) corner.</summary>
    SouthwestToNortheast,

    /// <summary>
    /// Mirror-symmetric about y = 0: a cell on the +y side is split from (xmin, inner y) to
    /// (xmax, outer y), i.e. (xmin, ymin) to (xmax, ymax); a cell on the -y side from (xmin, ymax)
    /// to (xmax, ymin). "Inner" is the side nearer y = 0. Requires y = 0 to be a grid line when
    /// the grid crosses it.
    /// </summary>
    TowardCenterline,
}

/// <summary>Generators for the section 8 fixture surfaces.</summary>
public static class FixtureSurfaces
{
    /// <summary>EG elevation for fixtures A, B and D, drawing units.</summary>
    public const double EgElevation = 100.0;

    /// <summary>EG plan extent for fixtures A, B and D: (-200, -300) to (2200, 300).</summary>
    public static readonly (double MinX, double MinY, double MaxX, double MaxY) EgExtent = (-200, -300, 2200, 300);

    /// <summary>Fixture A pyramids: center x on y = 0, apex elevation. 90 x 90 base at z = 100.</summary>
    public static readonly IReadOnlyList<(double CenterX, double ApexZ)> PyramidsA =
        [(125, 99.0), (375, 100.5), (1125, 99.0), (1375, 100.5), (1750, 101.0)];

    /// <summary>Half the side of a fixture A pyramid base, drawing units.</summary>
    public const double PyramidHalfSide = 45.0;

    /// <summary>
    /// EG: flat z = 100 on the corners (-200,-300), (2200,-300), (2200,300), (-200,300).
    /// Two triangles split along (-200,-300)-(2200,300).
    /// </summary>
    public static TriangleListSource Eg() => Flat("EG", EgElevation, EgExtent.MinX, EgExtent.MinY, EgExtent.MaxX, EgExtent.MaxY);

    /// <summary>
    /// FG_D: EG raised by 1.00 everywhere (z = 101), same two triangles as <see cref="Eg"/>.
    /// Fill inside any boundary on the EG extent equals its plan area.
    /// </summary>
    public static TriangleListSource FgD() => Flat("FG_D", EgElevation + 1.0, EgExtent.MinX, EgExtent.MinY, EgExtent.MaxX, EgExtent.MaxY);

    /// <summary>A flat rectangle at elevation <paramref name="z"/>, two triangles split from (x0, y0) to (x1, y1).</summary>
    public static TriangleListSource Flat(string name, double z, double x0, double y0, double x1, double y1)
        => Grid(name, [x0, x1], [y0, y1], (_, _) => z, GridDiagonal.SouthwestToNortheast);

    /// <summary>
    /// FG_A: EG plus five square pyramids (<see cref="PyramidsA"/>), each a 90 x 90 base at z = 100
    /// centered on y = 0 with its apex at the center.
    /// Triangulation: grid lines x = -200, 2200 and every pyramid base edge (center +- 45), y = -300,
    /// -45, 45, 300. A pyramid cell is four triangles, apex to each pair of adjacent base corners
    /// (breaklines on the base edges and apex-to-corner). Every other cell is flat at z = 100,
    /// split southwest to northeast. The mesh is conforming (pyramid corners are grid nodes).
    /// Volumes: base area 8100 * height / 3 = 2700 ft3 = 100 CY per 1.0 of height.
    /// </summary>
    public static TriangleListSource FgA()
    {
        var xs = new List<double> { EgExtent.MinX, EgExtent.MaxX };
        foreach (var (cx, _) in PyramidsA) { xs.Add(cx - PyramidHalfSide); xs.Add(cx + PyramidHalfSide); }
        xs.Sort();
        double[] ys = [EgExtent.MinY, -PyramidHalfSide, PyramidHalfSide, EgExtent.MaxY];
        var tris = new List<Tri3>();
        for (int i = 0; i + 1 < xs.Count; i++)
        {
            for (int j = 0; j + 1 < ys.Length; j++)
            {
                double x0 = xs[i], x1 = xs[i + 1], y0 = ys[j], y1 = ys[j + 1];
                var sw = new Pt3(x0, y0, EgElevation); var se = new Pt3(x1, y0, EgElevation);
                var ne = new Pt3(x1, y1, EgElevation); var nw = new Pt3(x0, y1, EgElevation);
                (double CenterX, double ApexZ)? pyr = null;
                if (j == 1)
                    foreach (var p in PyramidsA)
                        if (p.CenterX - PyramidHalfSide == x0 && p.CenterX + PyramidHalfSide == x1) pyr = p;
                if (pyr is { } py)
                {
                    var apex = new Pt3(py.CenterX, 0.0, py.ApexZ);
                    tris.Add(new Tri3(sw, se, apex));
                    tris.Add(new Tri3(se, ne, apex));
                    tris.Add(new Tri3(ne, nw, apex));
                    tris.Add(new Tri3(nw, sw, apex));
                }
                else
                {
                    tris.Add(new Tri3(sw, se, ne));
                    tris.Add(new Tri3(sw, ne, nw));
                }
            }
        }
        return new TriangleListSource("FG_A", tris);
    }

    /// <summary>
    /// FG_B (ridge): EG plus breaklines y = -30 (z 100), y = 0 (z 101), y = +30 (z 100) from
    /// x = -100 to 2100. Cross-section 30 ft2 of fill. Grid lines x = -200, -100, 2100, 2200 and
    /// y = -300, -30, 0, 30, 300; node z = 101 on y = 0 for -100 &lt;= x &lt;= 2100, else 100;
    /// <see cref="GridDiagonal.TowardCenterline"/>. Every cell between x = -100 and 2100 is planar,
    /// so the diagonal does not matter there; the end cells (x &lt; -100, x &gt; 2100) slope down to
    /// EG and lie outside every fixture band.
    /// </summary>
    public static TriangleListSource FgB()
        => Grid("FG_B", [-200, -100, 2100, 2200], [-300, -30, 0, 30, 300],
                (x, y) => y == 0 && x >= -100 && x <= 2100 ? EgElevation + 1.0 : EgElevation,
                GridDiagonal.TowardCenterline);

    /// <summary>EG_H section: bed 96.0 for |y| &lt;= 10, 3:1 side slopes to 106.0 at |y| = 40, flat to |y| = 100.</summary>
    public static double EgHSection(double y)
    {
        double a = Math.Abs(y);
        if (a <= 10) return 96.0;
        if (a <= 40) return 96.0 + (a - 10.0) / 3.0;
        return 106.0;
    }

    /// <summary>FG_H section for 0 &lt;= x &lt;= 1300: bed 94.0 for |y| &lt;= 10, rising to meet EG_H at |y| = 16 (98.0), equal to EG_H beyond.</summary>
    public static double FgHSection(double y)
    {
        double a = Math.Abs(y);
        if (a <= 10) return 94.0;
        if (a <= 16) return 94.0 + 2.0 * (a - 10.0) / 3.0;
        return EgHSection(y);
    }

    /// <summary>Fixture H longitudinal grid lines (every section break, plus y = 0 for the symmetric diagonal).</summary>
    public static readonly IReadOnlyList<double> HOffsets = [-100, -40, -16, -10, 0, 10, 16, 40, 100];

    /// <summary>
    /// EG_H, x = 0 to 2000. Triangulated on the same grid as <see cref="FgH"/> (see there): where the
    /// two surfaces coincide their triangles are bitwise identical, so depth is exactly zero and
    /// the impacted length on T28 is exactly 310.
    /// </summary>
    /// <param name="maxCellLength">Optional refinement along x (not inside the 1300-1310 transition);
    /// the surface is unchanged because every refined cell is planar. Used for the performance test.</param>
    public static TriangleListSource EgH(double maxCellLength = double.PositiveInfinity)
        => Grid("EG_H", HStations(maxCellLength), HOffsets, (_, y) => EgHSection(y), GridDiagonal.TowardCenterline);

    /// <summary>
    /// FG_H, x = 0 to 2000: <see cref="FgHSection"/> for x &lt;= 1300, EG_H for x &gt;= 1310, the 10 ft
    /// between a transition. Triangulation: grid lines x = 0, 1300, 1310, 2000 and
    /// y = <see cref="HOffsets"/> (breaklines along every longitudinal line), cells split
    /// <see cref="GridDiagonal.TowardCenterline"/>.
    /// All cells are planar except the transition cells x = 1300 to 1310, |y| = 10 to 16, whose
    /// corner depths are 2, 0, 0, 0 (2 at (1300, +-10)). The chosen diagonal runs from (1300, +-10)
    /// to (1310, +-16), through the depth-2 corner, so both triangles carry depth (2, 0, 0) and each
    /// holds 30 ft2 * 2/3 = 20 ft3: 40 ft3 per side. The other diagonal would give 20 ft3 per side.
    /// Transition cut = bed 20 * 10 * 1 = 200 + 2 * 40 = 280 ft3. T28 volume = 300 * 52 + 280 =
    /// 15,880 ft3 = 588.148 CY (handoff band 577.78 to 597.04 CY).
    /// </summary>
    /// <param name="maxCellLength">Optional refinement along x, as for <see cref="EgH"/>.</param>
    public static TriangleListSource FgH(double maxCellLength = double.PositiveInfinity)
        => Grid("FG_H", HStations(maxCellLength), HOffsets,
                (x, y) => x <= 1300 ? FgHSection(y)
                        : x >= 1310 ? EgHSection(y)
                        : throw new InvalidOperationException("FG_H has no grid line inside the 1300-1310 transition."),
                GridDiagonal.TowardCenterline);

    private static List<double> HStations(double maxCellLength)
    {
        if (!(maxCellLength > 0)) throw new ArgumentOutOfRangeException(nameof(maxCellLength));
        var xs = new List<double>();
        void Run(double a, double b)
        {
            int n = double.IsPositiveInfinity(maxCellLength) ? 1 : Math.Max(1, (int)Math.Ceiling((b - a) / maxCellLength));
            for (int i = 0; i < n; i++) xs.Add(a + (b - a) * i / n);
        }
        Run(0, 1300);
        xs.Add(1300);   // transition cell is never refined
        Run(1310, 2000);
        xs.Add(2000);
        return xs;
    }

    /// <summary>
    /// A surface defined by node elevations on a rectangular grid. Each cell becomes two
    /// counter-clockwise triangles per <paramref name="diagonal"/>: a southwest-northeast split gives
    /// (SW, SE, NE) and (SW, NE, NW); a northwest-southeast split gives (SW, SE, NW) and (SE, NE, NW).
    /// Two grids with the same lines and diagonal rule produce bitwise-identical triangles wherever
    /// their node elevations agree.
    /// </summary>
    /// <param name="name">Surface name.</param>
    /// <param name="xs">Strictly increasing x grid lines, drawing units.</param>
    /// <param name="ys">Strictly increasing y grid lines, drawing units.</param>
    /// <param name="z">Elevation at a node (x, y), drawing units. Called only at nodes.</param>
    /// <param name="diagonal">Cell split rule.</param>
    public static TriangleListSource Grid(string name, IReadOnlyList<double> xs, IReadOnlyList<double> ys,
                                          Func<double, double, double> z, GridDiagonal diagonal)
    {
        ArgumentNullException.ThrowIfNull(xs);
        ArgumentNullException.ThrowIfNull(ys);
        ArgumentNullException.ThrowIfNull(z);
        if (xs.Count < 2 || ys.Count < 2) throw new ArgumentException("A grid needs at least two lines each way.");
        for (int i = 1; i < xs.Count; i++) if (!(xs[i] > xs[i - 1])) throw new ArgumentException("xs must be strictly increasing.");
        for (int j = 1; j < ys.Count; j++) if (!(ys[j] > ys[j - 1])) throw new ArgumentException("ys must be strictly increasing.");

        var node = new double[xs.Count, ys.Count];
        for (int i = 0; i < xs.Count; i++)
            for (int j = 0; j < ys.Count; j++)
                node[i, j] = z(xs[i], ys[j]);

        var tris = new List<Tri3>(2 * (xs.Count - 1) * (ys.Count - 1));
        for (int i = 0; i + 1 < xs.Count; i++)
        {
            for (int j = 0; j + 1 < ys.Count; j++)
            {
                double x0 = xs[i], x1 = xs[i + 1], y0 = ys[j], y1 = ys[j + 1];
                var sw = new Pt3(x0, y0, node[i, j]);
                var se = new Pt3(x1, y0, node[i + 1, j]);
                var ne = new Pt3(x1, y1, node[i + 1, j + 1]);
                var nw = new Pt3(x0, y1, node[i, j + 1]);
                bool swne = diagonal switch
                {
                    GridDiagonal.SouthwestToNortheast => true,
                    GridDiagonal.TowardCenterline when y0 >= 0 => true,
                    GridDiagonal.TowardCenterline when y1 <= 0 => false,
                    _ => throw new ArgumentException("TowardCenterline needs y = 0 as a grid line when the grid crosses it."),
                };
                if (swne)
                {
                    tris.Add(new Tri3(sw, se, ne));
                    tris.Add(new Tri3(sw, ne, nw));
                }
                else
                {
                    tris.Add(new Tri3(sw, se, nw));
                    tris.Add(new Tri3(se, ne, nw));
                }
            }
        }
        return new TriangleListSource(name, tris);
    }
}

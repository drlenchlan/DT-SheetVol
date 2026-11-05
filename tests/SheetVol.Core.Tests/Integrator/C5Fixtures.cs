// Extra fixtures for the E10xx rows, built with the Core grid generator so their triangles follow
// the same rules as Fixture H (bitwise-identical triangles wherever the surfaces coincide).

using SheetVol.Core.Volumes.Integrator.Fixtures;

namespace SheetVol.Core.Tests.Integrator;

internal static class C5Fixtures
{
    /// <summary>
    /// Channel of Fixture H's section with cut in two stretches (E1005, E1007). Grid lines
    /// x = 0, 90, 100, 300, 310, 590, 600, 800, 810, 1000; y = Fixture H offsets. Comparison has the
    /// FG_H section at x = 100, 300, 600, 800 and equals the base elsewhere, so each stretch is a
    /// 10 ft ramp in, 200 ft of full cut, a 10 ft ramp out.
    /// Depth is positive on the open intervals (90, 310) and (590, 810); the gap is 280.
    /// Volume per stretch: full 200 * 52 = 10,400; ramp out (cells 300-310, diagonal through the
    /// depth-2 corner) 200 + 2 * 40 = 280 as in FG_H; ramp in (cells 90-100, depth 2 at x = 100,
    /// the SW-NE diagonal on the +y side runs (90,10)-(100,16), not through (100,10)) bed 200 +
    /// 2 * 20 = 240. Stretch = 10,920 ft3; both = 21,840 ft3.
    /// </summary>
    public static (TriangleListSource Base, TriangleListSource Comparison) TwoStretches()
    {
        double[] xs = [0, 90, 100, 300, 310, 590, 600, 800, 810, 1000];
        var full = new HashSet<double> { 100, 300, 600, 800 };
        var b = FixtureSurfaces.Grid("EG_2S", xs, FixtureSurfaces.HOffsets, (_, y) => FixtureSurfaces.EgHSection(y), GridDiagonal.TowardCenterline);
        var c = FixtureSurfaces.Grid("FG_2S", xs, FixtureSurfaces.HOffsets,
            (x, y) => full.Contains(x) ? FixtureSurfaces.FgHSection(y) : FixtureSurfaces.EgHSection(y), GridDiagonal.TowardCenterline);
        return (b, c);
    }

    /// <summary>
    /// Fixture H's channel cut for x = 0 to 300 (transition 300 to 310), plus a pond in the
    /// overbank away from the channel (E1006): base nodes (710 or 790, 55 or 65) at 98.0, every
    /// other overbank node at 106.0, so the base dips below OHWM 100 inside x = 707.5 to 792.5
    /// (100 is reached 7.5/10 of the way down each 10 ft ramp). The comparison is
    /// <paramref name="pondCut"/> below the base at those four core nodes and equal elsewhere in
    /// the overbank, so depth is positive on x = (700, 800), y = (50, 70).
    /// Impacted stations: channel [0, 310], pond [707.5, 792.5].
    /// </summary>
    public static (TriangleListSource Base, TriangleListSource Comparison) ChannelAndPond(double pondCut)
    {
        double[] xs = [0, 300, 310, 700, 710, 790, 800, 1000];
        double[] ys = [-100, -40, -16, -10, 0, 10, 16, 40, 50, 55, 65, 70, 100];
        static bool Core(double x, double y) => (x == 710 || x == 790) && (y == 55 || y == 65);
        var b = FixtureSurfaces.Grid("EG_P", xs, ys,
            (x, y) => Core(x, y) ? 98.0 : FixtureSurfaces.EgHSection(y), GridDiagonal.TowardCenterline);
        var c = FixtureSurfaces.Grid("FG_P", xs, ys,
            (x, y) => Core(x, y) ? 98.0 - pondCut : x <= 300 ? FixtureSurfaces.FgHSection(y) : FixtureSurfaces.EgHSection(y),
            GridDiagonal.TowardCenterline);
        return (b, c);
    }
}

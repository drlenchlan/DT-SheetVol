using System.Globalization;

namespace SheetVol.Core.Geometry;

/// <summary>Invariant-culture text for problem messages.</summary>
internal static class Fmt
{
    public static string Num(double v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    public static string Deg(double radians) => (radians * 180 / Math.PI).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Display station via the alignment (station equations honored) when the raw station is
    /// on the alignment; a plain raw value otherwise, so the adapter is never asked to format
    /// a station outside its domain.
    /// </summary>
    public static string Station(IAlignmentGeometry a, double raw, double tol)
    {
        if (raw >= a.StartStation - tol && raw <= a.EndStation + tol)
            return a.FormatStation(Math.Clamp(raw, a.StartStation, a.EndStation));
        return "raw " + raw.ToString("0.00####", CultureInfo.InvariantCulture);
    }

    public static string Range(IAlignmentGeometry a, double tol) =>
        $"{Station(a, a.StartStation, tol)} to {Station(a, a.EndStation, tol)}";

    public static string Segment(IAlignmentGeometry a, SegmentDef s, double tol) =>
        $"Segment {s.Label} ({Station(a, s.StartStation, tol)} to {Station(a, s.EndStation, tol)})";
}

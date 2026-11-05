using System.Globalization;

namespace SheetVol.Core.Volumes;

/// <summary>Plan-area helpers for boundaries and the 2D-area method of HANDOFF.md 6.5.</summary>
public static class PlanArea
{
    /// <summary>
    /// Unsigned shoelace area of a closed polygon, square drawing units. Coordinates are taken
    /// relative to the first vertex so state-plane magnitudes do not cost precision. A repeated
    /// closing vertex (first == last) contributes nothing.
    /// </summary>
    public static double Polygon(IReadOnlyList<Pt> polygon)
    {
        if (polygon.Count < 3) return 0;
        Pt o = polygon[0];
        double twice = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            Pt a = polygon[i];
            Pt b = polygon[(i + 1) % polygon.Count];
            twice += (a.X - o.X) * (b.Y - o.Y) - (b.X - o.X) * (a.Y - o.Y);
        }
        return Math.Abs(twice) / 2;
    }

    /// <summary>
    /// 2D area from two bounded volumes against different datums (6.5):
    /// <c>|Net(d1) − Net(d2)| / |d2 − d1|</c>, Net = Cut − Fill. Inputs are raw cubic drawing
    /// units and datums in drawing units; the result is square drawing units. Probe P22 must
    /// confirm the method against Civil 3D before the adapter relies on it.
    /// </summary>
    public static Outcome<double> FromDatums(RawVolume atDatum1, double datum1, RawVolume atDatum2, double datum2)
    {
        if (!double.IsFinite(datum1) || !double.IsFinite(datum2) || datum1 == datum2)
            return Outcome<double>.Fail(new Problem(
                VolumeCodes.InvalidDatumPair, Severity.Error,
                string.Create(CultureInfo.InvariantCulture, $"2D area needs two different finite datums; got {datum1:R} and {datum2:R}.")));
        foreach (var (v, which) in new[] { (atDatum1, "first"), (atDatum2, "second") })
        {
            if (!IsValidRaw(v))
                return Outcome<double>.Fail(new Problem(
                    VolumeCodes.InvalidSourceValue, Severity.Error,
                    string.Create(CultureInfo.InvariantCulture,
                        $"Bounded volume at the {which} datum is not a pair of non-negative numbers (cut {v.Cut:R}, fill {v.Fill:R}).")));
        }
        double net1 = atDatum1.Cut - atDatum1.Fill;
        double net2 = atDatum2.Cut - atDatum2.Fill;
        return Outcome<double>.Ok(Math.Abs(net1 - net2) / Math.Abs(datum2 - datum1));
    }

    /// <summary>
    /// The same calculation reading the two volumes from sources the adapter built at each datum.
    /// </summary>
    public static Outcome<double> FromDatums(
        IVolumeSource atDatum1, double datum1, IVolumeSource atDatum2, double datum2, IReadOnlyList<Pt> closedPolygon)
        => FromDatums(atDatum1.Bounded(closedPolygon), datum1, atDatum2.Bounded(closedPolygon), datum2);

    /// <summary>True when both parts are finite and not negative, as the <see cref="RawVolume"/> contract requires.</summary>
    public static bool IsValidRaw(RawVolume v)
        => double.IsFinite(v.Cut) && double.IsFinite(v.Fill) && v.Cut >= 0 && v.Fill >= 0;
}

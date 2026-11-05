using SheetVol.Core.Volumes;

namespace SheetVol.Core.Tests.Volumes;

/// <summary>
/// An <see cref="IVolumeSource"/> that answers from a table keyed by polygon instance. It
/// decides what Civil 3D would return; the tests check what Core does with it. Counts calls
/// so tests can prove a display change does not recompute (E418).
/// </summary>
internal sealed class FakeVolumeSource : IVolumeSource
{
    private readonly Dictionary<IReadOnlyList<Pt>, (RawVolume Volume, double Area)> _answers = new(ReferenceEqualityComparer.Instance);

    public RawVolume SurfaceTotals { get; set; }

    public int BoundedCalls { get; private set; }

    public int AreaCalls { get; private set; }

    public void Answer(IReadOnlyList<Pt> polygon, RawVolume volume, double area) => _answers[polygon] = (volume, area);

    public RawVolume Bounded(IReadOnlyList<Pt> closedPolygon)
    {
        BoundedCalls++;
        return _answers.TryGetValue(closedPolygon, out var a) ? a.Volume : throw new KeyNotFoundException("Fake has no answer for this polygon.");
    }

    public double Area2D(IReadOnlyList<Pt> closedPolygon)
    {
        AreaCalls++;
        return _answers.TryGetValue(closedPolygon, out var a) ? a.Area : throw new KeyNotFoundException("Fake has no answer for this polygon.");
    }
}

/// <summary>
/// Segments on alignment S (straight along +x from station 0, left is +y), rectangular
/// boundaries, and a fake source per analysis. Volumes are given in cubic yards and stored in
/// cubic feet, as Civil 3D would report them for a drawing in feet.
/// </summary>
internal sealed class Scenario
{
    public const double CubicFeetPerCubicYard = 27;

    public List<SegmentDef> Segments { get; } = [];

    public List<Boundary> Boundaries { get; } = [];

    public List<IReadOnlyList<Pt>> Bands { get; } = [];

    public List<AnalysisInput> Analyses { get; } = [];

    public string? PartitionSkipReason { get; set; }

    /// <summary>Closed rectangle for stations s0..s1 and signed offsets (negative = left = +y).</summary>
    public static Pt[] Rect(double s0, double s1, double offLeft, double offRight)
        => [new(s0, -offRight), new(s1, -offRight), new(s1, -offLeft), new(s0, -offLeft), new(s0, -offRight)];

    public static double RectArea(double s0, double s1, double offLeft, double offRight) => (s1 - s0) * (offRight - offLeft);

    /// <summary>Adds segments for consecutive breaks; returns their boundaries in order.</summary>
    public Scenario WithBreaks(params double[] breaks)
    {
        for (int i = 0; i + 1 < breaks.Length; i++)
        {
            var id = Guid.NewGuid();
            Segments.Add(new SegmentDef(id, (i + 1).ToString(), breaks[i], breaks[i + 1], -100, 100));
            var rect = Rect(breaks[i], breaks[i + 1], -100, 100);
            Boundaries.Add(new Boundary(id, [], rect));
        }
        Bands.Add(Rect(breaks[0], breaks[^1], -100, 100));
        return this;
    }

    /// <summary>
    /// Adds an analysis whose fake returns the given cut/fill per segment (CY), full coverage
    /// unless <paramref name="areas"/> overrides, the band as the sum, and surface totals = band
    /// plus <paramref name="outsideCy"/>.
    /// </summary>
    public FakeVolumeSource AddAnalysis(
        string name, (double CutCy, double FillCy)[] perSegment,
        double cutFactor = 1, double fillFactor = 1, double[]? areas = null,
        (double CutCy, double FillCy) outsideCy = default, string baseSurface = "EG", string comparison = "FG_A",
        IReadOnlyDictionary<Guid, ImpactResult>? impacts = null, Guid? id = null)
    {
        var src = new FakeVolumeSource();
        double bandCut = 0, bandFill = 0;
        for (int i = 0; i < Segments.Count; i++)
        {
            var s = Segments[i];
            double area = areas?[i] ?? RectArea(s.StartStation, s.EndStation, s.OffsetLeft, s.OffsetRight);
            var v = new RawVolume(perSegment[i].CutCy * CubicFeetPerCubicYard, perSegment[i].FillCy * CubicFeetPerCubicYard);
            src.Answer(Boundaries[i].Tessellated, v, area);
            bandCut += v.Cut;
            bandFill += v.Fill;
        }
        foreach (var band in Bands) src.Answer(band, new RawVolume(bandCut / Bands.Count, bandFill / Bands.Count), 0);
        src.SurfaceTotals = new RawVolume(bandCut + outsideCy.CutCy * CubicFeetPerCubicYard, bandFill + outsideCy.FillCy * CubicFeetPerCubicYard);
        var def = new AnalysisDef(id ?? Guid.NewGuid(), name, baseSurface, comparison, cutFactor, fillFactor);
        Analyses.Add(new AnalysisInput(def, src, impacts));
        return src;
    }

    public ReconcileRequest Request(DrawingUnits? units = null)
        => new(Segments, Boundaries, Bands, Analyses, units ?? Feet, PartitionSkipReason);

    public ReconcileResult Reconcile(VolumeConfig? config = null, DrawingUnits? units = null)
        => Reconciler.Reconcile(Request(units), config ?? new VolumeConfig());

    public static DrawingUnits Feet => new(UnitSystem.Imperial, 1.0);

    public static DrawingUnits Metres => new(UnitSystem.Metric, 1.0);

    /// <summary>T2: Fixture A, breaks 0, 500, 1300, 1800, 2000, offsets (−100, +100). C/F 100/50, 100/0, 0/150, 0/0 CY.</summary>
    public static Scenario T2(double cutFactor = 1, double fillFactor = 1)
    {
        var s = new Scenario().WithBreaks(0, 500, 1300, 1800, 2000);
        s.AddAnalysis("EG vs FG_A", [(100, 50), (100, 0), (0, 150), (0, 0)], cutFactor, fillFactor);
        return s;
    }

    /// <summary>Station text like 5+00 for the tables.</summary>
    public static string Station(double raw)
    {
        long hundreds = (long)Math.Floor(raw / 100);
        double rest = raw - hundreds * 100;
        return hundreds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "+" + rest.ToString("00.##", System.Globalization.CultureInfo.InvariantCulture);
    }
}

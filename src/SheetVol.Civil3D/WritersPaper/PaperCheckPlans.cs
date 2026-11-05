// A3 work package. Owner: A3.
//
// Deterministic inputs for the SVWRITERSPAPER headless check: hand-built straight
// alignments, the band polygon source C3's ViewCalculator needs, an area-proportional
// volume source for C2's Reconciler, and the T18 plan (T15's and T17's views with C2
// tables). Check-only code: nothing here is used by the writer. The same functions run in
// the reopen session, so the plan does not have to be carried between processes.

using System.Globalization;
using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Views;
using SheetVol.Core.Volumes;

namespace SheetVol.Civil3D.WritersPaper;

/// <summary>A straight alignment from <c>Start</c> to <c>End</c>, station 0 at Start. Negative offset = left.</summary>
internal sealed class StraightAlignment(string name, Pt start, Pt end) : IAlignmentGeometry
{
    private readonly double _length = Math.Sqrt((end.X - start.X) * (end.X - start.X) + (end.Y - start.Y) * (end.Y - start.Y));

    public string Name => name;
    public double StartStation => 0;
    public double EndStation => _length;
    public IReadOnlyList<AlignSeg> Segments
    {
        get
        {
            double dir = Math.Atan2(end.Y - start.Y, end.X - start.X);
            return [new AlignSeg(SegKind.Line, 0, _length, start, end, dir, dir)];
        }
    }

    public Pt PointAt(double rawStation, double offset)
    {
        double ux = (end.X - start.X) / _length, uy = (end.Y - start.Y) / _length;
        // Right normal of the direction of travel is (uy, -ux); a negative offset goes left.
        return new Pt(start.X + rawStation * ux + offset * uy, start.Y + rawStation * uy - offset * ux);
    }

    public string FormatStation(double rawStation)
    {
        long hundredths = (long)Math.Round(rawStation * 100);
        long whole = hundredths / 100, frac = hundredths % 100;
        string tail = (whole % 100).ToString("00", CultureInfo.InvariantCulture) + (frac == 0 ? "" : "." + frac.ToString("00", CultureInfo.InvariantCulture));
        return (whole / 100).ToString(CultureInfo.InvariantCulture) + "+" + tail;
    }

    public bool TryParseStation(string text, out double rawStation, out string? error)
    {
        rawStation = 0;
        error = "parsing is not used by the paper check";
        return false;
    }
}

/// <summary>The offset band between two stations, sampled at <c>samples</c> stations per side, no closing vertex, no arcs.</summary>
internal sealed class BandPolygonSource(IAlignmentGeometry alignment, double offsetLeft, double offsetRight, int samples) : IDisplayPolygonSource
{
    public IReadOnlyList<PolyVertex> Build(double fromStation, double toStation)
    {
        var v = new List<PolyVertex>();
        for (int i = 0; i < samples; i++) v.Add(new PolyVertex(alignment.PointAt(fromStation + (toStation - fromStation) * i / (samples - 1), offsetLeft), 0));
        for (int i = samples - 1; i >= 0; i--) v.Add(new PolyVertex(alignment.PointAt(fromStation + (toStation - fromStation) * i / (samples - 1), offsetRight), 0));
        return v;
    }
}

/// <summary>A volume source whose cut and fill are fixed multiples of the polygon's area, so the partition check holds exactly.</summary>
internal sealed class AreaVolumeSource(double cutPerArea, double fillPerArea, IReadOnlyList<IReadOnlyList<Pt>> band) : IVolumeSource
{
    public RawVolume SurfaceTotals => new(band.Sum(Area) * cutPerArea, band.Sum(Area) * fillPerArea);
    public RawVolume Bounded(IReadOnlyList<Pt> closedPolygon) => new(Area(closedPolygon) * cutPerArea, Area(closedPolygon) * fillPerArea);
    public double Area2D(IReadOnlyList<Pt> closedPolygon) => Area(closedPolygon);

    internal static double Area(IReadOnlyList<Pt> p)
    {
        double a = 0;
        for (int i = 0; i + 1 < p.Count; i++) a += p[i].X * p[i + 1].Y - p[i + 1].X * p[i].Y;
        return Math.Abs(a) / 2;
    }
}

/// <summary>One planned check sheet: its alignment, segment, view and table.</summary>
internal sealed record CheckSheet(string Run, int Number, IAlignmentGeometry Alignment, SegmentDef Segment, ViewSpec View, TableModel? Table)
{
    public LayoutNameParts Parts => new(Run, Number, Segment.Label, Alignment.FormatStation(Segment.StartStation), Alignment.FormatStation(Segment.EndStation), Alignment.Name);
    public PaperSheetRequest Request(bool lockView = false) => new(Segment.Id, Parts, View, Table, lockView);
}

/// <summary>Names, ids and plans for the check.</summary>
internal static class CheckPlans
{
    internal const string TemplateLayout = "SV-SHEET";               // FX's template layout (FixtureSpec.Names.TemplateLayout)
    internal const string LayoutLargest = "SV-T-LARGEST";
    internal const string LayoutNoViewport = "SV-T-NOVP";
    internal const string LayoutNoAnchor = "SV-T-NOANCHOR";
    internal const string LayoutTwoPlan = "SV-T-TWOPLAN";
    internal const string TitleBlock = "SV-A3-TITLE";
    internal const string FrozenLayer = "SV-A3-FROZEN";
    internal const double OffsetLeft = -100, OffsetRight = 100;

    internal static readonly Guid RunT18 = Guid.Parse("a3a3a3a3-0018-4000-8000-000000000018");
    internal static readonly Guid SegT15 = Guid.Parse("a3a3a3a3-0015-4000-8000-000000000015");
    internal static readonly Guid SegT17 = Guid.Parse("a3a3a3a3-0017-4000-8000-000000000017");
    internal static readonly Guid RunE6 = Guid.Parse("a3a3a3a3-00e6-4000-8000-0000000000e6");
    internal static readonly Guid SegE6 = Guid.Parse("a3a3a3a3-00e6-4000-8000-0000000006e6");
    internal static readonly Guid RunE602 = Guid.Parse("a3a3a3a3-0602-4000-8000-000000000602");
    internal static readonly Guid SegE602 = Guid.Parse("a3a3a3a3-0602-4000-8000-000000006602");
    internal static readonly Guid RunE605 = Guid.Parse("a3a3a3a3-0605-4000-8000-000000000605");
    internal static readonly Guid SegE605 = Guid.Parse("a3a3a3a3-0605-4000-8000-000000006605");
    internal static readonly Guid RunRen = Guid.Parse("a3a3a3a3-0307-4000-8000-000000000307");
    internal static readonly Guid SegRenA = Guid.Parse("a3a3a3a3-0307-4000-8000-00000000000a");
    internal static readonly Guid SegRenB = Guid.Parse("a3a3a3a3-0307-4000-8000-00000000000b");
    internal static readonly Guid SegRenC = Guid.Parse("a3a3a3a3-0307-4000-8000-00000000000c");
    internal static readonly Guid RunMany = Guid.Parse("a3a3a3a3-0613-4000-8000-000000000613");
    internal static readonly Guid RunKeep = Guid.Parse("a3a3a3a3-0307-4000-8000-0000000000ee");
    internal static readonly Guid RunClipped = Guid.Parse("a3a3a3a3-602c-4000-8000-00000000602c");
    internal static readonly Guid SegClippedNone = Guid.Parse("a3a3a3a3-602c-4000-8000-000000000001");
    internal static readonly Guid SegClippedBand = Guid.Parse("a3a3a3a3-602c-4000-8000-000000000002");
    internal const string LayoutClipped = "SV-T-CLIPPED";
    internal static readonly Guid AnalysisFg = Guid.Parse("a3a3a3a3-aaaa-4000-8000-000000000001");
    internal static readonly Guid AnalysisFg2 = Guid.Parse("a3a3a3a3-aaaa-4000-8000-000000000002");

    internal static readonly StraightAlignment AlignS = new("S", new Pt(0, 0), new Pt(2000, 0));               // section 8: Alignment S
    internal static readonly StraightAlignment AlignN = new("N", new Pt(3000, 0), new Pt(3000, 2000));         // T17: north-running

    internal static string OutDir => Path.Combine(Path.GetTempPath(), "sheetvol");
    internal static string TemplatePath => Path.Combine(OutDir, "a3-template.dwg");
    internal static string SavedPath => Path.Combine(OutDir, "a3-t18.dwg");

    internal static PaperConfig Config(string layout = TemplateLayout) => new() { TemplatePath = TemplatePath, TemplateLayout = layout };
    internal static LayoutNamer Namer() => new(new LayoutNameConfig { NamePattern = "{RUN}-{N}" });
    internal static PaperWriter Writer(PaperConfig? config = null) => new(config ?? Config(), ReportConfig.Default.Placement, Namer());

    /// <summary>C3's view for a segment on a straight alignment with the band offsets and a boundary clip.</summary>
    internal static ViewSpec View(IAlignmentGeometry a, SegmentDef seg, double templateWidth, double templateHeight, double scale = 50, ClipMode clip = ClipMode.Boundary)
    {
        var options = new ViewOptions(ViewOrientation.AlongAlignment, ScaleMode.Fixed, scale, null, clip, 0);
        var r = new ViewCalculator(new ViewMathConfig()).Compute(a, new BandPolygonSource(a, seg.OffsetLeft, seg.OffsetRight, 3),
            new ViewRequest(seg, options, templateWidth, templateHeight));
        return r.Spec ?? throw new InvalidOperationException("C3 ViewCalculator refused the check view: " + string.Join(" | ", r.Problems.Select(p => $"{p.Code} {p.Message}")));
    }

    /// <summary>
    /// A view whose clip is not centred on the view centre: ViewOverlap widens the display polygon
    /// (clamped at the alignment start) while the clip is the boundary alone
    /// (ClipIncludesOverlap = false), so AutoCAD's clip refit moves the viewport and its ViewCenter (P12).
    /// </summary>
    internal static CheckSheet Asymmetric(string run, int number, Guid segment, double start, double length, double overlap, double templateWidth, double templateHeight)
    {
        var seg = new SegmentDef(segment, number.ToString(CultureInfo.InvariantCulture), start, start + length, OffsetLeft, OffsetRight);
        var options = new ViewOptions(ViewOrientation.AlongAlignment, ScaleMode.Fixed, 50, null, ClipMode.Boundary, overlap);
        var r = new ViewCalculator(new ViewMathConfig { ClipIncludesOverlap = false }).Compute(AlignS, new BandPolygonSource(AlignS, OffsetLeft, OffsetRight, 3),
            new ViewRequest(seg, options, templateWidth, templateHeight));
        var spec = r.Spec ?? throw new InvalidOperationException("C3 ViewCalculator refused the asymmetric view: " + string.Join(" | ", r.Problems.Select(p => $"{p.Code} {p.Message}")));
        return new CheckSheet(run, number, AlignS, seg, spec, null);
    }

    internal static readonly Guid RunE608C = Guid.Parse("a3a3a3a3-608c-4000-8000-00000000608c");
    internal static readonly Guid SegE608C = Guid.Parse("a3a3a3a3-608c-4000-8000-00000006608c");

    /// <summary>C2 per-sheet tables for the given sheets, through the real Reconciler and SegmentTableBuilder.</summary>
    internal static IReadOnlyDictionary<Guid, TableModel> Tables(IReadOnlyList<(IAlignmentGeometry A, SegmentDef Seg)> sheets, int analyses, double rate, bool onlySecond = false)
    {
        var boundaries = new List<Boundary>();
        var band = new List<IReadOnlyList<Pt>>();
        foreach (var (a, seg) in sheets)
        {
            var drawn = new BandPolygonSource(a, seg.OffsetLeft, seg.OffsetRight, 3).Build(seg.StartStation, seg.EndStation);
            var tess = drawn.Select(v => v.Point).Append(drawn[0].Point).ToList();
            boundaries.Add(new Boundary(seg.Id, drawn, tess));
            band.Add(tess);
        }
        var inputs = new List<AnalysisInput>
        {
            new(new AnalysisDef(AnalysisFg, "EG vs FG", "EG", "FG"), new AreaVolumeSource(0.054 * rate, 0.027 * rate, band)),
        };
        if (analyses > 1 || onlySecond)
            inputs.Add(new(new AnalysisDef(AnalysisFg2, "EG vs FG2", "EG", "FG2"), new AreaVolumeSource(0.0135 * rate, 0, band)));
        if (onlySecond) inputs.RemoveAt(0);

        var result = Reconciler.Reconcile(new ReconcileRequest(sheets.Select(s => s.Seg).ToList(), boundaries, band, inputs,
            new DrawingUnits(UnitSystem.Imperial, 1.0)), ReportConfig.Default.Volumes);
        if (!result.Succeeded) throw new InvalidOperationException("C2 Reconciler refused the check run: " + string.Join(" | ", result.Problems.Select(p => $"{p.Code} {p.Message}")));
        var tables = new Dictionary<Guid, TableModel>();
        foreach (var (a, seg) in sheets)
        {
            var t = SegmentTableBuilder.Build(result, seg.Id, AreaUnit.SquareFeet, a.FormatStation, ReportConfig.Default);
            tables[seg.Id] = t.Succeeded ? t.Value : throw new InvalidOperationException("C2 SegmentTableBuilder refused: " + string.Join(" | ", t.Problems.Select(p => p.Message)));
        }
        return tables;
    }

    /// <summary>T18: T15's view (alignment S, 0+00 to 5+00) and T17's view (north-running, 0+00 to 5+00), 50 model units per paper unit, boundary clip, C2 tables with two analyses.</summary>
    internal static IReadOnlyList<CheckSheet> T18(double templateWidth, double templateHeight)
    {
        var s15 = new SegmentDef(SegT15, "1", 0, 500, OffsetLeft, OffsetRight);
        var s17 = new SegmentDef(SegT17, "2", 0, 500, OffsetLeft, OffsetRight);
        var tables = Tables([(AlignS, s15), (AlignN, s17)], 2, 1.0);
        return
        [
            new CheckSheet("T18", 1, AlignS, s15, View(AlignS, s15, templateWidth, templateHeight), tables[SegT15]),
            new CheckSheet("T18", 2, AlignN, s17, View(AlignN, s17, templateWidth, templateHeight), tables[SegT17]),
        ];
    }

    /// <summary>One sheet on alignment S from <paramref name="start"/> for 500 ft, for the E-row checks.</summary>
    internal static CheckSheet OnS(string run, int number, Guid segment, double start, double templateWidth, double templateHeight,
                                   int analyses = 2, double rate = 1.0, double scale = 50, ClipMode clip = ClipMode.Boundary,
                                   double offset = 100, double length = 500, bool onlySecond = false)
    {
        var seg = new SegmentDef(segment, number.ToString(CultureInfo.InvariantCulture), start, start + length, -offset, offset);
        var table = analyses == 0 ? null : Tables([(AlignS, seg)], analyses, rate, onlySecond)[segment];
        return new CheckSheet(run, number, AlignS, seg, View(AlignS, seg, templateWidth, templateHeight, scale, clip), table);
    }
}

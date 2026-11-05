using System.Globalization;

namespace SheetVol.Core.Views;

/// <summary>
/// Builds the closed offset-band polygon of one segment between two raw stations, WCS
/// drawing units, arcs as bulges. The pipeline adapts the C1 boundary builder to this,
/// with the segment's own offsets, so the view math sees exactly the geometry the volumes use.
/// </summary>
public interface IDisplayPolygonSource
{
    /// <summary>Closed polygon of the band from <paramref name="fromStation"/> to <paramref name="toStation"/> (raw, from &lt; to).</summary>
    IReadOnlyList<PolyVertex> Build(double fromStation, double toStation);
}

/// <summary>Stable problem codes raised by the view math (HANDOFF.md 6.13).</summary>
public static class ViewProblemCodes
{
    /// <summary>The display polygon does not fit the template viewport at the scale (E309, E607).</summary>
    public const string DoesNotFit = "SV-E309";

    /// <summary>A scale is missing, zero, negative or not finite (E606: scale only from ModelUnitsPerPaperUnit).</summary>
    public const string InvalidScale = "SV-E606";

    /// <summary>The template viewport size is zero, negative or not finite (E601).</summary>
    public const string InvalidTemplateViewport = "SV-E601";

    /// <summary>ViewOverlap is negative or not finite (E304: overlap affects the viewport only).</summary>
    public const string InvalidOverlap = "SV-E304";

    /// <summary>The display polygon is empty, not finite or has no area (E202).</summary>
    public const string InvalidDisplayPolygon = "SV-E202";

    /// <summary>A generated layout name was sanitized, truncated or de-duplicated; the message reports the final name (E603). Severity Info.</summary>
    public const string LayoutNameChanged = "SV-E603";
}

/// <summary>One segment's view request.</summary>
/// <param name="Segment">The segment; raw stations with End &gt; Start.</param>
/// <param name="Options">The run's view options (orientation, scale mode, scales, clip, overlap).</param>
/// <param name="TemplatePaperWidth">Width of the template's plan viewport, paper units.</param>
/// <param name="TemplatePaperHeight">Height of the template's plan viewport, paper units.</param>
/// <param name="Overrides">Per-segment overrides; a scale override is used as a fixed scale.</param>
public sealed record ViewRequest(
    SegmentDef Segment,
    ViewOptions Options,
    double TemplatePaperWidth, double TemplatePaperHeight,
    SegmentOverrides? Overrides = null);

/// <summary>
/// Outcome of <see cref="ViewCalculator.Compute"/>. <see cref="Spec"/> is null whenever
/// <see cref="Problems"/> holds an error: nothing is applied from a view that does not fit.
/// Measurements are filled as far as the computation got; NaN marks a value not reached.
/// </summary>
/// <param name="Spec">The view to apply, or null on error. Its PaperWidth and PaperHeight are the template viewport size the view was fitted to.</param>
/// <param name="ModelUnitsPerPaperUnit">Chosen scale; on a fit failure, the scale that was tested last (the fixed one, or the largest standard scale).</param>
/// <param name="TwistRadians">View twist, radians counter-clockwise, in (−π, π].</param>
/// <param name="ModelCenter">View target in WCS, drawing units.</param>
/// <param name="ModelWidth">Oriented-box width, drawing units.</param>
/// <param name="ModelHeight">Oriented-box height, drawing units.</param>
/// <param name="RequiredPaperWidth">Oriented-box width at <paramref name="ModelUnitsPerPaperUnit"/>, paper units.</param>
/// <param name="RequiredPaperHeight">Oriented-box height at <paramref name="ModelUnitsPerPaperUnit"/>, paper units.</param>
/// <param name="DisplayStartStation">Raw start station of the display polygon (segment start − overlap, clamped to the alignment).</param>
/// <param name="DisplayEndStation">Raw end station of the display polygon (segment end + overlap, clamped to the alignment).</param>
/// <param name="OverlapClamped">True when the alignment's start or end cut the overlap short.</param>
/// <param name="MaxFittingLength">On a fit failure: the longest run of alignment from the segment start that fits at the tested scale, raw station units. Null otherwise.</param>
/// <param name="Problems">Errors and notes, each with a stable SV-Exxx code.</param>
public sealed record ViewResult(
    ViewSpec? Spec,
    double ModelUnitsPerPaperUnit,
    double TwistRadians,
    Pt ModelCenter,
    double ModelWidth, double ModelHeight,
    double RequiredPaperWidth, double RequiredPaperHeight,
    double DisplayStartStation, double DisplayEndStation,
    bool OverlapClamped,
    double? MaxFittingLength,
    IReadOnlyList<Problem> Problems)
{
    /// <summary>True when a view was produced.</summary>
    public bool Succeeded => Spec is not null;
}

/// <summary>
/// Computes one segment's <see cref="ViewSpec"/> per HANDOFF.md 6.6: display polygon
/// (boundary extended by ViewOverlap along the alignment, clamped at its ends), twist,
/// oriented box, scale (Fixed or FitStandard, per-segment override as fixed), clip in paper.
/// Pure: reads the alignment and the polygon source, writes nothing.
/// </summary>
public sealed class ViewCalculator
{
    private readonly ViewMathConfig _config;

    /// <summary>Creates a calculator with the given tunables.</summary>
    public ViewCalculator(ViewMathConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <summary>
    /// Raw station range of the display polygon: segment start − overlap to segment end +
    /// overlap, clamped to the alignment. <c>Clamped</c> is true when an end was cut short.
    /// </summary>
    public static (double From, double To, bool Clamped) DisplayRange(
        IAlignmentGeometry alignment, double startStation, double endStation, double viewOverlap)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        double from = startStation - viewOverlap, to = endStation + viewOverlap;
        bool clamped = false;
        if (from < alignment.StartStation) { from = alignment.StartStation; clamped = true; }
        if (to > alignment.EndStation) { to = alignment.EndStation; clamped = true; }
        return (from, to, clamped);
    }

    /// <summary>Computes the view for one segment. See the type summary.</summary>
    public ViewResult Compute(IAlignmentGeometry alignment, IDisplayPolygonSource polygons, ViewRequest request)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        ArgumentNullException.ThrowIfNull(polygons);
        ArgumentNullException.ThrowIfNull(request);
        SegmentDef seg = request.Segment;
        ViewOptions opt = request.Options ?? throw new ArgumentException("Options are required.", nameof(request));
        if (!(seg.EndStation > seg.StartStation))
            throw new ArgumentException($"Segment {seg.Id} has End <= Start; the planner guarantees End > Start.", nameof(request));

        string label = SegmentName(alignment, seg);
        double tw = request.TemplatePaperWidth, th = request.TemplatePaperHeight;
        var problems = new List<Problem>();

        if (!(IsPositive(tw) && IsPositive(th)))
            return Fail(problems, new Problem(ViewProblemCodes.InvalidTemplateViewport, Severity.Error,
                $"{label}: the template plan viewport is {N(tw)} x {N(th)} paper units; both sides must be positive.",
                seg.Id, "Check the size of the template's plan viewport."));

        if (!(double.IsFinite(opt.ViewOverlap) && opt.ViewOverlap >= 0))
            return Fail(problems, new Problem(ViewProblemCodes.InvalidOverlap, Severity.Error,
                $"{label}: ViewOverlap is {N(opt.ViewOverlap)}; it must be zero or positive drawing units.",
                seg.Id, "Set ViewOverlap to zero or a positive length."));

        // Scale candidates, most detailed (smallest model units per paper unit) first.
        double? overrideScale = request.Overrides?.ModelUnitsPerPaperUnit;
        bool fixedScale = overrideScale is not null || opt.ScaleMode == ScaleMode.Fixed;
        IReadOnlyList<double> candidates = overrideScale is double o ? [o]
            : opt.ScaleMode == ScaleMode.Fixed ? [opt.ModelUnitsPerPaperUnit]
            : (opt.StandardScales ?? []).Distinct().Order().ToArray();
        if (candidates.Count == 0)
            return Fail(problems, new Problem(ViewProblemCodes.InvalidScale, Severity.Error,
                $"{label}: ScaleMode is FitStandard but StandardScales is empty.",
                seg.Id, "List the standard scales as model units per paper unit, for example 20, 30, 40, 50, 60, 100."));
        foreach (double c in candidates)
            if (!IsPositive(c))
                return Fail(problems, new Problem(ViewProblemCodes.InvalidScale, Severity.Error,
                    $"{label}: scale {N(c)} model units per paper unit is not a positive number. The scale comes only from ModelUnitsPerPaperUnit; units are never inferred.",
                    seg.Id, "Set ModelUnitsPerPaperUnit, for example 50 for 1\"=50' with feet and inches."));

        var (from, to, clamped) = DisplayRange(alignment, seg.StartStation, seg.EndStation, opt.ViewOverlap);
        IReadOnlyList<PolyVertex> display = polygons.Build(from, to);
        double twist = ViewMath.Twist(alignment, seg.StartStation, seg.EndStation, opt.Orientation, _config);

        if (!IsUsablePolygon(display, out OrientedBox box, twist))
            return new ViewResult(null, double.NaN, twist, new Pt(double.NaN, double.NaN), double.NaN, double.NaN,
                double.NaN, double.NaN, from, to, clamped, null,
                [new Problem(ViewProblemCodes.InvalidDisplayPolygon, Severity.Error,
                    $"{label}: the display polygon from {alignment.FormatStation(from)} to {alignment.FormatStation(to)} is empty, not finite or has no area.",
                    seg.Id, "Check the segment's offsets and the boundary validation messages.")]);

        double chosen = double.NaN;
        foreach (double c in candidates)
            if (ViewMath.Fits(box, c, tw, th, _config)) { chosen = c; break; }

        if (double.IsNaN(chosen))
        {
            double tested = candidates[^1];
            double maxLen = MaxFittingLength(alignment, polygons, seg, opt, tested, tw, th);
            string need = $"needs {N(box.Width / tested)} x {N(box.Height / tested)} paper units at {N(tested)} model units per paper unit; the template viewport is {N(tw)} x {N(th)}";
            string scaleText = fixedScale
                ? $"{label} {need}."
                : $"{label}: none of the standard scales {string.Join(", ", candidates.Select(N))} fits; at the largest it {need}.";
            string lengthText = maxLen > 0
                ? $" At that scale at most {N(maxLen)} drawing units of alignment from {alignment.FormatStation(seg.StartStation)} fit."
                : " No length of this band fits at that scale: the offset band alone is too wide for the viewport.";
            string fix = fixedScale
                ? "Use ScaleMode FitStandard, a larger ModelUnitsPerPaperUnit, or a shorter segment."
                : "Add a larger standard scale, narrow the offsets, or shorten the segment.";
            problems.Add(new Problem(ViewProblemCodes.DoesNotFit, Severity.Error, scaleText + lengthText, seg.Id, fix));
            return new ViewResult(null, tested, twist, box.ModelCenter, box.Width, box.Height,
                box.Width / tested, box.Height / tested, from, to, clamped, maxLen, problems);
        }

        IReadOnlyList<PolyVertex>? clip = null;
        if (opt.Clip == ClipMode.Boundary)
        {
            IReadOnlyList<PolyVertex> clipModel = _config.ClipIncludesOverlap
                ? display
                : polygons.Build(seg.StartStation, seg.EndStation);
            clip = ViewMath.ToPaper(clipModel, box.ModelCenter, twist, chosen);
        }

        var spec = new ViewSpec(seg.Id, box.ModelCenter, twist, chosen, tw, th, clip);
        return new ViewResult(spec, chosen, twist, box.ModelCenter, box.Width, box.Height,
            box.Width / chosen, box.Height / chosen, from, to, clamped, null, problems);
    }

    /// <summary>
    /// Longest length of alignment from the segment start whose display polygon fits at the
    /// scale, raw station units, found by bisection between <see cref="ViewMathConfig.LengthSearchTolerance"/>
    /// and the segment length. Exact for straight runs; on curves it is a length that fits.
    /// Zero when even the shortest length does not fit (the band is too wide).
    /// </summary>
    public double MaxFittingLength(IAlignmentGeometry alignment, IDisplayPolygonSource polygons, SegmentDef segment,
                                   ViewOptions options, double modelUnitsPerPaperUnit, double paperWidth, double paperHeight)
    {
        double start = segment.StartStation;
        double full = segment.EndStation - start;

        bool FitsLength(double length)
        {
            double end = start + length;
            var (from, to, _) = DisplayRange(alignment, start, end, options.ViewOverlap);
            double twist = ViewMath.Twist(alignment, start, end, options.Orientation, _config);
            OrientedBox b = ViewMath.Box(polygons.Build(from, to), twist, _config);
            return ViewMath.Fits(b, modelUnitsPerPaperUnit, paperWidth, paperHeight, _config);
        }

        double lo = Math.Min(_config.LengthSearchTolerance, full);
        if (!FitsLength(lo)) return 0;
        double hi = full;
        if (FitsLength(hi)) return hi;
        for (int i = 0; i < _config.MaxLengthSearchIterations && hi - lo > _config.LengthSearchTolerance; i++)
        {
            double mid = (lo + hi) / 2;
            if (FitsLength(mid)) lo = mid; else hi = mid;
        }
        return lo;
    }

    private bool IsUsablePolygon(IReadOnlyList<PolyVertex>? polygon, out OrientedBox box, double twist)
    {
        box = default;
        if (polygon is null || polygon.Count < 2) return false;
        foreach (PolyVertex v in polygon)
            if (!double.IsFinite(v.Point.X) || !double.IsFinite(v.Point.Y) || !double.IsFinite(v.Bulge)) return false;
        box = ViewMath.Box(polygon, twist, _config);
        return box.Width > 0 && box.Height > 0;
    }

    private static ViewResult Fail(List<Problem> problems, Problem problem)
    {
        problems.Add(problem);
        return new ViewResult(null, double.NaN, double.NaN, new Pt(double.NaN, double.NaN), double.NaN, double.NaN,
            double.NaN, double.NaN, double.NaN, double.NaN, false, null, problems);
    }

    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;

    private static string SegmentName(IAlignmentGeometry alignment, SegmentDef seg)
        => $"Segment '{seg.Label}' ({alignment.FormatStation(seg.StartStation)} to {alignment.FormatStation(seg.EndStation)})";

    private string N(double value) => value.ToString(_config.MessageNumberFormat, CultureInfo.InvariantCulture);
}

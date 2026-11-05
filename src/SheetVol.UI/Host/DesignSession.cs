// Design-time ISheetVolSession: lets SVDIALOG open, and UiSmoke render, before A4's
// database-side session exists. Adapted from C4's test fake
// (tests/SheetVol.Core.Tests/Presentation/FakeSheetVolSession.cs), which the UI assembly
// cannot reference. Same behaviour where it matters for the dialog: T2's run preloaded,
// a small subset of the 6.3 grammar, GUIDs carried forward by index, per-segment offset
// overrides honoured, SV-E201 on the segment at CurveSegmentIndex when its widest offset
// reaches CurveRadius, T2 cut/fill as canned results. Added for design work: real
// rectangular boundaries on a straight alignment along +X (so the host can highlight and
// zoom in fake mode), a stepped regen with visible progress and cancellation, canned
// HCFCD impacts, and an export that writes nothing and says so.
// No Autodesk types: UiSmoke runs this outside AutoCAD.

using System.Globalization;
using SheetVol.Core;

namespace SheetVol.UI.Host;

/// <summary>
/// A deterministic <see cref="ISheetVolSession"/> loaded with T2 (HANDOFF.md section 8):
/// alignment S from station 0 to 2000 along +X, breaks <c>0, 500, 1300, 1800, 2000</c>,
/// offsets ±100, EG vs FG_A, cut/fill 100/50, 100/0, 0/150, 0/0 CY. Design aid only: no
/// value it returns comes from a drawing.
/// </summary>
internal sealed class DesignSession : ISheetVolSession
{
    /// <summary>T2 cut/fill per segment in CY.</summary>
    public static readonly (double Cut, double Fill)[] T2CutFill = [(100, 50), (100, 0), (0, 150), (0, 0)];

    /// <summary>T2's break list.</summary>
    public const string T2BreakSpec = "0, 500, 1300, 1800, 2000";

    private const string DesignCode = "SV-E900";
    private const string DesignExportMessage = "Design fixture: no files are written.";

    private readonly Func<DrawingInfo?> _drawing;
    private EventHandler? _sourcesChanged;
    private int _nextId;
    private RegenResult? _lastSucceeded;

    /// <summary>Creates the session with T2's run stored as <paramref name="runName"/>.</summary>
    /// <param name="runName">Run name the dialog edits.</param>
    /// <param name="drawing">Drawing identity and units; the host passes the active document, UiSmoke a fixed name.</param>
    /// <param name="regenSteps">Progress steps per regen.</param>
    /// <param name="regenStepDelay">Delay per step; zero completes synchronously.</param>
    public DesignSession(string runName, Func<DrawingInfo?> drawing, int regenSteps = 1, TimeSpan regenStepDelay = default)
    {
        _drawing = drawing ?? throw new ArgumentNullException(nameof(drawing));
        RegenSteps = Math.Max(1, regenSteps);
        RegenStepDelay = regenStepDelay;
        Runs[runName] = T2Run(runName);
    }

    /// <summary>Alignment names offered.</summary>
    public List<string> Alignments { get; } = ["S", "C"];

    /// <summary>TIN surface names offered.</summary>
    public List<string> Surfaces { get; } = ["EG", "FG_A", "FG_B"];

    /// <summary>Stored runs by name.</summary>
    public Dictionary<string, RunDefinition> Runs { get; } = [];

    /// <summary>Alignment start station.</summary>
    public double AlignmentStart { get; set; }

    /// <summary>Alignment end station.</summary>
    public double AlignmentEnd { get; set; } = 2000;

    /// <summary>Row index (0-based) that sits on the curve for SV-E201.</summary>
    public int CurveSegmentIndex { get; set; } = 1;

    /// <summary>Radius of that curve.</summary>
    public double CurveRadius { get; set; } = 300;

    /// <summary>Progress steps per regen.</summary>
    public int RegenSteps { get; set; }

    /// <summary>Delay per progress step.</summary>
    public TimeSpan RegenStepDelay { get; set; }

    /// <summary>Highlight calls, newest last (UiSmoke checks the close clears it).</summary>
    public List<Guid?> Highlights { get; } = [];

    /// <summary>Tracker status returned by <see cref="GetRunStatus"/>.</summary>
    public RunStatus Status { get; set; } = new(false, null);

    /// <summary>Command state returned by <see cref="IsCommandActive"/>.</summary>
    public bool CommandActive { get; set; }

    /// <summary>T2's run on alignment S with offsets ±100 and one analysis, EG vs FG_A.</summary>
    public static RunDefinition T2Run(string name, string breakSpec = T2BreakSpec)
        => new(
            name, "S", SegmentDriver.Stations, breakSpec, null,
            -100, 100,
            [new AnalysisDef(new Guid("aaaaaaaa-0000-0000-0000-000000000001"), "EG vs FG_A", "EG", "FG_A")],
            [], [], new Dictionary<Guid, SegmentOverrides>(), new ViewOptions(),
            Draw: new DrawOptions());

    // ---- Rev 2 host members ------------------------------------------------------------

    /// <inheritdoc/>
    public DrawingInfo? ActiveDrawing => _drawing();

    /// <inheritdoc/>
    public bool IsCommandActive => CommandActive;

    /// <inheritdoc/>
    public string FormatStation(string alignmentName, double rawStation)
    {
        var scale = ActiveDrawing?.Units == UnitSystem.Metric ? 1000 : 100;
        var major = Math.Floor(rawStation / scale);
        var minor = rawStation - major * scale;
        var minorFormat = scale == 1000 ? "000.00" : "00.00";
        return major.ToString("0", CultureInfo.InvariantCulture) + "+" + minor.ToString(minorFormat, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public (double Start, double End)? StationRange(string alignmentName)
        => Alignments.Contains(alignmentName) ? (AlignmentStart, AlignmentEnd) : null;

    /// <inheritdoc/>
    public RunStatus GetRunStatus(string runName) => Status;

    /// <inheritdoc/>
    public Task<RegenResult?> ExportAsync(RunDefinition run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_lastSucceeded?.RunName != run.Name) return Task.FromResult<RegenResult?>(null);
        var note = new Problem(DesignCode, Severity.Warning, DesignExportMessage);
        return Task.FromResult<RegenResult?>(_lastSucceeded with { CsvPath = null, JsonPath = null, Problems = [note] });
    }

    // ---- Engine members ----------------------------------------------------------------

    /// <inheritdoc/>
    public IReadOnlyList<string> AlignmentNames => Alignments;

    /// <inheritdoc/>
    public IReadOnlyList<string> TinSurfaceNames => Surfaces;

    /// <inheritdoc/>
    public event EventHandler SourcesChanged
    {
        add => _sourcesChanged += value;
        remove => _sourcesChanged -= value;
    }

    /// <summary>Raises <see cref="SourcesChanged"/>, as the engine does after an external change.</summary>
    public void RaiseSourcesChanged() => _sourcesChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc/>
    public RunDefinition? LoadRun(string runName) => Runs.TryGetValue(runName, out var run) ? run : null;

    /// <inheritdoc/>
    public ValidationResult Validate(RunDefinition run) => Plan(run);

    /// <inheritdoc/>
    public async Task<RegenResult> RegenerateAsync(RunDefinition run, IProgress<double> progress, CancellationToken ct)
    {
        for (var step = 1; step <= RegenSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            if (RegenStepDelay > TimeSpan.Zero) await Task.Delay(RegenStepDelay, ct);
            progress.Report((double)step / RegenSteps);
        }
        ct.ThrowIfCancellationRequested();
        var plan = Plan(run);
        var result = BuildResult(run, plan);
        Runs[run.Name] = run with { KnownSegments = plan.Segments };
        _lastSucceeded = result;
        Status = new RunStatus(false, result);
        return result;
    }

    /// <inheritdoc/>
    public void Highlight(Guid? segmentId) => Highlights.Add(segmentId);

    /// <inheritdoc/>
    public void ZoomTo(Guid segmentId)
    {
    }

    /// <inheritdoc/>
    public Task<string?> PickAlignmentAsync() => Task.FromResult<string?>(Alignments.FirstOrDefault());

    /// <inheritdoc/>
    public Task<double?> PickStationAsync(string alignmentName) => Task.FromResult<double?>(null);

    /// <summary>Raw station of a WCS point on the straight design alignment, or null when it is outside the station range.</summary>
    public double? StationAt(Pt point)
        => point.X >= AlignmentStart && point.X <= AlignmentEnd ? point.X : null;

    // ---- Results -------------------------------------------------------------------------

    private RegenResult BuildResult(RunDefinition run, ValidationResult plan)
    {
        var rows = new List<SegmentResult>();
        for (var a = 0; a < run.Analyses.Count; a++)
        {
            var analysis = run.Analyses[a];
            for (var i = 0; i < plan.Segments.Count; i++)
            {
                var s = plan.Segments[i];
                var (cut, fill) = i < T2CutFill.Length ? T2CutFill[i] : (0, 0);
                cut *= a + 1;
                fill *= a + 1;
                var area = (s.EndStation - s.StartStation) * (s.OffsetRight - s.OffsetLeft);
                var impact = run.Preset == ReportPreset.HcfcdSegment ? Impact(s, cut, analysis.Name) : null;
                rows.Add(new SegmentResult(
                    s.Id, s.Label, s.StartStation, s.EndStation, analysis.Id,
                    area, 1.0, new RawVolume(cut * 27, fill * 27),
                    cut * analysis.CutFactor, fill * analysis.FillFactor, cut * analysis.CutFactor - fill * analysis.FillFactor,
                    impact, SegmentStatus.Current, []));
            }
        }
        return new RegenResult(true, run.Name, rows, [], [], true, TimeSpan.FromSeconds(1.25), null, null);
    }

    /// <summary>Canned HCFCD quantities: segments with cut have cut below OHWM over their middle 60%, 40 ft wide.</summary>
    private static ImpactResult Impact(SegmentDef s, double cutCy, string material)
    {
        if (cutCy <= 0) return new ImpactResult(s.Id, material, 0, 0, 0, [], 0, 0, false);
        var length = s.EndStation - s.StartStation;
        var from = s.StartStation + 0.2 * length;
        var to = s.StartStation + 0.8 * length;
        var impacted = to - from;
        return new ImpactResult(s.Id, material, impacted, impacted * 40, cutCy * 27 * 0.4, [(from, to)], cutCy * 27 * 0.6, 0, false);
    }

    // ---- Planning ------------------------------------------------------------------------

    private ValidationResult Plan(RunDefinition run)
    {
        var problems = new List<Problem>();
        if (!Alignments.Contains(run.AlignmentName))
        {
            problems.Add(new Problem("SV-E213", Severity.Error, $"Alignment '{run.AlignmentName}' was not found."));
            return new ValidationResult(false, [], [], problems);
        }
        if (!TryParse(run.BreakSpec, out var breaks, out var skips, out var bad))
        {
            problems.Add(new Problem("SV-E301", Severity.Error, $"'{bad}' is not a station.", null, "Type a station such as 5+00 or 500."));
            return new ValidationResult(false, [], [], problems);
        }
        var outside = breaks.FirstOrDefault(b => b < AlignmentStart - 1e-6 || b > AlignmentEnd + 1e-6, double.NaN);
        if (!double.IsNaN(outside))
        {
            problems.Add(new Problem("SV-E305", Severity.Error,
                $"Station {outside.ToString(CultureInfo.InvariantCulture)} is outside the alignment ({AlignmentStart} to {AlignmentEnd})."));
            return new ValidationResult(false, [], [], problems);
        }
        if (run.OffsetLeft >= run.OffsetRight)
            problems.Add(new Problem("SV-E210", Severity.Error, "The left offset must be less than the right offset."));

        var segments = new List<SegmentDef>();
        for (var i = 0; i + 1 < breaks.Count; i++)
        {
            var (a, b) = (breaks[i], breaks[i + 1]);
            if (skips.Any(s => s.From <= a + 1e-6 && b <= s.To + 1e-6)) continue;
            var index = segments.Count;
            var id = index < run.KnownSegments.Count ? run.KnownSegments[index].Id : NewId();
            run.Overrides.TryGetValue(id, out var overrides);
            segments.Add(new SegmentDef(id, (index + 1).ToString(CultureInfo.InvariantCulture), a, b,
                overrides?.OffsetLeft ?? run.OffsetLeft, overrides?.OffsetRight ?? run.OffsetRight));
        }

        if (CurveSegmentIndex < segments.Count)
        {
            var s = segments[CurveSegmentIndex];
            var widest = Math.Max(Math.Abs(s.OffsetLeft), Math.Abs(s.OffsetRight));
            if (widest >= CurveRadius)
                problems.Add(new Problem("SV-E201", Severity.Error,
                    $"Segment {s.Label}: offset {widest:0.00} on the inside of the curve at {(s.StartStation + s.EndStation) / 2:0.00} reaches the radius {CurveRadius:0.00}. Maximum usable offset is {CurveRadius - 0.01:0.00}.",
                    s.Id, $"Use an offset below {CurveRadius:0.00} on this segment."));
        }

        var errorIds = problems.Where(p => p.Severity == Severity.Error && p.SegmentId is not null).Select(p => p.SegmentId!.Value).ToHashSet();
        var boundaries = segments.Where(s => !errorIds.Contains(s.Id)).Select(Rectangle).ToList();
        return new ValidationResult(problems.All(p => p.Severity != Severity.Error), segments, boundaries, problems);
    }

    /// <summary>The segment's band on the straight design alignment: x = station, y = −offset (negative offset is left, +y).</summary>
    private static Boundary Rectangle(SegmentDef s)
    {
        Pt[] corners =
        [
            new(s.StartStation, -s.OffsetRight), new(s.EndStation, -s.OffsetRight),
            new(s.EndStation, -s.OffsetLeft), new(s.StartStation, -s.OffsetLeft),
        ];
        return new Boundary(s.Id, corners.Select(p => new PolyVertex(p, 0)).ToList(), [.. corners, corners[0]]);
    }

    private Guid NewId() => new(++_nextId, 0, 0, [0, 0, 0, 0, 0, 0, 0, 2]);

    private bool TryParse(string spec, out List<double> breaks, out List<(double From, double To)> skips, out string bad)
    {
        breaks = [];
        skips = [];
        bad = string.Empty;
        foreach (var raw in spec.Split(','))
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;
            if (token.StartsWith("skip ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = token[5..].Split("..");
                if (parts.Length != 2 || !TryStation(parts[0], out var a) || !TryStation(parts[1], out var b)) { bad = token; return false; }
                skips.Add((a, b));
                breaks.Add(a);
                breaks.Add(b);
                continue;
            }
            if (token.Contains(".."))
            {
                var parts = token.Split("..");
                if (parts.Length != 2 || !TryStation(parts[0], out var a)) { bad = token; return false; }
                var rest = parts[1];
                var at = rest.IndexOfAny(['@', '/']);
                if (at < 0 || !TryStation(rest[..at], out var b)
                    || !double.TryParse(rest[(at + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0)
                {
                    bad = token;
                    return false;
                }
                if (rest[at] == '@')
                {
                    for (var x = a; x < b - 1e-6; x += n) breaks.Add(x);
                }
                else
                {
                    for (var k = 0; k < (int)n; k++) breaks.Add(a + (b - a) * k / n);
                }
                breaks.Add(b);
                continue;
            }
            if (!TryStation(token, out var station)) { bad = token; return false; }
            breaks.Add(station);
        }
        breaks = breaks.OrderBy(x => x).Aggregate(new List<double>(), (acc, x) =>
        {
            if (acc.Count == 0 || x - acc[^1] > 1e-6) acc.Add(x);
            return acc;
        });
        return true;
    }

    private bool TryStation(string text, out double station)
    {
        text = text.Trim();
        if (text.Equals("START", StringComparison.OrdinalIgnoreCase)) { station = AlignmentStart; return true; }
        if (text.Equals("END", StringComparison.OrdinalIgnoreCase)) { station = AlignmentEnd; return true; }
        var plusScale = ActiveDrawing?.Units == UnitSystem.Metric ? 1000 : 100;
        var plus = text.IndexOf('+');
        if (plus > 0
            && double.TryParse(text[..plus], NumberStyles.Float, CultureInfo.InvariantCulture, out var major)
            && double.TryParse(text[(plus + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var minor))
        {
            station = major * plusScale + minor;
            return true;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out station);
    }
}

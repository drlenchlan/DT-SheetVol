// A deterministic ISheetVolSession for the presentation tests (HANDOFF.md 8, T22).
// Validate parses a small subset of the 6.3 grammar (single breaks, START, END,
// "a+bb" stations, A..B@L, A..B/N, skip A..B), carries segment GUIDs forward by index
// from KnownSegments, gives each segment the offsets that apply to it (per-segment
// override, else the run's: the planner behaviour D8 asks for), and raises SV-E201 on
// the segment at CurveSegmentIndex when its widest offset reaches CurveRadius.
// RegenerateAsync returns canned results (T2 by default), reports progress, honours
// cancellation and "persists" the run for LoadRun. The rev 2 host members (drawing,
// station text, tracker status, command state, export) are settable properties; they
// were FakeDialogHost before contract revision 2.

using System.Globalization;
using SheetVol.Core.Presentation;

namespace SheetVol.Core.Tests.Presentation;

internal sealed class FakeSheetVolSession : ISheetVolSession
{
    // T2 cut/fill per segment (HANDOFF.md 8): 100/50, 100/0, 0/150, 0/0.
    public static readonly (double Cut, double Fill)[] T2CutFill = [(100, 50), (100, 0), (0, 150), (0, 0)];

    private EventHandler? _sourcesChanged;
    private int _nextId;
    private RegenResult? _lastSucceeded;

    public List<string> Alignments { get; set; } = ["S", "C"];
    public List<string> Surfaces { get; set; } = ["EG", "FG_A", "FG_B"];
    public Dictionary<string, RunDefinition> Runs { get; } = [];

    public double AlignmentStart { get; set; }
    public double AlignmentEnd { get; set; } = 2000;
    public double PlusScale { get; set; } = 100;           // 12+00 = 1200; 1000 for metric 1+200
    public int CurveSegmentIndex { get; set; } = 1;        // row 2 sits on the curve
    public double CurveRadius { get; set; } = 300;

    public int ValidateCalls { get; private set; }
    public int RegenerateCalls { get; private set; }
    public RunDefinition? LastValidated { get; private set; }
    public RunDefinition? LastRegenerated { get; private set; }
    public List<Guid?> Highlights { get; } = [];
    public List<Guid> Zooms { get; } = [];
    public int SourcesChangedSubscribers { get; private set; }

    public string? PickAlignmentResult { get; set; }
    public double? PickStationResult { get; set; }

    /// <summary>When set, RegenerateAsync waits on it (or on cancellation) after reporting 0.5.</summary>
    public TaskCompletionSource? Gate { get; set; }
    public bool FailNextRegen { get; set; }
    public HashSet<int> OmitResultForSegment { get; } = [];
    public double VolumeScale { get; set; } = 1;
    public Dictionary<int, double> CoverageBySegment { get; } = [];
    public Func<int, SegmentDef, ImpactResult?>? ImpactFactory { get; set; }

    // ---- Rev 2 host members (formerly FakeDialogHost) ------------------------------------

    public DrawingInfo? ActiveDrawing { get; set; } = new(@"C:\proj\T2.dwg", UnitSystem.Imperial);
    public RunStatus Status { get; set; } = new(false, null);
    public bool IsCommandActive { get; set; }
    public (double Start, double End)? Range { get; set; } = (0, 2000);
    public List<string> ExportRequests { get; } = [];

    /// <summary>What ExportAsync returns; when unset, the last successful regen of that run with its paths, or null.</summary>
    public Func<RunDefinition, RegenResult?>? ExportFactory { get; set; }

    public string FormatStation(string alignmentName, double rawStation)
    {
        var scale = ActiveDrawing?.Units == UnitSystem.Metric ? 1000 : 100;
        var major = Math.Floor(rawStation / scale);
        var minor = rawStation - major * scale;
        var minorFormat = scale == 1000 ? "000.00" : "00.00";
        return major.ToString("0", CultureInfo.InvariantCulture) + "+" + minor.ToString(minorFormat, CultureInfo.InvariantCulture);
    }

    public (double Start, double End)? StationRange(string alignmentName) => Range;

    public RunStatus GetRunStatus(string runName) => Status;

    public Task<RegenResult?> ExportAsync(RunDefinition run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ExportRequests.Add(run.Name);
        var result = ExportFactory is not null
            ? ExportFactory(run)
            : _lastSucceeded?.RunName == run.Name ? _lastSucceeded : null;
        return Task.FromResult(result);
    }

    // ---- Engine members ------------------------------------------------------------------

    public IReadOnlyList<string> AlignmentNames => Alignments;
    public IReadOnlyList<string> TinSurfaceNames => Surfaces;

    public event EventHandler SourcesChanged
    {
        add { _sourcesChanged += value; SourcesChangedSubscribers++; }
        remove { _sourcesChanged -= value; SourcesChangedSubscribers--; }
    }

    public void RaiseSourcesChanged() => _sourcesChanged?.Invoke(this, EventArgs.Empty);

    public RunDefinition? LoadRun(string runName) => Runs.TryGetValue(runName, out var run) ? run : null;

    public ValidationResult Validate(RunDefinition run)
    {
        ValidateCalls++;
        LastValidated = run;
        return Plan(run);
    }

    public async Task<RegenResult> RegenerateAsync(RunDefinition run, IProgress<double> progress, CancellationToken ct)
    {
        RegenerateCalls++;
        LastRegenerated = run;
        progress.Report(0.5);
        if (Gate is not null) await Gate.Task.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        var plan = Plan(run);
        if (FailNextRegen)
        {
            FailNextRegen = false;
            var problem = new Problem("SV-E801", Severity.Error, "Integrator and bounded volume disagree on segment 3.", plan.Segments[2].Id);
            return new RegenResult(false, run.Name, [], [problem], [], false, TimeSpan.FromSeconds(1), null, null);
        }
        var result = BuildResult(run, plan, VolumeScale);
        progress.Report(1.0);
        Runs[run.Name] = run with { KnownSegments = plan.Segments };
        _lastSucceeded = result;
        return result;
    }

    /// <summary>Canned result: T2 cut/fill by index, times (analysis index + 1), times scale. Area = length × the segment's band width. No outside-limits rows.</summary>
    public RegenResult BuildResult(RunDefinition run, ValidationResult plan, double scale)
    {
        var rows = new List<SegmentResult>();
        for (var a = 0; a < run.Analyses.Count; a++)
        {
            var analysis = run.Analyses[a];
            for (var i = 0; i < plan.Segments.Count; i++)
            {
                if (OmitResultForSegment.Contains(i)) continue;
                var s = plan.Segments[i];
                var (cut, fill) = i < T2CutFill.Length ? T2CutFill[i] : (0, 0);
                cut *= (a + 1) * scale;
                fill *= (a + 1) * scale;
                var area = (s.EndStation - s.StartStation) * (s.OffsetRight - s.OffsetLeft);
                rows.Add(new SegmentResult(
                    s.Id, s.Label, s.StartStation, s.EndStation, analysis.Id,
                    area, CoverageBySegment.TryGetValue(i, out var coverage) ? coverage : 1.0, new RawVolume(cut * 27, fill * 27),
                    cut * analysis.CutFactor, fill * analysis.FillFactor, cut * analysis.CutFactor - fill * analysis.FillFactor,
                    ImpactFactory?.Invoke(i, s), SegmentStatus.Current, []));
            }
        }
        return new RegenResult(true, run.Name, rows, [], [], true, TimeSpan.FromSeconds(1.25), "run.csv", "run.json");
    }

    public void Highlight(Guid? segmentId) => Highlights.Add(segmentId);

    public void ZoomTo(Guid segmentId) => Zooms.Add(segmentId);

    public Task<string?> PickAlignmentAsync() => Task.FromResult(PickAlignmentResult);

    public Task<double?> PickStationAsync(string alignmentName) => Task.FromResult(PickStationResult);

    // ---- Planning ---------------------------------------------------------------------

    public ValidationResult Plan(RunDefinition run)
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
        var boundaries = segments.Where(s => !errorIds.Contains(s.Id)).Select(s => new Boundary(s.Id, [], [])).ToList();
        return new ValidationResult(problems.All(p => p.Severity != Severity.Error), segments, boundaries, problems);
    }

    private Guid NewId() => new(++_nextId, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);

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
        var plus = text.IndexOf('+');
        if (plus > 0
            && double.TryParse(text[..plus], NumberStyles.Float, CultureInfo.InvariantCulture, out var major)
            && double.TryParse(text[(plus + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var minor))
        {
            station = major * PlusScale + minor;
            return true;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out station);
    }
}

/// <summary>Shared setup: T2's run on alignment S, offsets ±100, EG vs FG_A.</summary>
internal static class Fixture
{
    public static readonly PresentationConfig Config = PresentationConfig.Default with { Culture = CultureInfo.InvariantCulture };
    public static readonly Guid AnalysisA = new("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid AnalysisB = new("bbbbbbbb-0000-0000-0000-000000000002");

    public static RunDefinition T2Run(string spec = "0, 500, 1300, 1800, 2000", params AnalysisDef[] analyses)
        => new(
            "Run1", "S", SegmentDriver.Stations, spec, null,
            -100, 100,
            analyses.Length > 0 ? analyses : [new AnalysisDef(AnalysisA, "EG vs FG_A", "EG", "FG_A")],
            [], [], new Dictionary<Guid, SegmentOverrides>(), new ViewOptions());

    public static (FakeSheetVolSession Session, PendingEdits Pending, DialogViewModel Vm) Open(
        RunDefinition? run = null, FakeSheetVolSession? session = null, PendingEdits? pending = null)
    {
        session ??= new FakeSheetVolSession();
        pending ??= new PendingEdits();
        if (run is not null) session.Runs[run.Name] = run;
        else if (!session.Runs.ContainsKey("Run1")) session.Runs["Run1"] = T2Run();
        var vm = new DialogViewModel(session, pending, Config);
        return (session, pending, vm);
    }

    public static string Sta(FakeSheetVolSession session, double raw) => session.FormatStation("S", raw);
}

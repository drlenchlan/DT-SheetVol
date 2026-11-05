using System.Globalization;
using System.Text.RegularExpressions;
using SheetVol.Core.Geometry;

namespace SheetVol.Core.Planning;

/// <summary>Form of one comma-separated entry of a break specification (HANDOFF.md 6.3).</summary>
public enum BreakTokenKind
{
    /// <summary><c>5+00</c>, <c>500</c>, <c>START</c> or <c>END</c>: one break.</summary>
    Single,
    /// <summary><c>A..B@L</c>: breaks every L from A to B, both ends included.</summary>
    Every,
    /// <summary><c>A..B/N</c>: N equal segments from A to B.</summary>
    Divide,
    /// <summary><c>skip A..B</c>: no segment between A and B; A and B are breaks.</summary>
    Skip,
}

/// <summary>One parsed entry.</summary>
/// <param name="Position">1-based position of the entry in the specification.</param>
/// <param name="Text">The entry as typed, trimmed.</param>
/// <param name="Kind">Form of the entry.</param>
/// <param name="From">Raw station of the entry (A for ranges).</param>
/// <param name="To">Raw station B for ranges; equal to <paramref name="From"/> for a single break.</param>
/// <param name="Interval">L for <see cref="BreakTokenKind.Every"/>, drawing units; 0 otherwise.</param>
/// <param name="Count">N for <see cref="BreakTokenKind.Divide"/>; 0 otherwise.</param>
public sealed record BreakToken(int Position, string Text, BreakTokenKind Kind, double From, double To, double Interval = 0, int Count = 0);

/// <summary>A raw station range, Start &lt; End.</summary>
public readonly record struct StationRange(double Start, double End);

/// <summary>A break after merging: its raw station and the entry that placed it first.</summary>
public readonly record struct BreakStation(double Station, string Source);

/// <summary>Output of <see cref="BreakSpecParser.Parse"/>.</summary>
/// <param name="Breaks">Sorted, merged within the station tolerance, snapped to alignment geometry points within it. May include stations outside the alignment; the planner reports them.</param>
/// <param name="Skips">Skip ranges, raw.</param>
/// <param name="Tokens">Entries that parsed.</param>
/// <param name="Problems">E208, E300, E302, E303, E304, E306 problems.</param>
public sealed record ParsedBreakSpec(
    IReadOnlyList<BreakStation> Breaks,
    IReadOnlyList<StationRange> Skips,
    IReadOnlyList<BreakToken> Tokens,
    IReadOnlyList<Problem> Problems);

/// <summary>
/// Parser for the break-specification grammar of HANDOFF.md 6.3. Stations are read as
/// displayed stations through <see cref="IAlignmentGeometry.TryParseStation"/>, so station
/// equations are honored (E208); <c>START</c> and <c>END</c> follow the alignment. Intervals
/// L in <c>A..B@L</c> are distances along the alignment in drawing units.
/// </summary>
public static partial class BreakSpecParser
{
    [GeneratedRegex(@"^[+-]?(\d+(\.\d*)?|\.\d+|\d+\+\d+(\.\d*)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex StationSyntax();

    private const string Grammar = "Use breaks such as 5+00, 500, START or END; ranges A..B@L or A..B/N; and skip A..B, separated by commas.";

    private sealed record Raw(double Station, int Token, bool Droppable);

    /// <summary>Parses <paramref name="spec"/> against <paramref name="alignment"/>.</summary>
    public static ParsedBreakSpec Parse(string? spec, IAlignmentGeometry alignment, PlanningOptions options)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        ArgumentNullException.ThrowIfNull(options);
        double tol = options.Geometry.StationTolerance;
        var problems = new List<Problem>();
        var tokens = new List<BreakToken>();
        var raws = new List<Raw>();
        var skips = new List<StationRange>();
        var pendingRemainders = new List<(BreakToken Token, double Station, double Remainder, bool WantMerge)>();

        if (string.IsNullOrWhiteSpace(spec))
        {
            problems.Add(Err(ProblemCodes.E300, "The break list is empty.", "Type at least two breaks, for example START, END."));
            return new ParsedBreakSpec([], [], [], problems);
        }

        var entries = spec.Split(',');
        for (int e = 0; e < entries.Length; e++)
        {
            string text = entries[e].Trim();
            if (text.Length == 0) continue;
            var token = ParseEntry(e + 1, text, alignment, options, problems);
            if (token is null) continue;
            if (!Expand(token, options, raws, tokens.Count, problems, pendingRemainders)) break;   // break limit reached; reported once
            tokens.Add(token);
            if (token.Kind == BreakTokenKind.Skip) skips.Add(new StationRange(token.From, token.To));
        }

        // E304: two generating ranges whose spans overlap.
        var spans = tokens.Where(t => t.Kind is BreakTokenKind.Every or BreakTokenKind.Divide).ToList();
        for (int i = 0; i < spans.Count; i++)
            for (int j = i + 1; j < spans.Count; j++)
                if (Math.Max(spans[i].From, spans[j].From) < Math.Min(spans[i].To, spans[j].To) - tol)
                    problems.Add(Err(ProblemCodes.E304,
                        $"Entries {spans[i].Position} ('{spans[i].Text}') and {spans[j].Position} ('{spans[j].Text}') cover overlapping stations; station segments may not overlap.",
                        "Split the ranges so each station belongs to one range, or add single breaks inside one range instead. Sheet overlap is set with ViewOverlap, not with the break list."));

        // E306: entries out of order, or one single break typed more than once. A single break
        // that repeats a range end (28+00 before skip 28+00..31+50) is ordinary and not reported.
        // START and END follow the alignment, so they never make the list "out of order": after
        // a shortening, END below a typed break is E211's business, not E306's.
        var leads = tokens
            .Where(t => t.Kind != BreakTokenKind.Skip && !(t.Kind == BreakTokenKind.Single && IsKeyword(t.Text)))
            .Select(t => t.From).ToList();
        bool unsorted = leads.Zip(leads.Skip(1)).Any(p => p.Second < p.First - tol);
        var singles = tokens.Where(t => t.Kind == BreakTokenKind.Single).Select(t => t.From).Order().ToList();
        bool duplicate = singles.Zip(singles.Skip(1)).Any(p => p.Second - p.First <= tol);

        // Snap to alignment geometry points, then merge within tolerance.
        var geometryStations = GeometryStations(alignment);
        var items = raws.Select(r => r with { Station = Snap(r.Station, geometryStations, tol) }).OrderBy(r => r.Station).ToList();
        var breaks = new List<BreakStation>();
        var dropped = new HashSet<double>();
        for (int i = 0; i < items.Count;)
        {
            int j = i;
            while (j + 1 < items.Count && items[j + 1].Station - items[i].Station <= tol) j++;
            var cluster = items.GetRange(i, j - i + 1);
            if (cluster.All(c => c.Droppable)) dropped.Add(cluster[0].Station);
            else breaks.Add(new BreakStation(cluster[0].Station, tokens[cluster[0].Token].Text));
            i = j + 1;
        }

        foreach (var (token, station, remainder, wantMerge) in pendingRemainders)
        {
            string len = Fmt.Num(remainder), sta = Fmt.Station(alignment, Snap(station, geometryStations, tol), tol);
            if (wantMerge && dropped.Contains(Snap(station, geometryStations, tol)))
                problems.Add(new Problem(ProblemCodes.E302, Severity.Warning,
                    $"Entry {token.Position} ('{token.Text}') leaves a {len} remainder, shorter than the minimum segment length {Fmt.Num(options.MinSegmentLength)}; the break at {sta} was removed so the remainder joins the previous segment.",
                    null, "Change the interval or the range end if a separate short segment is wanted."));
            else
                problems.Add(new Problem(ProblemCodes.E302, Severity.Info,
                    $"Entry {token.Position} ('{token.Text}') leaves a {len} remainder after {sta}; it is kept as its own segment.",
                    null, wantMerge ? "Another entry also places a break there, so it stays." : null));
        }

        if (unsorted || duplicate)
            problems.Add(new Problem(ProblemCodes.E306, Severity.Info,
                unsorted && duplicate ? "Breaks were given out of order and some more than once; they were sorted and duplicates removed."
                : unsorted ? "Breaks were given out of order; they were sorted."
                : "Some breaks were given more than once; duplicates were removed.",
                null, null));

        return new ParsedBreakSpec(breaks, skips, tokens, problems);
    }

    private static BreakToken? ParseEntry(int position, string text, IAlignmentGeometry a, PlanningOptions opt, List<Problem> problems)
    {
        if (text.StartsWith("skip", StringComparison.OrdinalIgnoreCase) && text.Length > 4 && char.IsWhiteSpace(text[4]))
        {
            string body = text[4..].Trim();
            int dots = body.IndexOf("..", StringComparison.Ordinal);
            if (dots < 0 || body.IndexOfAny(['@', '/']) >= 0)
            {
                problems.Add(Syntax(position, text, "a skip needs a range A..B with no @ or /"));
                return null;
            }
            if (!Station(position, text, body[..dots].Trim(), a, problems, out double from) ||
                !Station(position, text, body[(dots + 2)..].Trim(), a, problems, out double to)) return null;
            return Forward(position, text, from, to, opt, problems) ? new BreakToken(position, text, BreakTokenKind.Skip, from, to) : null;
        }

        int dd = text.IndexOf("..", StringComparison.Ordinal);
        if (dd < 0)
            return Station(position, text, text, a, problems, out double s) ? new BreakToken(position, text, BreakTokenKind.Single, s, s) : null;

        string left = text[..dd].Trim(), rest = text[(dd + 2)..].Trim();
        int at = rest.IndexOf('@'), slash = rest.IndexOf('/');
        if ((at < 0) == (slash < 0))
        {
            problems.Add(Syntax(position, text, at < 0 ? "a range needs @L (every L) or /N (N equal parts)" : "a range takes either @L or /N, not both"));
            return null;
        }
        int sep = at >= 0 ? at : slash;
        string right = rest[..sep].Trim(), param = rest[(sep + 1)..].Trim();
        if (!Station(position, text, left, a, problems, out double aSta) || !Station(position, text, right, a, problems, out double bSta)) return null;
        if (!Forward(position, text, aSta, bSta, opt, problems)) return null;

        if (at >= 0)
        {
            if (!double.TryParse(param, NumberStyles.Float, CultureInfo.InvariantCulture, out double l) || !double.IsFinite(l) || l <= opt.Geometry.StationTolerance)
            {
                problems.Add(Syntax(position, text, $"the interval '{param}' after @ is not a positive length"));
                return null;
            }
            return new BreakToken(position, text, BreakTokenKind.Every, aSta, bSta, Interval: l);
        }
        if (!int.TryParse(param, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1)
        {
            problems.Add(Syntax(position, text, $"the count '{param}' after / is not a whole number of at least 1"));
            return null;
        }
        return new BreakToken(position, text, BreakTokenKind.Divide, aSta, bSta, Count: n);
    }

    private static bool Expand(BreakToken t, PlanningOptions opt, List<Raw> raws, int index, List<Problem> problems,
        List<(BreakToken, double, double, bool)> remainders)
    {
        double tol = opt.Geometry.StationTolerance;
        // The limit is on the whole specification; arithmetic in double so a huge count cannot overflow.
        double added = t.Kind switch
        {
            BreakTokenKind.Single => 1,
            BreakTokenKind.Skip => 2,
            BreakTokenKind.Divide => (double)t.Count + 1,
            _ => (t.To - t.From) / t.Interval + 2,
        };
        if (raws.Count + added > opt.MaxBreaks) return TooMany(t, opt, problems);

        switch (t.Kind)
        {
            case BreakTokenKind.Single:
                raws.Add(new Raw(t.From, index, false));
                return true;

            case BreakTokenKind.Skip:
                raws.Add(new Raw(t.From, index, false));
                raws.Add(new Raw(t.To, index, false));
                return true;

            case BreakTokenKind.Divide:
                for (int i = 0; i <= t.Count; i++)
                    raws.Add(new Raw(i == t.Count ? t.To : t.From + (t.To - t.From) * i / t.Count, index, false));
                return true;

            case BreakTokenKind.Every:
                var generated = new List<double>();
                for (int k = 0; ; k++)
                {
                    double s = t.From + k * t.Interval;
                    if (s >= t.To - tol) break;
                    generated.Add(s);
                }
                double lastFull = generated[^1], remainder = t.To - lastFull;
                foreach (double s in generated) raws.Add(new Raw(s, index, false));
                raws.Add(new Raw(t.To, index, false));
                if (remainder < t.Interval - tol)
                {
                    bool merge = generated.Count >= 2 && remainder < opt.MinSegmentLength;
                    if (merge) raws[raws.Count - 2] = raws[^2] with { Droppable = true };
                    remainders.Add((t, lastFull, remainder, merge));
                }
                return true;
        }
        return false;
    }

    private static bool TooMany(BreakToken t, PlanningOptions opt, List<Problem> problems)
    {
        problems.Add(Err(ProblemCodes.E300,
            $"Entry {t.Position} ('{t.Text}') takes the break list past {opt.MaxBreaks.ToString(CultureInfo.InvariantCulture)} breaks; it and any later entries were not read.",
            "Use longer intervals or fewer parts, or split the alignment into several runs."));
        return false;
    }

    private static bool Station(int position, string entry, string text, IAlignmentGeometry a, List<Problem> problems, out double raw)
    {
        raw = 0;
        if (text.Equals("START", StringComparison.OrdinalIgnoreCase)) { raw = a.StartStation; return true; }
        if (text.Equals("END", StringComparison.OrdinalIgnoreCase)) { raw = a.EndStation; return true; }
        if (!StationSyntax().IsMatch(text))
        {
            problems.Add(Syntax(position, entry, $"'{text}' is not a station"));
            return false;
        }
        if (!a.TryParseStation(text, out raw, out string? error) || !double.IsFinite(raw))
        {
            problems.Add(Err(ProblemCodes.E208,
                $"Entry {position} ('{entry}'): station '{text}' cannot be placed on alignment '{a.Name}'. {error}".TrimEnd(),
                "Type the station exactly as it is labelled on the alignment; where a station equation makes it ambiguous, choose one of the listed stations."));
            return false;
        }
        return true;
    }

    private static bool IsKeyword(string text) =>
        text.Equals("START", StringComparison.OrdinalIgnoreCase) || text.Equals("END", StringComparison.OrdinalIgnoreCase);

    private static bool Forward(int position, string text, double from, double to, PlanningOptions opt, List<Problem> problems)
    {
        if (to - from > opt.Geometry.StationTolerance) return true;
        problems.Add(Syntax(position, text, "the range runs backwards or has no length"));
        return false;
    }

    private static Problem Syntax(int position, string text, string what) =>
        Err(ProblemCodes.E300, $"Entry {position} ('{text}') is not understood: {what}.", Grammar);

    private static Problem Err(string code, string message, string fix) => new(code, Severity.Error, message, null, fix);

    private static double[] GeometryStations(IAlignmentGeometry a)
    {
        var list = new List<double> { a.StartStation, a.EndStation };
        foreach (var s in a.Segments)
        {
            list.Add(s.StartStation);
            list.Add(s.EndStation);
        }
        list.Sort();
        return list.ToArray();
    }

    private static double Snap(double s, double[] geometry, double tol)
    {
        int i = Array.BinarySearch(geometry, s);
        if (i >= 0) return geometry[i];
        i = ~i;
        double best = s, bestD = tol;
        if (i < geometry.Length && Math.Abs(geometry[i] - s) <= bestD) { best = geometry[i]; bestD = Math.Abs(geometry[i] - s); }
        if (i > 0 && Math.Abs(geometry[i - 1] - s) <= bestD) best = geometry[i - 1];
        return best;
    }
}

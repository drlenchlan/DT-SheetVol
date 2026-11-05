// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4.
//
// SVBREAK's break-list rewrite (6.3): add, move or remove one break, or turn a segment
// into a manual segment, and write the list back in the 6.3 grammar. Identity is not
// decided here: the rewritten list goes back through C1's planner, whose rules keep the
// GUID on the left part of a split and on the left segment of a merge (6.3, E307, E308).
// START and END are kept when the original list began or ended with them so END keeps
// following the alignment (E211). Every other break is written as the station text the
// alignment shows when that text reads back to the same raw station, else as the raw
// number; a break that round-trips neither way is reported (E208).

using System.Globalization;
using System.Text.RegularExpressions;
using SheetVol.Core;
using SheetVol.Core.Planning;

namespace SheetVol.Civil3D.Session;

/// <summary>Result of one break edit.</summary>
/// <param name="Run">The edited run; unchanged when <paramref name="Error"/> is set.</param>
/// <param name="Error">Why the edit was refused, or null.</param>
/// <param name="Notes">Things the user should know (a station written as a raw number, a no-op).</param>
public sealed record BreakEdit(RunDefinition Run, string? Error, IReadOnlyList<string> Notes);

/// <summary>Pure break-list edits on a Stations-driver run.</summary>
public static partial class BreakEditor
{
    private const string StartToken = "START";
    private const string EndToken = "END";
    private const string SkipKeyword = "skip";
    private const string Range = "..";
    private const string Separator = ", ";

    [GeneratedRegex(@"^START\s*(\.\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StartPattern();

    [GeneratedRegex(@"(^|\.\.)\s*END\s*([@/].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EndPattern();

    private sealed class Stops
    {
        public readonly List<double> Raw = [];
        public readonly List<bool> GapAfter = [];
        public bool KeepStart, KeepEnd;
    }

    /// <summary>Inserts a break at <paramref name="station"/> (raw). The segment it splits keeps its GUID on the left part (C1).</summary>
    public static BreakEdit Add(RunDefinition run, IReadOnlyList<SegmentDef> planned, IAlignmentGeometry alignment, double station, double tolerance)
    {
        if (!Inside(alignment, station, tolerance)) return Refuse(run, $"Station {alignment.FormatStation(station)} is outside the alignment ({alignment.FormatStation(alignment.StartStation)} to {alignment.FormatStation(alignment.EndStation)}).");
        var s = From(run, planned, tolerance);
        if (s.Raw.Count == 0) return Refuse(run, "The run has no station segments to split.");
        int k = 0;
        while (k < s.Raw.Count && s.Raw[k] < station - tolerance) k++;
        if (k < s.Raw.Count && Math.Abs(s.Raw[k] - station) <= tolerance)
            return new BreakEdit(run, null, [$"A break already exists at {alignment.FormatStation(station)}; nothing changed."]);
        if (k == 0 || k == s.Raw.Count) return Refuse(run, $"Station {alignment.FormatStation(station)} is outside the run's segments; extend the break list with SVDEFINE instead.");
        if (s.GapAfter[k - 1]) return Refuse(run, $"Station {alignment.FormatStation(station)} lies inside a skip gap; there is no segment to split.");
        s.Raw.Insert(k, station);
        s.GapAfter.Insert(k, false);
        return Write(run, s, alignment, tolerance);
    }

    /// <summary>Removes the break at <paramref name="station"/>. The left segment survives and keeps its GUID; the right one's outputs go (E308).</summary>
    public static BreakEdit Remove(RunDefinition run, IReadOnlyList<SegmentDef> planned, IAlignmentGeometry alignment, double station, double tolerance)
    {
        var s = From(run, planned, tolerance);
        int k = s.Raw.FindIndex(r => Math.Abs(r - station) <= tolerance);
        if (k < 0) return Refuse(run, $"No break at {alignment.FormatStation(station)}. Breaks: {string.Join(", ", s.Raw.Select(alignment.FormatStation))}.");
        if (s.Raw.Count <= 2) return Refuse(run, "A run needs at least two breaks; remove the run with SVCLEAR instead.");
        // A break that bounds a skip gap has no segment on that side to merge with: removing it
        // would swallow the neighbouring segment into the gap. Gaps change only through the list.
        if ((k > 0 && s.GapAfter[k - 1]) || s.GapAfter[k])
            return Refuse(run, $"The break at {alignment.FormatStation(station)} bounds a skip gap; change the skip with SVDEFINE Breaks instead.");
        s.Raw.RemoveAt(k);
        s.GapAfter.RemoveAt(k);
        if (k == 0) s.KeepStart = false;
        if (k == s.Raw.Count) s.KeepEnd = false;
        Normalize(s);
        return Write(run, s, alignment, tolerance);
    }

    /// <summary>Moves the break at <paramref name="from"/> to <paramref name="to"/>, which must stay between its neighbours.</summary>
    public static BreakEdit Move(RunDefinition run, IReadOnlyList<SegmentDef> planned, IAlignmentGeometry alignment, double from, double to, double tolerance)
    {
        if (!Inside(alignment, to, tolerance)) return Refuse(run, $"Station {alignment.FormatStation(to)} is outside the alignment.");
        var s = From(run, planned, tolerance);
        int k = s.Raw.FindIndex(r => Math.Abs(r - from) <= tolerance);
        if (k < 0) return Refuse(run, $"No break at {alignment.FormatStation(from)}.");
        double lo = k > 0 ? s.Raw[k - 1] : double.NegativeInfinity, hi = k + 1 < s.Raw.Count ? s.Raw[k + 1] : double.PositiveInfinity;
        if (!(to > lo + tolerance && to < hi - tolerance))
            return Refuse(run, $"Station {alignment.FormatStation(to)} is not between the neighbouring breaks; remove and add instead.");
        s.Raw[k] = to;
        if (k == 0) s.KeepStart = false;
        if (k == s.Raw.Count - 1) s.KeepEnd = false;
        return Write(run, s, alignment, tolerance);
    }

    /// <summary>
    /// Turns a station segment into a manual segment (6.12 SVBREAK): its range becomes a skip, and a
    /// manual segment with the same GUID and target layout uses <paramref name="polylineHandle"/>.
    /// Manual segments have no stations, so they get no generated layout or view: the segment's
    /// generated layout becomes an orphan (E612) and its table goes to the target layout, or to
    /// model space when Draw.TableAtEachSegment is on. SVBREAK says so when it converts.
    /// </summary>
    public static BreakEdit ToManual(RunDefinition run, IReadOnlyList<SegmentDef> planned, IAlignmentGeometry alignment, Guid segmentId, string polylineHandle, double tolerance)
    {
        var seg = planned.FirstOrDefault(p => p.Id == segmentId);
        if (seg is null) return Refuse(run, "That segment is not in the current plan.");
        var s = From(run, planned, tolerance);
        int k = s.Raw.FindIndex(r => Math.Abs(r - seg.StartStation) <= tolerance);
        if (k < 0 || k + 1 >= s.Raw.Count) return Refuse(run, "The segment's breaks could not be found in the break list.");
        s.GapAfter[k] = true;
        Normalize(s);
        var edit = Write(run, s, alignment, tolerance);
        if (edit.Error is not null) return edit;
        var manual = run.ManualSegments.Append(new ManualSegmentDef(seg.Id, seg.Label, polylineHandle, seg.TargetLayout)).ToList();
        var notes = edit.Notes.Append(
            $"Segment {seg.Label} is manual now: it keeps its GUID{(seg.TargetLayout is null ? "" : $" and target layout '{seg.TargetLayout}'")}, but manual segments get no generated layout or view, so its generated layout (if any) becomes an orphan at the next SVREGEN. Its table goes to a target layout (SVDEFINE Segment Layout) or to model space.").ToList();
        return new BreakEdit(edit.Run with { ManualSegments = manual }, null, notes);
    }

    private static Stops From(RunDefinition run, IReadOnlyList<SegmentDef> planned, double tolerance)
    {
        var s = new Stops();
        SegmentDef? previous = null;
        foreach (var seg in planned.Where(p => p.SourceKey is null).OrderBy(p => p.StartStation))
        {
            if (previous is null) Append(s, seg.StartStation);
            else if (Math.Abs(seg.StartStation - previous.EndStation) > tolerance)
            {
                s.GapAfter[^1] = true;
                Append(s, seg.StartStation);
            }
            Append(s, seg.EndStation);
            previous = seg;
        }
        var tokens = (run.BreakSpec ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        s.KeepStart = tokens.Count > 0 && !IsSkip(tokens[0]) && StartPattern().IsMatch(tokens[0]);
        s.KeepEnd = tokens.Count > 0 && !IsSkip(tokens[^1]) && EndPattern().IsMatch(tokens[^1]);
        return s;
    }

    private static void Append(Stops s, double raw)
    {
        s.Raw.Add(raw);
        s.GapAfter.Add(false);
    }

    private static void Normalize(Stops s)
    {
        if (s.GapAfter.Count > 0) s.GapAfter[^1] = false;
        while (s.Raw.Count > 1 && s.GapAfter[0]) { s.Raw.RemoveAt(0); s.GapAfter.RemoveAt(0); s.KeepStart = false; }
        while (s.Raw.Count > 1 && s.GapAfter[^2]) { s.Raw.RemoveAt(s.Raw.Count - 1); s.GapAfter.RemoveAt(s.GapAfter.Count - 1); s.GapAfter[^1] = false; s.KeepEnd = false; }
    }

    private static BreakEdit Write(RunDefinition run, Stops s, IAlignmentGeometry alignment, double tolerance)
    {
        var notes = new List<string>();
        var text = new List<string>(s.Raw.Count);
        for (int k = 0; k < s.Raw.Count; k++)
        {
            if (k == 0 && s.KeepStart) { text.Add(StartToken); continue; }
            if (k == s.Raw.Count - 1 && s.KeepEnd) { text.Add(EndToken); continue; }
            text.Add(StationText(alignment, s.Raw[k], tolerance, notes));
        }
        var parts = new List<string>();
        for (int k = 0; k < text.Count; k++)
        {
            parts.Add(text[k]);
            if (k < text.Count - 1 && s.GapAfter[k]) parts.Add($"{SkipKeyword} {text[k]}{Range}{text[k + 1]}");
        }
        return new BreakEdit(run with { BreakSpec = string.Join(Separator, parts) }, null, notes);
    }

    /// <summary>The station text that reads back to <paramref name="raw"/>: the alignment's own format when it round-trips, else the raw number.</summary>
    public static string StationText(IAlignmentGeometry alignment, double raw, double tolerance, List<string>? notes = null)
    {
        string shown = alignment.FormatStation(raw);
        if (alignment.TryParseStation(shown, out double back, out _) && Math.Abs(back - raw) <= tolerance) return shown;
        string number = raw.ToString("R", CultureInfo.InvariantCulture);
        if (alignment.TryParseStation(number, out back, out _) && Math.Abs(back - raw) <= tolerance) return number;
        notes?.Add($"Raw station {number} has no unambiguous displayed form on '{alignment.Name}' (station equations); it was written as {number}. Check it with SVDEFINE (E208).");
        return number;
    }

    private static bool Inside(IAlignmentGeometry a, double s, double tol) => s >= a.StartStation - tol && s <= a.EndStation + tol;

    private static bool IsSkip(string token) => token.StartsWith(SkipKeyword, StringComparison.OrdinalIgnoreCase);

    private static BreakEdit Refuse(RunDefinition run, string why) => new(run, why, []);
}

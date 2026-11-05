using System.Text.RegularExpressions;

namespace SheetVol.Core.Presentation;

/// <summary>
/// The planned station segments as an explicit, editable break list, rewritten back into
/// the 6.3 grammar after one grid edit. Neighbouring segments share one break, so moving
/// it moves both (E904); gaps exist only as explicit <c>skip</c> tokens. START and END
/// are kept when the original spec began or ended with them, so END keeps following the
/// alignment (E211). Every other break is written as its raw station at full precision.
/// The text of an edited break is written exactly as typed and parsed by the session's
/// Validate, never here (E903). One instance serves one edit: build, mutate once, write.
/// </summary>
internal sealed partial class BreakList
{
    public const string StartToken = "START";
    public const string EndToken = "END";
    public const string SkipKeyword = "skip";
    public const string RangeToken = "..";
    public const string IntervalToken = "@";
    public const string Separator = ", ";
    public const char SeparatorChar = ',';

    private readonly List<string> _text = [];
    private readonly List<double> _raw = [];
    private readonly List<bool> _gapAfter = [];
    private readonly Dictionary<Guid, (int Begin, int End)> _rows = [];
    private readonly Func<double, string> _number;
    private readonly double _tolerance;

    private BreakList(Func<double, string> number, double tolerance)
    {
        _number = number;
        _tolerance = tolerance;
    }

    public int Count => _text.Count;

    public static BreakList From(IEnumerable<SegmentDef> stationSegments, string originalSpec, Func<double, string> number, double tolerance)
    {
        var list = new BreakList(number, tolerance);
        SegmentDef? previous = null;
        foreach (var segment in stationSegments.OrderBy(s => s.StartStation))
        {
            if (previous is null)
            {
                list.Append(segment.StartStation);
            }
            else if (Math.Abs(segment.StartStation - previous.EndStation) > tolerance)
            {
                list._gapAfter[^1] = true;
                list.Append(segment.StartStation);
            }
            var begin = list.Count - 1;
            list.Append(segment.EndStation);
            list._rows[segment.Id] = (begin, list.Count - 1);
            previous = segment;
        }

        if (list.Count > 0)
        {
            var tokens = Tokens(originalSpec);
            if (tokens.Count > 0 && StartsWithStart(tokens[0])) list._text[0] = StartToken;
            if (tokens.Count > 0 && EndsWithEnd(tokens[^1])) list._text[^1] = EndToken;
        }
        return list;
    }

    public bool TryGetRow(Guid segmentId, out int begin, out int end)
    {
        var found = _rows.TryGetValue(segmentId, out var row);
        (begin, end) = row;
        return found;
    }

    public void SetText(int index, string text) => _text[index] = text;

    /// <summary>Inserts a break at a raw station. Returns an error message, or null. A station already in the list is a no-op.</summary>
    public string? Insert(double rawStation)
    {
        var position = 0;
        while (position < Count && _raw[position] < rawStation - _tolerance) position++;
        if (position < Count && Math.Abs(_raw[position] - rawStation) <= _tolerance) return null;
        if (position > 0 && position < Count && _gapAfter[position - 1]) return DialogText.StationInsideSkip;
        _text.Insert(position, _number(rawStation));
        _raw.Insert(position, rawStation);
        _gapAfter.Insert(position, false);
        return null;
    }

    /// <summary>
    /// Removes a segment row. A row with a contiguous left neighbour merges into it (the
    /// left segment keeps its GUID, 6.3). The first row of a contiguous run merges with
    /// its right neighbour instead. A row with gaps on both sides joins the gap.
    /// </summary>
    public void RemoveRow(Guid segmentId)
    {
        if (!_rows.TryGetValue(segmentId, out var row)) return;
        var contiguousLeft = row.Begin > 0 && !_gapAfter[row.Begin - 1];
        var contiguousRight = row.End < Count - 1 && !_gapAfter[row.End];
        if (contiguousLeft) RemoveStop(row.Begin);
        else if (contiguousRight) RemoveStop(row.End);
        else RemoveStop(row.Begin);
        Normalize();
    }

    public string ToSpec()
    {
        var parts = new List<string>(Count + 2);
        for (var k = 0; k < Count; k++)
        {
            parts.Add(_text[k]);
            if (k < Count - 1 && _gapAfter[k])
                parts.Add($"{SkipKeyword} {_text[k]}{RangeToken}{_text[k + 1]}");
        }
        return string.Join(Separator, parts);
    }

    /// <summary>An interval spec for "Fill table": FROM..TO@EVERY.</summary>
    public static string IntervalSpec(string from, string to, string every) => $"{from}{RangeToken}{to}{IntervalToken}{every}";

    private void Append(double raw)
    {
        _text.Add(_number(raw));
        _raw.Add(raw);
        _gapAfter.Add(false);
    }

    private void RemoveStop(int k)
    {
        if (k > 0) _gapAfter[k - 1] = _gapAfter[k - 1] || _gapAfter[k];
        _text.RemoveAt(k);
        _raw.RemoveAt(k);
        _gapAfter.RemoveAt(k);
        if (Count > 0) _gapAfter[^1] = false;
    }

    private void Normalize()
    {
        while (Count > 1 && _gapAfter[0]) RemoveStop(0);
        while (Count > 1 && _gapAfter[Count - 2]) RemoveStop(Count - 1);
        for (var k = 1; k < Count - 1;)
        {
            if (_gapAfter[k - 1] && _gapAfter[k]) RemoveStop(k);
            else k++;
        }
    }

    private static List<string> Tokens(string spec)
        => spec.Split(SeparatorChar).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

    private static bool StartsWithStart(string token)
        => !IsSkip(token) && StartPattern().IsMatch(token);

    private static bool EndsWithEnd(string token)
        => !IsSkip(token) && EndPattern().IsMatch(token);

    private static bool IsSkip(string token)
        => token.StartsWith(SkipKeyword, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^START\s*(\.\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StartPattern();

    [GeneratedRegex(@"(^|\.\.)\s*END\s*([@/].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EndPattern();
}

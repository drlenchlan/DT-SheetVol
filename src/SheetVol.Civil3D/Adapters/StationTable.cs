// A1 adapters. Owner: A1.

using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.Civil.DatabaseServices;

namespace SheetVol.Civil3D.Adapters;

/// <summary>
/// Displayed-to-raw station conversion for one alignment (HANDOFF.md E208, probe P8). Civil 3D
/// formats raw to displayed (<c>GetStationStringWithEquations</c>) but has no inverse, so the
/// table is built from <c>Alignment.StationEquations</c>: region 0 runs from the start station
/// with displayed = raw; each equation starts a region at its <c>RawStationBack</c> showing
/// <c>StationAhead</c>, increasing or decreasing per <c>EquationType</c> (P8: raw 1000 shows
/// 8+00.00 for back 1000 = ahead 800; a decreasing equation counts down). A displayed station
/// that lies in two regions is ambiguous and rejected with both raw candidates listed; one in no
/// region falls in an equation gap and is rejected; one before the start or past the end is
/// returned by extrapolation so the planner reports it as out of range (E305), as C1's fake does.
/// </summary>
internal sealed partial class StationTable
{
    private readonly record struct Region(double RawFrom, double RawTo, double DisplayedFrom, int Direction);

    private readonly Region[] _regions;
    private readonly double _tolerance;

    // [0-9], not \d: \d matches non-ASCII digits that double.Parse then rejects.
    [GeneratedRegex(@"^\s*([+-]?)([0-9]+)\+([0-9]+)(?:\.([0-9]*))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PlusForm();

    private StationTable(Region[] regions, double plusBase, double tolerance, string formatEvidence)
    {
        _regions = regions;
        PlusBase = plusBase;
        _tolerance = tolerance;
        FormatEvidence = formatEvidence;
    }

    /// <summary>Displayed units per major station division read from the drawing's own format (100 for 1+00.00, 1000 for 1+000.000); 0 when the drawing shows stations without a plus.</summary>
    public double PlusBase { get; }

    /// <summary>The formatted samples the plus base was read from, for reports.</summary>
    public string FormatEvidence { get; }

    /// <summary>Number of station equations.</summary>
    public int EquationCount => _regions.Length - 1;

    /// <summary>Builds the table from the alignment's equations and station format.</summary>
    /// <param name="al">The alignment, open for read.</param>
    /// <param name="tolerance">Stations closer than this, drawing units, are one station.</param>
    public static StationTable Read(Alignment al, double tolerance)
    {
        var equations = Enumerable.Range(0, al.StationEquations.Count)
            .Select(i => al.StationEquations[i])
            .OrderBy(e => e.RawStationBack)
            .Select(e => (e.RawStationBack, e.StationAhead, Dir: e.EquationType == StationEquationType.Decreasing ? -1 : 1))
            .ToList();

        var regions = new List<Region>();
        double rawFrom = al.StartingStation, dispFrom = al.StartingStation;
        int dir = 1;
        foreach (var (back, ahead, d) in equations)
        {
            regions.Add(new Region(rawFrom, back, dispFrom, dir));
            rawFrom = back; dispFrom = ahead; dir = d;
        }
        regions.Add(new Region(rawFrom, al.EndingStation, dispFrom, dir));

        // The plus base comes from the drawing's own formatting: whatever FormatStation
        // displays is what TryParse must read back. Two samples must agree and parse back to
        // their displayed values, or plus-form input is refused.
        var table = new StationTable(regions.ToArray(), 0, tolerance, "");
        double[] samples = [al.StartingStation, al.EndingStation];
        var shown = samples.Select(al.GetStationStringWithEquations).ToArray();
        double plusBase = 0;
        var digits = shown.Select(s => PlusForm().Match(s)).Select(m => m.Success ? m.Groups[3].Value.Length : -1).Distinct().ToArray();
        if (digits.Length == 1 && digits[0] > 0)
        {
            double candidate = Math.Pow(10, digits[0]);
            bool roundTrips = true;
            for (int i = 0; i < samples.Length; i++)
            {
                var m = PlusForm().Match(shown[i]);
                double value = PlusValue(m, candidate);
                int decimals = m.Groups[4].Success ? m.Groups[4].Value.Length : 0;
                if (Math.Abs(value - table.Displayed(samples[i])) > 0.5 * Math.Pow(10, -decimals) + tolerance) roundTrips = false;
            }
            if (roundTrips) plusBase = candidate;
        }
        string evidence = string.Join(", ", samples.Select((s, i) => $"raw {s.ToString("0.###", CultureInfo.InvariantCulture)} -> '{shown[i]}'"));
        return new StationTable(regions.ToArray(), plusBase, tolerance, evidence);
    }

    /// <summary>Displayed station value of a raw station (the region that holds it; the next region at an equation).</summary>
    public double Displayed(double raw)
    {
        var g = _regions[0];
        foreach (var r in _regions)
            if (raw >= r.RawFrom - _tolerance) g = r;
        return g.DisplayedFrom + g.Direction * (raw - g.RawFrom);
    }

    /// <summary>
    /// Parses a displayed station (<c>12+34.56</c> or <c>1234.56</c>) to a raw station.
    /// </summary>
    public bool TryParse(string text, out double raw, out string? error)
    {
        raw = 0;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "No station was given."; return false; }
        string t = text.Trim();
        double displayed;
        var m = PlusForm().Match(t);
        if (m.Success)
        {
            if (PlusBase <= 0)
            {
                error = $"'{text}' uses the plus form, but this drawing shows stations as plain numbers; type {t.Replace("+", "", StringComparison.Ordinal)} instead.";
                return false;
            }
            double minor = Minor(m);
            if (minor >= PlusBase)
            {
                error = $"'{text}': the part after + must be less than {PlusBase.ToString(CultureInfo.InvariantCulture)}.";
                return false;
            }
            displayed = PlusValue(m, PlusBase);
        }
        else if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out displayed) || !double.IsFinite(displayed))
        {
            error = $"'{text}' is not a station.";
            return false;
        }

        var candidates = new List<double>();
        foreach (var g in _regions)
        {
            double r = g.RawFrom + g.Direction * (displayed - g.DisplayedFrom);
            if (r >= g.RawFrom - _tolerance && r <= g.RawTo + _tolerance && !candidates.Any(c => Math.Abs(c - r) <= _tolerance))
                candidates.Add(r);
        }
        if (candidates.Count == 1) { raw = candidates[0]; return true; }
        if (candidates.Count > 1)
        {
            error = $"Station {text} occurs {candidates.Count} times because of station equations: raw "
                + string.Join(" or raw ", candidates.Select(c => c.ToString("0.00####", CultureInfo.InvariantCulture))) + ".";
            return false;
        }

        // Before the start or past the end: extrapolate so the planner reports the valid range (E305).
        // When the last region counts down both ends can extrapolate; the nearer one is meant.
        var first = _regions[0];
        double before = first.RawFrom + first.Direction * (displayed - first.DisplayedFrom);
        var last = _regions[^1];
        double after = last.RawFrom + last.Direction * (displayed - last.DisplayedFrom);
        bool isBefore = before < first.RawFrom, isAfter = after > last.RawTo;
        if (isBefore && (!isAfter || first.RawFrom - before <= after - last.RawTo)) { raw = before; return true; }
        if (isAfter) { raw = after; return true; }
        error = $"Station {text} does not exist on this alignment: it falls in the gap of a station equation.";
        return false;
    }

    /// <summary>Regions as text, for reports.</summary>
    public string Describe() => string.Join("; ", _regions.Select(g =>
        string.Create(CultureInfo.InvariantCulture, $"raw {g.RawFrom:0.###}..{g.RawTo:0.###} shows {g.DisplayedFrom:0.###} {(g.Direction > 0 ? "up" : "down")}")));

    private static double PlusValue(Match m, double plusBase)
    {
        double major = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return (m.Groups[1].Value == "-" ? -1 : 1) * (major * plusBase + Minor(m));
    }

    private static double Minor(Match m) =>
        double.Parse(m.Groups[3].Value + (m.Groups[4].Success && m.Groups[4].Value.Length > 0 ? "." + m.Groups[4].Value : ""), CultureInfo.InvariantCulture);
}

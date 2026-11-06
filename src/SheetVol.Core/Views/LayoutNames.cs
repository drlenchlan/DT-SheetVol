using System.Globalization;
using System.Text;

namespace SheetVol.Core.Views;

/// <summary>
/// Layout naming settings (E603, E307, 6.7). Placeholder defaults; the run config supplies
/// the real values. AutoCAD compares layout names case-insensitively, so every collision
/// test here does too.
/// </summary>
public sealed record LayoutNameConfig
{
    /// <summary>Characters AutoCAD rejects in a layout name. Control characters are always rejected as well.</summary>
    public string InvalidCharacters { get; init; } = "<>/\\\":;?*|,=`";

    /// <summary>Text that replaces each rejected character. Empty removes them. Must itself be valid.</summary>
    public string Replacement { get; init; } = "_";

    /// <summary>Longest name produced, characters. 255 is AutoCAD's limit; a lower value is allowed.</summary>
    public int MaxLayoutNameLength { get; init; } = 255;

    /// <summary>Names that are never available (the model tab). Treated as taken during de-duplication.</summary>
    public IReadOnlyList<string> ReservedNames { get; init; } = ["Model"];

    /// <summary>Composite format for the de-duplication suffix; {0} is the number, invariant culture.</summary>
    public string DuplicateSuffixFormat { get; init; } = " ({0})";

    /// <summary>First number tried by the de-duplication suffix.</summary>
    public int FirstDuplicateNumber { get; init; } = 2;

    /// <summary>Name used when sanitizing leaves nothing.</summary>
    public string EmptyNameFallback { get; init; } = "Layout";

    /// <summary>Composite format for the first-pass temporary names of a rename plan; {0} is a counter.</summary>
    public string TemporaryNameFormat { get; init; } = "SV-TMP-{0}";

    /// <summary>
    /// Pattern for generated layout names. Tokens: {RUN} run name, {N} sheet number,
    /// {LABEL} segment label, {FROM} and {TO} formatted stations, {ALIGN} alignment name.
    /// "{{" and "}}" are literal braces.
    /// </summary>
    public string NamePattern { get; init; } = "{RUN} - {N}";

    /// <summary>Invariant-culture .NET numeric format for {N}.</summary>
    public string SheetNumberFormat { get; init; } = "0";

    /// <summary>Trim leading and trailing white space from names.</summary>
    public bool TrimWhitespace { get; init; } = true;
}

/// <summary>Result of sanitizing one name.</summary>
/// <param name="Name">The valid name.</param>
/// <param name="Sanitized">Characters were replaced, white space trimmed, or the fallback used.</param>
/// <param name="Truncated">The name was cut to <see cref="LayoutNameConfig.MaxLayoutNameLength"/>.</param>
public readonly record struct SanitizedName(string Name, bool Sanitized, bool Truncated);

/// <summary>The final name for one requested name.</summary>
/// <param name="Index">Position in the request list.</param>
/// <param name="Requested">The name as requested.</param>
/// <param name="Final">The name to use.</param>
/// <param name="Sanitized">Characters were replaced, white space trimmed, or the fallback used.</param>
/// <param name="Truncated">The name was cut to the length limit.</param>
/// <param name="Deduplicated">A numeric suffix was added because the name was taken.</param>
public sealed record LayoutNameAssignment(int Index, string Requested, string Final, bool Sanitized, bool Truncated, bool Deduplicated)
{
    /// <summary>True when <see cref="Final"/> differs from <see cref="Requested"/>.</summary>
    public bool Changed => !string.Equals(Requested, Final, StringComparison.Ordinal);
}

/// <summary>One layout the run owns (or will own): its current name, null when it does not exist yet, and the name it should have.</summary>
public sealed record OwnedLayout(Guid SegmentId, string? CurrentName, string RequestedName);

/// <summary>One rename step.</summary>
public sealed record LayoutRename(Guid SegmentId, string From, string To);

/// <summary>One layout to create (from the template) with its final name.</summary>
public sealed record LayoutCreate(Guid SegmentId, string Name);

/// <summary>
/// A collision-free rename plan (E307): apply every <see cref="FirstPass"/> rename (current
/// name to a unique temporary name), then every <see cref="SecondPass"/> rename (temporary
/// to final), then the <see cref="Creates"/>. No step ever targets a name that exists at
/// that moment. Layouts whose name does not change get no step.
/// </summary>
public sealed record LayoutRenamePlan(
    IReadOnlyList<LayoutNameAssignment> Assignments,
    IReadOnlyList<LayoutRename> FirstPass,
    IReadOnlyList<LayoutRename> SecondPass,
    IReadOnlyList<LayoutCreate> Creates,
    IReadOnlyList<Problem> Problems)
{
    /// <summary>True when nothing has to be renamed or created.</summary>
    public bool IsEmpty => FirstPass.Count == 0 && SecondPass.Count == 0 && Creates.Count == 0;
}

/// <summary>
/// Layout names: pattern formatting, AutoCAD sanitizing, deterministic de-duplication with a
/// numeric suffix, and the two-pass rename plan used when sheets renumber (E603, E307).
/// Deterministic: the same inputs in the same order always give the same names.
/// </summary>
public sealed class LayoutNamer
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
    private readonly LayoutNameConfig _config;

    /// <summary>Creates a namer and validates the config; an invalid config throws <see cref="ArgumentException"/>.</summary>
    public LayoutNamer(LayoutNameConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        Validate();
    }

    /// <summary>Makes one name valid for AutoCAD: replaces rejected and control characters, trims, falls back when empty, truncates.</summary>
    public SanitizedName Sanitize(string? raw)
    {
        string source = raw ?? string.Empty;
        var sb = new StringBuilder(source.Length);
        bool sanitized = false;
        foreach (char ch in source)
        {
            if (IsRejected(ch)) { sb.Append(_config.Replacement); sanitized = true; }
            else sb.Append(ch);
        }

        string name = sb.ToString();
        if (_config.TrimWhitespace)
        {
            string trimmed = name.Trim();
            if (trimmed.Length != name.Length) { name = trimmed; sanitized = true; }
        }
        if (name.Length == 0) { name = _config.EmptyNameFallback; sanitized = true; }

        bool truncated = false;
        if (name.Length > _config.MaxLayoutNameLength)
        {
            name = Truncate(name, _config.MaxLayoutNameLength);
            if (_config.TrimWhitespace) name = name.TrimEnd();
            truncated = true;
        }
        return new SanitizedName(name, sanitized, truncated);
    }

    /// <summary>
    /// Final names for the requested names, in order. Each is sanitized, then de-duplicated
    /// against <paramref name="occupied"/>, the reserved names and the names already assigned
    /// earlier in the list, by appending the suffix with the first free number.
    /// </summary>
    public IReadOnlyList<LayoutNameAssignment> Assign(IReadOnlyList<string> requested, IEnumerable<string> occupied)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(occupied);
        var taken = new HashSet<string>(occupied, NameComparer);
        foreach (string reserved in _config.ReservedNames) taken.Add(reserved);

        var result = new List<LayoutNameAssignment>(requested.Count);
        for (int i = 0; i < requested.Count; i++)
        {
            SanitizedName s = Sanitize(requested[i]);
            string final = s.Name;
            bool dedup = false;
            if (taken.Contains(final)) { final = Deduplicate(s.Name, taken); dedup = true; }
            taken.Add(final);
            result.Add(new LayoutNameAssignment(i, requested[i] ?? string.Empty, final, s.Sanitized, s.Truncated, dedup));
        }
        return result;
    }

    /// <summary>
    /// Plans renames and creations for the run's layouts. <paramref name="owned"/> is in sheet
    /// order; <paramref name="foreignNames"/> are every other layout in the drawing (the user's,
    /// other runs', orphans), which keep their names. Final names come from <see cref="Assign"/>
    /// with the foreign names occupied. Every changed name goes through a unique temporary name.
    /// </summary>
    public LayoutRenamePlan PlanRenames(IReadOnlyList<OwnedLayout> owned, IEnumerable<string> foreignNames)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(foreignNames);
        var foreign = new HashSet<string>(foreignNames, NameComparer);
        var current = new HashSet<string>(NameComparer);
        foreach (OwnedLayout o in owned)
        {
            if (o.CurrentName is null) continue;
            if (!current.Add(o.CurrentName))
                throw new ArgumentException($"Two owned layouts are both named '{o.CurrentName}'.", nameof(owned));
            if (foreign.Contains(o.CurrentName))
                throw new ArgumentException($"Layout '{o.CurrentName}' is listed as both owned and foreign.", nameof(owned));
        }

        IReadOnlyList<LayoutNameAssignment> assignments = Assign(owned.Select(o => o.RequestedName).ToArray(), foreign);

        var everyName = new HashSet<string>(foreign, NameComparer);
        everyName.UnionWith(current);
        everyName.UnionWith(_config.ReservedNames);
        foreach (LayoutNameAssignment a in assignments) everyName.Add(a.Final);

        var first = new List<LayoutRename>();
        var second = new List<LayoutRename>();
        var creates = new List<LayoutCreate>();
        var problems = new List<Problem>();
        int counter = 1;
        int budget = everyName.Count + owned.Count + 1;

        for (int i = 0; i < owned.Count; i++)
        {
            OwnedLayout o = owned[i];
            LayoutNameAssignment a = assignments[i];
            if (a.Changed) problems.Add(NameProblem(o.SegmentId, a, foreign));

            if (o.CurrentName is null) { creates.Add(new LayoutCreate(o.SegmentId, a.Final)); continue; }
            if (string.Equals(o.CurrentName, a.Final, StringComparison.Ordinal)) continue;

            string temp;
            do
            {
                if (budget-- <= 0) throw new InvalidOperationException("Could not find a free temporary layout name; check TemporaryNameFormat.");
                temp = Sanitize(string.Format(CultureInfo.InvariantCulture, _config.TemporaryNameFormat, counter++)).Name;
            }
            while (!everyName.Add(temp));

            first.Add(new LayoutRename(o.SegmentId, o.CurrentName, temp));
            second.Add(new LayoutRename(o.SegmentId, temp, a.Final));
        }

        return new LayoutRenamePlan(assignments, first, second, creates, problems);
    }

    /// <summary>Formats <see cref="LayoutNameConfig.NamePattern"/> with the parts. The result is not yet sanitized.</summary>
    public string FormatName(LayoutNameParts parts) => FormatPattern(_config.NamePattern, parts, _config.SheetNumberFormat);

    /// <summary>
    /// Replaces the tokens of <paramref name="pattern"/> ({RUN}, {N}, {LABEL}, {FROM}, {TO},
    /// {ALIGN}, case-insensitive; "{{" and "}}" are literal braces). An unknown or unclosed
    /// token throws <see cref="FormatException"/>: a bad pattern fails, it is never guessed at.
    /// </summary>
    public static string FormatPattern(string pattern, LayoutNameParts parts, string sheetNumberFormat)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(parts);
        var sb = new StringBuilder(pattern.Length);
        for (int i = 0; i < pattern.Length; i++)
        {
            char ch = pattern[i];
            if (ch == '{' && i + 1 < pattern.Length && pattern[i + 1] == '{') { sb.Append('{'); i++; continue; }
            if (ch == '}' && i + 1 < pattern.Length && pattern[i + 1] == '}') { sb.Append('}'); i++; continue; }
            if (ch == '}') throw new FormatException($"Unmatched '}}' at position {i} in layout name pattern '{pattern}'.");
            if (ch != '{') { sb.Append(ch); continue; }

            int close = pattern.IndexOf('}', i + 1);
            if (close < 0) throw new FormatException($"Unclosed '{{' at position {i} in layout name pattern '{pattern}'.");
            string token = pattern.Substring(i + 1, close - i - 1);
            sb.Append(token.ToUpperInvariant() switch
            {
                "RUN" => parts.Run,
                "N" => parts.SheetNumber.ToString(sheetNumberFormat, CultureInfo.InvariantCulture),
                "LABEL" => parts.Label,
                "FROM" => parts.FromStation,
                "TO" => parts.ToStation,
                "ALIGN" => parts.Alignment,
                _ => throw new FormatException(
                    $"Unknown token '{{{token}}}' in layout name pattern '{pattern}'. Known tokens: RUN, N, LABEL, FROM, TO, ALIGN."),
            });
            i = close;
        }
        return sb.ToString();
    }

    // D20: orphan layouts keep their names, so when the clean name is held by a layout outside
    // the run (an orphan among them) the message names that layout and points at SVPURGE.
    private Problem NameProblem(Guid segmentId, LayoutNameAssignment a, HashSet<string> foreign)
    {
        var reasons = new List<string>();
        if (a.Sanitized) reasons.Add("characters AutoCAD rejects or surrounding spaces were replaced or removed");
        if (a.Truncated) reasons.Add($"cut to {_config.MaxLayoutNameLength} characters");
        string? holder = null;
        if (a.Deduplicated)
        {
            if (foreign.TryGetValue(Sanitize(a.Requested).Name, out string? held)) holder = held;
            reasons.Add(holder is null
                ? "the name was taken, so a number was added"
                : $"the name is taken by layout '{holder}', which keeps it, so a number was added");
        }
        string tail = holder is null ? "" : $" If '{holder}' is an orphan, SVPURGE removes orphans.";
        return new Problem(ViewProblemCodes.LayoutNameChanged, Severity.Info,
            $"Layout '{a.Requested}' is named '{a.Final}': {string.Join("; ", reasons)}.{tail}", segmentId);
    }

    private string Deduplicate(string baseName, HashSet<string> taken)
    {
        int attempts = taken.Count + 1;
        for (long n = _config.FirstDuplicateNumber; attempts-- > 0; n++)
        {
            string suffix = string.Format(CultureInfo.InvariantCulture, _config.DuplicateSuffixFormat, n);
            string head = Truncate(baseName, _config.MaxLayoutNameLength - suffix.Length);
            if (_config.TrimWhitespace) head = head.TrimEnd();
            string candidate = head + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException($"Could not find a free name for layout '{baseName}'; check DuplicateSuffixFormat.");
    }

    private bool IsRejected(char ch) => char.IsControl(ch) || _config.InvalidCharacters.Contains(ch);

    private static string Truncate(string s, int max)
    {
        if (max <= 0) return string.Empty;
        if (s.Length <= max) return s;
        int cut = max;
        if (char.IsLowSurrogate(s[cut]) && char.IsHighSurrogate(s[cut - 1])) cut--;   // never split a surrogate pair
        return s[..cut];
    }

    private void Validate()
    {
        if (_config.InvalidCharacters is null) throw new ArgumentException("InvalidCharacters is required.");
        if (_config.ReservedNames is null) throw new ArgumentException("ReservedNames is required.");
        if (_config.MaxLayoutNameLength < 1) throw new ArgumentException("MaxLayoutNameLength must be at least 1.");
        if (_config.FirstDuplicateNumber < 0) throw new ArgumentException("FirstDuplicateNumber must not be negative.");
        RequireValid(nameof(_config.Replacement), _config.Replacement ?? throw new ArgumentException("Replacement is required."), allowEmpty: true);
        RequireValid(nameof(_config.EmptyNameFallback), _config.EmptyNameFallback, allowEmpty: false);

        string suffix = FormatSample(nameof(_config.DuplicateSuffixFormat), _config.DuplicateSuffixFormat, _config.FirstDuplicateNumber);
        RequireValid(nameof(_config.DuplicateSuffixFormat), suffix, allowEmpty: false);
        if (suffix.Length >= _config.MaxLayoutNameLength)
            throw new ArgumentException("DuplicateSuffixFormat leaves no room for a name within MaxLayoutNameLength.");

        string temp = FormatSample(nameof(_config.TemporaryNameFormat), _config.TemporaryNameFormat, 1);
        RequireValid(nameof(_config.TemporaryNameFormat), temp, allowEmpty: false);
        if (temp.Length > _config.MaxLayoutNameLength)
            throw new ArgumentException("TemporaryNameFormat is longer than MaxLayoutNameLength.");
    }

    private static string FormatSample(string setting, string? format, long number)
    {
        if (format is null || !format.Contains("{0}", StringComparison.Ordinal))
            throw new ArgumentException($"{setting} must contain {{0}}.");
        return string.Format(CultureInfo.InvariantCulture, format, number);
    }

    private void RequireValid(string setting, string? value, bool allowEmpty)
    {
        if (value is null || (!allowEmpty && value.Length == 0))
            throw new ArgumentException($"{setting} must not be empty.");
        foreach (char ch in value)
            if (IsRejected(ch)) throw new ArgumentException($"{setting} contains a character AutoCAD rejects in layout names: U+{(int)ch:X4}.");
    }
}

/// <summary>Values for the layout name pattern tokens.</summary>
/// <param name="Run">Run name ({RUN}).</param>
/// <param name="SheetNumber">Sheet number, 1-based in sheet order ({N}).</param>
/// <param name="Label">Segment label ({LABEL}).</param>
/// <param name="FromStation">Formatted start station ({FROM}), from <see cref="IAlignmentGeometry.FormatStation"/>.</param>
/// <param name="ToStation">Formatted end station ({TO}).</param>
/// <param name="Alignment">Alignment name ({ALIGN}).</param>
public sealed record LayoutNameParts(string Run, int SheetNumber, string Label, string FromStation, string ToStation, string Alignment);

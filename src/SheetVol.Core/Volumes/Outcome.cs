namespace SheetVol.Core.Volumes;

/// <summary>
/// A value or a refusal. A failed outcome carries at least one <see cref="Severity.Error"/>
/// problem and no value: reading <see cref="Value"/> on a failure throws, so a failed
/// computation can never be mistaken for a number (HANDOFF.md 6.13, no silently wrong numbers).
/// A successful outcome may still carry Info and Warning problems.
/// </summary>
public sealed class Outcome<T>
{
    private readonly T? _value;

    private Outcome(T? value, bool succeeded, IReadOnlyList<Problem> problems)
    {
        _value = value;
        Succeeded = succeeded;
        Problems = problems;
    }

    /// <summary>True when a value exists and no problem is an error.</summary>
    public bool Succeeded { get; }

    /// <summary>Every problem raised, errors first in the order found.</summary>
    public IReadOnlyList<Problem> Problems { get; }

    /// <summary>The value. Throws <see cref="InvalidOperationException"/> when the outcome failed.</summary>
    public T Value => Succeeded
        ? _value!
        : throw new InvalidOperationException(
            "No value: the computation failed. " + string.Join(" | ", Problems.Select(p => $"{p.Code}: {p.Message}")));

    /// <summary>A success, optionally with Info or Warning notes. Passing an error note is a programming error.</summary>
    public static Outcome<T> Ok(T value, IEnumerable<Problem>? notes = null)
    {
        var list = notes?.ToList() ?? [];
        if (list.Any(p => p.Severity == Severity.Error))
            throw new ArgumentException("A successful outcome cannot carry an error problem.", nameof(notes));
        return new Outcome<T>(value, true, list);
    }

    /// <summary>A failure. At least one problem must be an error.</summary>
    public static Outcome<T> Fail(IEnumerable<Problem> problems)
    {
        var list = problems.ToList();
        if (!list.Any(p => p.Severity == Severity.Error))
            throw new ArgumentException("A failed outcome needs at least one error problem.", nameof(problems));
        return new Outcome<T>(default, false, list);
    }

    /// <summary>A failure from one error problem.</summary>
    public static Outcome<T> Fail(Problem problem) => Fail([problem]);
}

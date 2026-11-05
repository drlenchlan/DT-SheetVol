// FX work package. Owner: FX.
//
// Collects the result of every check on a built fixture so one run reports all
// mismatches at once, then fails the build (SV-E192) if any check failed. The
// caller's transaction is then aborted, so a wrong fixture never stays in the drawing.

namespace SheetVol.Civil3D.SelfTest;

internal sealed class FixtureChecks
{
    private readonly List<string> _passed = new();
    private readonly List<string> _failed = new();

    public IReadOnlyList<string> Passed => _passed;
    public IReadOnlyList<string> Failed => _failed;

    public void Pass(string line) => _passed.Add(line);
    public void Fail(string line) => _failed.Add(line);
    public void Note(string line) => _passed.Add(line);

    public void ThrowIfFailed(string what)
    {
        if (_failed.Count == 0) return;
        throw new FixtureException(FixtureCodes.CheckFailed,
            $"{what}: {_failed.Count} check(s) failed: " + string.Join(" | ", _failed));
    }
}

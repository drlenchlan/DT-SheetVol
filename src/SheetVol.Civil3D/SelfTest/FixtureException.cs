// FX work package. Owner: FX.
//
// Stable error codes for the fixture builders. The E19x block is proposed for
// fixture and self-test setup failures; HANDOFF.md section 7 does not list it yet
// (reported to the orchestrator for registration).

namespace SheetVol.Civil3D.SelfTest;

public static class FixtureCodes
{
    /// <summary>A Civil 3D or AutoCAD call failed while building a fixture.</summary>
    public const string BuildFailed = "SV-E190";
    /// <summary>An object the fixture would create already exists and is not the fixture's own.</summary>
    public const string NameInUse = "SV-E191";
    /// <summary>The built object does not match section 8 (elevation, count, station or point).</summary>
    public const string CheckFailed = "SV-E192";
    /// <summary>Unknown fixture name or bad command input.</summary>
    public const string BadInput = "SV-E193";
}

public sealed class FixtureException : Exception
{
    public string Code { get; }

    public FixtureException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }
}

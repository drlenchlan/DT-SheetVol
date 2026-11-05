// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4.
//
// Stable problem codes raised by the regen pipeline, the session and the commands
// (HANDOFF.md 6.13). A code that implements a section 7 row carries that row's
// number; codes from 820 up have no row and are A4's own. Codes never change
// meaning once released.

namespace SheetVol.Civil3D.Session;

/// <summary>Problem codes raised at the pipeline, session and command level.</summary>
public static class PipelineCodes
{
    /// <summary>Error. A run failed; nothing in that run changed. Other runs still process (E801).</summary>
    public const string RunFailed = "SV-E801";

    /// <summary>Info. A stored handle was erased or invalid; the object was recreated and the mapping repaired (E802).</summary>
    public const string Recreated = "SV-E802";

    /// <summary>Warning. A source was found by name because its stored handle no longer matches (E803).</summary>
    public const string MatchedByName = "SV-E803";

    /// <summary>Error. The drawing is read-only or a reference edit is open; regen refuses at step 1 (E807).</summary>
    public const string ReadOnlyDrawing = "SV-E807";

    /// <summary>Warning. Two runs name the same target layout; shared layouts are not supported (E808).</summary>
    public const string SharedLayout = "SV-E808";

    /// <summary>Info. Locked owned layers were unlocked for the writes and locked again; frozen layers left frozen (6.10, E504).</summary>
    public const string LayerRelocked = "SV-E504";

    /// <summary>Info. Nothing changed: no database writes except the persistence timestamp (E811).</summary>
    public const string NothingChanged = "SV-E811";

    /// <summary>Warning. The alignment geometry or its stationing changed since the last regen; segments follow station values (E212).</summary>
    public const string StationingChanged = "SV-E212";

    /// <summary>Error. The run's alignment is missing or its reference is broken; regen fails at step 1 (E213).</summary>
    public const string AlignmentMissing = "SV-E213";

    /// <summary>Error. A picked point could not be placed on the alignment (E217 picks are transformed from the UCS first).</summary>
    public const string PickNotOnAlignment = "SV-E217";

    /// <summary>Error. A regen was cancelled before commit; the drawing is unchanged (E219, E416).</summary>
    public const string Cancelled = "SV-E219";

    /// <summary>Error. A source surface is out of date and RebuildSources is off, or a reference is stale (E404).</summary>
    public const string SourceOutOfDate = "SV-E404";

    /// <summary>Warning. File export skipped because the drawing was never saved (E506).</summary>
    public const string DrawingNeverSaved = "SV-E506";

    /// <summary>Warning. An export file could not be written; drawing changes are kept (E507).</summary>
    public const string ExportWriteFailed = "SV-E507";

    /// <summary>Error. Integrator band cut or fill disagrees with the bounded volume beyond tolerance (E1012).</summary>
    public const string IntegratorMismatch = "SV-E1012";

    /// <summary>Error. HcfcdSegment preset without an OHWM elevation (E1010).</summary>
    public const string OhwmMissing = "SV-E1010";

    /// <summary>Warning. A segment's target layout no longer exists; its table is left where it is or skipped, never re-created elsewhere (E1017).</summary>
    public const string TargetLayoutMissing = "SV-E1017";

    /// <summary>Error. The self-test failed a check (SVSELFTEST; NOW.md assigns SV-E199 to A4).</summary>
    public const string SelfTestFailed = "SV-E199";

    /// <summary>Error. The settings file is unreadable or invalid. C2's own checks report SV-E534 alongside.</summary>
    public const string SettingsInvalid = "SV-E820";

    /// <summary>Info. A setting still holds its placeholder default (A7). Reported once per session.</summary>
    public const string SettingsPlaceholder = "SV-E821";

    /// <summary>Error. An engine component (adapters, writers, persistence) is not wired in this build.</summary>
    public const string NotWired = "SV-E822";

    /// <summary>Error. An unexpected exception reached a command boundary or the pipeline.</summary>
    public const string Unexpected = "SV-E823";

    /// <summary>Error. The named run does not exist in this drawing.</summary>
    public const string RunNotFound = "SV-E824";

    /// <summary>Error. Typed input at a command prompt could not be used.</summary>
    public const string BadInput = "SV-E825";

    /// <summary>Warning. Manual segments are computed outside the partition check (their volume is not part of the band).</summary>
    public const string ManualOutsidePartition = "SV-E826";

    /// <summary>Warning. The integrator could not run on a segment (a surface does not reach it, D10); the cross-check and impact quantities are missing for that segment only.</summary>
    public const string IntegratorSkipped = "SV-E827";

    /// <summary>Error. The datum-method 2D area and the integrator's area disagree beyond tolerance (6.5 cross-check).</summary>
    public const string AreaMismatch = "SV-E828";

    /// <summary>Info. The run has no analyses: boundaries and views only.</summary>
    public const string NoAnalyses = "SV-E829";
}

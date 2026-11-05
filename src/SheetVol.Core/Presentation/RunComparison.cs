using System.Text.Json;
using System.Text.Json.Serialization;

namespace SheetVol.Core.Presentation;

/// <summary>Structural comparisons of run definitions for the dialog's dirty and staleness logic.</summary>
internal static class RunComparison
{
    private static readonly JsonSerializerOptions Options = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>
    /// True when two runs define the same thing. <see cref="RunDefinition.KnownSegments"/>
    /// is ignored: it is identity bookkeeping the planner refreshes, not something the
    /// user edited. <see cref="RunDefinition.Draw"/> null and all-on are the same (contract
    /// rev 2). Records holding lists compare by reference, so this compares JSON.
    /// </summary>
    public static bool SameDefinition(RunDefinition a, RunDefinition b)
        => ReferenceEquals(a, b) || Serialize(a) == Serialize(b);

    /// <summary>
    /// True when a result computed from <paramref name="regenerated"/> still holds for one
    /// segment and analysis of <paramref name="current"/>: same alignment, offsets that apply
    /// to the segment (per-segment overrides included), preset, analysis surfaces and factors,
    /// and for HCFCD the same OHWM. Station ranges are compared by the caller. Display-only
    /// settings (area unit, auto mode, draw options) do not matter.
    /// </summary>
    public static bool VolumeInputsEqual(RunDefinition regenerated, RunDefinition current, Guid analysisId, Guid segmentId)
    {
        if (!string.Equals(regenerated.AlignmentName, current.AlignmentName, StringComparison.Ordinal)) return false;
        if (EffectiveOffsets(regenerated, segmentId) != EffectiveOffsets(current, segmentId)) return false;
        if (regenerated.Preset != current.Preset) return false;

        var a = regenerated.Analyses.FirstOrDefault(x => x.Id == analysisId);
        var b = current.Analyses.FirstOrDefault(x => x.Id == analysisId);
        if (a is null || b is null) return false;
        if (a.BaseSurface != b.BaseSurface || a.ComparisonSurface != b.ComparisonSurface) return false;
        if (a.CutFactor != b.CutFactor || a.FillFactor != b.FillFactor) return false;

        if (current.Preset == ReportPreset.HcfcdSegment && EffectiveOhwm(regenerated, segmentId) != EffectiveOhwm(current, segmentId))
            return false;
        return true;
    }

    public static double? EffectiveOhwm(RunDefinition run, Guid segmentId)
    {
        if (run.Ohwm is null) return null;
        return run.Ohwm.BySegment.TryGetValue(segmentId, out var value) ? value : run.Ohwm.DefaultElevation;
    }

    /// <summary>Signed offsets that apply to one segment: <see cref="SegmentOverrides.OffsetLeft"/> and <see cref="SegmentOverrides.OffsetRight"/> when set (D8), else the run's.</summary>
    public static (double Left, double Right) EffectiveOffsets(RunDefinition run, Guid segmentId)
    {
        run.Overrides.TryGetValue(segmentId, out var overrides);
        return (overrides?.OffsetLeft ?? run.OffsetLeft, overrides?.OffsetRight ?? run.OffsetRight);
    }

    private static string Serialize(RunDefinition run)
        => JsonSerializer.Serialize(run with { KnownSegments = Array.Empty<SegmentDef>(), Draw = run.Draw ?? new DrawOptions() }, Options);
}

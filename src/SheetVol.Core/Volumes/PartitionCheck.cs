using System.Globalization;

namespace SheetVol.Core.Volumes;

/// <summary>Whether the partition check ran and what it found.</summary>
public enum PartitionStatus
{
    /// <summary>Segments sum to the band within tolerance.</summary>
    Passed,

    /// <summary>Segments do not sum to the band. A bug: the run is aborted and nothing is written (6.5).</summary>
    Failed,

    /// <summary>Not run; <see cref="PartitionResult.Note"/> says why (E215 overlap, or a segment error).</summary>
    Skipped,
}

/// <summary>
/// Result of the 6.5 partition check for one analysis. All volumes are raw cubic drawing units.
/// </summary>
/// <param name="Status">Passed, failed or skipped.</param>
/// <param name="SegmentSum">Sum of the segments' raw volumes.</param>
/// <param name="Band">Raw volume of the whole-band polygon(s).</param>
/// <param name="Tolerance">The tolerance applied, cubic drawing units. 0 when skipped.</param>
/// <param name="Note">Why the check was skipped, or the mismatch in words; null on a pass.</param>
public sealed record PartitionResult(PartitionStatus Status, RawVolume SegmentSum, RawVolume Band, double Tolerance, string? Note);

/// <summary>
/// The outside-limits row of 6.5: <c>SurfaceTotals − band</c>, raw cubic drawing units.
/// </summary>
/// <param name="Difference">Surface totals minus band, per component. Always carried for the export.</param>
/// <param name="Tolerance">Tolerance used, cubic drawing units.</param>
/// <param name="Shown">True when either component exceeds the tolerance, so the master table shows the row.</param>
public sealed record OutsideLimitsResult(RawVolume Difference, double Tolerance, bool Shown);

/// <summary>Pure functions for the partition check and the outside-limits row (HANDOFF.md 6.5).</summary>
public static class PartitionCheck
{
    /// <summary>
    /// Compares the sum of the segment volumes with the band volume, cut and fill separately,
    /// within <c>max(0.5 ft³, 1e-9 × (band cut + band fill))</c>. The band must exclude skip
    /// gaps (E303) so their volume lands in the outside-limits row instead of failing the check.
    /// Pass <paramref name="skipReason"/> when boundaries overlap (E215); the check then reports
    /// <see cref="PartitionStatus.Skipped"/> with that reason and never fails.
    /// </summary>
    public static PartitionResult Run(
        IReadOnlyList<RawVolume> segments, RawVolume band, QuantityUnits units, string? skipReason = null)
    {
        var sum = Sum(segments);
        if (skipReason is not null)
            return new PartitionResult(PartitionStatus.Skipped, sum, band, 0, skipReason);

        double tol = units.PartitionTolerance(band.Cut + band.Fill);
        double dCut = sum.Cut - band.Cut;
        double dFill = sum.Fill - band.Fill;
        if (Math.Abs(dCut) <= tol && Math.Abs(dFill) <= tol)
            return new PartitionResult(PartitionStatus.Passed, sum, band, tol, null);

        string note = string.Create(CultureInfo.InvariantCulture,
            $"Segments sum to cut {sum.Cut:R}, fill {sum.Fill:R}; the band is cut {band.Cut:R}, fill {band.Fill:R} " +
            $"(differences {dCut:R}, {dFill:R}; tolerance {tol:R} cubic drawing units).");
        return new PartitionResult(PartitionStatus.Failed, sum, band, tol, note);
    }

    /// <summary>
    /// <c>SurfaceTotals − band</c> with the same tolerance formula applied to the surface gross total.
    /// Shown only above tolerance (6.5). A component below <c>−tolerance</c> is impossible for
    /// correct inputs and is returned as an SV-E439 failure, never as a negative row.
    /// </summary>
    public static Outcome<OutsideLimitsResult> OutsideLimits(RawVolume surfaceTotals, RawVolume band, QuantityUnits units)
    {
        double tol = units.PartitionTolerance(surfaceTotals.Cut + surfaceTotals.Fill);
        var diff = new RawVolume(surfaceTotals.Cut - band.Cut, surfaceTotals.Fill - band.Fill);
        if (diff.Cut < -tol || diff.Fill < -tol)
            return Outcome<OutsideLimitsResult>.Fail(new Problem(
                VolumeCodes.OutsideLimitsNegative, Severity.Error,
                string.Create(CultureInfo.InvariantCulture,
                    $"The band (cut {band.Cut:R}, fill {band.Fill:R}) exceeds the whole-surface totals " +
                    $"(cut {surfaceTotals.Cut:R}, fill {surfaceTotals.Fill:R}) beyond tolerance {tol:R} cubic drawing units.")));
        bool shown = Math.Abs(diff.Cut) > tol || Math.Abs(diff.Fill) > tol;
        return Outcome<OutsideLimitsResult>.Ok(new OutsideLimitsResult(diff, tol, shown));
    }

    /// <summary>Component-wise sum of raw volumes.</summary>
    public static RawVolume Sum(IEnumerable<RawVolume> volumes)
    {
        double c = 0, f = 0;
        foreach (var v in volumes)
        {
            c += v.Cut;
            f += v.Fill;
        }
        return new RawVolume(c, f);
    }
}

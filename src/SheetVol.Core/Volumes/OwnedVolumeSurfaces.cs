namespace SheetVol.Core.Volumes;

/// <summary>
/// One owned volume surface: a distinct base/comparison pair and the analyses that share it (E411).
/// </summary>
/// <param name="BaseSurface">Base surface name.</param>
/// <param name="ComparisonSurface">Comparison surface name.</param>
/// <param name="Name">Owned surface name, <c>SV-&lt;base&gt;-&lt;comparison&gt;</c> by default (6.5). The adapter resolves collisions (E412).</param>
/// <param name="CutFactor">Cut factor written to the owned surface: the first sharing analysis's.</param>
/// <param name="FillFactor">Fill factor written to the owned surface: the first sharing analysis's.</param>
/// <param name="AnalysisIds">Analyses that read this surface, in run order.</param>
public sealed record OwnedSurfacePlan(
    string BaseSurface, string ComparisonSurface, string Name,
    double CutFactor, double FillFactor,
    IReadOnlyList<Guid> AnalysisIds);

/// <summary>Plans one owned volume surface per distinct base/comparison pair (6.5, E411).</summary>
public static class OwnedVolumeSurfaces
{
    /// <summary>
    /// Groups analyses by exact (ordinal) base and comparison names. Rejects empty names,
    /// duplicate ids, a surface compared with itself (E401) and invalid factors (E410).
    /// Analyses sharing a pair with different factors get a warning: the Volumes Dashboard on
    /// the shared surface can agree with only one of them.
    /// </summary>
    public static Outcome<IReadOnlyList<OwnedSurfacePlan>> Plan(IReadOnlyList<AnalysisDef> analyses, VolumeConfig config)
    {
        var problems = new List<Problem>();
        problems.AddRange(Validate(analyses));
        if (problems.Any(p => p.Severity == Severity.Error))
            return Outcome<IReadOnlyList<OwnedSurfacePlan>>.Fail(problems);

        var plans = new List<OwnedSurfacePlan>();
        foreach (var group in analyses.GroupBy(a => (a.BaseSurface, a.ComparisonSurface)))
        {
            var first = group.First();
            foreach (var other in group.Skip(1))
            {
                if (other.CutFactor != first.CutFactor || other.FillFactor != first.FillFactor)
                    problems.Add(new Problem(
                        VolumeCodes.SharedPairFactorMismatch, Severity.Warning,
                        $"Analyses '{first.Name}' and '{other.Name}' share {first.BaseSurface} / {first.ComparisonSurface} " +
                        $"but not their factors. The owned surface carries the factors of '{first.Name}'; the tables use each analysis's own.",
                        Fix: "Give both analyses the same factors, or compare different surfaces."));
            }
            string name = config.OwnedSurfacePrefix + first.BaseSurface + config.OwnedSurfaceSeparator + first.ComparisonSurface;
            plans.Add(new OwnedSurfacePlan(
                first.BaseSurface, first.ComparisonSurface, name,
                first.CutFactor, first.FillFactor,
                group.Select(a => a.Id).ToList()));
        }
        return Outcome<IReadOnlyList<OwnedSurfacePlan>>.Ok(plans, problems);
    }

    /// <summary>Definition checks shared with the reconciler: names, ids, same surface, factors.</summary>
    public static IReadOnlyList<Problem> Validate(IReadOnlyList<AnalysisDef> analyses)
    {
        var problems = new List<Problem>();
        foreach (var dup in analyses.GroupBy(a => a.Id).Where(g => g.Count() > 1))
            problems.Add(new Problem(VolumeCodes.DuplicateAnalysisId, Severity.Error,
                $"{dup.Count()} analyses share the id {dup.Key}."));

        foreach (var a in analyses)
        {
            if (string.IsNullOrWhiteSpace(a.Name) || string.IsNullOrWhiteSpace(a.BaseSurface) || string.IsNullOrWhiteSpace(a.ComparisonSurface))
            {
                problems.Add(new Problem(VolumeCodes.InvalidAnalysis, Severity.Error,
                    $"Analysis {a.Id} needs a name, a base surface and a comparison surface.",
                    Fix: "Complete the analysis in SVANALYSIS."));
                continue;
            }
            if (string.Equals(a.BaseSurface, a.ComparisonSurface, StringComparison.Ordinal))
                problems.Add(new Problem(VolumeCodes.SameSurface, Severity.Error,
                    $"Analysis '{a.Name}' compares {a.BaseSurface} with itself.",
                    Fix: "Pick a different comparison surface."));
            if (!IsValidFactor(a.CutFactor) || !IsValidFactor(a.FillFactor))
                problems.Add(new Problem(VolumeCodes.InvalidFactor, Severity.Error,
                    $"Analysis '{a.Name}' has cut factor {a.CutFactor.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"and fill factor {a.FillFactor.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}; both must be numbers above zero.",
                    Fix: "Enter factors above zero, 1.0 for none."));
        }
        return problems;
    }

    private static bool IsValidFactor(double f) => double.IsFinite(f) && f > 0;
}

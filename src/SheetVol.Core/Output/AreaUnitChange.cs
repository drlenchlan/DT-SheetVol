using SheetVol.Core.Volumes;

namespace SheetVol.Core.Output;

/// <summary>Result of switching the run's area unit.</summary>
/// <param name="Run">The run with the new area unit, to save with the run (E917).</param>
/// <param name="TablesOutOfDate">True when the unit changed, so drawn tables are marked out of date until regen (E418).</param>
public sealed record AreaUnitChangeResult(RunDefinition Run, bool TablesOutOfDate);

/// <summary>
/// E418: switching the area unit is display only. Nothing is recomputed and the raw area in
/// square drawing units is unchanged; tables and exports reformat from it on the next build.
/// </summary>
public static class AreaUnitChange
{
    /// <summary>Sets the run's area unit. A unit from the other unit system is refused (SV-E433).</summary>
    public static Outcome<AreaUnitChangeResult> Apply(RunDefinition run, AreaUnit newUnit, UnitSystem system)
    {
        if (QuantityUnits.CheckAreaUnit(system, newUnit) is { } mismatch)
            return Outcome<AreaUnitChangeResult>.Fail(mismatch);
        return Outcome<AreaUnitChangeResult>.Ok(new AreaUnitChangeResult(run with { AreaUnit = newUnit }, newUnit != run.AreaUnit));
    }
}

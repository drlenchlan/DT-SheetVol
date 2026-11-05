namespace SheetVol.Core.Tests.Fakes;

/// <summary>Builds <see cref="RunDefinition"/> values for tests with everything not under test at its default.</summary>
public static class TestRuns
{
    /// <summary>A Stations-driver run on <paramref name="alignment"/>.</summary>
    public static RunDefinition Stations(
        IAlignmentGeometry alignment, string breakSpec, double offsetLeft = -100, double offsetRight = 100,
        IReadOnlyList<SegmentDef>? known = null,
        IReadOnlyList<ManualSegmentDef>? manual = null,
        IReadOnlyDictionary<Guid, SegmentOverrides>? overrides = null) =>
        new("Run1", alignment.Name, SegmentDriver.Stations, breakSpec, null, offsetLeft, offsetRight,
            [], manual ?? [], known ?? [], overrides ?? new Dictionary<Guid, SegmentOverrides>(), new ViewOptions());
}

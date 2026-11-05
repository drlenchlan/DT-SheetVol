namespace SheetVol.Core.Presentation;

/// <summary>Everything one grid row and one chart bar need, computed once by the dialog.</summary>
/// <param name="Segment">The planned segment from the last accepted validation.</param>
/// <param name="Number">1-based row number.</param>
/// <param name="StationsEditable">Begin and end cells editable (Stations driver, not a manual segment, not busy).</param>
/// <param name="Values">The result for the selected analysis when it still applies, otherwise null.</param>
/// <param name="Status">Status chip.</param>
/// <param name="Problem">The most severe problem for the row, shown under it with its fix.</param>
/// <param name="SheetLayout">Layout that receives the segment's table (A14), or null.</param>
/// <param name="OhwmEffective">OHWM elevation that applies to the segment, or null.</param>
/// <param name="OhwmIsOverride">True when the segment overrides the run's OHWM.</param>
internal sealed record RowModel(
    SegmentDef Segment,
    int Number,
    bool StationsEditable,
    SegmentResult? Values,
    SegmentStatus Status,
    Problem? Problem,
    string? SheetLayout,
    double? OhwmEffective,
    bool OhwmIsOverride);

/// <summary>Display context shared by the grid and the chart for one refresh.</summary>
internal sealed record GridContext(
    DisplayFormatter Fmt,
    Func<double, string> FormatStation,
    ReportPreset Preset,
    AreaUnit AreaUnit,
    RegenResult? Result,
    Guid? SelectedId,
    bool IsBusy);

/// <summary>What the grid asks of its owner, the dialog.</summary>
internal interface IGridOwner
{
    string? EditStation(Guid segmentId, bool isEnd, string text);
    string? EditOhwm(Guid segmentId, string text);
    void AddStation(Guid segmentId);
    Task PickStationAsync();
    void RemoveSegment(Guid segmentId);
    void ZoomTo(Guid segmentId);
    void SelectionChanged(Guid? segmentId);
    void ComparisonChanged();
    void FieldErrorChanged();
    bool CanEditStations { get; }
    bool CanPickStation { get; }
    bool CanRemove(Guid segmentId);
    bool CanZoom { get; }
    IReadOnlyList<AreaUnitOption> AreaUnits { get; }
    AreaUnitOption? SelectedAreaUnit { get; set; }
}

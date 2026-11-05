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
/// <param name="OffsetFixAvailable">True when the row shows the per-segment offset fix: a station segment in Error, or one that already overrides the offsets (6.14, D8).</param>
/// <param name="OffsetLeftEffective">Signed left offset that applies to the segment: its override, else the run's.</param>
/// <param name="OffsetRightEffective">Signed right offset that applies to the segment: its override, else the run's.</param>
/// <param name="OffsetIsOverride">True when <see cref="SegmentOverrides.OffsetLeft"/> or <see cref="SegmentOverrides.OffsetRight"/> is set for the segment.</param>
internal sealed record RowModel(
    SegmentDef Segment,
    int Number,
    bool StationsEditable,
    SegmentResult? Values,
    SegmentStatus Status,
    Problem? Problem,
    string? SheetLayout,
    double? OhwmEffective,
    bool OhwmIsOverride,
    bool OffsetFixAvailable,
    double OffsetLeftEffective,
    double OffsetRightEffective,
    bool OffsetIsOverride);

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
    string? ApplyOffsetOverride(Guid segmentId, string leftText, string rightText);
    void AddStation(Guid segmentId);
    Task PickStationAsync();
    void RemoveSegment(Guid segmentId);
    void ZoomTo(Guid segmentId);
    void SelectionChanged(Guid? segmentId);
    void ComparisonChanged();
    void FieldErrorChanged();
    bool CanEditStations { get; }
    bool CanEditOffsets { get; }
    bool CanPickStation { get; }
    bool CanRemove(Guid segmentId);
    bool CanZoom { get; }
    IReadOnlyList<AreaUnitOption> AreaUnits { get; }
    AreaUnitOption? SelectedAreaUnit { get; set; }
}

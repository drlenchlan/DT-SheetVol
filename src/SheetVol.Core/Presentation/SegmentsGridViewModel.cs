using System.Collections.ObjectModel;

namespace SheetVol.Core.Presentation;

/// <summary>
/// The segments grid (6.14): rows, total row (hidden until every segment has a result),
/// reconciliation line, comparison selector above the grid when there is more than one
/// analysis (E916), the area unit dropdown in the 2D area header (E917), the HCFCD
/// column set (6.16), and the toolbar: Add station, Pick in drawing, Remove, Zoom to.
/// Rows are updated in place so selection and focus survive live validation.
/// </summary>
public sealed class SegmentsGridViewModel : ObservableObject
{
    private readonly IGridOwner _owner;
    private SegmentRowViewModel? _selectedRow;
    private ComparisonOption? _selectedComparison;
    private bool _totalsVisible;
    private bool _isHcfcd;
    private string _reconciliationText = string.Empty;
    private DisplayFormatter? _fmt;
    private bool _suppressCallbacks;

    internal SegmentsGridViewModel(IGridOwner owner)
    {
        _owner = owner;
        AddStationCommand = new RelayCommand(
            () => { if (_selectedRow is { } r) _owner.AddStation(r.SegmentId); },
            () => _selectedRow is { BeginStation.IsEditable: true } && _owner.CanEditStations);
        PickStationCommand = new AsyncRelayCommand(() => _owner.PickStationAsync(), () => _owner.CanPickStation);
        RemoveCommand = new RelayCommand(
            () => { if (_selectedRow is { } r) _owner.RemoveSegment(r.SegmentId); },
            () => _selectedRow is { } r && _owner.CanRemove(r.SegmentId));
        ZoomToCommand = new RelayCommand(
            () => { if (_selectedRow is { } r) _owner.ZoomTo(r.SegmentId); },
            () => _selectedRow is not null && _owner.CanZoom);
    }

    /// <summary>Grid rows in plan order.</summary>
    public ObservableCollection<SegmentRowViewModel> Rows { get; } = [];

    /// <summary>The total row.</summary>
    public TotalRowViewModel Total { get; } = new();

    /// <summary>True when every segment has a result and none is in error (6.14).</summary>
    public bool TotalsVisible { get => _totalsVisible; private set => SetProperty(ref _totalsVisible, value); }

    /// <summary>The one reconciliation line under the grid, for the selected analysis (E916).</summary>
    public string ReconciliationText { get => _reconciliationText; private set => SetProperty(ref _reconciliationText, value); }

    /// <summary>True when the HCFCD column set is shown.</summary>
    public bool IsHcfcd
    {
        get => _isHcfcd;
        private set
        {
            if (SetProperty(ref _isHcfcd, value)) OnPropertyChanged(nameof(IsStandard));
        }
    }

    /// <summary>True when the Standard column set is shown.</summary>
    public bool IsStandard => !_isHcfcd;

    /// <summary>The selected row; selecting highlights its boundary in the drawing (E911).</summary>
    public SegmentRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value)) return;
            RefreshCommands();
            if (!_suppressCallbacks) _owner.SelectionChanged(value?.SegmentId);
        }
    }

    /// <summary>Analyses offered by the selector above the grid (E916).</summary>
    public ObservableCollection<ComparisonOption> Comparisons { get; } = [];

    /// <summary>The analysis that drives the 2D area, cut, fill and net columns and the chart (E916).</summary>
    public ComparisonOption? SelectedComparison
    {
        get => _selectedComparison;
        set
        {
            if (!SetProperty(ref _selectedComparison, value)) return;
            if (!_suppressCallbacks) _owner.ComparisonChanged();
        }
    }

    /// <summary>True when there is more than one analysis (E916).</summary>
    public bool ShowComparisonSelector => Comparisons.Count > 1;

    /// <summary>Units offered in the area header dropdown; follow the drawing (E917).</summary>
    public IReadOnlyList<AreaUnitOption> AreaUnits => _owner.AreaUnits;

    /// <summary>Selected area unit; changing it reformats the grid at once and is saved with the run (E917, E418).</summary>
    public AreaUnitOption? SelectedAreaUnit
    {
        get => _owner.SelectedAreaUnit;
        set => _owner.SelectedAreaUnit = value;
    }

    /// <summary>Splits the selected segment at its middle station.</summary>
    public RelayCommand AddStationCommand { get; }

    /// <summary>Adds a break at a station picked in the drawing.</summary>
    public AsyncRelayCommand PickStationCommand { get; }

    /// <summary>Removes the selected segment by merging it into a neighbour.</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>Zooms the drawing to the selected segment.</summary>
    public RelayCommand ZoomToCommand { get; }

    /// <summary>Header of the # column.</summary>
    public string NumberHeader => DialogText.ColumnNumber;
    /// <summary>Header of the begin column.</summary>
    public string BeginHeader => DialogText.ColumnBegin;
    /// <summary>Header of the end column.</summary>
    public string EndHeader => DialogText.ColumnEnd;
    /// <summary>Header of the length column, with its unit.</summary>
    public string LengthHeader => WithUnit(DialogText.ColumnLength, _fmt?.LengthUnit);
    /// <summary>Header of the 2D area column; the unit is in the dropdown beside it.</summary>
    public string Area2DHeader => DialogText.ColumnArea2D;
    /// <summary>Header of the cut column, with its unit.</summary>
    public string CutHeader => WithUnit(DialogText.ColumnCut, _fmt?.VolumeUnit);
    /// <summary>Header of the fill column, with its unit.</summary>
    public string FillHeader => WithUnit(DialogText.ColumnFill, _fmt?.VolumeUnit);
    /// <summary>Header of the net column, with its unit.</summary>
    public string NetHeader => WithUnit(DialogText.ColumnNet, _fmt?.VolumeUnit);
    /// <summary>Header of the coverage column.</summary>
    public string CoverageHeader => DialogText.ColumnCoverage;
    /// <summary>Header of the status column.</summary>
    public string StatusHeader => DialogText.ColumnStatus;
    /// <summary>HCFCD header of the sheet column.</summary>
    public string SheetHeader => DialogText.ColumnSheet;
    /// <summary>HCFCD header of the OHWM column, with its unit.</summary>
    public string OhwmHeader => WithUnit(DialogText.ColumnOhwm, _fmt?.LengthUnit);
    /// <summary>HCFCD header of the impacted length column, with its unit.</summary>
    public string ImpactedLengthHeader => WithUnit(DialogText.ColumnImpactedLength, _fmt?.LengthUnit);
    /// <summary>HCFCD header of the impacted area column; the unit is in the dropdown beside it.</summary>
    public string ImpactedAreaHeader => DialogText.ColumnImpactedArea;
    /// <summary>HCFCD header of the cut volume column, with its unit.</summary>
    public string CutVolumeHeader => WithUnit(DialogText.ColumnCutVolume, _fmt?.VolumeUnit);
    /// <summary>HCFCD header of the average column, with its unit.</summary>
    public string AverageHeader => WithUnit(DialogText.ColumnAverage, _fmt?.AverageUnit);

    internal Guid? SelectedAnalysisId => _selectedComparison?.AnalysisId;

    internal bool HasFieldError => Rows.Any(r => r.HasFieldError);

    internal void SyncComparisons(IReadOnlyList<AnalysisDef> analyses)
    {
        var keep = _selectedComparison?.AnalysisId;
        _suppressCallbacks = true;
        try
        {
            var wanted = analyses.Select(a => new ComparisonOption(a.Id, a.Name)).ToList();
            if (!wanted.SequenceEqual(Comparisons))
            {
                Comparisons.Clear();
                foreach (var option in wanted) Comparisons.Add(option);
            }
            SelectedComparison = Comparisons.FirstOrDefault(c => c.AnalysisId == keep) ?? Comparisons.FirstOrDefault();
        }
        finally
        {
            _suppressCallbacks = false;
        }
        OnPropertyChanged(nameof(ShowComparisonSelector));
    }

    internal void Update(IReadOnlyList<RowModel> rows, GridContext ctx)
    {
        _fmt = ctx.Fmt;
        IsHcfcd = ctx.Preset == ReportPreset.HcfcdSegment;

        for (var i = 0; i < rows.Count; i++)
        {
            if (i == Rows.Count) Rows.Add(new SegmentRowViewModel(_owner));
            Rows[i].Apply(rows[i], ctx);
        }
        _suppressCallbacks = true;
        try
        {
            while (Rows.Count > rows.Count) Rows.RemoveAt(Rows.Count - 1);
            SelectedRow = ctx.SelectedId is Guid id ? Rows.FirstOrDefault(r => r.SegmentId == id) : null;
        }
        finally
        {
            _suppressCallbacks = false;
        }

        var visible = rows.Count > 0 && rows.All(r => r.Values is not null && r.Status != SegmentStatus.Error);
        TotalsVisible = visible;
        if (visible) Total.Apply(rows, ctx);
        else Total.Clear();
        ReconciliationText = visible ? Reconciliation(ctx) : rows.Count > 0 ? DialogText.TotalsPending : string.Empty;

        OnPropertiesChanged(
            nameof(LengthHeader), nameof(CutHeader), nameof(FillHeader), nameof(NetHeader),
            nameof(OhwmHeader), nameof(ImpactedLengthHeader), nameof(CutVolumeHeader), nameof(AverageHeader),
            nameof(AreaUnits), nameof(SelectedAreaUnit));
        RefreshCommands();
    }

    /// <summary>Selects the row of a segment, as a click in the chart does.</summary>
    internal void SelectSegment(Guid? segmentId)
        => SelectedRow = segmentId is Guid id ? Rows.FirstOrDefault(r => r.SegmentId == id) : null;

    internal void RefreshCommands()
    {
        AddStationCommand.RaiseCanExecuteChanged();
        PickStationCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        ZoomToCommand.RaiseCanExecuteChanged();
        foreach (var row in Rows) row.RefreshCommands();
    }

    private string Reconciliation(GridContext ctx)
    {
        var fmt = ctx.Fmt;
        if (ctx.Preset == ReportPreset.HcfcdSegment)
        {
            var area = fmt.AreaDisplay(ctx.AreaUnit);
            return string.Format(fmt.Culture, DialogText.ReconciliationHcfcdFormat,
                fmt.Config.ImpactedLength.Format(Total.ImpactedLength, fmt.Culture), fmt.LengthUnit,
                area.Format(Total.ImpactedArea, fmt.Culture), DisplayFormatter.AreaUnitLabel(ctx.AreaUnit),
                fmt.Volume(Total.CutVolume), fmt.VolumeUnit);
        }

        // RegenResult.OutsideLimits holds one entry per analysis above tolerance (contract rev 2);
        // the line shows the one for the analysis the selector drives (E916).
        var selected = SelectedAnalysisId is Guid id ? ctx.Result?.OutsideLimits.FirstOrDefault(o => o.AnalysisId == id) : null;
        var outside = selected is { Raw: var o }
            ? string.Format(fmt.Culture, DialogText.OutsideLimitsFormat,
                fmt.Volume(fmt.VolumeFromCubicDrawingUnits(o.Cut)),
                fmt.Volume(fmt.VolumeFromCubicDrawingUnits(o.Fill)),
                fmt.VolumeUnit)
            : DialogText.OutsideLimitsNone;
        var partition = ctx.Result?.PartitionCheckPassed == true ? DialogText.PartitionPassed : DialogText.PartitionFailed;
        return string.Format(fmt.Culture, DialogText.ReconciliationFormat,
            fmt.Volume(Total.Cut), fmt.Volume(Total.Fill), fmt.VolumeUnit, outside, partition);
    }

    private string WithUnit(string header, string? unit)
        => unit is null || _fmt is null ? header : _fmt.Header(header, unit);
}

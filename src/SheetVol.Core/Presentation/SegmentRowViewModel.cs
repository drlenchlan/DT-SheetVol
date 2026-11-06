namespace SheetVol.Core.Presentation;

/// <summary>
/// One row of the segments grid (6.14): number, begin and end station (editable, parsed
/// by the session's Validate), length, 2D area in the selected unit, cut, fill, net with
/// its CUT or FILL tag, coverage, status chip, and the row's problem with its fix. A
/// failing station segment also offers the per-segment offset fix (two distance fields and
/// Apply, D8). The HCFCD columns (6.16) are filled when that preset is active. Value texts
/// are empty until the row has a result that still applies.
/// </summary>
public sealed class SegmentRowViewModel : ObservableObject
{
    private DisplayFormatter? _fmt;
    private bool _showOffsetFix;
    private bool _offsetIsOverride;
    private string? _offsetFixError;
    private Guid _segmentId;
    private int _number;
    private string _label = string.Empty;
    private double _beginRaw;
    private double _endRaw;
    private double _length;
    private string _lengthText = string.Empty;
    private bool _hasValues;
    private double? _area2D;
    private double? _cut;
    private double? _fill;
    private double? _net;
    private double? _coverage;
    private string _area2DText = DialogText.NoValue;
    private string _cutText = DialogText.NoValue;
    private string _fillText = DialogText.NoValue;
    private string _netText = DialogText.NoValue;
    private string _netTag = string.Empty;
    private string _coverageText = DialogText.NoValue;
    private bool _coverageIsLow;
    private SegmentStatus _status = SegmentStatus.OutOfDate;
    private string? _problemCode;
    private string? _message;
    private string? _fix;
    private string? _sheetLayout;
    private string _impactedLengthText = DialogText.NoValue;
    private string _impactedAreaText = DialogText.NoValue;
    private string _cutVolumeText = DialogText.NoValue;
    private string _averageText = DialogText.NoValue;
    private double? _cutVolume;
    private string? _impactNote;

    internal SegmentRowViewModel(IGridOwner owner)
    {
        BeginStation = new TextFieldViewModel(DialogText.ColumnBegin, t => owner.EditStation(SegmentId, isEnd: false, t), owner.FieldErrorChanged);
        EndStation = new TextFieldViewModel(DialogText.ColumnEnd, t => owner.EditStation(SegmentId, isEnd: true, t), owner.FieldErrorChanged);
        OffsetFixLeft = new TextFieldViewModel(DialogText.OffsetFixLeft, CheckOffsetText, owner.FieldErrorChanged);
        OffsetFixRight = new TextFieldViewModel(DialogText.OffsetFixRight, CheckOffsetText, owner.FieldErrorChanged);
        ApplyOffsetFixCommand = new RelayCommand(
            () => OffsetFixError = owner.ApplyOffsetOverride(SegmentId, OffsetFixLeft.Text, OffsetFixRight.Text),
            () => _showOffsetFix && owner.CanEditOffsets);
    }

    /// <summary>Segment GUID.</summary>
    public Guid SegmentId { get => _segmentId; private set => SetProperty(ref _segmentId, value); }

    /// <summary>1-based row number (the # column).</summary>
    public int Number { get => _number; private set => SetProperty(ref _number, value); }

    /// <summary>Planner label of the segment.</summary>
    public string Label { get => _label; private set => SetProperty(ref _label, value); }

    /// <summary>Begin station cell. Shares its break with the previous row's end (E904).</summary>
    public TextFieldViewModel BeginStation { get; }

    /// <summary>End station cell. Shares its break with the next row's begin (E904).</summary>
    public TextFieldViewModel EndStation { get; }

    /// <summary>Raw begin station of the last accepted plan.</summary>
    public double BeginRaw { get => _beginRaw; private set => SetProperty(ref _beginRaw, value); }

    /// <summary>Raw end station of the last accepted plan.</summary>
    public double EndRaw { get => _endRaw; private set => SetProperty(ref _endRaw, value); }

    /// <summary>Length along the alignment, drawing length units.</summary>
    public double Length { get => _length; private set => SetProperty(ref _length, value); }

    /// <summary>Formatted length.</summary>
    public string LengthText { get => _lengthText; private set => SetProperty(ref _lengthText, value); }

    /// <summary>True when the row shows values from a result that still applies.</summary>
    public bool HasValues { get => _hasValues; private set => SetProperty(ref _hasValues, value); }

    /// <summary>2D area in the selected display unit, unrounded; null without values.</summary>
    public double? Area2D { get => _area2D; private set => SetProperty(ref _area2D, value); }

    /// <summary>Cut after factors in the volume unit; null without values.</summary>
    public double? Cut { get => _cut; private set => SetProperty(ref _cut, value); }

    /// <summary>Fill after factors in the volume unit; null without values.</summary>
    public double? Fill { get => _fill; private set => SetProperty(ref _fill, value); }

    /// <summary>Net = cut − fill (A5); null without values.</summary>
    public double? Net { get => _net; private set => SetProperty(ref _net, value); }

    /// <summary>Coverage 0..1; null without values.</summary>
    public double? Coverage { get => _coverage; private set => SetProperty(ref _coverage, value); }

    /// <summary>Formatted 2D area in the selected unit.</summary>
    public string Area2DText { get => _area2DText; private set => SetProperty(ref _area2DText, value); }

    /// <summary>Formatted cut.</summary>
    public string CutText { get => _cutText; private set => SetProperty(ref _cutText, value); }

    /// <summary>Formatted fill.</summary>
    public string FillText { get => _fillText; private set => SetProperty(ref _fillText, value); }

    /// <summary>Formatted magnitude of net.</summary>
    public string NetText { get => _netText; private set => SetProperty(ref _netText, value); }

    /// <summary>CUT, FILL, or empty when the displayed net is zero (E414).</summary>
    public string NetTag { get => _netTag; private set => SetProperty(ref _netTag, value); }

    /// <summary>Formatted coverage percentage.</summary>
    public string CoverageText { get => _coverageText; private set => SetProperty(ref _coverageText, value); }

    /// <summary>Coverage below full: zero because identical is told apart from zero because no data (6.5).</summary>
    public bool CoverageIsLow { get => _coverageIsLow; private set => SetProperty(ref _coverageIsLow, value); }

    /// <summary>Status chip value.</summary>
    public SegmentStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertiesChanged(nameof(StatusText), nameof(IsError));
        }
    }

    /// <summary>Status chip text: Current, Out of date or Error.</summary>
    public string StatusText => _status switch
    {
        SegmentStatus.Current => DialogText.StatusCurrent,
        SegmentStatus.Error => DialogText.StatusError,
        _ => DialogText.StatusOutOfDate,
    };

    /// <summary>True for a failing row; the view tints it.</summary>
    public bool IsError => _status == SegmentStatus.Error;

    /// <summary>Code of the row's problem (SV-Exxx), or null.</summary>
    public string? ProblemCode { get => _problemCode; private set => SetProperty(ref _problemCode, value); }

    /// <summary>The row's problem in plain words, shown under the row; null when none.</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }

    /// <summary>The fix shown beside the message; null when none.</summary>
    public string? Fix { get => _fix; private set => SetProperty(ref _fix, value); }

    /// <summary>True when <see cref="Message"/> is set.</summary>
    public bool HasMessage => _message is not null;

    /// <summary>HCFCD: layout that receives the table, or null (model space).</summary>
    public string? SheetLayout { get => _sheetLayout; private set => SetProperty(ref _sheetLayout, value); }

    /// <summary>HCFCD: impacted length.</summary>
    public string ImpactedLengthText { get => _impactedLengthText; private set => SetProperty(ref _impactedLengthText, value); }

    /// <summary>HCFCD: impacted area in the selected unit.</summary>
    public string ImpactedAreaText { get => _impactedAreaText; private set => SetProperty(ref _impactedAreaText, value); }

    /// <summary>HCFCD: cut volume below OHWM in the volume unit.</summary>
    public string CutVolumeText { get => _cutVolumeText; private set => SetProperty(ref _cutVolumeText, value); }

    /// <summary>HCFCD: cut volume ÷ impacted length from unrounded values, or N/A (E1019).</summary>
    public string AverageText { get => _averageText; private set => SetProperty(ref _averageText, value); }

    /// <summary>HCFCD: cut volume below OHWM in the volume unit, unrounded; null without values.</summary>
    public double? CutVolume { get => _cutVolume; private set => SetProperty(ref _cutVolume, value); }

    /// <summary>HCFCD: note when the values are marked, for example "widen the offsets" (E1002).</summary>
    public string? ImpactNote { get => _impactNote; private set => SetProperty(ref _impactNote, value); }

    // ---- Per-segment offset fix (6.14, D8) -------------------------------------------------

    /// <summary>True when the row shows the offset fix: a station segment in Error, or one whose offsets are overridden.</summary>
    public bool ShowOffsetFix { get => _showOffsetFix; private set => SetProperty(ref _showOffsetFix, value); }

    /// <summary>True when the segment's offsets override the run's (<see cref="SegmentOverrides.OffsetLeft"/> or <see cref="SegmentOverrides.OffsetRight"/>).</summary>
    public bool OffsetIsOverride { get => _offsetIsOverride; private set => SetProperty(ref _offsetIsOverride, value); }

    /// <summary>Left offset for this segment as a distance (positive is left), prefilled with the offset that applies. Blank means the run's.</summary>
    public TextFieldViewModel OffsetFixLeft { get; }

    /// <summary>Right offset for this segment as a distance (positive is right), prefilled with the offset that applies. Blank means the run's.</summary>
    public TextFieldViewModel OffsetFixRight { get; }

    /// <summary>Writes the two offsets as the segment's override and re-runs Validate. A side equal to the run's offset carries no override.</summary>
    public RelayCommand ApplyOffsetFixCommand { get; }

    /// <summary>Why the last Apply was refused, or null.</summary>
    public string? OffsetFixError { get => _offsetFixError; private set => SetProperty(ref _offsetFixError, value); }

    internal void RefreshCommands() => ApplyOffsetFixCommand.RaiseCanExecuteChanged();

    private string? CheckOffsetText(string text)
        => string.IsNullOrWhiteSpace(text) || _fmt is null || _fmt.TryParse(text, out _) ? null : DialogText.NotANumber;

    internal void Apply(RowModel model, GridContext ctx)
    {
        var fmt = ctx.Fmt;
        _fmt = fmt;
        var segment = model.Segment;
        if (segment.Id != SegmentId) OffsetFixError = null;
        SegmentId = segment.Id;
        Number = model.Number;
        Label = segment.Label;
        BeginRaw = segment.StartStation;
        EndRaw = segment.EndStation;
        BeginStation.SetFromModel(ctx.FormatStation(segment.StartStation));
        EndStation.SetFromModel(ctx.FormatStation(segment.EndStation));
        BeginStation.IsEditable = model.StationsEditable;
        EndStation.IsEditable = model.StationsEditable;
        Length = segment.EndStation - segment.StartStation;
        LengthText = fmt.Length(Length);

        var values = model.Values;
        HasValues = values is not null;
        if (values is not null)
        {
            Area2D = fmt.AreaValue(values.Area2D, ctx.AreaUnit);
            Area2DText = fmt.Area(values.Area2D, ctx.AreaUnit);
            Cut = values.Cut;
            Fill = values.Fill;
            Net = values.Net;
            CutText = fmt.Volume(values.Cut);
            FillText = fmt.Volume(values.Fill);
            NetText = fmt.Volume(Math.Abs(values.Net));
            NetTag = NetTagFor(values.Net, fmt);
            Coverage = values.Coverage;
            CoverageText = fmt.Coverage(values.Coverage);
            CoverageIsLow = values.Coverage < fmt.Config.FullCoverage;
        }
        else
        {
            Area2D = Cut = Fill = Net = Coverage = null;
            Area2DText = CutText = FillText = NetText = CoverageText = DialogText.NoValue;
            NetTag = string.Empty;
            CoverageIsLow = false;
        }

        var impact = values?.Impact;
        if (impact is not null)
        {
            var cutVolume = fmt.VolumeFromCubicDrawingUnits(impact.CutVolume);
            CutVolume = cutVolume;
            ImpactedLengthText = fmt.Config.ImpactedLength.Format(impact.ImpactedLength, fmt.Culture);
            ImpactedAreaText = fmt.Area(impact.ImpactedArea, ctx.AreaUnit);
            CutVolumeText = fmt.Volume(cutVolume);
            AverageText = impact.ImpactedLength > 0
                ? fmt.Config.ImpactAverage.Format(cutVolume / impact.ImpactedLength, fmt.Culture)
                : DialogText.NotAvailable;
            ImpactNote = impact.TouchesSearchLimit ? DialogText.WidenOffsets : null;
        }
        else if (values is not null && ctx.Preset == ReportPreset.HcfcdSegment && double.IsFinite(segment.StartStation))
        {
            // D10/D18: a computed station segment with no impact result (SV-E827, the surface has no
            // triangles in its window) shows zero impact, never blank; its coverage flag marks it.
            CutVolume = 0;
            ImpactedLengthText = fmt.Config.ImpactedLength.Format(0, fmt.Culture);
            ImpactedAreaText = fmt.Area(0, ctx.AreaUnit);
            CutVolumeText = fmt.Volume(0);
            AverageText = DialogText.NotAvailable;
            ImpactNote = null;
        }
        else
        {
            CutVolume = null;
            ImpactedLengthText = ImpactedAreaText = CutVolumeText = AverageText = DialogText.NoValue;
            ImpactNote = null;
        }

        Status = model.Status;
        ProblemCode = model.Problem?.Code;
        Message = model.Problem?.Message;
        Fix = model.Problem?.Fix;
        SheetLayout = model.SheetLayout;

        ShowOffsetFix = model.OffsetFixAvailable;
        OffsetIsOverride = model.OffsetIsOverride;
        OffsetFixLeft.SetFromModel(fmt.Offset(-model.OffsetLeftEffective));
        OffsetFixRight.SetFromModel(fmt.Offset(model.OffsetRightEffective));
        OffsetFixLeft.IsEditable = OffsetFixRight.IsEditable = !ctx.IsBusy;
    }

    internal static string NetTagFor(double net, DisplayFormatter fmt)
    {
        var shown = fmt.Config.Volume.Round(net);
        return shown > 0 ? DialogText.NetTagCut : shown < 0 ? DialogText.NetTagFill : string.Empty;
    }

    internal bool HasFieldError => BeginStation.HasError || EndStation.HasError || OffsetFixLeft.HasError || OffsetFixRight.HasError;
}

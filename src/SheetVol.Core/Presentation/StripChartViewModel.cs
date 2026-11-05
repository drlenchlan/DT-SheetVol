using System.Collections.ObjectModel;

namespace SheetVol.Core.Presentation;

/// <summary>
/// One segment in the strip chart. Positions and heights are fractions 0..1 of the plot
/// area so the view multiplies them by its own size: <see cref="Left"/> and
/// <see cref="Width"/> along the station axis, <see cref="FillHeight"/> above the axis,
/// <see cref="CutHeight"/> below it, both on one shared scale.
/// </summary>
public sealed class StripBarViewModel : ObservableObject
{
    private bool _isSelected;

    internal StripBarViewModel(Guid segmentId, int number, double left, double width, double fillHeight, double cutHeight,
        bool hasValue, string fillText, string cutText, bool isSelected)
    {
        SegmentId = segmentId;
        Number = number;
        Left = left;
        Width = width;
        FillHeight = fillHeight;
        CutHeight = cutHeight;
        HasValue = hasValue;
        FillText = fillText;
        CutText = cutText;
        _isSelected = isSelected;
    }

    /// <summary>Segment GUID; the command parameter for selection.</summary>
    public Guid SegmentId { get; }
    /// <summary>1-based row number.</summary>
    public int Number { get; }
    /// <summary>Start of the bar along the station axis, 0..1.</summary>
    public double Left { get; }
    /// <summary>Bar width, proportional to segment length, 0..1.</summary>
    public double Width { get; }
    /// <summary>Fill above the axis, 0..1 of the half height. Zero in the HCFCD variant.</summary>
    public double FillHeight { get; }
    /// <summary>Cut below the axis, 0..1 of the half height. HCFCD: cut volume below OHWM.</summary>
    public double CutHeight { get; }
    /// <summary>False when the segment has no result that applies; the view draws it hollow.</summary>
    public bool HasValue { get; }
    /// <summary>Formatted fill, for a tooltip.</summary>
    public string FillText { get; }
    /// <summary>Formatted cut, for a tooltip.</summary>
    public string CutText { get; }

    /// <summary>True for the selected segment; the view outlines it.</summary>
    public bool IsSelected { get => _isSelected; internal set => SetProperty(ref _isSelected, value); }
}

/// <summary>A station label at a break, positioned 0..1 along the station axis.</summary>
/// <param name="Position">Fraction along the axis.</param>
/// <param name="Text">Displayed station.</param>
public sealed record StripLabel(double Position, string Text);

/// <summary>HCFCD: a stretch of stations that has cut below OHWM, from <see cref="ImpactResult.Intervals"/>.</summary>
/// <param name="Left">Start, fraction along the axis.</param>
/// <param name="Width">Width, fraction of the axis.</param>
/// <param name="SegmentId">Segment the stretch belongs to.</param>
public sealed record StripMark(double Left, double Width, Guid SegmentId);

/// <summary>
/// The strip chart "Cut and fill by segment" (6.14): segment width proportional to
/// length, fill above the axis, cut below, station labels at each break, the selected
/// segment outlined. The HCFCD variant shows cut volume below OHWM per segment and a
/// strip under it marking the stations that have cut.
/// </summary>
public sealed class StripChartViewModel : ObservableObject
{
    private readonly Action<Guid> _select;
    private bool _isHcfcd;
    private double _maxMagnitude;
    private string _unitLabel = string.Empty;

    internal StripChartViewModel(Action<Guid> select)
    {
        _select = select;
        SelectSegmentCommand = new RelayCommand<Guid>(id => _select(id));
    }

    /// <summary>Chart title for the active preset.</summary>
    public string Title => _isHcfcd ? DialogText.ChartTitleHcfcd : DialogText.ChartTitle;

    /// <summary>True for the HCFCD variant.</summary>
    public bool IsHcfcd
    {
        get => _isHcfcd;
        private set
        {
            if (SetProperty(ref _isHcfcd, value)) OnPropertyChanged(nameof(Title));
        }
    }

    /// <summary>The volume a full-height bar stands for, in the volume unit.</summary>
    public double MaxMagnitude { get => _maxMagnitude; private set => SetProperty(ref _maxMagnitude, value); }

    /// <summary>Volume unit of the bar heights (CY or m³).</summary>
    public string UnitLabel { get => _unitLabel; private set => SetProperty(ref _unitLabel, value); }

    /// <summary>One bar per segment, in plan order.</summary>
    public ObservableCollection<StripBarViewModel> Bars { get; } = [];

    /// <summary>Station labels, one per distinct break.</summary>
    public ObservableCollection<StripLabel> Labels { get; } = [];

    /// <summary>HCFCD: stations with cut below OHWM.</summary>
    public ObservableCollection<StripMark> CutMarks { get; } = [];

    /// <summary>Selects a segment; the parameter is the bar's <see cref="StripBarViewModel.SegmentId"/>.</summary>
    public RelayCommand<Guid> SelectSegmentCommand { get; }

    internal void Update(IReadOnlyList<RowModel> rows, GridContext ctx)
    {
        var fmt = ctx.Fmt;
        IsHcfcd = ctx.Preset == ReportPreset.HcfcdSegment;
        UnitLabel = fmt.VolumeUnit;
        Bars.Clear();
        Labels.Clear();
        CutMarks.Clear();
        MaxMagnitude = 0;
        if (rows.Count == 0) return;

        var first = rows.Min(r => r.Segment.StartStation);
        var last = rows.Max(r => r.Segment.EndStation);
        var span = last - first;
        if (span <= 0) return;

        double Cut(RowModel r) => IsHcfcd
            ? r.Values?.Impact is { } i ? Math.Max(0, fmt.VolumeFromCubicDrawingUnits(i.CutVolume)) : 0
            : Math.Max(0, r.Values?.Cut ?? 0);
        double Fill(RowModel r) => IsHcfcd ? 0 : Math.Max(0, r.Values?.Fill ?? 0);

        var max = rows.Where(r => r.Values is not null).Select(r => Math.Max(Cut(r), Fill(r))).DefaultIfEmpty(0).Max();
        MaxMagnitude = max;

        foreach (var row in rows)
        {
            var s = row.Segment;
            var has = row.Values is not null;
            var cut = Cut(row);
            var fill = Fill(row);
            Bars.Add(new StripBarViewModel(
                s.Id, row.Number,
                (s.StartStation - first) / span,
                (s.EndStation - s.StartStation) / span,
                has && max > 0 ? fill / max : 0,
                has && max > 0 ? cut / max : 0,
                has,
                has ? fmt.Volume(fill) : DialogText.NoValue,
                has ? fmt.Volume(cut) : DialogText.NoValue,
                s.Id == ctx.SelectedId));

            if (IsHcfcd && row.Values?.Impact is { } impact)
            {
                foreach (var (from, to) in impact.Intervals)
                {
                    var a = Math.Clamp(Math.Min(from, to), first, last);
                    var b = Math.Clamp(Math.Max(from, to), first, last);
                    if (b > a) CutMarks.Add(new StripMark((a - first) / span, (b - a) / span, s.Id));
                }
            }
        }

        var tolerance = fmt.Config.StationTolerance;
        var stations = rows.SelectMany(r => new[] { r.Segment.StartStation, r.Segment.EndStation }).OrderBy(x => x).ToList();
        double? previous = null;
        foreach (var station in stations)
        {
            if (previous is double p && station - p <= tolerance) continue;
            Labels.Add(new StripLabel((station - first) / span, ctx.FormatStation(station)));
            previous = station;
        }
    }

    internal void SetSelected(Guid? segmentId)
    {
        foreach (var bar in Bars) bar.IsSelected = bar.SegmentId == segmentId;
    }
}

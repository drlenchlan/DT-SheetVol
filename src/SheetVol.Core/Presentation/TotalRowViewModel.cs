namespace SheetVol.Core.Presentation;

/// <summary>
/// The grid's total row. Each total is the sum of the displayed (rounded) row values
/// (A6, E413, E420), so the grid adds up the way the drawn tables do. The HCFCD average
/// is the one exception: total cut ÷ total length from unrounded values (6.15, E1019).
/// </summary>
public sealed class TotalRowViewModel : ObservableObject
{
    private double _length;
    private double _area2D;
    private double _cut;
    private double _fill;
    private double _net;
    private double _impactedLength;
    private double _impactedArea;
    private double _cutVolume;
    private string _lengthText = DialogText.NoValue;
    private string _area2DText = DialogText.NoValue;
    private string _cutText = DialogText.NoValue;
    private string _fillText = DialogText.NoValue;
    private string _netText = DialogText.NoValue;
    private string _netTag = string.Empty;
    private string _impactedLengthText = DialogText.NoValue;
    private string _impactedAreaText = DialogText.NoValue;
    private string _cutVolumeText = DialogText.NoValue;
    private string _averageText = DialogText.NoValue;

    /// <summary>Row label.</summary>
    public string Label => DialogText.TotalRow;

    /// <summary>Sum of displayed lengths.</summary>
    public double Length { get => _length; private set => SetProperty(ref _length, value); }
    /// <summary>Sum of displayed 2D areas in the selected unit.</summary>
    public double Area2D { get => _area2D; private set => SetProperty(ref _area2D, value); }
    /// <summary>Sum of displayed cut.</summary>
    public double Cut { get => _cut; private set => SetProperty(ref _cut, value); }
    /// <summary>Sum of displayed fill.</summary>
    public double Fill { get => _fill; private set => SetProperty(ref _fill, value); }
    /// <summary>Sum of displayed signed net.</summary>
    public double Net { get => _net; private set => SetProperty(ref _net, value); }
    /// <summary>HCFCD: sum of displayed impacted lengths.</summary>
    public double ImpactedLength { get => _impactedLength; private set => SetProperty(ref _impactedLength, value); }
    /// <summary>HCFCD: sum of displayed impacted areas in the selected unit.</summary>
    public double ImpactedArea { get => _impactedArea; private set => SetProperty(ref _impactedArea, value); }
    /// <summary>HCFCD: sum of displayed cut volumes.</summary>
    public double CutVolume { get => _cutVolume; private set => SetProperty(ref _cutVolume, value); }

    /// <summary>Formatted total length.</summary>
    public string LengthText { get => _lengthText; private set => SetProperty(ref _lengthText, value); }
    /// <summary>Formatted total 2D area.</summary>
    public string Area2DText { get => _area2DText; private set => SetProperty(ref _area2DText, value); }
    /// <summary>Formatted total cut.</summary>
    public string CutText { get => _cutText; private set => SetProperty(ref _cutText, value); }
    /// <summary>Formatted total fill.</summary>
    public string FillText { get => _fillText; private set => SetProperty(ref _fillText, value); }
    /// <summary>Formatted magnitude of the total net.</summary>
    public string NetText { get => _netText; private set => SetProperty(ref _netText, value); }
    /// <summary>CUT, FILL or empty for the total net.</summary>
    public string NetTag { get => _netTag; private set => SetProperty(ref _netTag, value); }
    /// <summary>HCFCD: formatted total impacted length.</summary>
    public string ImpactedLengthText { get => _impactedLengthText; private set => SetProperty(ref _impactedLengthText, value); }
    /// <summary>HCFCD: formatted total impacted area.</summary>
    public string ImpactedAreaText { get => _impactedAreaText; private set => SetProperty(ref _impactedAreaText, value); }
    /// <summary>HCFCD: formatted total cut volume.</summary>
    public string CutVolumeText { get => _cutVolumeText; private set => SetProperty(ref _cutVolumeText, value); }
    /// <summary>HCFCD: formatted overall average, or N/A.</summary>
    public string AverageText { get => _averageText; private set => SetProperty(ref _averageText, value); }

    internal void Apply(IReadOnlyList<RowModel> rows, GridContext ctx)
    {
        var fmt = ctx.Fmt;
        var cfg = fmt.Config;
        var area = fmt.AreaDisplay(ctx.AreaUnit);
        double length = 0, area2D = 0, cut = 0, fill = 0, net = 0;
        double impactedLength = 0, impactedArea = 0, cutVolume = 0;
        double rawLength = 0, rawCutVolume = 0;

        foreach (var row in rows)
        {
            var v = row.Values!;
            length += cfg.Length.Round(row.Segment.EndStation - row.Segment.StartStation);
            area2D += area.Round(fmt.AreaValue(v.Area2D, ctx.AreaUnit));
            cut += cfg.Volume.Round(v.Cut);
            fill += cfg.Volume.Round(v.Fill);
            net += cfg.Volume.Round(v.Net);
            if (v.Impact is { } impact)
            {
                var volume = fmt.VolumeFromCubicDrawingUnits(impact.CutVolume);
                impactedLength += cfg.ImpactedLength.Round(impact.ImpactedLength);
                impactedArea += area.Round(fmt.AreaValue(impact.ImpactedArea, ctx.AreaUnit));
                cutVolume += cfg.Volume.Round(volume);
                rawLength += impact.ImpactedLength;
                rawCutVolume += volume;
            }
        }

        Length = length;
        Area2D = area2D;
        Cut = cut;
        Fill = fill;
        Net = net;
        LengthText = fmt.Length(length);
        Area2DText = area.Format(area2D, fmt.Culture);
        CutText = fmt.Volume(cut);
        FillText = fmt.Volume(fill);
        NetText = fmt.Volume(Math.Abs(net));
        NetTag = SegmentRowViewModel.NetTagFor(net, fmt);

        var hcfcd = ctx.Preset == ReportPreset.HcfcdSegment;
        ImpactedLength = impactedLength;
        ImpactedArea = impactedArea;
        CutVolume = cutVolume;
        ImpactedLengthText = hcfcd ? cfg.ImpactedLength.Format(impactedLength, fmt.Culture) : DialogText.NoValue;
        ImpactedAreaText = hcfcd ? area.Format(impactedArea, fmt.Culture) : DialogText.NoValue;
        CutVolumeText = hcfcd ? fmt.Volume(cutVolume) : DialogText.NoValue;
        AverageText = !hcfcd ? DialogText.NoValue
            : rawLength > 0 ? cfg.ImpactAverage.Format(rawCutVolume / rawLength, fmt.Culture)
            : DialogText.NotAvailable;
    }

    internal void Clear()
    {
        Length = Area2D = Cut = Fill = Net = ImpactedLength = ImpactedArea = CutVolume = 0;
        LengthText = Area2DText = CutText = FillText = NetText = DialogText.NoValue;
        ImpactedLengthText = ImpactedAreaText = CutVolumeText = AverageText = DialogText.NoValue;
        NetTag = string.Empty;
    }
}

using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using SheetVol.Core.Presentation;

namespace SheetVol.UI.Dialog;

/// <summary>
/// Draws <see cref="StripChartViewModel"/> (HANDOFF.md 6.14): one bar per segment with width
/// proportional to length, fill above the axis and cut below, a station label at each break
/// (labels that would overlap are skipped, ticks are always drawn), the selected segment
/// outlined, hollow bars for segments without a result. HCFCD variant: cut volume per segment
/// below an axis at the top, and a strip under the plot marking the stations that have cut.
/// <para>
/// Width follows the view: when the narrowest segment would fall under
/// <see cref="MinBarWidth"/>, the panel grows past its viewport (up to
/// <see cref="MaxChartWidth"/>) and its parent <see cref="ScrollViewer"/> scrolls; a new
/// selection is brought into view, so the chart follows the grid selection (E913). A click
/// selects the bar's segment through <see cref="StripChartViewModel.SelectSegmentCommand"/>.
/// All colours are dependency properties set from theme keys, so a theme swap re-renders (E908).
/// </para>
/// </summary>
public sealed class StripChartPanel : FrameworkElement
{
    /// <summary>The chart view model.</summary>
    public static readonly DependencyProperty ChartProperty = DependencyProperty.Register(
        nameof(Chart), typeof(StripChartViewModel), typeof(StripChartPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnChartChanged));

    /// <summary>Bar colour above the axis.</summary>
    public static readonly DependencyProperty FillBrushProperty = BrushProperty(nameof(FillBrush));
    /// <summary>Bar colour below the axis.</summary>
    public static readonly DependencyProperty CutBrushProperty = BrushProperty(nameof(CutBrush));
    /// <summary>Axis and tick colour.</summary>
    public static readonly DependencyProperty AxisBrushProperty = BrushProperty(nameof(AxisBrush));
    /// <summary>Station label colour.</summary>
    public static readonly DependencyProperty LabelBrushProperty = BrushProperty(nameof(LabelBrush));
    /// <summary>Selected segment outline colour.</summary>
    public static readonly DependencyProperty SelectionBrushProperty = BrushProperty(nameof(SelectionBrush));
    /// <summary>HCFCD strip colour.</summary>
    public static readonly DependencyProperty CutMarkBrushProperty = BrushProperty(nameof(CutMarkBrush));
    /// <summary>Outline colour of a segment without a result.</summary>
    public static readonly DependencyProperty HollowBrushProperty = BrushProperty(nameof(HollowBrush));
    /// <summary>Plot background.</summary>
    public static readonly DependencyProperty PlotBackgroundProperty = BrushProperty(nameof(PlotBackground));

    /// <summary>Narrowest bar, device-independent pixels, before the chart scrolls (E913).</summary>
    public static readonly DependencyProperty MinBarWidthProperty = DependencyProperty.Register(
        nameof(MinBarWidth), typeof(double), typeof(StripChartPanel),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Upper bound of the chart width, device-independent pixels.</summary>
    public static readonly DependencyProperty MaxChartWidthProperty = DependencyProperty.Register(
        nameof(MaxChartWidth), typeof(double), typeof(StripChartPanel),
        new FrameworkPropertyMetadata(30_000.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private const double LabelGap = 6;
    private const double TickLength = 4;
    private const double StripHeight = 8;
    private const double StripGap = 4;
    private const double PlotPadding = 6;

    private readonly List<StripBarViewModel> _subscribed = [];
    private StripChartViewModel? _chart;
    private ScrollViewer? _scroller;

    /// <summary>Creates the panel.</summary>
    public StripChartPanel()
    {
        Focusable = false;
        ClipToBounds = true;
        Loaded += (_, _) => AttachScroller();
        Unloaded += (_, _) => DetachScroller();
    }

    /// <summary>The chart view model.</summary>
    public StripChartViewModel? Chart { get => (StripChartViewModel?)GetValue(ChartProperty); set => SetValue(ChartProperty, value); }
    /// <summary>Bar colour above the axis.</summary>
    public Brush? FillBrush { get => (Brush?)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    /// <summary>Bar colour below the axis.</summary>
    public Brush? CutBrush { get => (Brush?)GetValue(CutBrushProperty); set => SetValue(CutBrushProperty, value); }
    /// <summary>Axis and tick colour.</summary>
    public Brush? AxisBrush { get => (Brush?)GetValue(AxisBrushProperty); set => SetValue(AxisBrushProperty, value); }
    /// <summary>Station label colour.</summary>
    public Brush? LabelBrush { get => (Brush?)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    /// <summary>Selected segment outline colour.</summary>
    public Brush? SelectionBrush { get => (Brush?)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    /// <summary>HCFCD strip colour.</summary>
    public Brush? CutMarkBrush { get => (Brush?)GetValue(CutMarkBrushProperty); set => SetValue(CutMarkBrushProperty, value); }
    /// <summary>Outline colour of a segment without a result.</summary>
    public Brush? HollowBrush { get => (Brush?)GetValue(HollowBrushProperty); set => SetValue(HollowBrushProperty, value); }
    /// <summary>Plot background.</summary>
    public Brush? PlotBackground { get => (Brush?)GetValue(PlotBackgroundProperty); set => SetValue(PlotBackgroundProperty, value); }
    /// <summary>Narrowest bar before the chart scrolls.</summary>
    public double MinBarWidth { get => (double)GetValue(MinBarWidthProperty); set => SetValue(MinBarWidthProperty, value); }
    /// <summary>Upper bound of the chart width.</summary>
    public double MaxChartWidth { get => (double)GetValue(MaxChartWidthProperty); set => SetValue(MaxChartWidthProperty, value); }

    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(StripChartPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // ---- View model wiring -------------------------------------------------------------------

    private static void OnChartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((StripChartPanel)d).Attach(e.NewValue as StripChartViewModel);

    private void Attach(StripChartViewModel? chart)
    {
        if (_chart is not null)
        {
            _chart.PropertyChanged -= OnChartPropertyChanged;
            _chart.Bars.CollectionChanged -= OnCollectionChanged;
            _chart.Labels.CollectionChanged -= OnCollectionChanged;
            _chart.CutMarks.CollectionChanged -= OnCollectionChanged;
        }
        _chart = chart;
        if (_chart is not null)
        {
            _chart.PropertyChanged += OnChartPropertyChanged;
            _chart.Bars.CollectionChanged += OnCollectionChanged;
            _chart.Labels.CollectionChanged += OnCollectionChanged;
            _chart.CutMarks.CollectionChanged += OnCollectionChanged;
        }
        ResubscribeBars();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnChartPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _chart?.Bars)) ResubscribeBars();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void ResubscribeBars()
    {
        foreach (var bar in _subscribed) bar.PropertyChanged -= OnBarPropertyChanged;
        _subscribed.Clear();
        if (_chart is null) return;
        foreach (var bar in _chart.Bars)
        {
            bar.PropertyChanged += OnBarPropertyChanged;
            _subscribed.Add(bar);
        }
    }

    private void OnBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StripBarViewModel.IsSelected)) return;
        InvalidateVisual();
        if (sender is StripBarViewModel { IsSelected: true } bar) BringBarIntoView(bar);
    }

    // ---- Scrolling -------------------------------------------------------------------------------

    private void AttachScroller()
    {
        DetachScroller();
        DependencyObject? node = VisualTreeHelper.GetParent(this);
        while (node is not null and not ScrollViewer) node = VisualTreeHelper.GetParent(node);
        _scroller = node as ScrollViewer;
        if (_scroller is not null) _scroller.SizeChanged += OnScrollerSizeChanged;
        InvalidateMeasure();
    }

    private void DetachScroller()
    {
        if (_scroller is not null) _scroller.SizeChanged -= OnScrollerSizeChanged;
        _scroller = null;
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => InvalidateMeasure();

    /// <summary>Centres the selected bar in the visible part of the chart when the chart is wider than its viewport.</summary>
    private void BringBarIntoView(StripBarViewModel bar)
    {
        var width = ActualWidth;
        if (width <= 0) return;
        if (_scroller is { ScrollableWidth: > 0 } scroller)
        {
            var centre = (bar.Left + bar.Width / 2) * width;
            scroller.ScrollToHorizontalOffset(Math.Clamp(centre - scroller.ViewportWidth / 2, 0, scroller.ScrollableWidth));
            return;
        }
        BringIntoView(new Rect(bar.Left * width, 0, Math.Max(bar.Width * width, 1), Math.Max(ActualHeight, 1)));
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var viewport = !double.IsInfinity(availableSize.Width)
            ? availableSize.Width
            : _scroller is { ViewportWidth: > 0 } s ? s.ViewportWidth : _scroller?.ActualWidth ?? 0;
        var width = viewport;
        var bars = _chart?.Bars;
        if (bars is { Count: > 0 })
        {
            var narrowest = bars.Min(b => b.Width);
            if (narrowest > 0 && narrowest * viewport < MinBarWidth)
                width = Math.Min(MaxChartWidth, MinBarWidth / narrowest);
        }
        var height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        return new Size(Math.Max(width, 0), height);
    }

    // ---- Input -------------------------------------------------------------------------------------

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (BarAt(e.GetPosition(this).X) is { } bar && _chart?.SelectSegmentCommand.CanExecute(bar.SegmentId) == true)
        {
            _chart.SelectSegmentCommand.Execute(bar.SegmentId);
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var bar = BarAt(e.GetPosition(this).X);
        ToolTip = bar is null ? null : TooltipFor(bar);
    }

    private StripBarViewModel? BarAt(double x)
    {
        var width = ActualWidth;
        if (_chart is null || width <= 0) return null;
        var t = x / width;
        return _chart.Bars.FirstOrDefault(b => t >= b.Left && t < b.Left + b.Width);
    }

    private string TooltipFor(StripBarViewModel bar)
    {
        var unit = _chart?.UnitLabel ?? string.Empty;
        var cut = $"{DialogText.ChartCut} {bar.CutText} {unit}";
        return _chart?.IsHcfcd == true
            ? $"{DialogText.ColumnNumber}{bar.Number}   {cut}"
            : $"{DialogText.ColumnNumber}{bar.Number}   {DialogText.ChartFill} {bar.FillText} {unit}   {cut}";
    }

    /// <inheritdoc/>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    // ---- Rendering ---------------------------------------------------------------------------------

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        dc.DrawRectangle(PlotBackground ?? Brushes.Transparent, null, new Rect(0, 0, width, height));
        var chart = _chart;
        if (chart is null || chart.Bars.Count == 0) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var fontSize = TextElement.GetFontSize(this);
        var labelHeight = new FormattedText("0+00", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, LabelBrush ?? Brushes.Gray, dpi).Height;

        var hcfcd = chart.IsHcfcd;
        var labelsTop = height - labelHeight - TickLength;
        var stripTop = hcfcd ? labelsTop - StripGap - StripHeight : labelsTop;
        var plotTop = PlotPadding;
        var plotBottom = (hcfcd ? stripTop - StripGap : labelsTop) - TickLength;
        if (plotBottom - plotTop < 4) return;
        var axisY = hcfcd ? plotTop : (plotTop + plotBottom) / 2;
        var downSpan = plotBottom - axisY;
        var upSpan = axisY - plotTop;

        var hollowPen = new Pen(HollowBrush ?? Brushes.Gray, 1) { DashStyle = DashStyles.Dash };
        StripBarViewModel? selected = null;
        foreach (var bar in chart.Bars)
        {
            var x = bar.Left * width;
            var w = Math.Max(bar.Width * width, 1);
            var inner = w > 3 ? new Rect(x + 0.5, 0, w - 1, 0) : new Rect(x, 0, w, 0);
            if (!bar.HasValue)
            {
                dc.DrawRectangle(null, hollowPen, new Rect(inner.X, plotTop + 0.5, inner.Width, Math.Max(plotBottom - plotTop - 1, 1)));
            }
            else
            {
                if (bar.FillHeight > 0 && upSpan > 0)
                    dc.DrawRectangle(FillBrush, null, new Rect(inner.X, axisY - bar.FillHeight * upSpan, inner.Width, bar.FillHeight * upSpan));
                if (bar.CutHeight > 0 && downSpan > 0)
                    dc.DrawRectangle(CutBrush, null, new Rect(inner.X, axisY, inner.Width, bar.CutHeight * downSpan));
            }
            if (bar.IsSelected) selected = bar;
        }

        var axisPen = new Pen(AxisBrush ?? Brushes.Gray, 1);
        dc.DrawLine(axisPen, new Point(0, Snap(axisY)), new Point(width, Snap(axisY)));

        if (hcfcd)
        {
            dc.DrawRectangle(null, new Pen(AxisBrush ?? Brushes.Gray, 0.5), new Rect(0, stripTop, width, StripHeight));
            foreach (var mark in chart.CutMarks)
                dc.DrawRectangle(CutMarkBrush, null, new Rect(mark.Left * width, stripTop, Math.Max(mark.Width * width, 1), StripHeight));
        }

        if (selected is not null)
        {
            var x = selected.Left * width;
            var w = Math.Max(selected.Width * width, 2);
            dc.DrawRectangle(null, new Pen(SelectionBrush ?? Brushes.White, 2), new Rect(x + 1, plotTop, Math.Max(w - 2, 1), plotBottom - plotTop));
        }

        // Ticks at every break. Labels: the first and last always, interior ones only where they do not collide.
        var labels = chart.Labels;
        if (labels.Count == 0) return;
        FormattedText Text(StripLabel l) => new(l.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, LabelBrush ?? Brushes.Gray, dpi);
        double LeftOf(StripLabel l, FormattedText t) => Math.Clamp(l.Position * width - t.Width / 2, 0, Math.Max(width - t.Width, 0));
        foreach (var label in labels)
        {
            var x = Snap(label.Position * width);
            dc.DrawLine(axisPen, new Point(x, labelsTop - TickLength), new Point(x, labelsTop));
        }
        var lastText = Text(labels[^1]);
        var lastLeft = LeftOf(labels[^1], lastText);
        var right = double.NegativeInfinity;
        for (var i = 0; i < labels.Count - 1; i++)
        {
            var text = Text(labels[i]);
            var left = LeftOf(labels[i], text);
            if (i > 0 && (left < right + LabelGap || left + text.Width > lastLeft - LabelGap)) continue;
            dc.DrawText(text, new Point(left, labelsTop));
            right = left + text.Width;
        }
        if (labels.Count == 1 || lastLeft >= right + LabelGap) dc.DrawText(lastText, new Point(lastLeft, labelsTop));
    }

    private static double Snap(double v) => Math.Floor(v) + 0.5;
}

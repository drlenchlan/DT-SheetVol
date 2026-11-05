// Off-screen load-and-render check of the SVDIALOG window against the design session.
// No Autodesk types anywhere on this path, so it runs both from SVDIALOGSMOKE in Civil 3D
// and from a plain WPF process that loads SheetVol.UI.dll by reflection.

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SheetVol.Core;
using SheetVol.Core.Presentation;
using SheetVol.UI.Dialog;
using SheetVol.UI.Dialog.Themes;

namespace SheetVol.UI.Host;

/// <summary>
/// Builds the dialog off-screen with <see cref="DesignSession"/> (T2), drives it through the
/// states of the 6.14 artboards, renders each to PNG, and checks what can be checked
/// without a person: XAML loads, bindings resolve (zero binding errors), the grid shows T2,
/// totals appear only with every result, the theme swap restyles live (E908), the window
/// lays out at its minimum size with the footer inside (E909), every focusable control has
/// an accessible name (E910), 250 segments realize only the visible rows and the chart
/// scrolls to a grid selection (E913), and closing clears the highlight (E911).
/// Must run on an STA thread with a dispatcher.
/// </summary>
internal static class UiSmoke
{
    private const string DrawingName = "T2-design.dwg";
    private const string RunName = "Run1";

    /// <summary>Runs every check; returns true when all pass. PNGs go to <paramref name="outputFolder"/> when given.</summary>
    /// <param name="outputFolder">Folder for the PNG renders, or null for none.</param>
    /// <param name="log">Receives one line per check.</param>
    /// <param name="bindingErrorsFail">
    /// True in a process that shows no other WPF UI. The binding trace is process-wide, so inside
    /// AutoCAD it also catches the ribbon's and palettes' errors: SVDIALOGSMOKE passes false and the
    /// count is reported, not judged. The plain-WPF run is the one that proves zero.
    /// </param>
    public static bool RunDesign(string? outputFolder, Action<string> log, bool bindingErrorsFail = true)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (outputFolder is not null) Directory.CreateDirectory(outputFolder);
        var listener = new BindingErrorListener();
        var source = PresentationTraceSources.DataBindingSource;
        PresentationTraceSources.Refresh();
        var previousLevel = source.Switch.Level;
        source.Switch.Level = SourceLevels.Warning;
        source.Listeners.Add(listener);
        var report = new Report(log);
        try
        {
            StandardStates(report, outputFolder);
            HcfcdState(report, outputFolder);
            ManySegments(report, outputFolder);
        }
        catch (Exception ex)
        {
            report.Check("no exception", false, ex.GetType().Name + ": " + ex.Message);
            foreach (var line in ex.ToString().Split(Environment.NewLine).Skip(1).Take(12)) log("    " + line.Trim());
        }
        finally
        {
            source.Listeners.Remove(listener);
            source.Switch.Level = previousLevel;
        }
        var errors = listener.Errors;
        var distinct = errors.Select(m => System.Text.RegularExpressions.Regex.Replace(m, @"\(HashCode=\d+\)", string.Empty)).Distinct().ToList();
        var summary = $"{errors.Count} binding error(s), {distinct.Count} distinct";
        if (bindingErrorsFail) report.Check("binding errors", errors.Count == 0, summary);
        else report.Info($"binding errors in this process (includes the host's own UI, not judged): {summary}");
        foreach (var message in distinct.Take(15)) log("    " + message);
        log(report.Failures == 0 ? "UiSmoke: PASS" : $"UiSmoke: FAIL ({report.Failures} failed)");
        return report.Failures == 0;
    }

    // ---- Artboards 1-3: Volumes current, theme swap, Out of date with one error, minimum size --------

    private static void StandardStates(Report report, string? folder)
    {
        var session = NewSession();
        var window = Open(session, DialogTheme.Dark);
        var vm = window.ViewModel;
        try
        {
            var rows = vm.Grid.Rows;
            report.Check("T2 rows", rows.Count == 4 && rows.Select(r => r.Length).SequenceEqual([500d, 800d, 500d, 200d]),
                $"{rows.Count} rows, lengths {string.Join(", ", rows.Select(r => r.Length.ToString(CultureInfo.InvariantCulture)))}");
            var panelWidth = ((FrameworkElement)window.LeftPanel.Parent).ActualWidth;
            report.Check("left panel width", Math.Abs(panelWidth - 328) <= 0.5, $"{panelWidth:0.#}");
            report.Check("totals hidden before regen", !vm.Grid.TotalsVisible, vm.Grid.ReconciliationText);

            Execute(vm.RegenerateCommand);
            report.Check("regen", vm.LastResult is { Succeeded: true } && rows.All(r => r.Status == SegmentStatus.Current),
                vm.StatusMessage ?? "(no status)");
            report.Check("totals visible after regen", vm.Grid.TotalsVisible && vm.Grid.Total.CutText.Length > 0,
                $"cut {vm.Grid.Total.CutText}, fill {vm.Grid.Total.FillText}, net {vm.Grid.Total.NetText} {vm.Grid.Total.NetTag}");
            report.Check("chart bars", vm.Chart.Bars.Count == 4 && vm.Chart.Labels.Count == 5,
                $"{vm.Chart.Bars.Count} bars, {vm.Chart.Labels.Count} station labels");
            vm.Grid.SelectedRow = rows[1];
            Pump();
            report.Check("selection outlines bar", vm.Chart.Bars[1].IsSelected && session.Highlights.LastOrDefault() == rows[1].SegmentId,
                $"bar 2 selected={vm.Chart.Bars[1].IsSelected}, last highlight={session.Highlights.LastOrDefault()}");
            CheckTotalsAligned(report, window);
            CheckColumnsFit(report, window, "Standard");
            Render(window, folder, "1-volumes-current-dark.png", report);

            var darkBackground = Color(window.Root.Background);
            window.ApplyTheme(DialogTheme.Light);
            Pump();
            var expected = Color((Brush)new LightTheme()["SV.Brush.WindowBackground"]);
            var chartLight = Color(window.ChartPanel.PlotBackground);
            report.Check("theme swap to light (E908)", Color(window.Root.Background) == expected && chartLight == Color((Brush)new LightTheme()["SV.Brush.ChartPlotBackground"]),
                $"root {darkBackground} -> {Color(window.Root.Background)}, chart plot {chartLight}");
            Render(window, folder, "2-volumes-current-light.png", report);
            window.ApplyTheme(DialogTheme.Dark);
            Pump();
            report.Check("theme swap back to dark", Color(window.Root.Background) == darkBackground, Color(window.Root.Background).ToString());

            vm.OffsetRight.Text = "350";
            Pump();
            var failing = rows.FirstOrDefault(r => r.IsError);
            report.Check("one error row (E201)", rows.Count(r => r.IsError) == 1 && failing?.ProblemCode == "SV-E201",
                failing is null ? "no row in error" : $"row {failing.Number}: {failing.Message}");
            report.Check("stale banner", vm.ShowStaleBanner && vm.StaleBannerText == DialogText.StaleEdits, vm.StaleBannerText ?? "(none)");
            report.Check("totals hidden with an error", !vm.Grid.TotalsVisible, vm.Grid.ReconciliationText);
            if (failing is not null)
            {
                vm.Grid.SelectedRow = failing;
                Pump();
                var message = DialogWindow.Descendants<TextBlock>(window.SegmentsGrid).FirstOrDefault(t => t.IsVisible && t.Text == failing.Message);
                var fixText = DialogWindow.Descendants<TextBlock>(window.SegmentsGrid).FirstOrDefault(t => t.IsVisible && t.Text == failing.Fix);
                var fixShown = DialogWindow.Descendants<Button>(window.SegmentsGrid).Any(b => b.IsVisible && Equals(b.Content, DialogText.ApplyOffsetFix));
                report.Check("row message and offset fix under the row", message is not null && fixText is not null && fixShown,
                    $"message shown={message is not null}, fix shown={fixText is not null}, Apply shown={fixShown}");
                var viewport = GridScroller(window).ViewportWidth;
                var fixRight = fixText is null ? double.MaxValue : fixText.TranslatePoint(new Point(fixText.ActualWidth, 0), GridScroller(window)).X;
                report.Check("row message wraps inside the grid", fixRight <= viewport + 0.5, $"fix text right edge {fixRight:0} of viewport {viewport:0}");
            }
            Render(window, folder, "3-out-of-date-one-error-dark.png", report);

            if (failing is not null)
            {
                failing.OffsetFixLeft.Text = "250";
                failing.OffsetFixRight.Text = "250";
                failing.ApplyOffsetFixCommand.Execute(null);
                Pump();
                report.Check("offset fix clears the error (D8)", rows.All(r => !r.IsError), failing.OffsetFixError ?? "applied");
            }

            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            Pump();
            var root = window.Root;
            var regenerateRight = window.RegenerateButton.TranslatePoint(new Point(window.RegenerateButton.ActualWidth, 0), root).X;
            var gridBottom = window.SegmentsGrid.TranslatePoint(new Point(0, window.SegmentsGrid.ActualHeight), root).Y;
            report.Check("minimum size layout (E909)",
                regenerateRight <= root.ActualWidth + 0.5 && window.SegmentsGrid.ActualHeight > 60 && gridBottom < root.ActualHeight,
                $"window {window.ActualWidth:0}x{window.ActualHeight:0}, Regenerate right edge {regenerateRight:0} of {root.ActualWidth:0}, grid height {window.SegmentsGrid.ActualHeight:0}");
            Render(window, folder, "5-minimum-size-dark.png", report);

            CheckAccessibleNames(report, window);

            Execute(vm.CloseCommand);
            Pump();
            report.Check("close clears highlight (E911)", !window.IsVisible && session.Highlights.LastOrDefault() is null,
                $"visible={window.IsVisible}, last highlight={(session.Highlights.LastOrDefault()?.ToString() ?? "null")}");
        }
        finally
        {
            if (window.IsVisible) window.Close();
        }
    }

    // ---- Artboard 3: HCFCD preset ----------------------------------------------------------------------

    private static void HcfcdState(Report report, string? folder)
    {
        var session = NewSession();
        var window = Open(session, DialogTheme.Dark);
        var vm = window.ViewModel;
        try
        {
            vm.SelectedPreset = vm.Presets.First(p => p.Preset == ReportPreset.HcfcdSegment);
            vm.Ohwm.Text = "35";
            Pump();
            report.Check("HCFCD labels", vm.IsHcfcd && vm.BaseSurfaceLabel == DialogText.BaseSurfaceHcfcd && vm.SelectedAreaUnit?.Unit == AreaUnit.Acres,
                $"{vm.AnalysisNameLabel} / {vm.BaseSurfaceLabel} / {vm.ComparisonSurfaceLabel}, area unit {vm.SelectedAreaUnit}");
            Execute(vm.RegenerateCommand);
            var hcfcdColumns = window.SegmentsGrid.Columns.Count(c => c.Visibility == Visibility.Visible);
            var cutColumnHidden = window.SegmentsGrid.Columns[5].Visibility != Visibility.Visible;
            report.Check("HCFCD columns", vm.Grid.IsHcfcd && hcfcdColumns == 10 && cutColumnHidden,
                $"{hcfcdColumns} visible columns, Standard cut column hidden={cutColumnHidden}");
            report.Check("HCFCD chart and strip", vm.Chart.IsHcfcd && vm.Chart.CutMarks.Count == 2 && vm.Grid.TotalsVisible,
                $"{vm.Chart.CutMarks.Count} cut marks, totals {vm.Grid.Total.ImpactedLengthText} ft / {vm.Grid.Total.ImpactedAreaText} / {vm.Grid.Total.CutVolumeText}");
            CheckTotalsAligned(report, window);
            CheckColumnsFit(report, window, "HCFCD");
            Render(window, folder, "4-hcfcd-preset-dark.png", report);
            Execute(vm.CloseCommand);
        }
        finally
        {
            if (window.IsVisible) window.Close();
        }
    }

    // ---- E913: 250 segments ----------------------------------------------------------------------------

    private static void ManySegments(Report report, string? folder)
    {
        var session = NewSession();
        session.Runs[RunName] = DesignSession.T2Run(RunName, "START..END@8");
        var timer = Stopwatch.StartNew();
        var window = Open(session, DialogTheme.Dark);
        var vm = window.ViewModel;
        try
        {
            Execute(vm.RegenerateCommand);
            var opened = timer.ElapsedMilliseconds;
            var realized = DialogWindow.Descendants<DataGridRow>(window.SegmentsGrid).Count();
            report.Check("250 rows virtualized (E913)", vm.Grid.Rows.Count == 250 && realized < 60,
                $"{vm.Grid.Rows.Count} rows, {realized} realized, open and regen {opened} ms");

            timer.Restart();
            vm.Grid.SelectedRow = vm.Grid.Rows[220];
            Pump();
            Pump();
            var gridScrolled = DialogWindow.Descendants<DataGridRow>(window.SegmentsGrid).Any(r => ReferenceEquals(r.Item, vm.Grid.Rows[220]));
            var chartOffset = window.ChartScroller.HorizontalOffset;
            report.Check("selection scrolls grid and chart (E913)", gridScrolled && chartOffset > 0,
                $"row 221 realized={gridScrolled}, chart offset {chartOffset:0} of {window.ChartScroller.ScrollableWidth:0}, {timer.ElapsedMilliseconds} ms");
            Render(window, folder, "6-250-segments-dark.png", report);
            Execute(vm.CloseCommand);
        }
        finally
        {
            if (window.IsVisible) window.Close();
        }
    }

    // ---- Checks ------------------------------------------------------------------------------------------

    private static void CheckTotalsAligned(Report report, DialogWindow window)
    {
        var worst = 0.0;
        var cells = 0;
        foreach (var child in window.TotalsRow.Children.OfType<FrameworkElement>())
        {
            if (window.TotalSpanWidth(child) is not double width || width <= 0) continue;
            cells++;
            worst = Math.Max(worst, Math.Abs(child.ActualWidth - width));
        }
        report.Check("total row aligned with columns", cells > 0 && worst <= 1, $"{cells} cells, largest width difference {worst:0.##} px");
    }

    /// <summary>At the default window size the columns fit without squeezing (each at least its MinWidth) and without a horizontal scroll bar.</summary>
    private static void CheckColumnsFit(Report report, DialogWindow window, string preset)
    {
        var squeezed = window.SegmentsGrid.Columns
            .Where(c => c.Visibility == Visibility.Visible && c.ActualWidth < c.MinWidth - 0.5)
            .Select(c => window.SegmentsGrid.Columns.IndexOf(c)).ToList();
        var scroller = GridScroller(window);
        report.Check($"{preset} columns fit at default size (E909)", squeezed.Count == 0 && scroller.ScrollableWidth <= 0.5,
            $"squeezed columns: {(squeezed.Count == 0 ? "none" : string.Join(", ", squeezed))}; horizontal overflow {scroller.ScrollableWidth:0} px");
    }

    private static ScrollViewer GridScroller(DialogWindow window)
        => DialogWindow.Descendants<ScrollViewer>(window.SegmentsGrid).First(s => s.Name == "DG_ScrollViewer");

    private static void CheckAccessibleNames(Report report, DialogWindow window)
    {
        var unnamed = new List<string>();
        var checkedCount = 0;
        foreach (var control in DialogWindow.Descendants<Control>(window))
        {
            if (!control.IsVisible || !control.Focusable || !control.IsTabStop) continue;
            if (control is DataGridCell or DataGridRow or DataGridColumnHeader or ScrollViewer or ComboBoxItem or Thumb or RepeatButton) continue;
            checkedCount++;
            var name = UIElementAutomationPeer.CreatePeerForElement(control)?.GetName();
            if (string.IsNullOrWhiteSpace(name)) unnamed.Add(control.GetType().Name);
        }
        report.Check("accessible names (E910)", unnamed.Count == 0,
            $"{checkedCount} focusable controls checked; unnamed: {(unnamed.Count == 0 ? "none" : string.Join(", ", unnamed))}");
    }

    // ---- Plumbing --------------------------------------------------------------------------------------

    private static DesignSession NewSession()
        => new(RunName, () => new DrawingInfo(DrawingName, UnitSystem.Imperial));

    private static DialogWindow Open(DesignSession session, DialogTheme theme)
    {
        var window = new DialogWindow(session, new PendingEdits(), PresentationConfig.Default, theme, HostText.DesignModeNote)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false,
            Top = 0,
        };
        window.Left = SystemParameters.VirtualScreenLeft - window.Width - 400;
        window.Show();
        Pump();
        return window;
    }

    private static void Execute(AsyncRelayCommand command)
    {
        var task = command.ExecuteAsync();
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(30)) Pump();
        task.GetAwaiter().GetResult();
        Pump();
    }

    private static void Execute(RelayCommand command)
    {
        command.Execute(null);
        Pump();
    }

    /// <summary>Runs the dispatcher until everything queued down to ContextIdle has run (layout, render, bindings).</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void Render(DialogWindow window, string? folder, string fileName, Report report)
    {
        if (folder is null) return;
        var root = window.Root;
        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(folder, fileName);
        using (var stream = File.Create(path)) encoder.Save(stream);
        report.Info($"rendered {path} ({bitmap.PixelWidth}x{bitmap.PixelHeight})");
    }

    private static Color Color(Brush? brush) => brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;

    private sealed class Report(Action<string> log)
    {
        public int Failures { get; private set; }

        public void Check(string name, bool passed, string detail)
        {
            if (!passed) Failures++;
            log($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        }

        public void Info(string line) => log("     " + line);
    }

    /// <summary>Collects data-binding trace events of severity Error (path errors, missing sources, conversion failures).</summary>
    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Errors { get; } = [];

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        {
            if (eventType <= TraceEventType.Error) Errors.Add($"{id}: {message}");
        }

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
        {
            if (eventType > TraceEventType.Error) return;
            Errors.Add($"{id}: {(format is null ? string.Empty : args is { Length: > 0 } ? string.Format(CultureInfo.InvariantCulture, format, args) : format)}");
        }

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
        }
    }
}

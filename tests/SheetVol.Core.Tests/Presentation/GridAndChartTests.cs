// Segments grid formatting and the strip chart (HANDOFF.md 6.14).

using SheetVol.Core.Presentation;

namespace SheetVol.Core.Tests.Presentation;

public class GridAndChartTests
{
    [Fact]
    public async Task Totals_are_hidden_until_every_segment_has_a_result()
    {
        var (session, _, vm) = Fixture.Open();
        session.OmitResultForSegment.Add(3);

        await vm.RegenerateCommand.ExecuteAsync();

        Assert.False(vm.Grid.Rows[3].HasValues);
        Assert.Equal(SegmentStatus.OutOfDate, vm.Grid.Rows[3].Status);
        Assert.Equal(DialogText.NoValue, vm.Grid.Rows[3].CutText);
        Assert.False(vm.Grid.TotalsVisible);
        Assert.Equal(DialogText.NoValue, vm.Grid.Total.CutText);
        Assert.Equal(DialogText.TotalsPending, vm.Grid.ReconciliationText);

        session.OmitResultForSegment.Clear();
        await vm.RegenerateCommand.ExecuteAsync();

        Assert.True(vm.Grid.TotalsVisible);
        Assert.Equal("200", vm.Grid.Total.CutText);
        Assert.Equal("200", vm.Grid.Total.FillText);
        Assert.Equal("0", vm.Grid.Total.NetText);
        Assert.Equal(string.Empty, vm.Grid.Total.NetTag);
        Assert.Equal("2,000.00", vm.Grid.Total.LengthText);
        Assert.Equal("Sum of segments: cut 200 CY, fill 200 CY. Outside the offsets: none. Partition check passed.", vm.Grid.ReconciliationText);
    }

    [Fact]
    public async Task Net_shows_magnitude_with_CUT_or_FILL_and_no_tag_at_zero()
    {
        var (_, _, vm) = Fixture.Open();
        await vm.RegenerateCommand.ExecuteAsync();
        var rows = vm.Grid.Rows;

        Assert.Equal(["50", "100", "150", "0"], rows.Select(r => r.NetText));
        Assert.Equal([DialogText.NetTagCut, DialogText.NetTagCut, DialogText.NetTagFill, string.Empty], rows.Select(r => r.NetTag));
        Assert.Equal([50d, 100d, -150d, 0d], rows.Select(r => r.Net!.Value));
        Assert.Equal("Net (CY)", vm.Grid.NetHeader);
    }

    [Fact]
    public async Task Coverage_below_full_is_flagged()
    {
        var (session, _, vm) = Fixture.Open();
        session.CoverageBySegment[2] = 0.8;

        await vm.RegenerateCommand.ExecuteAsync();

        Assert.Equal("100%", vm.Grid.Rows[0].CoverageText);
        Assert.False(vm.Grid.Rows[0].CoverageIsLow);
        Assert.Equal("80%", vm.Grid.Rows[2].CoverageText);
        Assert.True(vm.Grid.Rows[2].CoverageIsLow);
    }

    [Fact]
    public void Outside_limits_and_a_failed_partition_show_in_the_reconciliation_line()
    {
        var session = new FakeSheetVolSession();
        var plan = session.Plan(Fixture.T2Run());
        var stored = Fixture.T2Run() with { KnownSegments = plan.Segments };
        var result = session.BuildResult(stored, plan, 1) with
        {
            OutsideLimits = [new OutsideLimits(Fixture.AnalysisA, new RawVolume(270, 54))],
            PartitionCheckPassed = false,
        };
        session.Status = new RunStatus(false, result);

        var (_, _, vm) = Fixture.Open(stored, session);

        Assert.Equal("Sum of segments: cut 200 CY, fill 200 CY. Outside the offsets: cut 10 CY, fill 2 CY before factors. Partition check FAILED.", vm.Grid.ReconciliationText);
    }

    [Fact]
    public void E916_reconciliation_line_shows_the_outside_limits_row_of_the_selected_analysis()
    {
        var session = new FakeSheetVolSession();
        var run = Fixture.T2Run(analyses:
        [
            new AnalysisDef(Fixture.AnalysisA, "EG vs FG_A", "EG", "FG_A"),
            new AnalysisDef(Fixture.AnalysisB, "EG vs FG_B", "EG", "FG_B"),
        ]);
        var plan = session.Plan(run);
        var stored = run with { KnownSegments = plan.Segments };
        var result = session.BuildResult(stored, plan, 1) with
        {
            OutsideLimits = [new OutsideLimits(Fixture.AnalysisB, new RawVolume(540, 0))],   // only B is above tolerance
        };
        session.Status = new RunStatus(false, result);
        var (_, _, vm) = Fixture.Open(stored, session);

        Assert.Equal(Fixture.AnalysisA, vm.Grid.SelectedComparison!.AnalysisId);
        Assert.Equal("Sum of segments: cut 200 CY, fill 200 CY. Outside the offsets: none. Partition check passed.", vm.Grid.ReconciliationText);

        vm.Grid.SelectedComparison = vm.Grid.Comparisons[1];

        Assert.Equal("Sum of segments: cut 400 CY, fill 400 CY. Outside the offsets: cut 20 CY, fill 0 CY before factors. Partition check passed.", vm.Grid.ReconciliationText);
    }

    [Fact]
    public async Task Strip_chart_width_follows_length_with_fill_above_and_cut_below()
    {
        var (_, _, vm) = Fixture.Open();
        await vm.RegenerateCommand.ExecuteAsync();
        var bars = vm.Chart.Bars;

        Assert.Equal(DialogText.ChartTitle, vm.Chart.Title);
        Assert.Equal("CY", vm.Chart.UnitLabel);
        Assert.Equal(4, bars.Count);
        Assert.Equal([0, 0.25, 0.65, 0.9], bars.Select(b => Math.Round(b.Left, 12)));
        Assert.Equal([0.25, 0.4, 0.25, 0.1], bars.Select(b => Math.Round(b.Width, 12)));
        Assert.Equal(150, vm.Chart.MaxMagnitude);
        Assert.Equal(50.0 / 150, bars[0].FillHeight, 12);
        Assert.Equal(100.0 / 150, bars[0].CutHeight, 12);
        Assert.Equal(1, bars[2].FillHeight, 12);
        Assert.Equal(0, bars[2].CutHeight);
        Assert.Equal((0d, 0d), (bars[3].FillHeight, bars[3].CutHeight));
        Assert.Equal(("50", "100"), (bars[0].FillText, bars[0].CutText));
        Assert.Empty(vm.Chart.CutMarks);
    }

    [Fact]
    public void Strip_chart_labels_each_break_and_outlines_the_selected_segment()
    {
        var (session, _, vm) = Fixture.Open();

        Assert.Equal([0, 0.25, 0.65, 0.9, 1], vm.Chart.Labels.Select(l => Math.Round(l.Position, 12)));
        Assert.Equal(new[] { 0d, 500, 1300, 1800, 2000 }.Select(s => Fixture.Sta(session, s)), vm.Chart.Labels.Select(l => l.Text));

        vm.Grid.SelectedRow = vm.Grid.Rows[1];

        Assert.Equal([false, true, false, false], vm.Chart.Bars.Select(b => b.IsSelected));
    }

    [Fact]
    public void Strip_chart_without_results_draws_hollow_bars_of_the_right_width()
    {
        var (_, _, vm) = Fixture.Open();

        Assert.All(vm.Chart.Bars, b => Assert.False(b.HasValue));
        Assert.All(vm.Chart.Bars, b => Assert.Equal((0d, 0d), (b.FillHeight, b.CutHeight)));
        Assert.Equal(0.4, vm.Chart.Bars[1].Width, 12);
        Assert.Equal(0, vm.Chart.MaxMagnitude);
    }

    [Fact]
    public async Task Rows_update_in_place_so_selection_survives_live_validation()
    {
        var (_, _, vm) = Fixture.Open();
        await vm.RegenerateCommand.ExecuteAsync();
        var row = vm.Grid.Rows[3];
        vm.Grid.SelectedRow = row;

        vm.Grid.Rows[1].EndStation.Text = "12+00";

        Assert.Same(row, vm.Grid.Rows[3]);
        Assert.Same(row, vm.Grid.SelectedRow);
        Assert.True(row.HasValues);                              // its stations did not move
        Assert.Equal(SegmentStatus.OutOfDate, row.Status);       // but the drawing is behind the dialog
        Assert.False(vm.Grid.Rows[1].HasValues);                 // stations moved: no value until regen
        Assert.False(vm.Grid.Rows[2].HasValues);
    }
}

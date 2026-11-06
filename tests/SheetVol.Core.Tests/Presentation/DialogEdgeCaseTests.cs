// HANDOFF.md 7.9 rows marked U (E902, E903, E904, E907, E912 U part, E914, E916, E917),
// plus E418 and E1020 labels as they surface in the dialog.

using System.Globalization;
using SheetVol.Core.Presentation;

namespace SheetVol.Core.Tests.Presentation;

public class DialogEdgeCaseTests
{
    // ---- E902 ---------------------------------------------------------------------

    [Fact]
    public void E902_no_alignment_shows_empty_state_naming_it_and_disables_regenerate()
    {
        var (_, _, vm) = Fixture.Open(session: new FakeSheetVolSession { Alignments = [] });

        Assert.True(vm.IsEmptyState);
        Assert.Equal(DialogText.EmptyNoAlignment, vm.EmptyStateText);
        Assert.False(vm.CanRegenerate);
        Assert.False(vm.RegenerateCommand.CanExecute());
        Assert.Equal(vm.EmptyStateText, vm.RegenerateBlockedReason);
        Assert.Empty(vm.Grid.Rows);
    }

    [Fact]
    public void E902_fewer_than_two_tin_surfaces_names_the_count_and_disables_regenerate()
    {
        var (_, _, vm) = Fixture.Open(session: new FakeSheetVolSession { Surfaces = ["EG"] });

        Assert.True(vm.IsEmptyState);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, DialogText.EmptyTooFewSurfacesFormat, 1), vm.EmptyStateText);
        Assert.DoesNotContain(DialogText.EmptyNoAlignment, vm.EmptyStateText);
        Assert.False(vm.RegenerateCommand.CanExecute());
    }

    [Fact]
    public void E902_both_missing_names_both()
    {
        var (_, _, vm) = Fixture.Open(session: new FakeSheetVolSession { Alignments = [], Surfaces = [] });

        Assert.Contains(DialogText.EmptyNoAlignment, vm.EmptyStateText);
        Assert.Contains(string.Format(CultureInfo.InvariantCulture, DialogText.EmptyTooFewSurfacesFormat, 0), vm.EmptyStateText);
        Assert.False(vm.CanRegenerate);
    }

    [Fact]
    public void E902_empty_state_clears_when_a_second_surface_appears()
    {
        var session = new FakeSheetVolSession { Surfaces = ["EG"] };
        var (_, _, vm) = Fixture.Open(session: session);
        Assert.True(vm.IsEmptyState);

        session.Surfaces = ["EG", "FG_A"];
        session.RaiseSourcesChanged();

        Assert.False(vm.IsEmptyState);
        Assert.Null(vm.EmptyStateText);
        Assert.True(vm.RegenerateCommand.CanExecute());
    }

    // ---- E903 ---------------------------------------------------------------------

    [Fact]
    public void E903_invalid_station_text_marks_the_cell_and_keeps_the_last_valid_value()
    {
        var (session, _, vm) = Fixture.Open();
        var specBefore = vm.Run.BreakSpec;
        var row2 = vm.Grid.Rows[1];
        var row3 = vm.Grid.Rows[2];
        var validatesBefore = session.ValidateCalls;

        row2.EndStation.Text = "12+0x";

        Assert.True(session.ValidateCalls > validatesBefore);                 // parsed by the session's Validate
        Assert.Equal("0, 500, 12+0x, 1800, 2000", session.LastValidated!.BreakSpec);
        Assert.True(row2.EndStation.HasError);
        Assert.Equal("'12+0x' is not a station.", row2.EndStation.Error);
        Assert.Equal("12+0x", row2.EndStation.Text);                           // typed text stays to correct
        Assert.Single(row2.EndStation.GetErrors(nameof(TextFieldViewModel.Text)).Cast<string>());
        Assert.Equal(specBefore, vm.Run.BreakSpec);                            // last valid value kept
        Assert.True(vm.Validation!.IsValid);
        Assert.Equal(1300, row2.EndRaw);
        Assert.Equal(Fixture.Sta(session,1300), row3.BeginStation.Text);         // neighbour untouched
        Assert.False(row3.BeginStation.HasError);
        Assert.Equal(DialogText.BlockedFieldErrors, vm.RegenerateBlockedReason);

        row2.EndStation.Revert();                                               // Esc
        Assert.False(row2.EndStation.HasError);
        Assert.Equal(Fixture.Sta(session,1300), row2.EndStation.Text);
        Assert.True(vm.CanRegenerate);

        row2.EndStation.Text = "12+00";
        Assert.False(row2.EndStation.HasError);
        Assert.Equal(1200, row2.EndRaw);
        Assert.Equal(0, session.RegenerateCalls);
    }

    [Fact]
    public void E903_station_outside_the_alignment_is_refused_on_the_cell()
    {
        var (_, _, vm) = Fixture.Open();
        var last = vm.Grid.Rows[3];

        last.EndStation.Text = "25+00";

        Assert.True(last.EndStation.HasError);
        Assert.Contains("outside the alignment", last.EndStation.Error);
        Assert.Equal(2000, last.EndRaw);
        Assert.Equal("0, 500, 1300, 1800, 2000", vm.Run.BreakSpec);
    }

    [Fact]
    public void E903_empty_or_comma_station_text_is_refused_without_touching_the_break_list()
    {
        var (session, _, vm) = Fixture.Open();
        var validates = session.ValidateCalls;

        vm.Grid.Rows[0].EndStation.Text = "  ";
        Assert.Equal(DialogText.StationRequired, vm.Grid.Rows[0].EndStation.Error);

        vm.Grid.Rows[0].EndStation.Text = "5,00";
        Assert.Equal(DialogText.StationHasComma, vm.Grid.Rows[0].EndStation.Error);

        Assert.Equal(validates, session.ValidateCalls);
        Assert.Equal("0, 500, 1300, 1800, 2000", vm.Run.BreakSpec);
    }

    // ---- E904 ---------------------------------------------------------------------

    [Fact]
    public void E904_editing_an_end_station_moves_the_next_begin()
    {
        var (session, _, vm) = Fixture.Open();

        vm.Grid.Rows[1].EndStation.Text = "12+00";

        Assert.Equal("0, 500, 12+00, 1800, 2000", vm.Run.BreakSpec);
        Assert.Equal(1200, vm.Grid.Rows[2].BeginRaw);
        Assert.Equal(Fixture.Sta(session,1200), vm.Grid.Rows[2].BeginStation.Text);
        Assert.Equal([500d, 700d, 600d, 200d], vm.Grid.Rows.Select(r => r.Length));
    }

    [Fact]
    public void E904_editing_a_begin_station_moves_the_previous_end()
    {
        var (_, _, vm) = Fixture.Open();

        vm.Grid.Rows[2].BeginStation.Text = "1250";

        Assert.Equal(1250, vm.Grid.Rows[1].EndRaw);
        Assert.Equal(1250, vm.Grid.Rows[2].BeginRaw);
        Assert.Equal([500d, 750d, 550d, 200d], vm.Grid.Rows.Select(r => r.Length));
    }

    [Fact]
    public void E904_a_gap_exists_only_through_an_explicit_skip()
    {
        var (_, _, vm) = Fixture.Open(Fixture.T2Run("0, 500, skip 500..800, 800, 1300"));
        Assert.Equal(2, vm.Grid.Rows.Count);

        vm.Grid.Rows[0].EndStation.Text = "400";

        Assert.Equal("0, 400, skip 400..800, 800, 1300", vm.Run.BreakSpec);
        Assert.Equal(400, vm.Grid.Rows[0].EndRaw);
        Assert.Equal(800, vm.Grid.Rows[1].BeginRaw);                           // the gap is the skip, not the edit
    }

    [Fact]
    public void E904_START_and_END_survive_a_grid_edit_so_END_keeps_following_the_alignment()
    {
        var (_, _, vm) = Fixture.Open(Fixture.T2Run("START..END@500"));
        Assert.Equal(4, vm.Grid.Rows.Count);

        vm.Grid.Rows[1].EndStation.Text = "12+00";

        Assert.Equal("START, 500, 12+00, 1500, END", vm.Run.BreakSpec);
        Assert.Equal([500d, 700d, 300d, 500d], vm.Grid.Rows.Select(r => r.Length));
    }

    // ---- E907 ---------------------------------------------------------------------

    [Fact]
    public void E907_edits_not_regenerated_are_kept_for_the_drawing_and_restored_on_reopen()
    {
        var (session, pending, vm) = Fixture.Open();
        vm.Grid.Rows[1].EndStation.Text = "12+00";
        vm.SameOnBothSides = false;
        vm.OffsetLeft.Text = "120";
        vm.DrawSummaryTable = false;

        vm.Close();

        Assert.True(pending.TryGet(session.ActiveDrawing!.Name, out var edit));
        Assert.Equal("0, 500, 12+00, 1800, 2000", edit.Run.BreakSpec);
        Assert.Equal(0, session.RegenerateCalls);                               // nothing written
        Assert.Equal("0, 500, 1300, 1800, 2000", session.Runs["Run1"].BreakSpec);

        var reopened = new DialogViewModel(session, pending, Fixture.Config);

        Assert.Equal(1200, reopened.Grid.Rows[1].EndRaw);
        Assert.Equal(-120, reopened.Run.OffsetLeft);
        Assert.Equal(100, reopened.Run.OffsetRight);
        Assert.Equal("120", reopened.OffsetLeft.Text);
        Assert.False(reopened.SameOnBothSides);
        Assert.False(reopened.DrawSummaryTable);
        Assert.True(reopened.IsDirty);
        Assert.Equal(DialogText.StaleEdits, reopened.StaleBannerText);
    }

    [Fact]
    public void E907_pending_edits_belong_to_their_drawing_only()
    {
        var session = new FakeSheetVolSession { ActiveDrawing = new(@"C:\proj\A.dwg", UnitSystem.Imperial) };
        var pending = new PendingEdits();
        var (_, _, a) = Fixture.Open(session: session, pending: pending);
        a.Grid.Rows[1].EndStation.Text = "12+00";
        a.Close();

        session.ActiveDrawing = new(@"C:\proj\B.dwg", UnitSystem.Imperial);
        var b = new DialogViewModel(session, pending, Fixture.Config);

        Assert.Equal(1300, b.Grid.Rows[1].EndRaw);
        Assert.False(b.IsDirty);
        Assert.True(pending.TryGet(@"c:\PROJ\a.dwg", out _));                    // key ignores case, like Windows paths
    }

    [Fact]
    public async Task E907_regenerate_clears_the_pending_edits()
    {
        var (session, pending, vm) = Fixture.Open();
        vm.Grid.Rows[1].EndStation.Text = "12+00";
        vm.Close();
        var reopened = new DialogViewModel(session, pending, Fixture.Config);

        await reopened.RegenerateCommand.ExecuteAsync();

        Assert.False(pending.TryGet(session.ActiveDrawing!.Name, out _));
        Assert.Equal("0, 500, 12+00, 1800, 2000", session.Runs["Run1"].BreakSpec);
        reopened.Close();
        Assert.Equal(0, pending.Count);
        var third = new DialogViewModel(session, pending, Fixture.Config);
        Assert.False(third.IsDirty);
        Assert.Equal(1200, third.Grid.Rows[1].EndRaw);
    }

    [Fact]
    public void E907_switching_the_active_drawing_keeps_the_old_drawings_edits()
    {
        var (session, pending, vm) = Fixture.Open();
        vm.Grid.Rows[1].EndStation.Text = "12+00";

        session.ActiveDrawing = new(@"C:\proj\other.dwg", UnitSystem.Imperial);
        session.RaiseSourcesChanged();

        Assert.True(pending.TryGet(@"C:\proj\T2.dwg", out _));
        Assert.Equal(@"C:\proj\other.dwg", vm.Drawing!.Name);
        Assert.False(vm.IsDirty);

        session.ActiveDrawing = new(@"C:\proj\T2.dwg", UnitSystem.Imperial);
        session.RaiseSourcesChanged();
        Assert.Equal(1200, vm.Grid.Rows[1].EndRaw);
        Assert.True(vm.IsDirty);
    }

    // ---- E912 (U part) ---------------------------------------------------------------

    [Fact]
    public async Task E912_tracker_staleness_raises_the_banner_and_marks_chips_out_of_date()
    {
        var (session, _, vm) = Fixture.Open();
        await vm.RegenerateCommand.ExecuteAsync();
        Assert.Null(vm.StaleBannerText);

        session.Status =new RunStatus(true, vm.LastResult);
        session.RaiseSourcesChanged();

        Assert.Equal(DialogText.StaleExternal, vm.StaleBannerText);
        Assert.True(vm.ShowStaleBanner);
        Assert.All(vm.Grid.Rows, r => Assert.Equal(SegmentStatus.OutOfDate, r.Status));
        Assert.All(vm.Grid.Rows, r => Assert.True(r.HasValues));                 // values kept, marked
        Assert.Equal(1, session.RegenerateCalls);

        session.Status =new RunStatus(false, vm.LastResult);
        session.RaiseSourcesChanged();
        Assert.Null(vm.StaleBannerText);
        Assert.All(vm.Grid.Rows, r => Assert.Equal(SegmentStatus.Current, r.Status));
    }

    [Fact]
    public async Task E912_values_refresh_after_an_external_regen()
    {
        var (session, _, vm) = Fixture.Open();
        await vm.RegenerateCommand.ExecuteAsync();
        Assert.Equal("100", vm.Grid.Rows[0].CutText);

        var stored = session.Runs["Run1"];                                       // a typed SVREGEN after a surface rebuild
        var external = session.BuildResult(stored, session.Plan(stored), scale: 2);
        session.Status =new RunStatus(false, external);
        session.RaiseSourcesChanged();

        Assert.Same(external, vm.LastResult);
        Assert.Equal(["200", "200", "0", "0"], vm.Grid.Rows.Select(r => r.CutText));
        Assert.All(vm.Grid.Rows, r => Assert.Equal(SegmentStatus.Current, r.Status));
        Assert.Equal("400", vm.Grid.Total.CutText);
        Assert.Equal(1, session.RegenerateCalls);
    }

    [Fact]
    public void E912_values_from_a_regen_made_before_the_dialog_opened_are_shown()
    {
        var session = new FakeSheetVolSession();
        var plan = session.Plan(Fixture.T2Run());
        var stored = Fixture.T2Run() with { KnownSegments = plan.Segments };
        session.Status = new RunStatus(false, session.BuildResult(stored, plan, 1));

        var (_, _, vm) = Fixture.Open(stored, session);

        Assert.All(vm.Grid.Rows, r => Assert.True(r.HasValues));
        Assert.All(vm.Grid.Rows, r => Assert.Equal(SegmentStatus.Current, r.Status));
        Assert.True(vm.Grid.TotalsVisible);
        Assert.Equal(0, session.RegenerateCalls);
    }

    [Fact]
    public void E912_object_lists_and_an_external_definition_edit_refresh_when_nothing_is_pending()
    {
        var (session, _, vm) = Fixture.Open();

        session.Alignments.Add("K");
        session.Runs["Run1"] = Fixture.T2Run("0, 1000, 2000");                 // SVBREAK typed at the command line
        session.RaiseSourcesChanged();

        Assert.Contains("K", vm.Alignments);
        Assert.Equal(2, vm.Grid.Rows.Count);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void E912_an_external_definition_edit_does_not_overwrite_pending_dialog_edits()
    {
        var (session, _, vm) = Fixture.Open();
        vm.Grid.Rows[1].EndStation.Text = "12+00";

        session.Runs["Run1"] = Fixture.T2Run("0, 1000, 2000");
        session.RaiseSourcesChanged();

        Assert.Equal(4, vm.Grid.Rows.Count);
        Assert.Equal(1200, vm.Grid.Rows[1].EndRaw);
        Assert.True(vm.IsDirty);
    }

    // ---- E914, E1020 ---------------------------------------------------------------------

    [Fact]
    public void E914_metric_drawing_switches_labels_units_and_offered_area_units()
    {
        var session = new FakeSheetVolSession { PlusScale = 1000, ActiveDrawing = new(@"C:\proj\metric.dwg", UnitSystem.Metric) };
        var (_, _, vm) = Fixture.Open(Fixture.T2Run() with { AreaUnit = AreaUnit.SquareMetres }, session);

        Assert.Equal(UnitSystem.Metric, vm.Units);
        Assert.Equal("m", vm.LengthUnitLabel);
        Assert.Equal("m³", vm.VolumeUnitLabel);
        Assert.Equal("Boundary offsets (m)", vm.OffsetsHeader);
        Assert.Equal(["m²", "ha"], vm.AreaUnits.Select(u => u.Label));
        Assert.Equal(AreaUnit.SquareMetres, vm.SelectedAreaUnit!.Unit);
        Assert.Equal("Length (m)", vm.Grid.LengthHeader);
        Assert.Equal("Cut (m³)", vm.Grid.CutHeader);
        Assert.Equal("Fill (m³)", vm.Grid.FillHeader);
        Assert.Equal("Net (m³)", vm.Grid.NetHeader);
        Assert.Equal(session.FormatStation("S", 500), vm.Grid.Rows[0].EndStation.Text);
        Assert.Equal("m³", vm.Chart.UnitLabel);
    }

    [Fact]
    public void E914_new_run_in_a_metric_drawing_takes_metric_defaults()
    {
        var session = new FakeSheetVolSession { Alignments = ["S"], ActiveDrawing = new(@"C:\proj\metric.dwg", UnitSystem.Metric) };
        var vm = new DialogViewModel(session, new PendingEdits(), Fixture.Config);

        Assert.Equal(AreaUnit.SquareMetres, vm.Run.AreaUnit);
        Assert.Equal(Fixture.Config.DefaultBreakSpecMetric, vm.Run.BreakSpec);
        Assert.Equal(-Fixture.Config.DefaultOffsetMetric, vm.Run.OffsetLeft);
        Assert.Equal(Fixture.Config.DefaultOffsetMetric, vm.Run.OffsetRight);
    }

    [Fact]
    public void E914_a_stored_imperial_area_unit_maps_to_its_metric_counterpart()
    {
        var session = new FakeSheetVolSession { ActiveDrawing = new(@"C:\proj\metric.dwg", UnitSystem.Metric) };
        var (_, _, vm) = Fixture.Open(Fixture.T2Run() with { AreaUnit = AreaUnit.Acres }, session);

        Assert.Equal(AreaUnit.Hectares, vm.SelectedAreaUnit!.Unit);
        Assert.Equal(AreaUnit.Hectares, vm.Run.AreaUnit);
    }

    [Fact]
    public void E1020_metric_hcfcd_labels_switch_to_m_ha_m3_and_m3_per_m()
    {
        var session = new FakeSheetVolSession { ActiveDrawing = new(@"C:\proj\metric.dwg", UnitSystem.Metric) };
        var (_, _, vm) = Fixture.Open(Fixture.T2Run() with { AreaUnit = AreaUnit.SquareMetres }, session);

        vm.SelectedPreset = vm.Presets.Single(p => p.Preset == ReportPreset.HcfcdSegment);

        Assert.Equal(AreaUnit.Hectares, vm.Run.AreaUnit);
        Assert.Equal("ha", vm.Grid.SelectedAreaUnit!.Label);
        Assert.Equal("Impacted length (m)", vm.Grid.ImpactedLengthHeader);
        Assert.Equal("Cut volume (m³)", vm.Grid.CutVolumeHeader);
        Assert.Equal("Average (m³/m)", vm.Grid.AverageHeader);
        // Rev 3 (X6): the OHWM elevation header and label are gone; nothing else changes with the unit.
    }

    // ---- E916 ---------------------------------------------------------------------

    [Fact]
    public async Task E916_comparison_selector_appears_with_two_analyses_and_drives_grid_and_chart()
    {
        var run = Fixture.T2Run(analyses:
        [
            new AnalysisDef(Fixture.AnalysisA, "EG vs FG_A", "EG", "FG_A"),
            new AnalysisDef(Fixture.AnalysisB, "EG vs FG_B", "EG", "FG_B"),
        ]);
        var (session, _, vm) = Fixture.Open(run);
        await vm.RegenerateCommand.ExecuteAsync();

        Assert.True(vm.Grid.ShowComparisonSelector);
        Assert.Equal(["EG vs FG_A", "EG vs FG_B"], vm.Grid.Comparisons.Select(c => c.Name));
        Assert.Equal(Fixture.AnalysisA, vm.Grid.SelectedComparison!.AnalysisId);
        Assert.Equal(["100", "100", "0", "0"], vm.Grid.Rows.Select(r => r.CutText));
        Assert.Equal(150, vm.Chart.MaxMagnitude);

        vm.Grid.SelectedComparison = vm.Grid.Comparisons[1];

        Assert.Equal(["200", "200", "0", "0"], vm.Grid.Rows.Select(r => r.CutText));
        Assert.Equal(["100", "0", "300", "0"], vm.Grid.Rows.Select(r => r.FillText));
        Assert.Equal("400", vm.Grid.Total.CutText);
        Assert.Equal(300, vm.Chart.MaxMagnitude);
        Assert.Equal(200.0 / 300, vm.Chart.Bars[0].CutHeight, 12);
        Assert.Equal(1, session.RegenerateCalls);
    }

    [Fact]
    public void E916_one_analysis_hides_the_selector()
    {
        var (_, _, vm) = Fixture.Open();

        Assert.False(vm.Grid.ShowComparisonSelector);
        Assert.Single(vm.Grid.Comparisons);
    }

    // ---- E917, E418 ------------------------------------------------------------------

    [Fact]
    public async Task E917_area_unit_change_reformats_at_once_without_regenerating_and_is_saved_with_the_run()
    {
        var (session, _, vm) = Fixture.Open();
        await vm.RegenerateCommand.ExecuteAsync();
        var rows = vm.Grid.Rows;
        Assert.Equal(["SF", "AC"], vm.AreaUnits.Select(u => u.Label));            // offered units follow the drawing
        Assert.Equal(["100,000", "160,000", "100,000", "40,000"], rows.Select(r => r.Area2DText));   // T23
        Assert.Equal("400,000", vm.Grid.Total.Area2DText);
        var raised = new List<string?>();
        rows[0].PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Grid.SelectedAreaUnit = vm.AreaUnits.Single(u => u.Unit == AreaUnit.Acres);

        Assert.Contains(nameof(SegmentRowViewModel.Area2DText), raised);
        Assert.Equal(["2.30", "3.67", "2.30", "0.92"], rows.Select(r => r.Area2DText));   // T23 in AC
        Assert.Equal("9.19", vm.Grid.Total.Area2DText);                                     // sum of displayed rows (A6)
        Assert.Equal(AreaUnit.Acres, vm.Run.AreaUnit);
        Assert.Equal("AC", vm.SelectedAreaUnit!.Label);
        Assert.Equal(1, session.RegenerateCalls);                                           // E418: no recompute
        Assert.Equal(100_000, vm.LastResult!.Segments[0].Area2D);                           // raw area unchanged

        await vm.RegenerateCommand.ExecuteAsync();
        Assert.Equal(AreaUnit.Acres, session.LastRegenerated!.AreaUnit);                    // saved with the run
        Assert.Equal(AreaUnit.Acres, session.Runs["Run1"].AreaUnit);
    }

    [Fact]
    public async Task E418_area_unit_switch_marks_the_drawn_tables_out_of_date_until_regen()
    {
        var (_, _, vm) = Fixture.Open();
        await vm.RegenerateCommand.ExecuteAsync();

        vm.SelectedAreaUnit = vm.AreaUnits.Single(u => u.Unit == AreaUnit.Acres);

        Assert.All(vm.Grid.Rows, r => Assert.Equal(SegmentStatus.OutOfDate, r.Status));
        Assert.All(vm.Grid.Rows, r => Assert.True(r.HasValues));
        Assert.True(vm.Grid.TotalsVisible);
        Assert.Equal(DialogText.StaleEdits, vm.StaleBannerText);

        await vm.RegenerateCommand.ExecuteAsync();
        Assert.All(vm.Grid.Rows, r => Assert.Equal(SegmentStatus.Current, r.Status));
        Assert.Null(vm.StaleBannerText);
    }
}

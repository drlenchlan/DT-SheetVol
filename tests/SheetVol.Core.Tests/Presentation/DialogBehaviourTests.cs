// Dialog behaviours from HANDOFF.md 6.14 that are not a numbered E-row: live
// validation, left panel, footer, regen progress and cancel, HCFCD preset, grid toolbar.

using SheetVol.Core.Presentation;

namespace SheetVol.Core.Tests.Presentation;

public class DialogBehaviourTests
{
    // ---- Live validation ---------------------------------------------------------------

    [Fact]
    public void Every_edit_reruns_Validate_only_and_never_Regenerate()
    {
        var (session, _, vm) = Fixture.Open();
        var before = session.ValidateCalls;

        vm.SameOnBothSides = false;                         // a view setting, not a run edit
        vm.OffsetLeft.Text = "110";
        vm.OffsetRight.Text = "90";
        vm.Grid.Rows[0].EndStation.Text = "450";
        vm.Analyses[0].CutFactor.Text = "1.25";
        vm.Analyses[0].ComparisonSurface = "FG_B";
        vm.SelectedAlignment = "C";
        vm.AddAnalysisCommand.Execute();

        Assert.Equal(before + 7, session.ValidateCalls);
        Assert.Equal(0, session.RegenerateCalls);
        Assert.Equal(-110, vm.Run.OffsetLeft);
        Assert.Equal(90, vm.Run.OffsetRight);
        Assert.Equal(1.25, vm.Run.Analyses[0].CutFactor);
        Assert.Equal("FG_B", vm.Run.Analyses[0].ComparisonSurface);
        Assert.Equal("C", vm.Run.AlignmentName);
        Assert.Equal(2, vm.Run.Analyses.Count);
    }

    // ---- Left panel ------------------------------------------------------------------------

    [Fact]
    public void Same_on_both_sides_mirrors_offsets_and_bad_text_is_refused_inline()
    {
        var (_, _, vm) = Fixture.Open();
        Assert.True(vm.SameOnBothSides);                     // loaded run is ±100

        vm.OffsetLeft.Text = "150";
        Assert.Equal((-150d, 150d), (vm.Run.OffsetLeft, vm.Run.OffsetRight));
        Assert.Equal("150", vm.OffsetRight.Text);

        vm.SameOnBothSides = false;
        vm.OffsetRight.Text = "80";
        Assert.Equal((-150d, 80d), (vm.Run.OffsetLeft, vm.Run.OffsetRight));

        vm.SameOnBothSides = true;
        Assert.Equal((-150d, 150d), (vm.Run.OffsetLeft, vm.Run.OffsetRight));
        Assert.Equal("150", vm.OffsetRight.Text);

        vm.OffsetLeft.Text = "wide";
        Assert.Equal(DialogText.NotANumber, vm.OffsetLeft.Error);
        Assert.Equal(-150, vm.Run.OffsetLeft);
        Assert.Equal(DialogText.BlockedFieldErrors, vm.RegenerateBlockedReason);
    }

    [Fact]
    public async Task Pick_alignment_selects_the_picked_name_and_shows_its_station_range()
    {
        var (session, _, vm) = Fixture.Open();
        session.PickAlignmentResult = "C";

        await vm.PickAlignmentCommand.ExecuteAsync();

        Assert.Equal("C", vm.SelectedAlignment);
        Assert.Equal("C", vm.Run.AlignmentName);
        Assert.Equal($"{Fixture.Sta(session, 0)} to {Fixture.Sta(session, 2000)}", vm.StationRangeText);

        session.PickAlignmentResult = null;                  // Esc at the pick prompt
        await vm.PickAlignmentCommand.ExecuteAsync();
        Assert.Equal("C", vm.SelectedAlignment);
    }

    [Fact]
    public void New_run_selects_the_only_alignment_but_never_guesses_surfaces()
    {
        var session = new FakeSheetVolSession { Alignments = ["S"] };
        var vm = new DialogViewModel(session, new PendingEdits(), Fixture.Config);

        Assert.Equal("S", vm.SelectedAlignment);
        Assert.Equal(Fixture.Config.DefaultBreakSpecImperial, vm.Run.BreakSpec);
        Assert.Equal(AreaUnit.SquareFeet, vm.Run.AreaUnit);
        Assert.Equal(AutoMode.Views, vm.Run.AutoMode);
        var analysis = Assert.Single(vm.Analyses);
        Assert.Null(analysis.BaseSurface);
        Assert.Null(analysis.ComparisonSurface);
        Assert.Equal("Comparison 1", analysis.Name.Text);
        Assert.Equal(DialogText.BlockedSurfaces, vm.RegenerateBlockedReason);
        Assert.Equal(DialogText.StaleNoResult, vm.StaleBannerText);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void Analyses_add_auto_name_factor_errors_and_remove()
    {
        var (_, _, vm) = Fixture.Open();

        vm.AddAnalysisCommand.Execute();
        var added = vm.Analyses[1];
        Assert.Equal("Comparison 2", added.Name.Text);
        Assert.Equal(DialogText.BlockedSurfaces, vm.RegenerateBlockedReason);

        added.BaseSurface = "EG";
        added.ComparisonSurface = "FG_B";
        Assert.Equal("EG vs FG_B", added.Name.Text);
        Assert.Equal("EG vs FG_B", vm.Run.Analyses[1].Name);
        Assert.True(vm.Grid.ShowComparisonSelector);
        Assert.True(vm.CanRegenerate);

        added.FillFactor.Text = "0";
        Assert.Equal(DialogText.FactorMustBePositive, added.FillFactor.Error);
        added.FillFactor.Text = "x";
        Assert.Equal(DialogText.NotANumber, added.FillFactor.Error);
        Assert.Equal(1.0, vm.Run.Analyses[1].FillFactor);
        Assert.False(vm.CanRegenerate);
        added.FillFactor.Text = "0.9";
        Assert.Equal(0.9, vm.Run.Analyses[1].FillFactor);

        added.Name.Text = "SEDIMENT";
        added.ComparisonSurface = "FG_A";
        Assert.Equal("SEDIMENT", vm.Run.Analyses[1].Name);    // a typed name is kept

        Assert.True(added.RemoveCommand.CanExecute());
        added.RemoveCommand.Execute();
        Assert.Single(vm.Analyses);
        Assert.Single(vm.Run.Analyses);
        Assert.False(vm.Analyses[0].RemoveCommand.CanExecute());   // the last one stays
    }

    [Fact]
    public void Fill_table_writes_an_interval_spec_and_reports_bad_input_inline()
    {
        var (_, _, vm) = Fixture.Open();
        Assert.False(vm.FillTableCommand.CanExecute());

        vm.IntervalEvery.Text = "400";
        Assert.True(vm.FillTableCommand.CanExecute());
        vm.FillTableCommand.Execute();
        Assert.Equal("START..END@400", vm.Run.BreakSpec);
        Assert.Equal(5, vm.Grid.Rows.Count);
        Assert.All(vm.Grid.Rows, r => Assert.Equal(400, r.Length));

        vm.IntervalFrom.Text = "5+00";
        vm.IntervalTo.Text = "15+00";
        vm.IntervalEvery.Text = "250";
        vm.FillTableCommand.Execute();
        Assert.Equal("5+00..15+00@250", vm.Run.BreakSpec);
        Assert.Equal(4, vm.Grid.Rows.Count);
        Assert.Null(vm.IntervalError);

        vm.IntervalFrom.Text = "abc";
        vm.FillTableCommand.Execute();
        Assert.NotNull(vm.IntervalError);
        Assert.Contains("is not a station", vm.IntervalError);
        Assert.Equal("5+00..15+00@250", vm.Run.BreakSpec);   // last valid spec kept

        vm.IntervalEvery.Text = "-5";
        Assert.Equal(DialogText.IntervalMustBePositive, vm.IntervalEvery.Error);
        Assert.False(vm.FillTableCommand.CanExecute());
    }

    [Fact]
    public void Draw_options_are_dialog_state_and_count_as_pending_edits()
    {
        var (_, _, vm) = Fixture.Open();
        Assert.Null(vm.Run.Draw);                                // the stored run has none: draw everything
        Assert.True(vm.DrawBoundaryPolylines && vm.DrawTableAtEachSegment && vm.DrawSummaryTable);

        vm.DrawBoundaryPolylines = false;

        Assert.False(vm.Draw.BoundaryPolylines);
        Assert.Equal(new DrawOptions(BoundaryPolylines: false), vm.Run.Draw);   // D6: the checkbox is RunDefinition.Draw
        Assert.True(vm.IsDirty);
        Assert.Equal(DialogText.StaleEdits, vm.StaleBannerText);
    }

    [Fact]
    public async Task D6_draw_options_reach_the_engine_on_the_run_and_all_on_equals_no_draw_options()
    {
        var (session, _, vm) = Fixture.Open();

        vm.DrawBoundaryPolylines = false;
        vm.DrawBoundaryPolylines = true;                         // back to all on: same as Draw = null
        Assert.False(vm.IsDirty);

        vm.DrawSummaryTable = false;
        vm.DrawTableAtEachSegment = false;
        await vm.RegenerateCommand.ExecuteAsync();

        Assert.Equal(new DrawOptions(BoundaryPolylines: true, TableAtEachSegment: false, SummaryTable: false), session.LastRegenerated!.Draw);
        Assert.Equal(session.LastRegenerated.Draw, session.Runs["Run1"].Draw);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void D6_a_new_run_takes_the_configured_draw_options()
    {
        var session = new FakeSheetVolSession { Alignments = ["S"] };
        var config = Fixture.Config with { DefaultDrawOptions = new DrawOptions(SummaryTable: false) };

        var vm = new DialogViewModel(session, new PendingEdits(), config);

        Assert.Equal(new DrawOptions(SummaryTable: false), vm.Run.Draw);
        Assert.False(vm.DrawSummaryTable);
        Assert.False(vm.IsDirty);
    }

    // ---- Footer --------------------------------------------------------------------------

    [Fact]
    public void Regenerate_on_change_checkbox_maps_to_AutoMode()
    {
        var (_, _, vm) = Fixture.Open();
        Assert.True(vm.RegenerateOnChange);                  // A4 default Views

        vm.RegenerateOnChange = false;
        Assert.Equal(AutoMode.Off, vm.Run.AutoMode);
        vm.RegenerateOnChange = true;
        Assert.Equal(AutoMode.Views, vm.Run.AutoMode);

        var (_, _, all) = Fixture.Open(Fixture.T2Run() with { AutoMode = AutoMode.All });
        all.RegenerateOnChange = false;
        all.RegenerateOnChange = true;
        Assert.Equal(AutoMode.All, all.Run.AutoMode);        // the run's own mode comes back

        var (_, _, off) = Fixture.Open(Fixture.T2Run() with { AutoMode = AutoMode.Off });
        Assert.False(off.RegenerateOnChange);
        off.RegenerateOnChange = true;
        Assert.Equal(Fixture.Config.AutoModeWhenChecked, off.Run.AutoMode);
    }

    [Fact]
    public async Task Export_csv_asks_the_session_after_a_regen_and_not_while_edits_are_pending()
    {
        var (session, _, vm) = Fixture.Open();
        Assert.False(vm.ExportCsvCommand.CanExecute());

        await vm.RegenerateCommand.ExecuteAsync();
        Assert.True(vm.ExportCsvCommand.CanExecute());
        await vm.ExportCsvCommand.ExecuteAsync();
        Assert.Equal(["Run1"], session.ExportRequests);
        Assert.Equal("run.csv", vm.ExportCsvPath);                 // the paths the session's export returned
        Assert.Equal("run.json", vm.ExportJsonPath);
        Assert.Equal("Exported run.csv and run.json.", vm.StatusMessage);

        vm.OffsetLeft.Text = "90";
        Assert.False(vm.ExportCsvCommand.CanExecute());
    }

    [Fact]
    public async Task Export_without_a_result_or_with_nothing_written_says_so()
    {
        var (session, _, vm) = Fixture.Open();
        await vm.RegenerateCommand.ExecuteAsync();

        session.ExportFactory = _ => null;                          // the session has no result for the run
        await vm.ExportCsvCommand.ExecuteAsync();
        Assert.Equal(DialogText.ExportNoResult, vm.StatusMessage);
        Assert.Null(vm.ExportCsvPath);

        var skipped = new Problem("SV-E506", Severity.Warning, "The CSV folder is read-only.");
        session.ExportFactory = _ => vm.LastResult! with { CsvPath = null, JsonPath = null, Problems = [skipped] };
        await vm.ExportCsvCommand.ExecuteAsync();
        Assert.Equal("Export did not write a file: The CSV folder is read-only.", vm.StatusMessage);
        Assert.Null(vm.ExportCsvPath);
        Assert.Null(vm.ExportJsonPath);

        session.ExportFactory = _ => throw new IOException("disk full");
        await vm.ExportCsvCommand.ExecuteAsync();
        Assert.Equal("Export stopped: disk full", vm.StatusMessage);
    }

    [Fact]
    public void Close_clears_the_highlight_unsubscribes_and_raises_CloseRequested()
    {
        var (session, _, vm) = Fixture.Open();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.Grid.SelectedRow = vm.Grid.Rows[0];
        Assert.Equal(1, session.SourcesChangedSubscribers);

        vm.CloseCommand.Execute();

        Assert.True(closed);
        Assert.Null(session.Highlights[^1]);
        Assert.Equal(0, session.SourcesChangedSubscribers);
    }

    [Fact]
    public void Active_editor_command_disables_regenerate()
    {
        var (session, _, vm) = Fixture.Open();
        Assert.True(vm.CanRegenerate);

        session.IsCommandActive = true;
        vm.RefreshCommandState();
        Assert.False(vm.RegenerateCommand.CanExecute());
        Assert.Equal(DialogText.BlockedCommandActive, vm.RegenerateBlockedReason);

        session.IsCommandActive = false;
        vm.RefreshCommandState();
        Assert.True(vm.RegenerateCommand.CanExecute());
    }

    [Fact]
    public void No_drawing_disables_the_dialog_with_a_one_line_reason_and_rebinds_when_one_opens()
    {
        var (session, _, vm) = Fixture.Open(session: new FakeSheetVolSession { ActiveDrawing = null });

        Assert.False(vm.IsDrawingOpen);
        Assert.False(vm.CanEdit);
        Assert.Equal(DialogText.NoDrawingOpen, vm.DisabledReason);
        Assert.Equal(DialogText.NoDrawingOpen, vm.RegenerateBlockedReason);
        Assert.Empty(vm.Grid.Rows);
        Assert.Equal(0, session.ValidateCalls);

        session.ActiveDrawing = new(@"C:\proj\T2.dwg", UnitSystem.Imperial);
        session.RaiseSourcesChanged();
        Assert.True(vm.IsDrawingOpen);
        Assert.Equal(4, vm.Grid.Rows.Count);
    }

    // ---- Regenerate, progress, cancel, banner ---------------------------------------------

    [Fact]
    public async Task Regenerate_shows_progress_and_cancel_leaves_the_previous_values()
    {
        var (session, _, vm) = Fixture.Open();
        await vm.RegenerateCommand.ExecuteAsync();
        var first = vm.LastResult;
        session.Gate = new TaskCompletionSource();

        var running = vm.RegenerateCommand.ExecuteAsync();

        Assert.True(vm.IsBusy);
        Assert.Equal(0.5, vm.Progress);
        Assert.False(vm.CanEdit);
        Assert.True(vm.CancelCommand.CanExecute());
        Assert.False(vm.RegenerateCommand.CanExecute());
        Assert.False(vm.CloseCommand.CanExecute());
        Assert.False(vm.Grid.Rows[0].EndStation.IsEditable);
        vm.Grid.Rows[0].EndStation.Text = "450";                // the view blocks this; the view model refuses it too
        Assert.Equal(DialogText.BusyRefused, vm.Grid.Rows[0].EndStation.Error);

        vm.CancelCommand.Execute();
        await running;

        Assert.False(vm.IsBusy);
        Assert.Equal(DialogText.RegenCancelled, vm.StatusMessage);
        Assert.Same(first, vm.LastResult);
        Assert.All(vm.Grid.Rows, r => Assert.Equal(SegmentStatus.Current, r.Status));
        Assert.True(vm.Grid.Rows[0].EndStation.IsEditable);
    }

    [Fact]
    public async Task Successful_regenerate_reports_elapsed_time_and_values()
    {
        var (session, _, vm) = Fixture.Open();

        await vm.RegenerateCommand.ExecuteAsync();

        Assert.Equal("Regenerated in 1.3 s.", vm.StatusMessage);
        Assert.Equal(1.0, vm.Progress);
        Assert.Equal(1, session.RegenerateCalls);
        Assert.Equal(vm.Run.BreakSpec, session.LastRegenerated!.BreakSpec);
        Assert.All(vm.Grid.Rows, r => Assert.True(r.HasValues));
    }

    [Fact]
    public async Task Stale_banner_follows_edits_and_clears_after_regenerate()
    {
        var (_, _, vm) = Fixture.Open();
        Assert.Equal(DialogText.StaleNoResult, vm.StaleBannerText);

        await vm.RegenerateCommand.ExecuteAsync();
        Assert.Null(vm.StaleBannerText);

        vm.OffsetLeft.Text = "90";
        Assert.Equal(DialogText.StaleEdits, vm.StaleBannerText);
        Assert.All(vm.Grid.Rows, r => Assert.False(r.HasValues));   // offsets changed: old values no longer apply
        Assert.All(vm.Grid.Rows, r => Assert.Equal(SegmentStatus.OutOfDate, r.Status));
        Assert.False(vm.Grid.TotalsVisible);

        await vm.RegenerateCommand.ExecuteAsync();
        Assert.Null(vm.StaleBannerText);
        Assert.True(vm.Grid.TotalsVisible);
    }

    [Fact]
    public async Task Failed_regenerate_keeps_previous_values_and_shows_the_row_problem()
    {
        var (session, _, vm) = Fixture.Open();
        await vm.RegenerateCommand.ExecuteAsync();
        var first = vm.LastResult;
        session.FailNextRegen = true;

        await vm.RegenerateCommand.ExecuteAsync();

        Assert.Equal(DialogText.RegenFailed, vm.StatusMessage);
        Assert.Same(first, vm.LastResult);
        Assert.Equal(SegmentStatus.Error, vm.Grid.Rows[2].Status);
        Assert.Equal("SV-E801", vm.Grid.Rows[2].ProblemCode);
        Assert.True(vm.Grid.Rows[0].HasValues);
        Assert.False(vm.Grid.TotalsVisible);

        vm.OffsetLeft.Text = "100.5";                         // the next edit clears the failed attempt's marks
        Assert.NotEqual(SegmentStatus.Error, vm.Grid.Rows[2].Status);
    }

    // ---- HCFCD preset ---------------------------------------------------------------------

    [Fact]
    public void Hcfcd_preset_switches_labels_columns_chart_and_unit_and_needs_an_OHWM()
    {
        var (_, _, vm) = Fixture.Open();

        vm.SelectedPreset = vm.Presets.Single(p => p.Preset == ReportPreset.HcfcdSegment);

        Assert.True(vm.IsHcfcd);
        Assert.True(vm.Grid.IsHcfcd);
        Assert.False(vm.Grid.IsStandard);
        Assert.True(vm.Chart.IsHcfcd);
        Assert.Equal(DialogText.ChartTitleHcfcd, vm.Chart.Title);
        Assert.Equal(AreaUnit.Acres, vm.Run.AreaUnit);
        Assert.Equal(DialogText.AnalysisNameHcfcd, vm.AnalysisNameLabel);
        Assert.Equal(DialogText.BaseSurfaceHcfcd, vm.BaseSurfaceLabel);
        Assert.Equal(DialogText.ComparisonSurfaceHcfcd, vm.ComparisonSurfaceLabel);
        Assert.Equal("Impacted length (ft)", vm.Grid.ImpactedLengthHeader);
        Assert.Equal(DialogText.ColumnImpactedArea, vm.Grid.ImpactedAreaHeader);
        Assert.Equal("Cut volume (CY)", vm.Grid.CutVolumeHeader);
        Assert.Equal("Average (CY/ft)", vm.Grid.AverageHeader);
        Assert.Equal("OHWM elevation (ft)", vm.Grid.OhwmHeader);
        Assert.Equal(DialogText.BlockedOhwm, vm.RegenerateBlockedReason);

        vm.Ohwm.Text = "100";

        Assert.Equal(100, vm.Run.Ohwm!.DefaultElevation);
        Assert.True(vm.CanRegenerate);
        Assert.Equal("100.00", vm.Grid.Rows[0].Ohwm.Text);
    }

    [Fact]
    public async Task Hcfcd_rows_show_impacted_length_area_volume_and_average_T25()
    {
        var session = new FakeSheetVolSession
        {
            ImpactFactory = (_, s) => new ImpactResult(s.Id, "SEDIMENT", 500, 16_000, 26_000,
                [(s.StartStation, s.EndStation)], 0, 0, false),
        };
        var run = Fixture.T2Run("0, 500, 1000") with
        {
            Preset = ReportPreset.HcfcdSegment,
            AreaUnit = AreaUnit.Acres,
            Ohwm = new OhwmSpec(100, new Dictionary<Guid, double>()),
        };
        var (_, _, vm) = Fixture.Open(run, session);

        await vm.RegenerateCommand.ExecuteAsync();

        var row = vm.Grid.Rows[0];
        Assert.Equal("500", row.ImpactedLengthText);           // T25: length 500
        Assert.Equal("0.37", row.ImpactedAreaText);             // 16,000 ft² = 0.37 ac
        Assert.Equal("963", row.CutVolumeText);                 // 962.963 CY
        Assert.Equal("1.93", row.AverageText);                  // from unrounded values (E1019)
        Assert.Equal("1,000", vm.Grid.Total.ImpactedLengthText);
        Assert.Equal("0.74", vm.Grid.Total.ImpactedAreaText);
        Assert.Equal("1,926", vm.Grid.Total.CutVolumeText);
        Assert.Equal("1.93", vm.Grid.Total.AverageText);
        Assert.Equal("Sum of segments: impacted length 1,000 ft, impacted area 0.74 AC, cut volume 1,926 CY.", vm.Grid.ReconciliationText);

        Assert.Equal(2, vm.Chart.CutMarks.Count);
        Assert.Equal((0d, 0.5d), (vm.Chart.CutMarks[0].Left, vm.Chart.CutMarks[0].Width));
        Assert.Equal((0.5d, 0.5d), (vm.Chart.CutMarks[1].Left, vm.Chart.CutMarks[1].Width));
        Assert.All(vm.Chart.Bars, b => Assert.Equal(1, b.CutHeight, 12));
        Assert.All(vm.Chart.Bars, b => Assert.Equal(0, b.FillHeight));
    }

    [Fact]
    public async Task Hcfcd_zero_length_average_is_NA_and_a_search_limit_hit_is_noted()
    {
        var session = new FakeSheetVolSession
        {
            ImpactFactory = (i, s) => new ImpactResult(s.Id, "SEDIMENT", 0, 0, 0, [], 0, 0, TouchesSearchLimit: i == 1),
        };
        var run = Fixture.T2Run() with { Preset = ReportPreset.HcfcdSegment, AreaUnit = AreaUnit.Acres, Ohwm = new OhwmSpec(95, new Dictionary<Guid, double>()) };
        var (_, _, vm) = Fixture.Open(run, session);

        await vm.RegenerateCommand.ExecuteAsync();

        Assert.Equal(DialogText.NotAvailable, vm.Grid.Rows[0].AverageText);
        Assert.Equal("0.00", vm.Grid.Rows[0].ImpactedAreaText);
        Assert.Null(vm.Grid.Rows[0].ImpactNote);
        Assert.Equal(DialogText.WidenOffsets, vm.Grid.Rows[1].ImpactNote);
        Assert.Equal(DialogText.NotAvailable, vm.Grid.Total.AverageText);
    }

    [Fact]
    public async Task D10_D18_E827_segment_without_an_impact_shows_zeros_NA_and_the_coverage_flag_never_blank()
    {
        // Segment 2 is the SV-E827 case: the surface has no triangles in its window, so no impact
        // result and coverage 0 %. The fake's cut and fill are canned; only the impact cells and the flag matter.
        var session = new FakeSheetVolSession
        {
            ImpactFactory = (i, s) => i == 1 ? null : new ImpactResult(s.Id, "SEDIMENT", 500, 16_000, 26_000, [], 0, 0, false),
        };
        session.CoverageBySegment[1] = 0;
        var run = Fixture.T2Run() with { Preset = ReportPreset.HcfcdSegment, AreaUnit = AreaUnit.Acres, Ohwm = new OhwmSpec(100, new Dictionary<Guid, double>()) };
        var (_, _, vm) = Fixture.Open(run, session);

        await vm.RegenerateCommand.ExecuteAsync();

        var row = vm.Grid.Rows[1];
        Assert.True(row.HasValues);
        Assert.Equal("0", row.ImpactedLengthText);
        Assert.Equal("0.00", row.ImpactedAreaText);
        Assert.Equal("0", row.CutVolumeText);
        Assert.Equal(DialogText.NotAvailable, row.AverageText);
        Assert.Equal("0%", row.CoverageText);
        Assert.True(row.CoverageIsLow);
        string[] texts = [row.ImpactedLengthText, row.ImpactedAreaText, row.CutVolumeText, row.AverageText, row.CoverageText, row.CutText, row.FillText, row.Area2DText];
        Assert.All(texts, t => { Assert.NotEqual(DialogText.NoValue, t); Assert.DoesNotContain("NaN", t); });
        Assert.Equal("963", vm.Grid.Rows[0].CutVolumeText);     // the other segments are unchanged
    }

    [Fact]
    public async Task Hcfcd_per_segment_OHWM_override_is_set_from_the_row_and_removed_when_blank()
    {
        var session = new FakeSheetVolSession
        {
            ImpactFactory = (_, s) => new ImpactResult(s.Id, "SEDIMENT", 500, 16_000, 26_000, [], 0, 0, false),
        };
        var run = Fixture.T2Run() with { Preset = ReportPreset.HcfcdSegment, AreaUnit = AreaUnit.Acres, Ohwm = new OhwmSpec(100, new Dictionary<Guid, double>()) };
        var (_, _, vm) = Fixture.Open(run, session);
        await vm.RegenerateCommand.ExecuteAsync();
        var row2 = vm.Grid.Rows[1];

        row2.Ohwm.Text = "97";

        Assert.Equal(97, vm.Run.Ohwm!.BySegment[row2.SegmentId]);
        Assert.True(row2.OhwmIsOverride);
        Assert.False(row2.HasValues);                           // its OHWM changed: the old value no longer applies
        Assert.True(vm.Grid.Rows[0].HasValues);

        row2.Ohwm.Text = string.Empty;
        Assert.False(vm.Run.Ohwm!.BySegment.ContainsKey(row2.SegmentId));
        Assert.False(row2.OhwmIsOverride);
        Assert.Equal("100.00", row2.Ohwm.Text);
    }

    // ---- Per-segment offset fix beside a failing row (6.14, D8) -----------------------------

    [Fact]
    public async Task D8_offset_fix_beside_a_failing_row_is_stored_on_the_run_on_Apply_and_revalidated()
    {
        var (session, _, vm) = Fixture.Open();
        vm.OffsetRight.Text = "350";                             // same on both sides: ±350 reaches the curve radius on row 2
        var row2 = vm.Grid.Rows[1];
        Assert.True(row2.IsError);
        Assert.True(row2.ShowOffsetFix);
        Assert.False(row2.OffsetIsOverride);
        Assert.False(vm.Grid.Rows[0].ShowOffsetFix);              // only a failing row offers the fix
        Assert.Equal(("350", "350"), (row2.OffsetFixLeft.Text, row2.OffsetFixRight.Text));
        Assert.True(row2.ApplyOffsetFixCommand.CanExecute());
        var validates = session.ValidateCalls;

        row2.OffsetFixLeft.Text = "250";
        row2.OffsetFixRight.Text = "250";
        Assert.Empty(vm.Run.Overrides);                           // typing writes nothing
        Assert.Equal(validates, session.ValidateCalls);
        row2.ApplyOffsetFixCommand.Execute();

        var expected = new SegmentOverrides(OffsetLeft: -250, OffsetRight: 250);
        Assert.Equal(validates + 1, session.ValidateCalls);                         // Apply re-runs Validate
        Assert.Equal(expected, vm.Run.Overrides[row2.SegmentId]);                  // stored on the run (D8)
        Assert.Equal(expected, session.LastValidated!.Overrides[row2.SegmentId]);  // and handed to Validate
        Assert.Equal((-350d, 350d), (vm.Run.OffsetLeft, vm.Run.OffsetRight));      // the run's offsets are untouched
        Assert.Null(row2.OffsetFixError);
        Assert.False(row2.IsError);                               // this fake plans with the override; C1's Planner does not yet
        Assert.True(row2.ShowOffsetFix);                          // still shown: the override can be changed or cleared
        Assert.True(row2.OffsetIsOverride);
        Assert.Equal(("250", "250"), (row2.OffsetFixLeft.Text, row2.OffsetFixRight.Text));
        Assert.True(vm.CanRegenerate);

        await vm.RegenerateCommand.ExecuteAsync();
        Assert.Equal(expected, session.LastRegenerated!.Overrides[row2.SegmentId]);
        Assert.Equal(expected, session.Runs["Run1"].Overrides[row2.SegmentId]);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void D8_a_side_equal_to_the_run_or_blank_carries_no_override_and_bad_input_is_refused()
    {
        var (_, _, vm) = Fixture.Open();
        vm.OffsetRight.Text = "350";
        var row2 = vm.Grid.Rows[1];
        var id = row2.SegmentId;

        row2.OffsetFixRight.Text = "250";                        // left stays 350, the run's
        row2.ApplyOffsetFixCommand.Execute();
        Assert.Equal(new SegmentOverrides(OffsetRight: 250), vm.Run.Overrides[id]);

        row2.OffsetFixLeft.Text = "wide";
        Assert.Equal(DialogText.NotANumber, row2.OffsetFixLeft.Error);
        Assert.Equal(DialogText.BlockedFieldErrors, vm.RegenerateBlockedReason);
        row2.ApplyOffsetFixCommand.Execute();
        Assert.Equal(DialogText.NotANumber, row2.OffsetFixError);
        Assert.Equal(new SegmentOverrides(OffsetRight: 250), vm.Run.Overrides[id]);   // unchanged

        row2.OffsetFixLeft.Text = "-300";                        // 300 right of the alignment, past the right edge at 250
        row2.ApplyOffsetFixCommand.Execute();
        Assert.Equal(DialogText.OffsetFixNoWidth, row2.OffsetFixError);
        Assert.Equal(new SegmentOverrides(OffsetRight: 250), vm.Run.Overrides[id]);

        row2.OffsetFixLeft.Text = string.Empty;
        row2.OffsetFixRight.Text = string.Empty;
        row2.ApplyOffsetFixCommand.Execute();
        Assert.Null(row2.OffsetFixError);
        Assert.False(vm.Run.Overrides.ContainsKey(id));          // nothing else overridden: the entry goes
        Assert.False(row2.OffsetIsOverride);
        Assert.Equal(("350", "350"), (row2.OffsetFixLeft.Text, row2.OffsetFixRight.Text));
    }

    [Fact]
    public async Task D8_an_overridden_row_offers_the_fix_and_a_new_override_invalidates_only_that_row()
    {
        var session = new FakeSheetVolSession();
        var plan = session.Plan(Fixture.T2Run());
        var seg2 = plan.Segments[1].Id;
        var stored = Fixture.T2Run() with
        {
            KnownSegments = plan.Segments,
            Overrides = new Dictionary<Guid, SegmentOverrides> { [seg2] = new SegmentOverrides("C-102", OffsetLeft: -80, OffsetRight: 80) },
        };
        var (_, _, vm) = Fixture.Open(stored, session);
        await vm.RegenerateCommand.ExecuteAsync();
        var row2 = vm.Grid.Rows[1];
        Assert.False(row2.IsError);
        Assert.True(row2.ShowOffsetFix);                          // not failing, but its offsets are overridden
        Assert.True(row2.OffsetIsOverride);
        Assert.Equal(("80", "80"), (row2.OffsetFixLeft.Text, row2.OffsetFixRight.Text));
        Assert.Equal("128,000", row2.Area2DText);                 // 800 × 160: the segment's own band
        Assert.All(vm.Grid.Rows, r => Assert.True(r.HasValues));

        row2.OffsetFixLeft.Text = "90";
        row2.OffsetFixRight.Text = "90";
        row2.ApplyOffsetFixCommand.Execute();

        Assert.Equal(new SegmentOverrides("C-102", OffsetLeft: -90, OffsetRight: 90), vm.Run.Overrides[seg2]);   // the layout override is kept
        Assert.False(row2.HasValues);                             // its band changed: the old value no longer applies
        Assert.True(vm.Grid.Rows[0].HasValues);
        Assert.True(vm.Grid.Rows[2].HasValues);
        Assert.True(vm.IsDirty);
    }

    // ---- Grid toolbar ---------------------------------------------------------------------

    [Fact]
    public void Add_station_splits_the_selected_segment_at_its_middle()
    {
        var (session, _, vm) = Fixture.Open();
        Assert.False(vm.Grid.AddStationCommand.CanExecute());

        vm.Grid.SelectedRow = vm.Grid.Rows[1];
        Assert.True(vm.Grid.AddStationCommand.CanExecute());
        vm.Grid.AddStationCommand.Execute();

        Assert.Equal("0, 500, 900, 1300, 1800, 2000", vm.Run.BreakSpec);
        Assert.Equal([500d, 400d, 400d, 500d, 200d], vm.Grid.Rows.Select(r => r.Length));
        Assert.Equal(0, session.RegenerateCalls);
    }

    [Fact]
    public async Task Pick_in_drawing_inserts_a_break_at_the_picked_station()
    {
        var (session, _, vm) = Fixture.Open();
        session.PickStationResult = 250;

        await vm.Grid.PickStationCommand.ExecuteAsync();

        Assert.Equal(5, vm.Grid.Rows.Count);
        Assert.Equal((0d, 250d), (vm.Grid.Rows[0].BeginRaw, vm.Grid.Rows[0].EndRaw));

        session.PickStationResult = null;
        await vm.Grid.PickStationCommand.ExecuteAsync();
        Assert.Equal(5, vm.Grid.Rows.Count);
    }

    [Fact]
    public void Remove_merges_the_selected_segment_into_a_neighbour()
    {
        var (_, _, vm) = Fixture.Open();

        vm.Grid.SelectedRow = vm.Grid.Rows[2];
        vm.Grid.RemoveCommand.Execute();
        Assert.Equal("0, 500, 1800, 2000", vm.Run.BreakSpec);
        Assert.Equal([500d, 1300d, 200d], vm.Grid.Rows.Select(r => r.Length));

        vm.Grid.SelectedRow = vm.Grid.Rows[0];                  // the first row merges to the right
        vm.Grid.RemoveCommand.Execute();
        Assert.Equal("0, 1800, 2000", vm.Run.BreakSpec);

        vm.Grid.SelectedRow = vm.Grid.Rows[1];
        vm.Grid.RemoveCommand.Execute();
        Assert.Single(vm.Grid.Rows);
        vm.Grid.SelectedRow = vm.Grid.Rows[0];
        Assert.False(vm.Grid.RemoveCommand.CanExecute());        // one segment always stays
    }

    [Fact]
    public void Remove_after_a_skip_merges_right_and_keeps_the_skip()
    {
        var (_, _, vm) = Fixture.Open(Fixture.T2Run("0, 500, skip 500..800, 800, 1300, 2000"));
        Assert.Equal(3, vm.Grid.Rows.Count);

        vm.Grid.SelectedRow = vm.Grid.Rows[1];                  // 800..1300: a gap on its left, a neighbour on its right
        vm.Grid.RemoveCommand.Execute();

        Assert.Equal("0, 500, skip 500..800, 800, 2000", vm.Run.BreakSpec);
        Assert.Equal([(0d, 500d), (800d, 2000d)], vm.Grid.Rows.Select(r => (r.BeginRaw, r.EndRaw)));
    }

    [Fact]
    public void Remove_an_isolated_segment_joins_the_skips_around_it()
    {
        var (_, _, vm) = Fixture.Open(Fixture.T2Run("0, 500, skip 500..800, 800, 1300, skip 1300..1500, 1500, 2000"));
        Assert.Equal(3, vm.Grid.Rows.Count);

        vm.Grid.SelectedRow = vm.Grid.Rows[1];                  // 800..1300, gaps on both sides
        vm.Grid.RemoveCommand.Execute();

        Assert.Equal("0, 500, skip 500..1500, 1500, 2000", vm.Run.BreakSpec);
        Assert.Equal([(0d, 500d), (1500d, 2000d)], vm.Grid.Rows.Select(r => (r.BeginRaw, r.EndRaw)));
    }

    [Fact]
    public void Selection_highlights_in_the_drawing_and_zoom_to_reaches_the_session()
    {
        var (session, _, vm) = Fixture.Open();
        var row = vm.Grid.Rows[3];
        Assert.False(vm.Grid.ZoomToCommand.CanExecute());

        vm.Grid.SelectedRow = row;
        Assert.Equal(row.SegmentId, session.Highlights[^1]);
        Assert.True(vm.Chart.Bars[3].IsSelected);
        Assert.False(vm.Chart.Bars[0].IsSelected);

        vm.Grid.ZoomToCommand.Execute();
        Assert.Equal([row.SegmentId], session.Zooms);

        vm.Chart.SelectSegmentCommand.Execute(vm.Chart.Bars[1].SegmentId);   // click in the chart
        Assert.Same(vm.Grid.Rows[1], vm.Grid.SelectedRow);
        Assert.Equal(vm.Grid.Rows[1].SegmentId, session.Highlights[^1]);
        Assert.True(vm.Chart.Bars[1].IsSelected);
    }
}

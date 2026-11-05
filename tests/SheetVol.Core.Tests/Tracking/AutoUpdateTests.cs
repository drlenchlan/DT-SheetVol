using SheetVol.Core.Tracking;

namespace SheetVol.Core.Tests.Tracking;

/// <summary>The auto-update state machine of 6.11 as pure functions: E804, E805, E806 and the three modes.</summary>
public class AutoUpdateTests
{
    private static readonly AutoUpdatePolicy Policy = new(new TrackingConfig());

    private static TrackerContext Ctx(AutoMode mode = AutoMode.Views, bool toolRunning = false) => new(
        new Dictionary<string, AutoMode> { ["R1"] = mode },
        new Dictionary<string, IReadOnlyList<string>> { ["1A"] = ["R1"], ["2B"] = ["R1"] },
        toolRunning);

    /// <summary>Feeds events in order; returns the final state, every decision made, and the last WantsIdle.</summary>
    private static (TrackerState State, List<AutoDecision> Decisions, bool WantsIdle) Feed(
        TrackerState state, TrackerContext ctx, params TrackerEvent[] events)
    {
        var decisions = new List<AutoDecision>();
        bool wantsIdle = false;
        foreach (TrackerEvent e in events)
        {
            TrackerStep step = Policy.Step(state, e, ctx);
            state = step.State;
            decisions.AddRange(step.Decisions);
            wantsIdle = step.WantsIdle;
        }
        return (state, decisions, wantsIdle);
    }

    private static TrackerEvent Mod(string handle = "1A") => new TrackerEvent.ObjectModified(handle);
    private static TrackerEvent End(string command) => new TrackerEvent.CommandEnded(command);
    private static TrackerEvent Idle() => new TrackerEvent.Idle();

    [Fact]
    public void E805_a_burst_of_modifications_gives_one_update_after_the_command_ends()
    {
        var burst = Enumerable.Range(0, 1000).Select(i => Mod(i % 2 == 0 ? "1A" : "2B")).ToArray();
        var (state, decisions, wantsIdle) = Feed(TrackerState.Empty, Ctx(), burst);
        Assert.Empty(decisions);                    // nothing happens inside the modified handler
        Assert.False(wantsIdle);

        (state, decisions, wantsIdle) = Feed(state, Ctx(), End("REBUILDCORRIDOR"));
        Assert.Empty(decisions);                    // work waits for Idle
        Assert.True(wantsIdle);

        (state, decisions, wantsIdle) = Feed(state, Ctx(), Idle());
        AutoDecision d = Assert.Single(decisions);
        Assert.Equal(new AutoDecision("R1", AutoAction.UpdateViews, TablesOutOfDate: true), d);
        Assert.False(wantsIdle);

        (_, decisions, _) = Feed(state, Ctx(), Idle());
        Assert.Empty(decisions);                    // one-shot
    }

    [Fact]
    public void E805_several_command_ends_before_idle_still_give_one_update()
    {
        var (_, decisions, _) = Feed(TrackerState.Empty, Ctx(),
            Mod(), End("MOVE"), Mod("2B"), End("GRIPEDIT"), Mod(), End("STRETCH"), Idle(), Idle());
        Assert.Single(decisions);
    }

    [Theory]
    [InlineData("U")]
    [InlineData("UNDO")]
    [InlineData("REDO")]
    [InlineData("MREDO")]
    [InlineData("_UNDO")]
    [InlineData(".u")]
    [InlineData("redo")]
    public void E804_nothing_fires_during_undo_or_redo(string command)
    {
        var (state, decisions, wantsIdle) = Feed(TrackerState.Empty, Ctx(), Mod(), Mod("2B"), End(command));
        Assert.Empty(decisions);
        Assert.False(wantsIdle);
        Assert.Empty(state.Dirty);

        (_, decisions, _) = Feed(state, Ctx(), Idle());
        Assert.Empty(decisions);
    }

    [Fact]
    public void E804_undo_cancels_an_update_still_waiting_for_idle()
    {
        var (_, decisions, wantsIdle) = Feed(TrackerState.Empty, Ctx(), Mod(), End("MOVE"), End("U"), Idle());
        Assert.Empty(decisions);
        Assert.False(wantsIdle);
    }

    [Fact]
    public void E804_changes_made_while_the_tool_runs_never_fire()
    {
        var (state, decisions, wantsIdle) = Feed(TrackerState.Empty, Ctx(toolRunning: true), Mod(), Mod("2B"), End("SVREGEN"));
        Assert.Empty(decisions);
        Assert.False(wantsIdle);
        Assert.Empty(state.Dirty);
        (_, decisions, _) = Feed(state, Ctx(), Idle());
        Assert.Empty(decisions);
    }

    [Theory]
    [InlineData("SVREGEN")]
    [InlineData("_svbreak")]
    [InlineData("SVDEFINE")]
    public void E804_the_tools_own_command_end_discards_its_changes(string command)
    {
        // Even if the wiring did not raise the running flag, the command name is enough.
        var (_, decisions, wantsIdle) = Feed(TrackerState.Empty, Ctx(), Mod(), End(command), Idle());
        Assert.Empty(decisions);
        Assert.False(wantsIdle);
        Assert.True(Policy.IsToolCommand(command));
    }

    [Fact]
    public void E806_a_failed_auto_update_drops_to_report_only_once_with_no_retry()
    {
        var (state, decisions, _) = Feed(TrackerState.Empty, Ctx(), Mod(), End("MOVE"), Idle());
        Assert.Equal(AutoAction.UpdateViews, Assert.Single(decisions).Action);

        TrackerStep failed = Policy.Step(state, new TrackerEvent.AutoUpdateFailed("R1", "Alignment 'S' was erased."), Ctx());
        AutoDecision drop = Assert.Single(failed.Decisions);
        Assert.Equal(AutoAction.DropToReportOnly, drop.Action);
        Assert.Contains("R1", drop.Message);
        Assert.Contains("Alignment 'S' was erased.", drop.Message);
        Assert.False(failed.WantsIdle);             // no retry is scheduled
        state = failed.State;
        Assert.Contains("R1", state.ReportOnly);
        Assert.Contains("R1", state.Stale);

        (state, decisions, _) = Feed(state, Ctx(), Idle());
        Assert.Empty(decisions);

        // Later edits only report.
        (state, decisions, _) = Feed(state, Ctx(), Mod(), End("MOVE"), Idle());
        Assert.Equal(new AutoDecision("R1", AutoAction.MarkStale, TablesOutOfDate: true), Assert.Single(decisions));

        // A second failure report prints nothing more.
        (state, decisions, _) = Feed(state, Ctx(), new TrackerEvent.AutoUpdateFailed("R1", "again"));
        Assert.Empty(decisions);

        // A successful SVREGEN restores auto-update.
        (state, decisions, _) = Feed(state, Ctx(), new TrackerEvent.RunRegenerated("R1"), Mod(), End("MOVE"), Idle());
        Assert.Equal(AutoAction.UpdateViews, Assert.Single(decisions).Action);
        Assert.Empty(state.ReportOnly);
    }

    [Fact]
    public void Setting_the_auto_mode_again_leaves_report_only()
    {
        var (state, _, _) = Feed(TrackerState.Empty, Ctx(), new TrackerEvent.AutoUpdateFailed("R1", "x"));
        (_, var decisions, _) = Feed(state, Ctx(AutoMode.All), new TrackerEvent.AutoModeChanged("R1"), Mod(), End("MOVE"), Idle());
        Assert.Equal(AutoAction.UpdateAll, Assert.Single(decisions).Action);
    }

    [Theory]
    [InlineData(AutoMode.Off, AutoAction.MarkStale, true)]
    [InlineData(AutoMode.Views, AutoAction.UpdateViews, true)]
    [InlineData(AutoMode.All, AutoAction.UpdateAll, false)]
    public void Each_mode_maps_to_its_action(AutoMode mode, AutoAction action, bool tablesOutOfDate)
    {
        var (state, decisions, _) = Feed(TrackerState.Empty, Ctx(mode), Mod(), End("MOVE"), Idle());
        Assert.Equal(new AutoDecision("R1", action, tablesOutOfDate), Assert.Single(decisions));
        Assert.Contains("R1", state.Stale);
    }

    [Fact]
    public void Only_a_full_update_clears_the_stale_flag()
    {
        var (state, _, _) = Feed(TrackerState.Empty, Ctx(), Mod(), End("MOVE"), Idle());
        state = Policy.Step(state, new TrackerEvent.AutoUpdateSucceeded("R1", AutoAction.UpdateViews), Ctx()).State;
        Assert.Contains("R1", state.Stale);         // tables still out of date
        state = Policy.Step(state, new TrackerEvent.AutoUpdateSucceeded("R1", AutoAction.UpdateAll), Ctx()).State;
        Assert.DoesNotContain("R1", state.Stale);
    }

    [Fact]
    public void Untracked_objects_are_ignored()
    {
        var (_, decisions, wantsIdle) = Feed(TrackerState.Empty, Ctx(), Mod("FFFF"), new TrackerEvent.ObjectErased("EEEE"), End("ERASE"), Idle());
        Assert.Empty(decisions);
        Assert.False(wantsIdle);
    }

    [Fact]
    public void Erasing_a_tracked_object_counts_as_a_change()
    {
        var (_, decisions, _) = Feed(TrackerState.Empty, Ctx(), new TrackerEvent.ObjectErased("1A"), End("ERASE"), Idle());
        Assert.Single(decisions);
    }

    [Fact]
    public void Opening_a_document_with_a_stale_run_behaves_as_an_edit()
    {
        var stale = new Dictionary<string, FingerprintComparison> { ["R1"] = new(true, [Fingerprints.AlignmentComponent]) };
        TrackerStep opened = Policy.Step(TrackerState.Empty, new TrackerEvent.DocumentOpened(stale), Ctx());
        Assert.Empty(opened.Decisions);             // work waits for Idle, never inside the open handler
        Assert.True(opened.WantsIdle);
        var (_, decisions, _) = Feed(opened.State, Ctx(), Idle());
        Assert.Equal(AutoAction.UpdateViews, Assert.Single(decisions).Action);
    }

    [Fact]
    public void Activation_and_reference_sync_with_current_runs_do_nothing_and_clear_stale()
    {
        var (state, _, _) = Feed(TrackerState.Empty, Ctx(AutoMode.Off), Mod(), End("MOVE"), Idle());
        Assert.Contains("R1", state.Stale);

        var current = new Dictionary<string, FingerprintComparison> { ["R1"] = FingerprintComparison.Current };
        TrackerStep activated = Policy.Step(state, new TrackerEvent.DocumentActivated(current), Ctx());
        Assert.False(activated.WantsIdle);
        Assert.DoesNotContain("R1", activated.State.Stale);

        var stale = new Dictionary<string, FingerprintComparison> { ["R1"] = new(true, [Fingerprints.SurfacesComponent]) };
        TrackerStep synced = Policy.Step(activated.State, new TrackerEvent.ReferenceSynchronized(stale), Ctx());
        Assert.True(synced.WantsIdle);
    }

    [Fact]
    public void Idle_skips_a_run_whose_fresh_fingerprint_is_unchanged()
    {
        var (state, _, _) = Feed(TrackerState.Empty, Ctx(), Mod(), End("LAYER"));
        var checks = new Dictionary<string, FingerprintComparison> { ["R1"] = FingerprintComparison.Current };
        TrackerStep step = Policy.Step(state, new TrackerEvent.Idle(checks), Ctx());
        Assert.Empty(step.Decisions);
        Assert.False(step.WantsIdle);
    }

    [Fact]
    public void Runs_removed_before_idle_get_no_decision_and_runs_are_ordered()
    {
        var ctx = new TrackerContext(
            new Dictionary<string, AutoMode> { ["B"] = AutoMode.All, ["A"] = AutoMode.Off },
            new Dictionary<string, IReadOnlyList<string>> { ["1A"] = ["B", "A", "GONE"] });
        var (_, decisions, _) = Feed(TrackerState.Empty, ctx, Mod(), End("MOVE"), Idle());
        Assert.Equal(["A", "B"], decisions.Select(d => d.RunName));
        Assert.Equal([AutoAction.MarkStale, AutoAction.UpdateAll], decisions.Select(d => d.Action));
    }

    [Fact]
    public void Step_is_pure()
    {
        TrackerState start = Feed(TrackerState.Empty, Ctx(), Mod(), End("MOVE")).State;
        TrackerStep a = Policy.Step(start, Idle(), Ctx());
        TrackerStep b = Policy.Step(start, Idle(), Ctx());
        Assert.Equal(a.Decisions, b.Decisions);
        Assert.Equal(a.State.Pending, b.State.Pending);
        Assert.NotEmpty(start.Pending);             // the input state is not mutated
    }
}

using System.Collections.Immutable;
using System.Globalization;

namespace SheetVol.Core.Tracking;

/// <summary>What the auto-update wiring must do for one run.</summary>
public enum AutoAction
{
    /// <summary>Nothing. Never emitted as a decision; an empty decision list means none.</summary>
    None,

    /// <summary>Report only: flag the run stale (status, dialog banner). No drawing writes.</summary>
    MarkStale,

    /// <summary>Update boundaries, viewports and match marks; mark the volume tables "(OUT OF DATE)" (<see cref="AutoMode.Views"/>).</summary>
    UpdateViews,

    /// <summary>Full regen including volumes (<see cref="AutoMode.All"/>).</summary>
    UpdateAll,

    /// <summary>An auto-update failed: the run is report-only from now on; print <see cref="AutoDecision.Message"/> once (E806).</summary>
    DropToReportOnly,
}

/// <summary>One decision for one run.</summary>
/// <param name="RunName">The run.</param>
/// <param name="Action">What to do.</param>
/// <param name="TablesOutOfDate">The volume tables must carry the out-of-date marker after this action.</param>
/// <param name="Message">One-line message to print, when the action has one.</param>
public sealed record AutoDecision(string RunName, AutoAction Action, bool TablesOutOfDate = false, string? Message = null);

/// <summary>
/// Inputs to the auto-update policy. The Civil 3D wiring (L2) turns AutoCAD events into these
/// and does nothing else in its handlers (6.11): modified and erased only mark dirty, work
/// waits for <see cref="CommandEnded"/> and then a one-shot <see cref="Idle"/>. Command
/// cancelled and command failed are passed as <see cref="CommandEnded"/> too.
/// </summary>
public abstract record TrackerEvent
{
    private TrackerEvent() { }

    /// <summary>Database.ObjectModified for an object, by handle (hex text as the wiring writes it).</summary>
    public sealed record ObjectModified(string Handle) : TrackerEvent;

    /// <summary>Database.ObjectErased for an object, by handle.</summary>
    public sealed record ObjectErased(string Handle) : TrackerEvent;

    /// <summary>Document.CommandEnded (or cancelled, or failed) with the global command name.</summary>
    public sealed record CommandEnded(string CommandName) : TrackerEvent;

    /// <summary>
    /// The one-shot Application.Idle. <paramref name="Checks"/> optionally carries a fresh
    /// fingerprint comparison for pending runs; a pending run whose comparison is not stale
    /// gets no action (the edit did not change any source the run depends on).
    /// </summary>
    public sealed record Idle(IReadOnlyDictionary<string, FingerprintComparison>? Checks = null) : TrackerEvent;

    /// <summary>A document was opened: stored fingerprints compared with current sources (L3).</summary>
    public sealed record DocumentOpened(IReadOnlyDictionary<string, FingerprintComparison> Checks) : TrackerEvent;

    /// <summary>A document became active: fingerprint comparison per run (L3).</summary>
    public sealed record DocumentActivated(IReadOnlyDictionary<string, FingerprintComparison> Checks) : TrackerEvent;

    /// <summary>A data-shortcut reference was synchronized: fingerprint comparison per run (L3).</summary>
    public sealed record ReferenceSynchronized(IReadOnlyDictionary<string, FingerprintComparison> Checks) : TrackerEvent;

    /// <summary>An auto-update the policy asked for threw or failed validation.</summary>
    public sealed record AutoUpdateFailed(string RunName, string Reason) : TrackerEvent;

    /// <summary>An auto-update the policy asked for completed; <paramref name="Performed"/> is the action carried out.</summary>
    public sealed record AutoUpdateSucceeded(string RunName, AutoAction Performed) : TrackerEvent;

    /// <summary>A full regen of the run (SVREGEN, the dialog's Regenerate) succeeded: the run is current and leaves report-only.</summary>
    public sealed record RunRegenerated(string RunName) : TrackerEvent;

    /// <summary>SVAUTO set the run's mode: the run leaves report-only.</summary>
    public sealed record AutoModeChanged(string RunName) : TrackerEvent;
}

/// <summary>What the wiring knows at the moment of an event.</summary>
/// <param name="RunModes">Auto mode per run name, for every run in the document.</param>
/// <param name="TrackedHandles">Source handle to the runs that depend on it (alignment, frames, manual polylines). Untracked handles are ignored.</param>
/// <param name="ToolCommandRunning">One of the tool's own commands or a regen from the dialog is executing.</param>
public sealed record TrackerContext(
    IReadOnlyDictionary<string, AutoMode> RunModes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> TrackedHandles,
    bool ToolCommandRunning = false);

/// <summary>
/// The tracker's state for one document. Immutable; every step returns a new one.
/// Run names compare ordinally.
/// </summary>
/// <param name="Dirty">Runs with tracked changes since the last command boundary.</param>
/// <param name="Pending">Runs waiting for the one-shot Idle.</param>
/// <param name="Stale">Runs whose outputs do not fully match their sources (needs SVREGEN for full currency).</param>
/// <param name="ReportOnly">Runs dropped to report-only after a failed auto-update (E806).</param>
/// <param name="Notified">Report-only runs whose one-time message was already issued.</param>
public sealed record TrackerState(
    ImmutableHashSet<string> Dirty,
    ImmutableHashSet<string> Pending,
    ImmutableHashSet<string> Stale,
    ImmutableHashSet<string> ReportOnly,
    ImmutableHashSet<string> Notified)
{
    private static ImmutableHashSet<string> None => ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    /// <summary>A fresh document: nothing dirty, pending, stale or report-only.</summary>
    public static TrackerState Empty { get; } = new(None, None, None, None, None);
}

/// <summary>Result of one policy step.</summary>
/// <param name="State">The new state.</param>
/// <param name="Decisions">What to do now, in run-name order. Empty means nothing.</param>
public sealed record TrackerStep(TrackerState State, IReadOnlyList<AutoDecision> Decisions)
{
    /// <summary>The wiring must have a one-shot Idle handler attached (attach if not already).</summary>
    public bool WantsIdle => !State.Pending.IsEmpty;
}

/// <summary>
/// The auto-update decision logic of HANDOFF.md 6.11 as a pure function of
/// (state, event, context). No events, no timers, no database: the Civil 3D wiring feeds
/// it and carries out its decisions. Rules:
/// tracked modifications only mark dirty; a command end moves dirty runs to pending and asks
/// for one Idle, so a burst gives one update (E805); U, UNDO, REDO and the tool's own commands
/// discard their changes and never fire (E804); a failed auto-update drops the run to
/// report-only with one message and no retry (E806); Off reports, Views updates views and
/// marks tables out of date, All recomputes.
/// </summary>
public sealed class AutoUpdatePolicy
{
    private readonly TrackingConfig _config;
    private readonly ImmutableHashSet<string> _undo;
    private readonly ImmutableHashSet<string> _tool;

    /// <summary>Creates the policy with its command lists.</summary>
    public AutoUpdatePolicy(TrackingConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _undo = (config.UndoCommandNames ?? []).Select(Normalize).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        _tool = (config.ToolCommandNames ?? []).Select(Normalize).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when the command is one of <see cref="TrackingConfig.UndoCommandNames"/>.</summary>
    public bool IsUndoCommand(string commandName) => _undo.Contains(Normalize(commandName));

    /// <summary>True when the command is one of <see cref="TrackingConfig.ToolCommandNames"/>.</summary>
    public bool IsToolCommand(string commandName) => _tool.Contains(Normalize(commandName));

    /// <summary>Applies one event. Pure: the same inputs always give the same step.</summary>
    public TrackerStep Step(TrackerState state, TrackerEvent evt, TrackerContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(context);

        return evt switch
        {
            TrackerEvent.ObjectModified e => OnObjectChanged(state, e.Handle, context),
            TrackerEvent.ObjectErased e => OnObjectChanged(state, e.Handle, context),
            TrackerEvent.CommandEnded e => OnCommandEnded(state, e.CommandName, context),
            TrackerEvent.Idle e => OnIdle(state, e.Checks, context),
            TrackerEvent.DocumentOpened e => OnFingerprints(state, e.Checks),
            TrackerEvent.DocumentActivated e => OnFingerprints(state, e.Checks),
            TrackerEvent.ReferenceSynchronized e => OnFingerprints(state, e.Checks),
            TrackerEvent.AutoUpdateFailed e => OnFailed(state, e.RunName, e.Reason),
            TrackerEvent.AutoUpdateSucceeded e => Quiet(e.Performed == AutoAction.UpdateAll
                ? state with { Stale = state.Stale.Remove(e.RunName) }
                : state),
            TrackerEvent.RunRegenerated e => Quiet(state with
            {
                Dirty = state.Dirty.Remove(e.RunName),
                Pending = state.Pending.Remove(e.RunName),
                Stale = state.Stale.Remove(e.RunName),
                ReportOnly = state.ReportOnly.Remove(e.RunName),
                Notified = state.Notified.Remove(e.RunName),
            }),
            TrackerEvent.AutoModeChanged e => Quiet(state with
            {
                ReportOnly = state.ReportOnly.Remove(e.RunName),
                Notified = state.Notified.Remove(e.RunName),
            }),
            _ => throw new ArgumentException($"Unknown tracker event {evt.GetType().Name}.", nameof(evt)),
        };
    }

    private static TrackerStep OnObjectChanged(TrackerState state, string handle, TrackerContext context)
    {
        if (context.ToolCommandRunning) return Quiet(state);
        if (handle is null || !context.TrackedHandles.TryGetValue(handle, out IReadOnlyList<string>? runs) || runs is null || runs.Count == 0)
            return Quiet(state);
        return Quiet(state with { Dirty = state.Dirty.Union(runs) });
    }

    private TrackerStep OnCommandEnded(TrackerState state, string commandName, TrackerContext context)
    {
        // Undo restores sources, outputs and the stored fingerprint together: drop everything.
        if (IsUndoCommand(commandName))
            return Quiet(state with { Dirty = state.Dirty.Clear(), Pending = state.Pending.Clear() });

        // The tool's own writes are not user edits.
        if (context.ToolCommandRunning || IsToolCommand(commandName))
            return Quiet(state with { Dirty = state.Dirty.Clear() });

        if (state.Dirty.IsEmpty) return Quiet(state);
        return Quiet(state with { Pending = state.Pending.Union(state.Dirty), Dirty = state.Dirty.Clear() });
    }

    private static TrackerStep OnIdle(TrackerState state, IReadOnlyDictionary<string, FingerprintComparison>? checks, TrackerContext context)
    {
        if (state.Pending.IsEmpty) return Quiet(state);

        var decisions = new List<AutoDecision>();
        ImmutableHashSet<string> stale = state.Stale;
        foreach (string run in state.Pending.OrderBy(r => r, StringComparer.Ordinal))
        {
            if (!context.RunModes.TryGetValue(run, out AutoMode mode)) continue;   // run no longer exists
            if (checks is not null && checks.TryGetValue(run, out FingerprintComparison? check) && check is { IsStale: false })
            {
                stale = stale.Remove(run);
                continue;
            }

            stale = stale.Add(run);
            if (state.ReportOnly.Contains(run)) { decisions.Add(new AutoDecision(run, AutoAction.MarkStale, TablesOutOfDate: true)); continue; }
            decisions.Add(mode switch
            {
                AutoMode.Off => new AutoDecision(run, AutoAction.MarkStale, TablesOutOfDate: true),
                AutoMode.Views => new AutoDecision(run, AutoAction.UpdateViews, TablesOutOfDate: true),
                AutoMode.All => new AutoDecision(run, AutoAction.UpdateAll),
                _ => throw new ArgumentOutOfRangeException(nameof(context), mode, $"Unknown auto mode for run '{run}'."),
            });
        }
        return new TrackerStep(state with { Pending = state.Pending.Clear(), Stale = stale }, decisions);
    }

    private static TrackerStep OnFingerprints(TrackerState state, IReadOnlyDictionary<string, FingerprintComparison> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ImmutableHashSet<string> pending = state.Pending, stale = state.Stale;
        foreach (var (run, check) in checks.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (check is { IsStale: true }) pending = pending.Add(run);   // stale runs behave as L2
            else stale = stale.Remove(run);
        }
        return Quiet(state with { Pending = pending, Stale = stale });
    }

    private TrackerStep OnFailed(TrackerState state, string run, string reason)
    {
        var next = state with
        {
            Dirty = state.Dirty.Remove(run),
            Pending = state.Pending.Remove(run),       // no retry loop
            Stale = state.Stale.Add(run),
            ReportOnly = state.ReportOnly.Add(run),
        };
        if (state.Notified.Contains(run)) return Quiet(next);

        string message = string.Format(CultureInfo.InvariantCulture, _config.ReportOnlyMessageFormat, run, reason);
        return new TrackerStep(next with { Notified = next.Notified.Add(run) },
            [new AutoDecision(run, AutoAction.DropToReportOnly, TablesOutOfDate: true, Message: message)]);
    }

    private string Normalize(string? commandName)
        => (commandName ?? string.Empty).Trim().TrimStart((_config.CommandPrefixCharacters ?? string.Empty).ToCharArray());

    private static TrackerStep Quiet(TrackerState state) => new(state, []);
}

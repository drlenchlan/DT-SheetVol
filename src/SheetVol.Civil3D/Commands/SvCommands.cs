// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4.
//
// The engine commands of HANDOFF.md 6.12. All are Modal, scriptable and dialog-free (A9):
// every prompt takes typed input, picks are an optional "Pick" keyword, and SVREGEN has
// no prompts at all. Definition edits (SVDEFINE, SVANALYSIS, SVBREAK) save the run record
// with its stored segment identities untouched (C1: KnownSegments is only ever the last
// applied plan); SVREGEN applies them.
//
//   SVDEFINE    create or edit a run: alignment, breaks, offsets, views, preset, area unit,
//               OHWM, auto mode, draw options, per-segment overrides
//   SVANALYSIS  add, remove or re-factor an analysis (base, comparison, factors)
//   SVBREAK     add, move or remove a break by typed station or pick; convert a segment to manual
//   SVREGEN     regenerate every run (no prompts); one transaction per run (E801)
//   SVSTATUS    clean or stale per run, warnings, coverage flags, orphaned layouts
//   SVEXPORT    CSV and JSON beside the drawing; no drawing writes
//   SVCLEAR     remove a run and its owned model-space objects; layouts only when confirmed
//   SVPURGE     delete orphaned layouts after confirmation
//   SVCONFIG    show the effective settings and their source; write a settings file; reload

using System.Globalization;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using SheetVol.Civil3D.Session;
using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Planning;
using SheetVol.Core.Tracking;
using SheetVol.Core.Volumes;
using SheetVol.Core.Volumes.Integrator;

[assembly: CommandClass(typeof(SheetVol.Civil3D.Commands.SvCommands))]

namespace SheetVol.Civil3D.Commands;

/// <summary>SheetVol engine commands (6.12).</summary>
public static class SvCommands
{
    // ============================================================ SVREGEN

    /// <summary>Regenerates every run in the drawing. No prompts. The single click (6.12).</summary>
    [CommandMethod("SVREGEN", CommandFlags.Modal)]
    public static void Regen() => Boundary.Run("SVREGEN", c =>
    {
        var names = DrawingRead.Run(c.Doc, c.Engine, (_, store) => store.RunNames());
        if (names.Count == 0)
        {
            c.Say($"{PipelineCodes.RunNotFound} SVREGEN: no SheetVol runs in this drawing. Create one with SVDEFINE.");
            return;
        }
        var outcomes = RegenerateAll(c.Doc, c.Engine, names, watchEscape: true);
        foreach (var o in outcomes) PrintOutcome(c, o);
        int ok = outcomes.Count(o => o.Result.Succeeded);
        c.Say($"SVREGEN: {outcomes.Count} run(s), {ok} regenerated, {outcomes.Count - ok} failed and left unchanged.");
    });

    /// <summary>Regenerates the named runs, each in its own transaction: one failure leaves that run untouched and the others still process (E801).</summary>
    internal static List<RunOutcome> RegenerateAll(Autodesk.AutoCAD.ApplicationServices.Document doc, RegenEngine engine, IReadOnlyList<string> names, bool watchEscape)
    {
        var outcomes = new List<RunOutcome>();
        foreach (string name in names.Order(StringComparer.Ordinal))
        {
            var load = DrawingRead.Run(doc, engine, (_, store) => store.Load(name));
            if (load.Record is not { } record || !load.CanModify)
            {
                var problems = new List<Problem> { new(PipelineCodes.RunFailed, Severity.Error, $"Run '{name}' could not be read; it was left unchanged.") };
                problems.AddRange(load.Problems);
                var failed = new RegenResult(false, name, [], problems, [], false, TimeSpan.Zero, null, null);
                var placeholder = new RunDefinition(name, "", SegmentDriver.Stations, "", null, 0, 0, [], [], [], new Dictionary<Guid, SegmentOverrides>(), new ViewOptions());
                outcomes.Add(new RunOutcome(failed, placeholder, null, null, new Dictionary<Guid, Core.Views.ViewResult>(), null, null, null, 1));
                continue;
            }
            RunOutcome outcome;
            try
            {
                outcome = engine.Regenerate(doc, record.Run, RegenMode.Full, null, CancellationToken.None, watchEscape);
            }
            catch (OperationCanceledException)
            {
                // Esc: this run is rolled back, earlier runs stay committed, later runs are not processed.
                var skipped = names.Order(StringComparer.Ordinal).SkipWhile(n => n != name).Skip(1).ToList();
                var cancelled = new RegenResult(false, name, [], [new Problem(PipelineCodes.Cancelled, Severity.Error,
                    $"Run '{name}' was cancelled before commit and is unchanged."
                    + (outcomes.Count > 0 ? $" {outcomes.Count} earlier run(s) were committed." : "")
                    + (skipped.Count > 0 ? $" Not processed: {string.Join(", ", skipped)}." : ""))], [], false, TimeSpan.Zero, null, null);
                outcomes.Add(new RunOutcome(cancelled, record.Run, null, null, new Dictionary<Guid, Core.Views.ViewResult>(), null, null, null, 0));
                break;
            }
            SessionState.Record(doc.Database, outcome);
            outcomes.Add(outcome);
        }
        return outcomes;
    }

    private static void PrintOutcome(Cmd c, RunOutcome o)
    {
        var r = o.Result;
        if (!r.Succeeded)
        {
            c.Say($"SVREGEN run '{r.RunName}': FAILED, nothing in this run changed.");
            c.ReportAll(r.Problems);
            return;
        }
        int segments = r.Segments.Select(s => s.SegmentId).Distinct().Count();
        string counts = o.Applied is { } a ? $"created {a.Created}, updated {a.Updated}, erased {a.Erased}, renamed {a.Renamed}, new orphans {a.Orphaned}" : "";
        c.Say($"SVREGEN run '{r.RunName}': OK in {r.Elapsed.TotalSeconds:0.0} s, {segments} segment(s); {counts}; partition check {(r.PartitionCheckPassed ? "passed" : "not passed (see notes)")}.");
        foreach (var ol in r.OutsideLimits) c.Say($"  outside limits, analysis {ol.AnalysisId:D}: raw cut {Cmd.Num(ol.Raw.Cut)}, fill {Cmd.Num(ol.Raw.Fill)} cubic drawing units");
        if (r.CsvPath is not null) c.Say($"  CSV:  {r.CsvPath}");
        if (r.JsonPath is not null) c.Say($"  JSON: {r.JsonPath}");
        c.ReportAll(r.Problems);
    }

    // ============================================================ SVSTATUS

    /// <summary>Clean or stale per run, warnings, coverage flags, orphaned layouts (6.12). No prompts. Read-only: NoUndoMarker, so the next U undoes the user's edit, not this (S3, E804).</summary>
    [CommandMethod("SVSTATUS", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
    public static void Status() => Boundary.Run("SVSTATUS", c =>
    {
        var reports = DrawingRead.Run(c.Doc, c.Engine, (src, store) =>
            store.RunNames().Select(n => RunStatusReader.Read(src, store, n, c.Engine.Settings)).ToList());
        if (reports.Count == 0) c.Say("SVSTATUS: no SheetVol runs in this drawing.");
        foreach (var rep in reports)
        {
            var rec = rep.Record;
            string state = rec is null ? "UNREADABLE" : rec.Fingerprint is null ? "NEVER REGENERATED" : rep.IsStale ? "STALE" : "CLEAN";
            c.Say($"SVSTATUS run '{rep.RunName}': {state}"
                + (rep.ChangedComponents.Count > 0 && rec?.Fingerprint is not null ? $" (changed: {string.Join(", ", rep.ChangedComponents)})" : "")
                + (rec?.LastRegenUtc is { } t ? $"; last regen {t.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : ""));
            if (rec is not null)
            {
                c.Say($"  alignment {rec.Run.AlignmentName}, {rec.Run.KnownSegments.Count} segment(s), {rec.Run.Analyses.Count} analysis(es), auto mode {rec.Run.AutoMode}, preset {rec.Run.Preset}");
                var coverage = rec.LastProblems.Where(p => p.Code is VolumeCodes.NoSurfaceData or VolumeCodes.PartialCoverage).ToList();
                if (coverage.Count > 0) c.Say($"  coverage flags: {coverage.Count}");
                c.ReportAll(rec.LastProblems);
                foreach (var orphan in rec.Orphans)
                    c.Say($"  orphaned layout '{orphan.Name}' (handle {orphan.Handle}) since {orphan.SinceUtc.ToLocalTime():yyyy-MM-dd}; SVPURGE deletes it");
            }
            foreach (var p in rep.Problems) c.Report(p, "  ");
        }
        var placeholders = c.Engine.Settings.PlaceholdersInUse();
        if (placeholders.Count > 0) c.Say($"SVSTATUS: {placeholders.Count} setting(s) still hold placeholder defaults (A7): {string.Join(", ", placeholders)}");
    });

    // ============================================================ SVEXPORT

    /// <summary>Writes CSV and JSON beside the drawing for one run or all (*). No drawing writes (6.12): its volume transaction is aborted, so NoUndoMarker (S3, E804).</summary>
    [CommandMethod("SVEXPORT", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
    public static void Export() => Boundary.Run("SVEXPORT", c =>
    {
        var names = DrawingRead.Run(c.Doc, c.Engine, (_, store) => store.RunNames());
        if (names.Count == 0) { c.Say($"{PipelineCodes.RunNotFound} SVEXPORT: no runs in this drawing."); return; }
        string which = c.String($"Run to export ({string.Join(", ", names)}) or *", defaultValue: "*");
        var targets = which == "*" ? names : names.Where(n => string.Equals(n, which, StringComparison.Ordinal)).ToList();
        if (targets.Count == 0) { c.Say($"{PipelineCodes.RunNotFound} SVEXPORT: no run named '{which}'."); return; }
        foreach (string name in targets)
        {
            var load = DrawingRead.Run(c.Doc, c.Engine, (_, store) => store.Load(name));
            if (load.Record is not { } rec) { c.ReportAll(load.Problems); continue; }
            var outcome = c.Engine.Regenerate(c.Doc, rec.Run, RegenMode.ExportOnly, null, CancellationToken.None, watchEscape: true);
            var r = outcome.Result;
            c.Say($"SVEXPORT run '{name}': {(r.Succeeded ? "computed" : "FAILED")}; CSV {r.CsvPath ?? "not written"}; JSON {r.JsonPath ?? "not written"}");
            c.ReportAll(r.Problems);
        }
    });

    // ============================================================ SVDEFINE

    private static readonly string[] DefineKeywords =
        ["Alignment", "Breaks", "Offsets", "Scale", "Orientation", "Clip", "Overlap", "Preset", "Area", "Ohwm", "Auto", "Draw", "Segment", "Done"];

    /// <summary>Creates or edits a run (6.12). Every value is typed; Done validates and saves.</summary>
    [CommandMethod("SVDEFINE", CommandFlags.Modal)]
    public static void Define() => Boundary.Run("SVDEFINE", c =>
    {
        string name = c.String("Run name");
        if (name.Length == 0) { c.Say($"{PipelineCodes.BadInput} SVDEFINE: a run needs a name."); return; }
        var load = DrawingRead.Run(c.Doc, c.Engine, (_, store) => store.Load(name));
        if (load.Exists && !load.CanModify) { c.ReportAll(load.Problems); return; }
        var (units, alignments) = DrawingRead.Run(c.Doc, c.Engine, (src, _) => (src.Units(), src.AlignmentNames()));
        if (!units.Succeeded) { c.ReportAll(units.Problems); return; }
        var s = c.Engine.Settings;
        double upf = s.DrawingUnitsPerFoot(units.Value);

        RunDefinition run;
        if (load.Record is { } existing) run = existing.Run;
        else
        {
            if (alignments.Count == 0) { c.Say($"{PipelineCodes.AlignmentMissing} SVDEFINE: the drawing has no alignment."); return; }
            string alignment = c.String($"Alignment ({string.Join(", ", alignments)})", allowSpaces: true, alignments[0]);
            if (!alignments.Contains(alignment, StringComparer.Ordinal)) { c.Say($"{PipelineCodes.AlignmentMissing} SVDEFINE: no alignment named '{alignment}'."); return; }
            run = NewRun(name, alignment, units.Value, upf, s);
            c.Say($"SVDEFINE: new run '{name}' on {alignment}, breaks '{run.BreakSpec}', offsets {Cmd.Num(run.OffsetLeft)} / {Cmd.Num(run.OffsetRight)}.");
        }

        while (true)
        {
            string kw = c.Keyword($"SVDEFINE {name}", DefineKeywords, "Done");
            if (kw == "Done") break;
            run = kw switch
            {
                "Alignment" => EditAlignment(c, run, alignments),
                "Breaks" => run with { BreakSpec = c.String("Break list (6.3 grammar)", allowSpaces: true, run.BreakSpec) },
                "Offsets" => EditOffsets(c, run),
                "Scale" => EditScale(c, run),
                "Orientation" => run with { Views = run.Views with { Orientation = c.Keyword("Orientation", ["Along", "North"], run.Views.Orientation == ViewOrientation.North ? "North" : "Along") == "North" ? ViewOrientation.North : ViewOrientation.AlongAlignment } },
                "Clip" => run with { Views = run.Views with { Clip = c.Keyword("Viewport clip", ["None", "Boundary"], run.Views.Clip.ToString()) == "None" ? ClipMode.None : ClipMode.Boundary } },
                "Overlap" => run with { Views = run.Views with { ViewOverlap = c.Number("View overlap at each end, drawing units", run.Views.ViewOverlap) } },
                "Preset" => EditPreset(c, run, units.Value.System),
                "Area" => EditArea(c, run, units.Value.System),
                "Ohwm" => run with { Ohwm = new OhwmSpec(c.Number("OHWM elevation", run.Ohwm?.DefaultElevation), run.Ohwm?.BySegment ?? new Dictionary<Guid, double>()) },
                "Auto" => run with { AutoMode = Enum.Parse<AutoMode>(c.Keyword("Auto-update mode", ["Off", "Views", "All"], run.AutoMode.ToString())) },
                "Draw" => run with
                {
                    Draw = new DrawOptions(
                        c.YesNo("Draw boundary polylines", (run.Draw ?? new DrawOptions()).BoundaryPolylines),
                        c.YesNo("Table at each segment", (run.Draw ?? new DrawOptions()).TableAtEachSegment),
                        c.YesNo("Summary table", (run.Draw ?? new DrawOptions()).SummaryTable)),
                },
                "Segment" => EditSegment(c, run),
                _ => run,
            };
        }

        ValidateAndSave(c, run);
    });

    private static RunDefinition NewRun(string name, string alignment, DrawingUnits units, double upf, SheetVolSettings s)
    {
        var d = s.RunDefaults;
        return new RunDefinition(
            name, alignment, SegmentDriver.Stations, d.BreakSpec, null,
            d.OffsetLeftFeet * upf, d.OffsetRightFeet * upf,
            [], [], [], new Dictionary<Guid, SegmentOverrides>(),
            new ViewOptions(d.Orientation, d.ScaleMode, d.ModelUnitsPerPaperUnit, d.StandardScales.ToList(), d.Clip, d.ViewOverlapFeet * upf),
            ReportPreset.Standard, QuantityUnits.DefaultAreaUnit(units.System, ReportPreset.Standard), null, d.AutoMode);
    }

    private static RunDefinition EditAlignment(Cmd c, RunDefinition run, IReadOnlyList<string> alignments)
    {
        string a = c.String($"Alignment ({string.Join(", ", alignments)})", allowSpaces: true, run.AlignmentName);
        if (alignments.Contains(a, StringComparer.Ordinal)) return run with { AlignmentName = a };
        c.Say($"{PipelineCodes.AlignmentMissing} no alignment named '{a}'; unchanged.");
        return run;
    }

    private static RunDefinition EditOffsets(Cmd c, RunDefinition run)
    {
        double l = c.Number("Left offset (negative is left)", run.OffsetLeft);
        double r = c.Number("Right offset", run.OffsetRight);
        if (!(l < r)) { c.Say($"{ProblemCodes.E210} left must be less than right; offsets unchanged (E210)."); return run; }
        return run with { OffsetLeft = l, OffsetRight = r };
    }

    private static RunDefinition EditScale(Cmd c, RunDefinition run)
    {
        string mode = c.Keyword("Scale mode", ["Fixed", "FitStandard"], run.Views.ScaleMode.ToString());
        if (mode == "Fixed")
        {
            double v = c.Number("Model units per paper unit", run.Views.ModelUnitsPerPaperUnit);
            if (!(v > 0)) { c.Say($"{PipelineCodes.BadInput} the scale must be above zero; unchanged."); return run; }
            return run with { Views = run.Views with { ScaleMode = ScaleMode.Fixed, ModelUnitsPerPaperUnit = v } };
        }
        string current = string.Join(",", (run.Views.StandardScales ?? []).Select(Cmd.Num));
        string text = c.String("Standard scales, comma separated", defaultValue: current.Length > 0 ? current : null);
        var scales = new List<double>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !(v > 0))
            { c.Say($"{PipelineCodes.BadInput} '{part}' is not a scale above zero; unchanged."); return run; }
            scales.Add(v);
        }
        if (scales.Count == 0) { c.Say($"{PipelineCodes.BadInput} no scales given; unchanged."); return run; }
        return run with { Views = run.Views with { ScaleMode = ScaleMode.FitStandard, StandardScales = scales } };
    }

    private static RunDefinition EditPreset(Cmd c, RunDefinition run, UnitSystem system)
    {
        var preset = c.Keyword("Report preset", ["Standard", "HcfcdSegment"], run.Preset.ToString()) == "HcfcdSegment" ? ReportPreset.HcfcdSegment : ReportPreset.Standard;
        if (preset == run.Preset) return run;
        var unit = QuantityUnits.DefaultAreaUnit(system, preset);
        c.Say($"Preset {preset}; area unit set to its default {unit} (6.16).");
        return run with { Preset = preset, AreaUnit = unit };
    }

    private static RunDefinition EditArea(Cmd c, RunDefinition run, UnitSystem system)
    {
        var offered = QuantityUnits.AreaUnitsFor(system);
        string kw = c.Keyword("Area unit", offered.Select(u => u.ToString()).ToList(), run.AreaUnit.ToString());
        var change = AreaUnitChange.Apply(run, Enum.Parse<AreaUnit>(kw), system);
        if (!change.Succeeded) { c.ReportAll(change.Problems); return run; }
        if (change.Value.TablesOutOfDate) c.Say("Area unit changed: display only, no recompute; drawn tables are out of date until SVREGEN (E418).");
        return change.Value.Run;
    }

    private static RunDefinition EditSegment(Cmd c, RunDefinition run)
    {
        var plan = PlanCurrent(c, run);
        if (plan is null || plan.Segments.Count == 0) { c.Say($"{PipelineCodes.BadInput} the run has no planned segments to override."); return run; }
        string label = c.String($"Segment label ({string.Join(", ", plan.Segments.Select(x => x.Label))})");
        var seg = plan.Segments.FirstOrDefault(x => string.Equals(x.Label, label, StringComparison.OrdinalIgnoreCase));
        if (seg is null) { c.Say($"{PipelineCodes.BadInput} no segment labelled '{label}'."); return run; }
        if (!run.KnownSegments.Any(k => k.Id == seg.Id))
            c.Say("Note: this segment has not been regenerated yet; its identity is fixed at the next SVREGEN, so set overrides after that if the break list changes first.");
        var overrides = run.Overrides.ToDictionary(kv => kv.Key, kv => kv.Value);
        var o = overrides.TryGetValue(seg.Id, out var cur) ? cur : new SegmentOverrides();
        string kw = c.Keyword($"Segment {seg.Label}", ["Layout", "Scale", "Lock", "Offsets", "Ohwm", "Clear"], "Layout");
        var ohwm = run.Ohwm;
        switch (kw)
        {
            case "Layout":
                string layout = c.String("Target layout for its table (. to clear)", allowSpaces: true, o.TargetLayout ?? ".");
                o = o with { TargetLayout = layout == "." ? null : layout };
                break;
            case "Scale":
                double sc = c.Number("Scale override, model units per paper unit (0 to clear)", o.ModelUnitsPerPaperUnit ?? 0);
                o = o with { ModelUnitsPerPaperUnit = sc > 0 ? sc : null };
                break;
            case "Lock":
                o = o with { LockView = c.YesNo("Lock the viewport view (E608)", o.LockView) };
                break;
            case "Offsets":
                double l = c.Number("Left offset for this segment", o.OffsetLeft ?? run.OffsetLeft);
                double r = c.Number("Right offset for this segment", o.OffsetRight ?? run.OffsetRight);
                if (!(l < r)) { c.Say($"{ProblemCodes.E210} left must be less than right; unchanged."); return run; }
                o = o with { OffsetLeft = l, OffsetRight = r };
                break;
            case "Ohwm":
                if (ohwm is null) { c.Say($"{PipelineCodes.OhwmMissing} set the run's OHWM first (SVDEFINE Ohwm)."); return run; }
                string text = c.String("OHWM elevation for this segment (. to clear)", defaultValue: ".");
                var by = ohwm.BySegment.ToDictionary(kv => kv.Key, kv => kv.Value);
                if (text == ".") by.Remove(seg.Id);
                else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double e) && double.IsFinite(e)) by[seg.Id] = e;
                else { c.Say($"{PipelineCodes.BadInput} '{text}' is not an elevation; unchanged."); return run; }
                ohwm = ohwm with { BySegment = by };
                break;
            case "Clear":
                overrides.Remove(seg.Id);
                return run with { Overrides = overrides };
        }
        overrides[seg.Id] = o;
        return run with { Overrides = overrides, Ohwm = ohwm };
    }

    /// <summary>Plans the run in its own read transaction. The PlanResult is plain data; the alignment is not (A1: valid only inside its transaction).</summary>
    private static PlanResult? PlanCurrent(Cmd c, RunDefinition run) =>
        DrawingRead.Run(c.Doc, c.Engine, (src, _) => PlanWith(c, src, run)?.Plan);

    /// <summary>Plans with sources the caller holds open; returns the plan, the alignment (valid only in that transaction) and the station tolerance.</summary>
    private static (PlanResult Plan, IAlignmentGeometry Alignment, double Tolerance)? PlanWith(Cmd c, IDrawingSources src, RunDefinition run)
    {
        var units = src.Units();
        var alignment = src.Alignment(run.AlignmentName, null);
        if (!units.Succeeded || !alignment.Succeeded) { c.ReportAll(units.Problems.Concat(alignment.Problems)); return null; }
        var outlines = run.ManualSegments.Select(m => (m.Id, O: src.ManualOutline(m.PolylineHandle))).Where(t => t.O.Succeeded).ToDictionary(t => t.Id, t => t.O.Value);
        double upf = c.Engine.Settings.DrawingUnitsPerFoot(units.Value);
        var options = c.Engine.Settings.Planning.ToOptions(upf);
        return (new Planner(options).Plan(run, alignment.Value.Geometry, outlines), alignment.Value.Geometry, options.Geometry.StationTolerance);
    }

    private static void ValidateAndSave(Cmd c, RunDefinition run)
    {
        var plan = PlanCurrent(c, run);
        if (plan is not null)
        {
            int errors = plan.Problems.Count(p => p.Severity == Severity.Error);
            c.Say($"Run '{run.Name}': {plan.Segments.Count} station segment(s), {run.ManualSegments.Count} manual, {run.Analyses.Count} analysis(es); {errors} error(s).");
            c.ReportAll(plan.Problems);
            foreach (var p in OwnedVolumeSurfaces.Validate(run.Analyses)) c.Report(p);
        }
        SaveDefinition(c, run);
        c.Say($"Run '{run.Name}' saved. Nothing is drawn until SVREGEN.");
    }

    /// <summary>Saves a definition, keeping the stored identities, handles and fingerprint (C1: KnownSegments only from a successful regen).</summary>
    internal static void SaveDefinition(Cmd c, RunDefinition run, Func<RunRecord, RunRecord>? adjust = null)
    {
        DrawingRead.Write(c.Doc, c.Engine, (_, store, _) =>
        {
            var load = store.Load(run.Name);
            if (load.Exists && !load.CanModify) throw new InvalidOperationException(string.Join(" ", load.Problems.Select(p => p.Code + " " + p.Message)));
            var record = load.Record is { } r ? r with { Run = run with { KnownSegments = r.Run.KnownSegments } } : RunRecord.New(run with { KnownSegments = [] });
            if (adjust is not null) record = adjust(record);
            store.Save(record);
            return true;
        });
        SessionState.Touch(c.Doc.Database);
    }

    private static RunDefinition? LoadForEdit(Cmd c, string command)
    {
        var names = DrawingRead.Run(c.Doc, c.Engine, (_, store) => store.RunNames());
        if (names.Count == 0) { c.Say($"{PipelineCodes.RunNotFound} {command}: no runs in this drawing. Create one with SVDEFINE."); return null; }
        string name = c.String($"Run name ({string.Join(", ", names)})", defaultValue: names.Count == 1 ? names[0] : null);
        var load = DrawingRead.Run(c.Doc, c.Engine, (_, store) => store.Load(name));
        if (!load.Exists) { c.Say($"{PipelineCodes.RunNotFound} {command}: no run named '{name}'."); return null; }
        if (load.Record is not { } rec || !load.CanModify) { c.ReportAll(load.Problems); return null; }
        return rec.Run;
    }

    // ============================================================ SVANALYSIS

    /// <summary>Adds, removes or re-factors an analysis (6.12).</summary>
    [CommandMethod("SVANALYSIS", CommandFlags.Modal)]
    public static void Analysis() => Boundary.Run("SVANALYSIS", c =>
    {
        var run = LoadForEdit(c, "SVANALYSIS");
        if (run is null) return;
        var surfaces = DrawingRead.Run(c.Doc, c.Engine, (src, _) => src.TinSurfaceNames());
        string kw = c.Keyword("SVANALYSIS", ["Add", "Remove", "Factors", "List"], "List");
        var analyses = run.Analyses.ToList();
        switch (kw)
        {
            case "List":
                if (analyses.Count == 0) c.Say("No analyses.");
                foreach (var a in analyses) c.Say($"  {a.Name}: {a.BaseSurface} vs {a.ComparisonSurface}, cut factor {Cmd.Num(a.CutFactor)}, fill factor {Cmd.Num(a.FillFactor)}");
                return;
            case "Add":
            {
                string name = c.String("Analysis name (the material row label for HCFCD)", allowSpaces: true);
                string b = c.String($"Base surface ({string.Join(", ", surfaces)})", allowSpaces: true);
                string cmp = c.String("Comparison surface", allowSpaces: true);
                double cf = c.Number("Cut factor", 1.0), ff = c.Number("Fill factor", 1.0);
                foreach (var (label, s) in new[] { ("Base", b), ("Comparison", cmp) })
                    if (!surfaces.Contains(s, StringComparer.Ordinal))
                    { c.Say($"SV-E402 {label} surface '{s}' is not a TIN surface in this drawing (grid and volume surfaces are rejected). Nothing changed."); return; }
                if (analyses.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
                { c.Say($"{PipelineCodes.BadInput} an analysis named '{name}' already exists. Nothing changed."); return; }
                analyses.Add(new AnalysisDef(Guid.NewGuid(), name, b, cmp, cf, ff));
                break;
            }
            case "Remove":
            {
                string name = c.String($"Analysis to remove ({string.Join(", ", analyses.Select(a => a.Name))})", allowSpaces: true);
                if (analyses.RemoveAll(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
                { c.Say($"{PipelineCodes.BadInput} no analysis named '{name}'."); return; }
                break;
            }
            case "Factors":
            {
                string name = c.String($"Analysis ({string.Join(", ", analyses.Select(a => a.Name))})", allowSpaces: true);
                int i = analyses.FindIndex(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
                if (i < 0) { c.Say($"{PipelineCodes.BadInput} no analysis named '{name}'."); return; }
                analyses[i] = analyses[i] with { CutFactor = c.Number("Cut factor", analyses[i].CutFactor), FillFactor = c.Number("Fill factor", analyses[i].FillFactor) };
                break;
            }
        }
        var problems = OwnedVolumeSurfaces.Validate(analyses);
        c.ReportAll(problems);
        if (problems.Any(p => p.Severity == Severity.Error)) { c.Say("SVANALYSIS: nothing saved."); return; }
        SaveDefinition(c, run with { Analyses = analyses });
        c.Say($"SVANALYSIS: run '{run.Name}' now has {analyses.Count} analysis(es). Nothing is drawn until SVREGEN.");
    });

    // ============================================================ SVBREAK

    /// <summary>Adds, moves or removes a break by typed station or pick, or converts a segment to manual (6.3, 6.12).</summary>
    [CommandMethod("SVBREAK", CommandFlags.Modal)]
    public static void Break() => Boundary.Run("SVBREAK", c =>
    {
        var run = LoadForEdit(c, "SVBREAK");
        if (run is null) return;
        if (run.Driver != SegmentDriver.Stations) { c.Say($"{ProblemCodes.E300} SVBREAK edits break lists; run '{run.Name}' uses the {run.Driver} driver."); return; }

        // Everything that touches the alignment happens inside one read transaction (A1's
        // geometry is valid only there); the result is a plain run definition saved afterwards.
        var outcome = DrawingRead.Run<(BreakEdit Edit, Func<RunRecord, RunRecord>? Adjust, PlanResult? Check)?>(c.Doc, c.Engine, (src, store) =>
        {
            if (PlanWith(c, src, run) is not { } current) return null;
            var (plan, alignment, tol) = current;
            var segs = plan.Segments.OrderBy(x => x.StartStation).ToList();
            string kw = c.Keyword("SVBREAK", ["Add", "Move", "Remove", "Manual"], "Add");
            BreakEdit edit;
            Func<RunRecord, RunRecord>? adjust = null;
            switch (kw)
            {
                case "Add":
                    edit = BreakEditor.Add(run, segs, alignment, AskStation(c, alignment, "Station of the new break"), tol);
                    break;
                case "Move":
                    double from = AskStation(c, alignment, "Break to move");
                    edit = BreakEditor.Move(run, segs, alignment, from, AskStation(c, alignment, "New station"), tol);
                    break;
                case "Remove":
                    edit = BreakEditor.Remove(run, segs, alignment, AskStation(c, alignment, "Break to remove"), tol);
                    break;
                default:
                {
                    string label = c.String($"Segment to make manual ({string.Join(", ", segs.Select(x => x.Label))})");
                    var seg = segs.FirstOrDefault(x => string.Equals(x.Label, label, StringComparison.OrdinalIgnoreCase));
                    if (seg is null) { c.Say($"{PipelineCodes.BadInput} no segment labelled '{label}'."); return null; }
                    string? boundaryHandle = store.Load(run.Name).Record?.Owned.FirstOrDefault(o => o.Key.SegmentId == seg.Id && o.Key.Role == OwnedRole.Boundary)?.Handle;
                    string handle = c.String("Polyline handle for the manual segment" + (boundaryHandle is null ? "" : " (Enter keeps the drawn boundary)"), defaultValue: boundaryHandle);
                    if (handle.Length == 0) { c.Say($"{PipelineCodes.BadInput} a manual segment needs a closed polyline."); return null; }
                    edit = BreakEditor.ToManual(run, segs, alignment, seg.Id, handle, tol);
                    if (string.Equals(handle, boundaryHandle, StringComparison.OrdinalIgnoreCase))
                        adjust = r => r with { Owned = r.Owned.Where(o => !(o.Key.SegmentId == seg.Id && o.Key.Role == OwnedRole.Boundary)).ToList() };   // the polyline is the user's now
                    break;
                }
            }
            if (edit.Error is not null || edit.Run == run) return (edit, adjust, null);
            return (edit, adjust, PlanWith(c, src, edit.Run)?.Plan);
        });
        if (outcome is not { } o) return;
        var (result, change, check) = o;
        if (result.Error is not null) { c.Say($"{PipelineCodes.BadInput} SVBREAK: {result.Error} Nothing changed."); return; }
        foreach (var n in result.Notes) c.Say("  " + n);
        if (result.Run == run) return;
        if (check is null || !check.IsValid)
        {
            c.Say("SVBREAK: the new break list does not plan cleanly; nothing saved.");
            if (check is not null) c.ReportAll(check.Problems);
            return;
        }
        var edit = result;
        SaveDefinition(c, edit.Run, change);
        c.Say($"SVBREAK: breaks now '{edit.Run.BreakSpec}'. Segment identities follow at the next SVREGEN (split keeps the left segment's layout and tables).");
    });

    /// <summary>A station by typed text (displayed stationing, equations honoured, E208) or by Pick (UCS to WCS, E217).</summary>
    private static double AskStation(Cmd c, IAlignmentGeometry alignment, string prompt)
    {
        string text = c.String($"{prompt} (station, or Pick)");
        if (text.Equals("Pick", StringComparison.OrdinalIgnoreCase))
        {
            var r = c.Ed.GetPoint(new PromptPointOptions("\nPoint on or near the alignment: "));
            if (r.Status != PromptStatus.OK) throw new PromptCancelledException(prompt);
            var wcs = r.Value.TransformBy(c.Ed.CurrentUserCoordinateSystem);   // picks arrive in the current UCS (6.13, E217)
            var p = new Pt(wcs.X, wcs.Y);
            if (!StationLocator.TryCreate(alignment, p, out var locator, out string? error))
                throw new InvalidOperationException($"{PipelineCodes.PickNotOnAlignment} stations cannot be measured on '{alignment.Name}': {error}");
            var hit = locator.Locate(new Pt(0, 0), alignment.StartStation, alignment.EndStation);
            if (hit.Station < alignment.StartStation || hit.Station > alignment.EndStation)
                throw new InvalidOperationException($"{PipelineCodes.PickNotOnAlignment} the picked point is beyond the ends of '{alignment.Name}'.");
            c.Say($"  picked station {alignment.FormatStation(hit.Station)} (offset {Cmd.Num(hit.Offset)})");
            return hit.Station;
        }
        if (!alignment.TryParseStation(text, out double raw, out string? why))
            throw new InvalidOperationException($"{ProblemCodes.E208} station '{text}' cannot be placed on '{alignment.Name}': {why}");
        return raw;
    }

    // ============================================================ SVCLEAR, SVPURGE

    /// <summary>Removes a run and its owned model-space objects; its layouts only when confirmed (6.12).</summary>
    [CommandMethod("SVCLEAR", CommandFlags.Modal)]
    public static void Clear() => Boundary.Run("SVCLEAR", c =>
    {
        var run = LoadForEdit(c, "SVCLEAR");
        if (run is null) return;
        bool layoutsToo = c.YesNo($"Also delete run '{run.Name}' layouts (cannot be undone after the drawing is saved)", false);
        int erased = 0, deleted = 0;
        using (var docLock = c.Doc.LockDocument())
        {
            var db = c.Doc.Database;
            using var tr = db.TransactionManager.StartTransaction();
            var w = c.Engine.Bindings.Writers;
            var store = w.Store(db, tr, c.Engine.Settings);
            var model = w.Model(db, tr, c.Engine.Settings);
            using var paper = w.Paper(db, tr, c.Engine.Settings);
            var rec = store.Load(run.Name).Record ?? throw new InvalidOperationException($"Run '{run.Name}' disappeared.");
            var cleared = model.Clear(run.Name, rec);
            if (!cleared.Succeeded)
            {
                c.ReportAll(cleared.Problems);
                c.Say($"SVCLEAR: run '{run.Name}' was not removed; nothing changed.");
                return;   // the transaction is aborted on dispose
            }
            erased = cleared.Counts.Erased;
            if (layoutsToo)
            {
                var live = paper.Layouts().Select(l => l.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var h in rec.Owned.Where(o => o.Key.Role == OwnedRole.Layout).Select(o => o.Handle).Concat(rec.Orphans.Select(o => o.Handle)))
                    if (live.Contains(h)) { paper.DeleteLayout(h); deleted++; }
            }
            store.Delete(run.Name);
            tr.Commit();
        }
        SessionState.Forget(c.Doc.Database, run.Name);
        c.Say($"SVCLEAR: run '{run.Name}' removed; {erased} model-space object(s) erased; {deleted} layout(s) deleted{(layoutsToo ? "" : " (layouts kept)")}.");
    });

    /// <summary>Deletes orphaned layouts after confirmation (6.12, E612).</summary>
    [CommandMethod("SVPURGE", CommandFlags.Modal)]
    public static void Purge() => Boundary.Run("SVPURGE", c =>
    {
        var orphans = DrawingRead.Run(c.Doc, c.Engine, (_, store) =>
            store.RunNames().Select(n => store.Load(n).Record).OfType<RunRecord>()
                .SelectMany(r => r.Orphans.Select(o => (Run: r.Run.Name, Orphan: o))).ToList());
        if (orphans.Count == 0) { c.Say("SVPURGE: no orphaned layouts."); return; }
        foreach (var (run, o) in orphans) c.Say($"  run '{run}': layout '{o.Name}' (handle {o.Handle})");
        if (!c.YesNo($"Delete these {orphans.Count} orphaned layout(s)", false)) { c.Say("SVPURGE: nothing deleted."); return; }
        int deleted = 0;
        using (var docLock = c.Doc.LockDocument())
        {
            var db = c.Doc.Database;
            using var tr = db.TransactionManager.StartTransaction();
            var w = c.Engine.Bindings.Writers;
            var store = w.Store(db, tr, c.Engine.Settings);
            using var paper = w.Paper(db, tr, c.Engine.Settings);
            var live = paper.Layouts().Select(l => l.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string name in orphans.Select(o => o.Run).Distinct(StringComparer.Ordinal))
            {
                var rec = store.Load(name).Record;
                if (rec is null) continue;
                foreach (var o in rec.Orphans.Where(o => live.Contains(o.Handle))) { paper.DeleteLayout(o.Handle); deleted++; }
                store.Save(rec with { Orphans = [] });
            }
            tr.Commit();
        }
        SessionState.Touch(c.Doc.Database);
        c.Say($"SVPURGE: {deleted} layout(s) deleted.");
    });

    // ============================================================ SVCONFIG

    /// <summary>Shows the effective settings and their source; writes a settings file; reloads (A7). Never writes the drawing: NoUndoMarker (S3, E804).</summary>
    [CommandMethod("SVCONFIG", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
    public static void Config() => Boundary.Run("SVCONFIG", c =>
    {
        string kw = c.Keyword("SVCONFIG", ["Show", "Write", "Reload"], "Show");
        var load = kw == "Reload" ? SettingsProvider.Reload() : SettingsProvider.Current;
        if (kw == "Write")
        {
            if (load.Settings is null) { c.Say($"{PipelineCodes.SettingsInvalid} SVCONFIG: the current settings are invalid; fix or delete {load.Source} first."); foreach (var p in load.Problems) c.Report(p); return; }
            string path = c.String("Settings file to write", allowSpaces: true, SettingsProvider.SettingsPath).Trim('"');
            if (File.Exists(path) && !c.YesNo($"{path} exists. Overwrite it", false)) { c.Say("SVCONFIG: nothing written."); return; }
            File.WriteAllText(path, load.Settings.ToJson(), new System.Text.UTF8Encoding(false));
            c.Say($"SVCONFIG: wrote the effective settings to {path}. SheetVol reads {SettingsProvider.SettingsPath}; run SVCONFIG Reload after editing it.");
            return;
        }
        c.Say($"SVCONFIG: settings source: {load.Source}{(load.FromFile ? "" : $" (no file at {SettingsProvider.SettingsPath})")}");
        c.Say($"SVCONFIG: engine bindings: {(EngineBindings.Current.IsWired ? "installed" : "NOT WIRED (A1-A3 integration pending); SV commands refuse with " + PipelineCodes.NotWired)}");
        foreach (var p in load.Problems) c.Report(p);
        if (load.Settings is null) return;
        var placeholders = load.Settings.PlaceholdersInUse();
        c.Say($"SVCONFIG: {placeholders.Count} placeholder setting(s): {string.Join(", ", placeholders)}");
        c.Say($"SVCONFIG: assumed settings awaiting confirmation: {string.Join(", ", ReportConfig.AssumedSettings.Select(x => "report." + x))}, planning.minSegmentLengthFeet, integrator.areaCheckRelative, integrator.areaCheckAbsoluteSquareFeet");
        foreach (var line in load.Settings.ToJson().Split('\n')) c.Say(line.TrimEnd('\r'));
    }, needsEngine: false);
}

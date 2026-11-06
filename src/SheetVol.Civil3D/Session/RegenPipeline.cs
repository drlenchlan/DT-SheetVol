// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4.
//
// The regen pipeline, HANDOFF.md 6.10:
//   1. Resolve references; fail early naming the missing object. Read-only or reference-
//      edited drawing refused (E807).
//   2. Read geometry, plan segments, build and validate boundaries, detect overlaps (C1).
//   3. Ensure and rebuild the owned volume surfaces (P18: always Rebuild), compute bounded
//      volumes, run the integrator for the cross-check and the HCFCD quantities (C5),
//      reconcile and partition-check (C2). A failed partition check aborts: nothing written.
//   4. Compute views (C3) and the planned owned objects; preflight the paper writer.
//   5. Apply in ONE transaction under the document lock: the model writer (A2: boundaries,
//      marks, outlines, model and master tables, HCFCD tables), then the paper writer (A3:
//      layouts with two-pass renames, viewports, clips, sheet tables, title attributes),
//      then persistence. Each writer writes only what the live objects show has changed
//      (E811). The paper writer runs last: it erases the template clones it creates, and an
//      abort after such an erase can crash the host (A1, 0xC0000005), so nothing that can
//      fail follows it; the export text is prepared before it.
//   6. Write exports; a file failure is a warning (E506, E507).
//
// Steps 1-4 write no output. The owned volume surface writes of step 3 (factors, the
// always-Rebuild of P18) happen in the same transaction as step 5, so any failure before
// commit rolls them back with everything else: the drawing is untouched (E801, T20). P20
// proved layout creation rolls back in an aborted transaction, so there is no two-phase
// layout fallback. One transaction per run inside one command means one UNDO reverts one
// regen (E804).

using System.Diagnostics;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Civil3D.Ownership;
using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Planning;
using SheetVol.Core.Tracking;
using SheetVol.Core.Views;
using SheetVol.Core.Volumes;
using SheetVol.Core.Volumes.Integrator;
using AcadDocument = Autodesk.AutoCAD.ApplicationServices.Document;

namespace SheetVol.Civil3D.Session;

/// <summary>What a regen does.</summary>
public enum RegenMode
{
    /// <summary>Steps 1 to 6.</summary>
    Full,

    /// <summary>Steps 1 to 3 and 6 (SVEXPORT): volumes are computed in a transaction that is then aborted, so the drawing is not written.</summary>
    ExportOnly,
}

/// <summary>A regen's full outcome: the contract result plus what the pipeline knew (for SVREGEN, SVSTATUS and the self-test).</summary>
/// <param name="Result">The contract result.</param>
/// <param name="Run">The definition as applied.</param>
/// <param name="Plan">The plan, when step 2 ran.</param>
/// <param name="Reconcile">The reconciled volumes, when step 3 ran.</param>
/// <param name="Views">Views per station segment, when step 4 ran.</param>
/// <param name="Applied">What the writers wrote (both writers together), when step 5 ran.</param>
/// <param name="OnlyTimestamp">True when the writers wrote nothing and the record changed only its timestamp (E811); null before step 5.</param>
/// <param name="Record">The record saved, when step 5 ran.</param>
/// <param name="FailedAtStep">The failing step; 0 on success.</param>
/// <param name="CrossCheckMaxRelative">Largest relative difference the 6.15 cross-check found between the integrator and the bounded volume (cut and fill against their own total); null when it did not run.</param>
public sealed record RunOutcome(
    RegenResult Result,
    RunDefinition Run,
    PlanResult? Plan,
    ReconcileResult? Reconcile,
    IReadOnlyDictionary<Guid, ViewResult> Views,
    ApplyCounts? Applied,
    bool? OnlyTimestamp,
    RunRecord? Record,
    int FailedAtStep,
    double? CrossCheckMaxRelative = null);

/// <summary>The regen engine. One instance per settings snapshot and bindings.</summary>
public sealed class RegenEngine
{
    private readonly EngineBindings _bindings;
    private readonly SheetVolSettings _settings;
    private readonly string _pluginDirectory;

    /// <summary>Creates an engine.</summary>
    /// <param name="bindings">Adapter and writer factories.</param>
    /// <param name="settings">Validated settings.</param>
    /// <param name="pluginDirectory">Directory relative paths in the settings resolve against.</param>
    public RegenEngine(EngineBindings bindings, SheetVolSettings settings, string pluginDirectory)
    {
        _bindings = bindings;
        _settings = settings;
        _pluginDirectory = pluginDirectory;
    }

    /// <summary>The settings this engine runs with.</summary>
    public SheetVolSettings Settings => _settings;

    /// <summary>The factories this engine runs on.</summary>
    public EngineBindings Bindings => _bindings;

    /// <summary>Optional clock for the persistence timestamp and exports (tests).</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Regenerates one run in <paramref name="doc"/>. Must run on the main thread in command
    /// context. Locks the document itself. Throws <see cref="OperationCanceledException"/> when
    /// cancelled before commit (the drawing is unchanged); every other failure comes back as
    /// <see cref="RegenResult.Succeeded"/> false with SV-E801 and the cause.
    /// </summary>
    public RunOutcome Regenerate(AcadDocument doc, RunDefinition requested, RegenMode mode, IProgress<double>? progress, CancellationToken ct, bool watchEscape = false)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(requested);
        var sw = Stopwatch.StartNew();
        var probe = new CancelProbe(ct, watchEscape);
        var bar = new ProgressSpan(progress);
        var problems = new List<Problem>();
        var views = new Dictionary<Guid, ViewResult>();
        var s = _settings;
        RunDefinition run = requested;
        PlanResult? plan = null;
        ReconcileResult? rec = null;
        double? crossCheck = null;

        RunOutcome Fail(int step, IEnumerable<Problem>? extra = null)
        {
            if (extra is not null) problems.AddRange(extra);
            var first = problems.FirstOrDefault(p => p.Severity == Severity.Error);
            problems.Insert(0, new Problem(PipelineCodes.RunFailed, Severity.Error,
                $"Run '{requested.Name}' failed at step {step} ({StepName(step)}): {(first is null ? "see the problems below" : first.Code + " " + first.Message)} Nothing in this run changed.",
                Fix: first?.Fix));
            var result = new RegenResult(false, requested.Name, rec?.AllRows ?? [], Dedupe(problems), [], false, sw.Elapsed, null, null);
            return new RunOutcome(result, run, plan, rec, views, null, null, null, step, crossCheck);
        }

        // ---- Step 1: refuse a read-only drawing, resolve references ----------------------
        if (mode == RegenMode.Full && ReadOnlyReason(doc) is { } why)
            return Fail(1, [new Problem(PipelineCodes.ReadOnlyDrawing, Severity.Error, why, Fix: "Open the drawing for editing, or close the reference edit, then regenerate.")]);

        bar.At(0);
        using var docLock = doc.LockDocument();
        var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        RunOutcome? committed = null;   // set at tr.Commit(): from then on nothing may report "nothing changed"
        try
        {
            var sources = _bindings.Adapters.Open(db, tr, s);
            var store = _bindings.Writers.Store(db, tr, s);
            var load = store.Load(requested.Name);
            if (load.Exists && !load.CanModify) return Fail(1, load.Problems);
            var prior = load.Record;
            run = requested with
            {
                KnownSegments = prior?.Run.KnownSegments ?? requested.KnownSegments,
                SchemaVersion = RunPersistence.PluginSchemaVersion,
            };
            // Rev 3 adoption: AnalysisDef.VolumeSurface is plumbed pair-only; the rev3-adapters package replaces this refusal.
            if (run.Analyses.Any(a => a.VolumeSurface is not null))
                return Fail(1, [Adapters.AdapterCodes.VolumeSurfaceNotYet()]);

            var unitsOutcome = sources.Units();
            if (!unitsOutcome.Succeeded) return Fail(1, unitsOutcome.Problems);
            var units = unitsOutcome.Value;
            double upf = s.DrawingUnitsPerFoot(units);

            string? priorAlignmentHandle = prior?.SourceHandleOf(SourceKinds.Alignment, run.AlignmentName);
            var alignmentOutcome = sources.Alignment(run.AlignmentName, priorAlignmentHandle);
            if (!alignmentOutcome.Succeeded) return Fail(1, alignmentOutcome.Problems);
            var alignmentSource = alignmentOutcome.Value;
            var alignment = alignmentSource.Geometry;
            problems.AddRange(alignmentOutcome.Problems);
            if (priorAlignmentHandle is not null && !string.Equals(priorAlignmentHandle, alignmentSource.Handle, StringComparison.OrdinalIgnoreCase)
                && !alignmentOutcome.Problems.Any(p => p.Code == PipelineCodes.MatchedByName))
                problems.Add(new Problem(PipelineCodes.MatchedByName, Severity.Warning,
                    $"Alignment '{run.AlignmentName}' now has handle {alignmentSource.Handle} (was {priorAlignmentHandle}); it was matched by name.",
                    Fix: "Nothing to do if the alignment was re-referenced on purpose."));

            var surfaces = new Dictionary<string, SurfaceSource>(StringComparer.Ordinal);
            var surfaceProblems = new List<Problem>();
            foreach (string name in run.Analyses.SelectMany(a => new[] { a.BaseSurface, a.ComparisonSurface }).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal))
            {
                string? priorHandle = prior?.SourceHandleOf(SourceKinds.Surface, name);
                var o = sources.Surface(name, priorHandle, s.Adapters.RebuildSources);
                surfaceProblems.AddRange(o.Problems);
                if (!o.Succeeded) continue;
                surfaces[name] = o.Value;
                // The adapters say it themselves when they did (A1's SV-E404 Info, SV-E803 Warning); the fakes do not.
                if (o.Value.Rebuilt && !o.Problems.Any(p => p.Code == PipelineCodes.SourceOutOfDate))
                    problems.Add(new Problem(PipelineCodes.SourceOutOfDate, Severity.Info, $"Surface '{name}' was out of date and was rebuilt (adapters.rebuildSources is on)."));
                if (priorHandle is not null && !string.Equals(priorHandle, o.Value.Handle, StringComparison.OrdinalIgnoreCase)
                    && !o.Problems.Any(p => p.Code == PipelineCodes.MatchedByName))
                    problems.Add(new Problem(PipelineCodes.MatchedByName, Severity.Warning,
                        $"Surface '{name}' now has handle {o.Value.Handle} (was {priorHandle}); it was matched by name."));
            }
            if (surfaceProblems.Any(p => p.Severity == Severity.Error)) return Fail(1, surfaceProblems);
            problems.AddRange(surfaceProblems);

            var outlines = new Dictionary<Guid, ManualOutline>();
            foreach (var m in run.ManualSegments)
            {
                var o = sources.ManualOutline(m.PolylineHandle);
                if (o.Succeeded) outlines[m.Id] = o.Value;   // a missing one is reported by the planner as E311
            }
            probe.Check();
            bar.At(0.05);

            // ---- Step 2: plan, boundaries, overlaps (C1) --------------------------------------
            var planningOptions = s.Planning.ToOptions(upf);
            plan = new Planner(planningOptions).Plan(run, alignment, outlines);
            problems.AddRange(plan.Problems);
            if (!plan.IsValid) return Fail(2);
            if (prior?.Fingerprint is { } pf && !string.Equals(pf.Alignment, Fingerprints.AlignmentSignature(alignment), StringComparison.Ordinal))
                problems.Add(new Problem(PipelineCodes.StationingChanged, Severity.Warning,
                    $"Alignment '{run.AlignmentName}' geometry or stationing changed since the last regen; segments follow their station values.",
                    Fix: "Check the break list if the alignment was reversed or re-stationed."));
            var stationSegs = plan.Segments.OrderBy(x => x.StartStation).ToList();
            var boundaries = plan.Boundaries.ToDictionary(b => b.SegmentId);
            var manualSegs = run.ManualSegments.Where(m => boundaries.ContainsKey(m.Id))
                .Select(m => new SegmentDef(m.Id, m.Label, double.NaN, double.NaN, run.OffsetLeft, run.OffsetRight, null, m.TargetLayout)).ToList();
            probe.Check();
            bar.At(0.1);

            // ---- Step 3: owned volume surfaces, volumes, integrator, reconcile ----------------
            var volumes = new Dictionary<Guid, MemoVolumeSource>();
            var newSources = new List<SourceHandle> { new(SourceKinds.Alignment, run.AlignmentName, alignmentSource.Handle) };
            newSources.AddRange(surfaces.Values.Select(x => new SourceHandle(SourceKinds.Surface, x.Name, x.Handle)));
            var surfaceStates = new List<AnalysisSurfaceState>();
            if (run.Analyses.Count == 0)
                problems.Add(new Problem(PipelineCodes.NoAnalyses, Severity.Info, $"Run '{run.Name}' has no analyses: boundaries and views only, no volumes or tables.", Fix: "Add one with SVANALYSIS."));
            else
            {
                var ownedSurfaces = OwnedVolumeSurfaces.Plan(run.Analyses, s.Report.Volumes);
                if (!ownedSurfaces.Succeeded) return Fail(3, ownedSurfaces.Problems);
                problems.AddRange(ownedSurfaces.Problems);
                foreach (var op in ownedSurfaces.Value)
                {
                    probe.Check();
                    var o = sources.EnsureVolumeSurface(op, prior?.SourceHandleOf(SourceKinds.VolumeSurface, op.Name));
                    if (!o.Succeeded) return Fail(3, o.Problems);
                    problems.AddRange(o.Problems);
                    if (o.Value.Created)
                        problems.Add(new Problem(PipelineCodes.Recreated, Severity.Info, $"Owned volume surface '{o.Value.Name}' was created for {op.BaseSurface} / {op.ComparisonSurface}."));
                    newSources.Add(new SourceHandle(SourceKinds.VolumeSurface, op.Name, o.Value.Handle));
                    var memo = new MemoVolumeSource(o.Value.Volumes);
                    foreach (var id in op.AnalysisIds) volumes[id] = memo;
                }
                foreach (var a in run.Analyses) surfaceStates.Add(new AnalysisSurfaceState(a.Id, false, volumes[a.Id].SurfaceTotals));
            }
            bar.At(0.2);

            var impacts = new Dictionary<Guid, Dictionary<Guid, ImpactResult>>();
            bool hcfcd = run.Preset == ReportPreset.HcfcdSegment;
            if (run.Analyses.Count > 0 && (s.Integrator.CrossCheck || hcfcd))
            {
                var checkProblems = Integrate(run, alignment, stationSegs, manualSegs, boundaries, surfaces, volumes, impacts, upf, probe, bar, out crossCheck);
                problems.AddRange(checkProblems.Where(p => p.Severity != Severity.Error));
                if (checkProblems.Any(p => p.Severity == Severity.Error)) return Fail(3, checkProblems.Where(p => p.Severity == Severity.Error));
            }
            bar.At(0.55);

            if (run.Analyses.Count > 0)
            {
                var inputs = run.Analyses.Select(a => new AnalysisInput(a, volumes[a.Id],
                    impacts.TryGetValue(a.Id, out var bySeg) ? bySeg : null)).ToList();
                var stationRequest = new ReconcileRequest(stationSegs, stationSegs.Select(x => boundaries[x.Id]).ToList(),
                    plan.Band.Select(b => b.Tessellated).ToList(), inputs, units, plan.PartitionCheckSkipReason);
                rec = Reconciler.Reconcile(stationRequest, s.Report.Volumes);
                if (manualSegs.Count > 0)
                {
                    var manualRequest = new ReconcileRequest(manualSegs, manualSegs.Select(x => boundaries[x.Id]).ToList(), [], inputs, units,
                        "Manual segments are not part of the station band.");
                    rec = MergeManual(rec, Reconciler.Reconcile(manualRequest, s.Report.Volumes));
                    problems.Add(new Problem(PipelineCodes.ManualOutsidePartition, Severity.Warning,
                        $"{manualSegs.Count} manual segment(s) are computed on their own polylines, outside the partition check; their volume may overlap station segments (E312)."));
                }
                problems.AddRange(rec.Problems);
                if (!rec.Succeeded) return Fail(3);
            }
            probe.Check();
            bar.At(0.6);

            // Fingerprint of the sources as they stand after the rebuild (6.11).
            var manualGeometry = outlines.ToDictionary(kv => kv.Key, kv => kv.Value.Vertices);
            var runApplied = run with { KnownSegments = stationSegs };
            var fingerprint = Fingerprints.Compute(new FingerprintInput(runApplied, alignment, [], surfaceStates, manualGeometry));

            if (mode == RegenMode.ExportOnly)
            {
                // Nothing of steps 1-3 is kept: the transaction is aborted on dispose. The files
                // are written while it is still open; they touch no drawing object.
                var preparedExport = rec is null ? null : ExportFiles.Prepare(runApplied, rec, DrawingPath(doc), alignment.FormatStation, s, Clock());
                var exported = preparedExport is null ? null : ExportFiles.WriteFiles(preparedExport, problems);
                bar.At(1);
                return new RunOutcome(Success(exported?.CsvPath, exported?.JsonPath), runApplied, plan, rec, views, null, null, null, 0, crossCheck);
            }

            // ---- Step 4: views, planned objects, preflight -----------------------------------
            var model = _bindings.Writers.Model(db, tr, s);
            using var paper = _bindings.Writers.Paper(db, tr, s);
            TemplateInfo? template = null;
            if (s.Regen.GenerateLayouts)
            {
                var t = paper.Template();
                if (!t.Succeeded) return Fail(4, t.Problems);
                template = t.Value;
                problems.AddRange(t.Problems);
                var calc = new ViewCalculator(s.ViewMath);
                var viewProblems = new List<Problem>();
                foreach (var seg in stationSegs)
                {
                    probe.Check();
                    run.Overrides.TryGetValue(seg.Id, out var ov);
                    var v = calc.Compute(alignment, new DisplayPolygonSource(alignment, seg, planningOptions.Geometry),
                        new ViewRequest(seg, run.Views, template.ViewportWidth, template.ViewportHeight, ov));
                    views[seg.Id] = v;
                    viewProblems.AddRange(v.Problems);
                }
                if (viewProblems.Any(p => p.Severity == Severity.Error)) return Fail(4, viewProblems);
                problems.AddRange(viewProblems);
            }

            HcfcdTemplate? hcfcdTemplate = null;
            QuantityFormatter? formatter = null;
            if (rec is not null)
            {
                var qu = QuantityUnits.Create(units, s.Report.Volumes);
                if (!qu.Succeeded) return Fail(4, qu.Problems);
                formatter = new QuantityFormatter(qu.Value, s.Report);
                if (hcfcd)
                {
                    var ht = HcfcdTemplate.Load(s.Report.Hcfcd.TemplatePath, _pluginDirectory);
                    if (!ht.Succeeded) return Fail(4, ht.Problems);
                    problems.AddRange(ht.Problems);
                    hcfcdTemplate = ht.Value;
                }
            }

            var layouts = paper.Layouts();
            problems.AddRange(SharedLayoutWarnings(store, run, stationSegs, manualSegs));
            var output = OutputPlanner.Build(new OutputInputs(runApplied, plan, manualSegs, rec, alignment,
                views.Where(kv => kv.Value.Spec is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Spec!),
                template, layouts, prior, hcfcdTemplate, formatter, planningOptions.Geometry, s));
            problems.AddRange(output.Problems);

            var nowUtc = Clock();
            var request = new WriteRequest(runApplied, RunIds.For(run.Name), output, prior, units, nowUtc);
            var preflight = paper.Validate(request);
            if (preflight.Any(p => p.Severity == Severity.Error)) return Fail(4, preflight);
            problems.AddRange(preflight);

            // Step 6 text is prepared here, before any write: station text needs the live
            // alignment, and nothing that can fail may run after the paper writer (see the header).
            var prepared = rec is null ? null : ExportFiles.Prepare(runApplied, rec, DrawingPath(doc), alignment.FormatStation, s, Clock());
            probe.Check();
            bar.At(0.7);

            // ---- Step 5: apply in this one transaction ---------------------------------------
            // 5a. Model space (A2). A refusal writes nothing of its own; the abort undoes step 3.
            var modelResult = model.Apply(request);
            if (!modelResult.Succeeded) return Fail(5, modelResult.Problems);
            problems.AddRange(modelResult.Problems);
            probe.Check();
            bar.At(0.8);

            // 5b. Paper space (A3), last. From here to the commit nothing may fail or be cancelled.
            var paperResult = paper.Apply(request);
            if (!paperResult.Succeeded) return Fail(5, paperResult.Problems);
            problems.AddRange(paperResult.Problems);

            // 5c. The record: owned handles from both writers, orphans kept with their first date.
            var owned = modelResult.Owned.Concat(paperResult.Owned).GroupBy(o => o.Key).Select(g => g.Last()).ToList();
            var ownedHandles = owned.Select(o => o.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var liveLayouts = layouts.Select(l => l.Handle).Concat(paperResult.Owned.Where(o => o.Key.Role == OwnedRole.Layout).Select(o => o.Handle))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var priorOrphans = (prior?.Orphans ?? []).Where(o => liveLayouts.Contains(o.Handle) && !ownedHandles.Contains(o.Handle)).ToList();
            var orphans = priorOrphans.Concat(paperResult.NewOrphans.Where(n => !ownedHandles.Contains(n.Handle))
                    .Where(n => !priorOrphans.Any(p => string.Equals(p.Handle, n.Handle, StringComparison.OrdinalIgnoreCase))))
                .ToList();
            var applied = modelResult.Counts.Plus(paperResult.Counts) with { Orphaned = orphans.Count - priorOrphans.Count };
            var record = new RunRecord(runApplied, fingerprint, owned, newSources, orphans, nowUtc, []);
            bool recordChanged = prior is null || !string.Equals(RunRecordCodec.Signature(record), RunRecordCodec.Signature(prior), StringComparison.Ordinal);
            bool onlyTimestamp = applied.Writes == 0 && !recordChanged;
            if (onlyTimestamp)
                problems.Add(new Problem(PipelineCodes.NothingChanged, Severity.Info,
                    $"Run '{run.Name}': nothing changed; the writers wrote nothing and only the regen timestamp was written (the owned volume surfaces are rebuilt on every regen, P18)."));
            record = record with { LastProblems = Dedupe(problems).Where(p => p.Severity != Severity.Info).ToList() };
            store.Save(record);
            tr.Commit();
            committed = new RunOutcome(Success(null, null), runApplied, plan, rec, views, applied, onlyTimestamp, record, 0, crossCheck);
            bar.At(0.95);

            // ---- Step 6: exports, after the commit; a failure here is a warning (E507) -----
            var files = prepared is null ? null : ExportFiles.WriteFiles(prepared, problems);
            bar.At(1);
            return committed with { Result = Success(files?.CsvPath, files?.JsonPath) };
        }
        catch (OperationCanceledException) when (committed is null)
        {
            throw;   // the transaction is aborted on dispose; the drawing is unchanged (E219)
        }
        catch (NotWiredException ex) when (committed is null)
        {
            return Fail(1, [new Problem(PipelineCodes.NotWired, Severity.Error, ex.Message, Fix: "Use a build with the adapters and writers integrated; SVSELFTEST runs on fakes.")]);
        }
        catch (Exception ex) when (committed is null)
        {
            return Fail(0, [new Problem(PipelineCodes.Unexpected, Severity.Error, $"{ex.GetType().FullName}: {ex.Message}", Fix: "Report this with the drawing; nothing was written.")]);
        }
        catch (Exception ex)
        {
            // The commit stands: report the late failure as a warning, never as "nothing changed".
            problems.Add(new Problem(PipelineCodes.Unexpected, Severity.Warning,
                $"After the regen was committed: {ex.GetType().FullName}: {ex.Message} The drawing changes are kept; exports may be missing.",
                Fix: "Run SVEXPORT to write the exports again."));
            return committed! with { Result = Success(null, null) };   // non-null: the filtered catches above take every exception before the commit
        }

        RegenResult Success(string? csv, string? json)
        {
            var outside = rec?.Analyses.Where(a => a.OutsideLimits is { Shown: true })
                .Select(a => new OutsideLimits(a.Analysis.Id, a.OutsideLimits!.Difference)).ToList() ?? [];
            return new RegenResult(true, run.Name, rec?.AllRows ?? [], Dedupe(problems), outside,
                rec?.PartitionCheckPassed ?? false, sw.Elapsed, csv, json);
        }
    }

    // ---- Step 3 detail: C5 integrator, cross-check and impacts ------------------------------

    private List<Problem> Integrate(
        RunDefinition run, IAlignmentGeometry alignment, List<SegmentDef> stationSegs, List<SegmentDef> manualSegs,
        Dictionary<Guid, Boundary> boundaries, Dictionary<string, SurfaceSource> surfaces, Dictionary<Guid, MemoVolumeSource> volumes,
        Dictionary<Guid, Dictionary<Guid, ImpactResult>> impacts, double upf, CancelProbe probe, ProgressSpan bar, out double? maxRelative)
    {
        var s = _settings;
        var problems = new List<Problem>();
        maxRelative = null;
        var settings = new IntegratorSettings(s.Integrator.MinCutDepthFeet * upf, s.Integrator.GapToleranceFeet * upf);
        double absVolume = s.Integrator.CrossCheckAbsoluteCubicFeet * upf * upf * upf;
        double absArea = s.Integrator.AreaCheckAbsoluteSquareFeet * upf * upf;
        bool hcfcd = run.Preset == ReportPreset.HcfcdSegment;
        int total = run.Analyses.Count * (stationSegs.Count + manualSegs.Count), done = 0;

        foreach (var a in run.Analyses)
        {
            var baseTri = surfaces[a.BaseSurface].Triangles;
            var compTri = surfaces[a.ComparisonSurface].Triangles;
            var bySeg = new Dictionary<Guid, ImpactResult>();
            foreach (var seg in stationSegs.Concat(manualSegs))
            {
                probe.Check();
                bar.Report(0.2, 0.55, total == 0 ? 1 : (double)done++ / total);
                var tess = boundaries[seg.Id].Tessellated;
                BandTotals? band;
                IReadOnlyList<Problem> found;
                if (hcfcd && double.IsFinite(seg.StartStation))
                {
                    var r = OverlayIntegrator.IntegrateSegment(baseTri, compTri, alignment, seg, tess, a.Name, settings);
                    band = r.Band;
                    found = r.Problems;
                    if (r.Impact is { } impact) bySeg[seg.Id] = impact;
                }
                else
                {
                    var r = OverlayIntegrator.IntegrateBand(baseTri, compTri, tess, seg.Id);
                    band = r.Totals;
                    found = r.Problems;
                }

                foreach (var p in found)
                {
                    if (p.Code == IntegratorCodes.EmptySurface)
                        problems.Add(new Problem(PipelineCodes.IntegratorSkipped, Severity.Warning,
                            $"Sheet {seg.Label}, {a.Name}: {p.Message} The cross-check{(hcfcd ? " and the impact quantities" : "")} for this segment are missing; the run continues (D10).",
                            seg.Id, "Extend the surface over the segment if impacts are expected here."));
                    else if (p.Severity == Severity.Error)
                        problems.Add(p with { Message = $"Sheet {seg.Label}, {a.Name}: {p.Message}" });
                    else if (p.Code is IntegratorCodes.SearchLimit or IntegratorCodes.NoCut)
                        problems.Add(p with { Message = $"Sheet {seg.Label}, {a.Name}: {p.Message}" });
                }
                if (band is null) continue;

                // 6.15 check: integrator band cut and fill equal GetBoundedVolumes on the owned surface (E1012):
                // max(absolute, relative x total), total = bounded cut + fill of this polygon. Project data:
                // max(0.5 ft3, 1e-4 x total); SVSELFTEST on fixtures: 1e-9 relative over the adapters' 1e-6 noise floor.
                var bounded = volumes[a.Id].Bounded(tess);
                double magnitude = Math.Abs(bounded.Cut) + Math.Abs(bounded.Fill);
                double tol = Math.Max(absVolume, s.Integrator.CrossCheckRelative * magnitude);
                double diff = Math.Max(Math.Abs(band.Cut - bounded.Cut), Math.Abs(band.Fill - bounded.Fill));
                double rel = magnitude > 0 ? diff / magnitude : diff > 0 ? double.PositiveInfinity : 0;
                maxRelative = Math.Max(maxRelative ?? 0, rel);
                if (Math.Abs(band.Cut - bounded.Cut) > tol || Math.Abs(band.Fill - bounded.Fill) > tol)
                    problems.Add(new Problem(PipelineCodes.IntegratorMismatch, Severity.Error,
                        Inv($"Sheet {seg.Label}, {a.Name}: the integrator gives cut {band.Cut:R}, fill {band.Fill:R}; the bounded volume gives cut {bounded.Cut:R}, fill {bounded.Fill:R} (tolerance {tol:R} cubic drawing units)."),
                        seg.Id, "The surfaces' visible triangles and the volume surface disagree; rebuild both surfaces and report this if it repeats."));

                // 6.5 check: the datum-method 2D area agrees with the integrator's area.
                double area = volumes[a.Id].Area2D(tess);
                double areaTol = Math.Max(absArea, s.Integrator.AreaCheckRelative * Math.Abs(area));
                if (double.IsFinite(area) && Math.Abs(area - band.PlanArea) > areaTol)
                    problems.Add(new Problem(PipelineCodes.AreaMismatch, Severity.Error,
                        Inv($"Sheet {seg.Label}, {a.Name}: 2D area {area:R} from the volume surface, {band.PlanArea:R} from the integrator (tolerance {areaTol:R} square drawing units)."),
                        seg.Id, "Rebuild both surfaces; report this with the drawing if it repeats."));
            }
            if (bySeg.Count > 0) impacts[a.Id] = bySeg;
        }
        return problems;
    }

    /// <summary>The drawing's saved path, or null when it was never saved (E506). Follows SAVEAS (E809).</summary>
    public static string? DrawingPath(AcadDocument doc)
    {
        var db = doc.Database;
        bool titled;
        if (IsActive(doc))
            titled = Convert.ToInt32(Application.GetSystemVariable("DWGTITLED"), CultureInfo.InvariantCulture) == 1;
        else
            titled = !string.IsNullOrEmpty(db.Filename) && File.Exists(db.Filename)
                     && string.Equals(Path.GetExtension(db.Filename), ".dwg", StringComparison.OrdinalIgnoreCase);
        return titled && !string.IsNullOrEmpty(db.Filename) ? db.Filename : null;
    }

    /// <summary>Why regen must refuse this drawing (E807), or null.</summary>
    public static string? ReadOnlyReason(AcadDocument doc)
    {
        if (doc.IsReadOnly) return $"Drawing '{doc.Name}' is open read-only (or locked by another user); regen refuses before reading anything.";
        if (IsActive(doc) && Application.GetSystemVariable("REFEDITNAME") is string refedit && refedit.Length > 0)
            return $"A reference edit of '{refedit}' is open in drawing '{doc.Name}'; regen refuses until it is closed.";
        return null;
    }

    /// <summary>True when <paramref name="doc"/> is the active document, compared by native database (wrappers are not stable objects).</summary>
    public static bool IsActive(AcadDocument doc) =>
        Application.DocumentManager.MdiActiveDocument is { } active && active.Database.UnmanagedObject == doc.Database.UnmanagedObject;

    // ---- helpers -----------------------------------------------------------------------------

    private static IEnumerable<Problem> SharedLayoutWarnings(IPersistenceStore store, RunDefinition run, List<SegmentDef> stationSegs, List<SegmentDef> manualSegs)
    {
        var mine = stationSegs.Concat(manualSegs).Select(x => x.TargetLayout).Where(l => !string.IsNullOrWhiteSpace(l))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (mine.Count == 0) yield break;
        foreach (string other in store.RunNames().Where(n => !string.Equals(n, run.Name, StringComparison.Ordinal)))
        {
            var rec = store.Load(other).Record;
            if (rec is null) continue;
            var theirs = rec.Run.KnownSegments.Select(k => k.TargetLayout).Concat(rec.Run.ManualSegments.Select(m => m.TargetLayout))
                .Where(l => !string.IsNullOrWhiteSpace(l) && mine.Contains(l!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (theirs.Count > 0)
                yield return new Problem(PipelineCodes.SharedLayout, Severity.Warning,
                    $"Runs '{run.Name}' and '{other}' both place tables on layout(s) {string.Join(", ", theirs)}; shared layouts are not supported (E808).",
                    Fix: "Use one run with several analyses instead.");
        }
    }

    private static ReconcileResult MergeManual(ReconcileResult station, ReconcileResult manual)
    {
        var analyses = station.Analyses.Select(a =>
        {
            var m = manual.Analyses.FirstOrDefault(x => x.Analysis.Id == a.Analysis.Id);
            return m is null ? a : a with { Rows = a.Rows.Concat(m.Rows).ToList() };
        }).ToList();
        // The manual reconcile's partition is skipped by design and its outside-limits row is
        // meaningless (no band), so only its row problems are kept.
        var manualProblems = manual.Problems.Where(p => p.Code is not (VolumeCodes.PartitionSkipped or VolumeCodes.OutsideLimitsNegative)).ToList();
        bool ok = station.Succeeded && !manualProblems.Any(p => p.Severity == Severity.Error);
        return new ReconcileResult(ok, station.Units, station.Segments.Concat(manual.Segments).ToList(), analyses,
            station.Problems.Concat(manualProblems).ToList());
    }

    private static string StepName(int step) => step switch
    {
        0 => "unexpected error",
        1 => "resolve references",
        2 => "plan segments and boundaries",
        3 => "volumes",
        4 => "views and preflight",
        5 => "apply",
        _ => "export",
    };

    /// <summary>Removes exact duplicates, keeping the first occurrence.</summary>
    public static IReadOnlyList<Problem> Dedupe(IEnumerable<Problem> problems)
    {
        var seen = new HashSet<Problem>();
        return problems.Where(seen.Add).ToList();
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Result of writing the CSV and JSON exports.</summary>
public sealed record ExportFilesResult(string? CsvPath, string? JsonPath, IReadOnlyList<Problem> Problems);

/// <summary>Export text built while the transaction is open, waiting to be written (step 6).</summary>
/// <param name="RunName">Run name, for messages.</param>
/// <param name="CsvPath">Target CSV path, or null when nothing is to be written.</param>
/// <param name="CsvText">CSV text.</param>
/// <param name="JsonPath">Target JSON path, or null.</param>
/// <param name="JsonText">JSON text.</param>
/// <param name="CsvBom">Write a UTF-8 BOM on the CSV.</param>
/// <param name="Problems">Why nothing is to be written (E506, a refused export document).</param>
public sealed record PreparedExport(string RunName, string? CsvPath, string CsvText, string? JsonPath, string JsonText, bool CsvBom, IReadOnlyList<Problem> Problems);

/// <summary>
/// Step 6: CSV and JSON beside the drawing (6.8, E506, E507, E508, E809). Two halves:
/// <see cref="Prepare"/> needs the live alignment (station text) and runs inside the transaction;
/// <see cref="WriteFiles"/> touches only the file system and runs after the commit.
/// </summary>
public static class ExportFiles
{
    /// <summary>Builds the export text. Never saved: nothing to write, SV-E506. Call while the alignment's transaction is open.</summary>
    public static PreparedExport Prepare(RunDefinition run, ReconcileResult rec, string? drawingPath, Func<double, string> formatStation,
        SheetVolSettings settings, DateTimeOffset now)
    {
        var ef = settings.ExportFiles;
        if (drawingPath is null)
            return new PreparedExport(run.Name, null, "", null, "", ef.CsvUtf8Bom,
            [
                new Problem(PipelineCodes.DrawingNeverSaved, Severity.Warning,
                    $"Run '{run.Name}': the drawing has never been saved, so no CSV or JSON was written. Everything else ran.",
                    Fix: "Save the drawing, then run SVEXPORT."),
            ]);

        // Manual segments carry NaN stations (6.3); the real FormatStation throws on them
        // (E311 self-test on installed bindings, 2026-11-05). Format only finite stations.
        string FormatOrManual(double raw) => double.IsFinite(raw) ? formatStation(raw) : settings.ModelOutput.ManualStationText;
        var doc = ExportBuilder.Build(run, rec, now, drawingPath, FormatOrManual, settings.Report);
        if (!doc.Succeeded)
            return new PreparedExport(run.Name, null, "", null, "", ef.CsvUtf8Bom, doc.Problems.Select(p => p with { Severity = Severity.Warning }).ToList());

        string dir = Path.GetDirectoryName(drawingPath) ?? ".";
        string stem = ef.FileNamePattern
            .Replace("{DWG}", Path.GetFileNameWithoutExtension(drawingPath), StringComparison.Ordinal)
            .Replace("{RUN}", run.Name, StringComparison.Ordinal);
        foreach (char c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        return new PreparedExport(run.Name,
            Path.Combine(dir, stem + ef.CsvExtension), CsvExport.Write(doc.Value, settings.Report),
            Path.Combine(dir, stem + ef.JsonExtension), JsonExport.Write(doc.Value),
            ef.CsvUtf8Bom, []);
    }

    /// <summary>Writes the prepared files. A file that cannot be written: SV-E507 for that file and its path null. Never throws.</summary>
    public static ExportFilesResult WriteFiles(PreparedExport prepared, List<Problem> runProblems)
    {
        var problems = new List<Problem>(prepared.Problems);
        string? csv = prepared.CsvPath is null ? null : TryWrite(prepared.CsvPath, prepared.CsvText, new System.Text.UTF8Encoding(prepared.CsvBom), prepared.RunName, problems);
        string? json = prepared.JsonPath is null ? null : TryWrite(prepared.JsonPath, prepared.JsonText, new System.Text.UTF8Encoding(false), prepared.RunName, problems);
        runProblems.AddRange(problems);
        return new ExportFilesResult(csv, json, problems);
    }

    /// <summary>Prepare and write in one call, for callers holding an open transaction (SVEXPORT, the self-test).</summary>
    public static ExportFilesResult Write(RunDefinition run, ReconcileResult rec, string? drawingPath, Func<double, string> formatStation,
        SheetVolSettings settings, DateTimeOffset now) =>
        WriteFiles(Prepare(run, rec, drawingPath, formatStation, settings, now), []);

    private static string? TryWrite(string path, string text, System.Text.Encoding encoding, string runName, List<Problem> problems)
    {
        try
        {
            File.WriteAllText(path, text, encoding);
            return path;
        }
        catch (Exception ex)
        {
            problems.Add(new Problem(PipelineCodes.ExportWriteFailed, Severity.Warning,
                $"Run '{runName}': could not write {path}: {ex.Message} The drawing changes are kept.",
                Fix: "Close the file in any program that has it open, or make the folder writable, then run SVEXPORT."));
            return null;
        }
    }
}

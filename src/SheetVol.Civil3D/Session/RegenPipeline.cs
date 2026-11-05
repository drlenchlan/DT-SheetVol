// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4.
//
// The regen pipeline, HANDOFF.md 6.10:
//   1. Resolve references; fail early naming the missing object. Read-only or reference-
//      edited drawing refused (E807).
//   2. Read geometry, plan segments, build and validate boundaries, detect overlaps (C1).
//   3. Ensure and rebuild the owned volume surfaces (P18: always Rebuild), compute bounded
//      volumes, run the integrator for the cross-check and the HCFCD quantities (C5),
//      reconcile and partition-check (C2). A failed partition check aborts: nothing written.
//   4. Compute views (C3) and the planned owned objects; preflight the writers.
//   5. Apply in ONE transaction under the document lock: layouts (two-pass renames),
//      viewports, title attributes, tables, boundaries, marks, master table, erasures,
//      persistence. Only what the ChangeSet (C3) marks as changed is written (E811).
//   6. Write exports; a file failure is a warning (E506, E507).
//
// Steps 1-4 write no output. The owned volume surface writes of step 3 happen in the same
// transaction as step 5, so any failure before commit rolls them back with everything
// else: the drawing is untouched (E801, T20). P20 proved layout creation rolls back in an
// aborted transaction, so there is no two-phase layout fallback. One transaction per run
// inside one command means one UNDO reverts one regen (E804).

using System.Diagnostics;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
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

/// <summary>Counts of what step 5 did, for SVREGEN's summary and the self-test.</summary>
/// <param name="Created">Objects created.</param>
/// <param name="Updated">Objects updated that the writer reported as modified.</param>
/// <param name="Erased">Objects erased.</param>
/// <param name="Renamed">Layout renames (both passes).</param>
/// <param name="Orphaned">Layouts that became orphans.</param>
public sealed record ApplyCounts(int Created, int Updated, int Erased, int Renamed, int Orphaned)
{
    /// <summary>Database writes other than the persistence record.</summary>
    public int Writes => Created + Updated + Erased + Renamed;
}

/// <summary>A regen's full outcome: the contract result plus what the pipeline knew (for SVREGEN, SVSTATUS and the self-test).</summary>
public sealed record RunOutcome(
    RegenResult Result,
    RunDefinition Run,
    PlanResult? Plan,
    ReconcileResult? Reconcile,
    IReadOnlyDictionary<Guid, ViewResult> Views,
    ChangeSet? Changes,
    ApplyCounts? Applied,
    RunRecord? Record,
    int FailedAtStep);

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

        RunOutcome Fail(int step, IEnumerable<Problem>? extra = null)
        {
            if (extra is not null) problems.AddRange(extra);
            var first = problems.FirstOrDefault(p => p.Severity == Severity.Error);
            problems.Insert(0, new Problem(PipelineCodes.RunFailed, Severity.Error,
                $"Run '{requested.Name}' failed at step {step} ({StepName(step)}): {(first is null ? "see the problems below" : first.Code + " " + first.Message)} Nothing in this run changed.",
                Fix: first?.Fix));
            var result = new RegenResult(false, requested.Name, rec?.AllRows ?? [], Dedupe(problems), [], false, sw.Elapsed, null, null);
            return new RunOutcome(result, run, plan, rec, views, null, null, null, step);
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
            if (priorAlignmentHandle is not null && !string.Equals(priorAlignmentHandle, alignmentSource.Handle, StringComparison.OrdinalIgnoreCase))
                problems.Add(new Problem(PipelineCodes.MatchedByName, Severity.Warning,
                    $"Alignment '{run.AlignmentName}' now has handle {alignmentSource.Handle} (was {priorAlignmentHandle}); it was matched by name.",
                    Fix: "Nothing to do if the alignment was re-referenced on purpose."));

            var surfaces = new Dictionary<string, SurfaceSource>(StringComparer.Ordinal);
            var surfaceProblems = new List<Problem>();
            foreach (string name in run.Analyses.SelectMany(a => new[] { a.BaseSurface, a.ComparisonSurface }).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal))
            {
                string? priorHandle = prior?.SourceHandleOf(SourceKinds.Surface, name);
                var o = sources.Surface(name, priorHandle, s.Regen.RebuildSources);
                surfaceProblems.AddRange(o.Problems);
                if (!o.Succeeded) continue;
                surfaces[name] = o.Value;
                if (o.Value.Rebuilt)
                    problems.Add(new Problem(PipelineCodes.SourceOutOfDate, Severity.Info, $"Surface '{name}' was out of date and was rebuilt (regen.rebuildSources is on)."));
                if (priorHandle is not null && !string.Equals(priorHandle, o.Value.Handle, StringComparison.OrdinalIgnoreCase))
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
            if (run.Preset == ReportPreset.HcfcdSegment && run.Ohwm is null)
                return Fail(2, [new Problem(PipelineCodes.OhwmMissing, Severity.Error, $"Run '{run.Name}' uses the HCFCD preset but has no OHWM elevation.", Fix: "Set the OHWM elevation with SVDEFINE Ohwm.")]);
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
                var owned = OwnedVolumeSurfaces.Plan(run.Analyses, s.Report.Volumes);
                if (!owned.Succeeded) return Fail(3, owned.Problems);
                problems.AddRange(owned.Problems);
                foreach (var op in owned.Value)
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
                var checkProblems = Integrate(run, alignment, stationSegs, manualSegs, boundaries, surfaces, volumes, impacts, upf, probe, bar);
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
                return new RunOutcome(Success(exported?.CsvPath, exported?.JsonPath), runApplied, plan, rec, views, null, null, null, 0);
            }

            // ---- Step 4: views, planned objects, preflight -----------------------------------
            var model = _bindings.Writers.Model(db, tr, s);
            var paper = _bindings.Writers.Paper(db, tr, s);
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
            var priorHandles = (prior?.Owned ?? []).ToDictionary(o => o.Key, o => o.Handle);
            var output = OutputPlanner.Build(new OutputInputs(runApplied, plan, manualSegs, rec, alignment,
                views.Where(kv => kv.Value.Spec is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Spec!),
                template, layouts, prior, hcfcdTemplate, formatter, upf, s,
                key => priorHandles.TryGetValue(key, out var h) && WriterFor(key.Role).Inspect(key, h).Exists));
            problems.AddRange(output.Problems);

            IOwnedObjectWriter WriterFor(OwnedRole role) =>
                model.Roles.Contains(role) ? model
                : paper.Roles.Contains(role) ? paper
                : throw new InvalidOperationException($"No writer owns role {role}.");

            var preflight = new List<Problem>();
            preflight.AddRange(model.Preflight(output.Objects.Where(o => model.Roles.Contains(o.Key.Role)).ToList()));
            preflight.AddRange(paper.Preflight(output.Objects.Where(o => paper.Roles.Contains(o.Key.Role)).ToList()));
            if (preflight.Any(p => p.Severity == Severity.Error)) return Fail(4, preflight);
            problems.AddRange(preflight);
            probe.Check();
            bar.At(0.7);

            // ---- Step 5: apply in this one transaction ---------------------------------------
            var stored = (prior?.Owned ?? []).Select(o => WriterFor(o.Key.Role).Inspect(o.Key, o.Handle)).ToList();
            var storedByKey = stored.ToDictionary(x => x.Key);
            var plannedObjects = output.Objects.Select(o => new PlannedObject(o.Key, o.Signature, output.Locked.Contains(o.Key))).ToList();

            var nowUtc = Clock();
            var layoutHandles = layouts.ToDictionary(l => l.Handle, l => l.Name, StringComparer.OrdinalIgnoreCase);
            var keptOrphans = (prior?.Orphans ?? []).Where(o => layoutHandles.ContainsKey(o.Handle)).ToList();
            var provisionalOwned = output.Objects
                .Select(o => new OwnedHandle(o.Key, storedByKey.TryGetValue(o.Key, out var so) && so.Exists && so.Handle is not null ? so.Handle : "(new)"))
                .ToList();
            var provisionalOrphans = keptOrphans.Concat(stored.Where(x => x.Exists && x.Key.Role == OwnedRole.Layout && !plannedObjects.Any(p => p.Key == x.Key))
                .Select(x => new OrphanLayout(x.Key.SegmentId, x.Handle!, layoutHandles.GetValueOrDefault(x.Handle!, ""), nowUtc))).ToList();
            var provisional = new RunRecord(runApplied, fingerprint, provisionalOwned, newSources, provisionalOrphans, nowUtc, []);
            var changes = ChangeSetComparer.Compare(plannedObjects, stored,
                RunRecordCodec.Signature(provisional), prior is null ? null : RunRecordCodec.Signature(prior with { LastRegenUtc = null }));

            var payload = output.Objects.ToDictionary(o => o.Key);
            var handles = new Dictionary<OwnedKey, string>();
            int created = 0, updated = 0, erased = 0, renamed = 0;
            var orphans = new List<OrphanLayout>(keptOrphans);

            // Owned layers that are locked are unlocked for the writes and relocked after them,
            // in this transaction (6.10). Only when something is written: an unchanged regen
            // touches no layer (E811). Frozen layers are left frozen (E504).
            bool writes = changes.Writes.Any() || (output.Renames is { } rn && rn.FirstPass.Count + rn.SecondPass.Count > 0);
            var relock = writes ? UnlockOwnedLayers(db, tr, OwnedLayerNames(s)) : [];

            // 5a. Layout renames, first pass to temporary names, then to final names (E307).
            if (output.Renames is { } rp)
            {
                foreach (var step in rp.FirstPass.Concat(rp.SecondPass))
                {
                    probe.Check();
                    paper.RenameLayout(output.SegmentLayoutHandles[step.SegmentId], step.To);
                    renamed++;
                }
            }

            // 5b. Creates and updates in role order: a viewport or a paper table needs its layout first.
            foreach (var change in changes.Changes.Where(c => c.Kind is not (ChangeKind.Erase or ChangeKind.Orphan))
                         .OrderBy(c => RoleOrder(c.Key.Role)))
            {
                probe.Check();
                switch (change.Kind)
                {
                    case ChangeKind.Unchanged:
                    case ChangeKind.Kept:
                        handles[change.Key] = change.Handle!;
                        break;
                    case ChangeKind.Update when change.Key.Role == OwnedRole.Layout:
                        handles[change.Key] = change.Handle!;   // renamed in 5a
                        break;
                    case ChangeKind.Create:
                    case ChangeKind.Update:
                    {
                        var w = WriterFor(change.Key.Role).Write(payload[change.Key], change.Kind == ChangeKind.Create ? null : change.Handle);
                        if (w.Problems.Any(p => p.Severity == Severity.Error)) return Fail(5, w.Problems);
                        problems.AddRange(w.Problems);
                        handles[change.Key] = w.Handle;
                        if (change.Kind == ChangeKind.Create)
                        {
                            created++;
                            if (storedByKey.ContainsKey(change.Key))
                                problems.Add(new Problem(PipelineCodes.Recreated, Severity.Info,
                                    $"{Describe(change.Key, plan)} was missing (handle {storedByKey[change.Key].Handle}) and was recreated as {w.Handle}.", change.Key.SegmentId));
                        }
                        else if (w.Modified) updated++;
                        break;
                    }
                }
            }

            // 5c. Erase what is no longer planned; orphan layouts are kept and listed (E308, E612).
            foreach (var change in changes.Changes.Where(c => c.Kind is ChangeKind.Erase or ChangeKind.Orphan))
            {
                probe.Check();
                if (change.Kind == ChangeKind.Erase)
                {
                    WriterFor(change.Key.Role).Erase(change.Key, change.Handle!);
                    erased++;
                }
                else if (change.Key.Role == OwnedRole.Layout && change.Handle is { } lh)
                {
                    orphans.Add(new OrphanLayout(change.Key.SegmentId, lh, layoutHandles.GetValueOrDefault(lh, ""), nowUtc));
                    problems.Add(new Problem(PipelineCodes.Recreated, Severity.Info,
                        $"Layout '{layoutHandles.GetValueOrDefault(lh, lh)}' no longer has a segment; it is kept as an orphan (SVSTATUS lists it, SVPURGE deletes it).", change.Key.SegmentId));
                }
            }

            foreach (var id in relock) ((LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite)).IsLocked = true;
            if (relock.Count > 0)
                problems.Add(new Problem(PipelineCodes.LayerRelocked, Severity.Info, $"{relock.Count} locked owned layer(s) were unlocked for the update and locked again."));

            var applied = new ApplyCounts(created, updated, erased, renamed, orphans.Count - keptOrphans.Count);
            if (changes.OnlyTimestamp && renamed == 0)
                problems.Add(new Problem(PipelineCodes.NothingChanged, Severity.Info, $"Run '{run.Name}': nothing changed; only the regen timestamp was written."));

            var ownedList = output.Objects.Where(o => handles.ContainsKey(o.Key)).Select(o => new OwnedHandle(o.Key, handles[o.Key])).ToList();
            var keptProblems = Dedupe(problems).Where(p => p.Severity != Severity.Info).ToList();
            var record = new RunRecord(runApplied, fingerprint, ownedList, newSources, orphans, nowUtc, keptProblems);
            store.Save(record);

            // Step 6 is prepared here, while the transaction (and the adapters' objects) are
            // still open: station text needs the live alignment. Only the file writes follow the commit.
            var prepared = rec is null ? null : ExportFiles.Prepare(runApplied, rec, DrawingPath(doc), alignment.FormatStation, s, Clock());
            probe.Check();
            tr.Commit();
            committed = new RunOutcome(Success(null, null), runApplied, plan, rec, views, changes, applied, record, 0);
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
        Dictionary<Guid, Dictionary<Guid, ImpactResult>> impacts, double upf, CancelProbe probe, ProgressSpan bar)
    {
        var s = _settings;
        var problems = new List<Problem>();
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
                    var r = OverlayIntegrator.IntegrateSegment(baseTri, compTri, alignment, seg, tess, run.Ohwm!, a.Name, settings);
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
                    else if (p.Code is IntegratorCodes.SearchLimit or IntegratorCodes.NoGroundBelowOhwm)
                        problems.Add(p with { Message = $"Sheet {seg.Label}, {a.Name}: {p.Message}" });
                }
                if (band is null) continue;

                // 6.15 check: integrator band cut and fill equal GetBoundedVolumes on the owned surface (E1012).
                var bounded = volumes[a.Id].Bounded(tess);
                double tol = Math.Max(absVolume, s.Integrator.CrossCheckRelative * (Math.Abs(bounded.Cut) + Math.Abs(bounded.Fill)));
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

    /// <summary>Every layer the tool's outputs live on, from the settings.</summary>
    private static IEnumerable<string> OwnedLayerNames(SheetVolSettings s) =>
    [
        s.ModelOutput.BoundaryLayer, s.ModelOutput.MatchMarkLayer,
        s.Report.SegmentTable.Style.Layer, s.Report.MasterTable.Style.Layer, s.Report.Hcfcd.Style.Layer,
        s.Template.PlanViewportLayer,
    ];

    /// <summary>Unlocks the named layers that exist and are locked; returns them for relocking. Frozen state is never touched.</summary>
    private static List<ObjectId> UnlockOwnedLayers(Database db, Transaction tr, IEnumerable<string> names)
    {
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var unlocked = new List<ObjectId>();
        foreach (string name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!table.Has(name)) continue;   // a missing owned layer is the writers' E501 preflight error
            var layer = (LayerTableRecord)tr.GetObject(table[name], OpenMode.ForRead);
            if (!layer.IsLocked) continue;
            layer.UpgradeOpen();
            layer.IsLocked = false;
            unlocked.Add(layer.ObjectId);
        }
        return unlocked;
    }

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

    private static int RoleOrder(OwnedRole role) => role switch
    {
        OwnedRole.Layout => 0,
        OwnedRole.Viewport => 1,
        OwnedRole.Clip => 2,
        OwnedRole.TitleAttributes => 3,
        OwnedRole.PaperTable => 4,
        OwnedRole.Boundary => 5,
        OwnedRole.MatchMark => 6,
        OwnedRole.ImpactOutline => 7,
        OwnedRole.ModelTable => 8,
        _ => 9,
    };

    private static string Describe(OwnedKey key, PlanResult? plan)
    {
        string label = plan?.Segments.FirstOrDefault(x => x.Id == key.SegmentId)?.Label ?? key.SegmentId.ToString("D");
        return key.Role == OwnedRole.MasterTable ? "The master table" : $"Sheet {label} {key.Role}{(key.Qualifier.Length > 0 ? " (" + key.Qualifier + ")" : "")}";
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

        var doc = ExportBuilder.Build(run, rec, now, drawingPath, formatStation, settings.Report);
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

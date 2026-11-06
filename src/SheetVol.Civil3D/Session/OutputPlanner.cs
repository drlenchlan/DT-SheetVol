// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4; reshaped at the Wave 2
// integration to hand A2 and A3 their own specs.
//
// Step 4 of the regen (6.10), after the views: what the run should own once applied.
// Pure: reads the plan, the reconciled volumes, the views and the drawing's layout list,
// writes nothing. Model-space objects carry A2's specs (boundaries, MatchMarkPlanner marks,
// ImpactOutlines, ModelTableSpec, HcfcdTableSpec); paper objects carry what A3's sheet
// request needs (name parts, view, table model, LockView). Each object also carries the
// signature the fake writers compare, so the self-test's fakes and the real writers are
// driven by one plan.
//
// Placement rules (6.7, 6.8, 6.16, A14):
//   - boundary polyline per station segment when Draw.BoundaryPolylines;
//   - match marks at shared breaks when modelWriters.matchMarks.enabled (A2's planner);
//   - impact outline rings (ImpactResult.Outlines, rev 3 D13) per station segment and analysis
//     for the HcfcdSegment preset when modelWriters.drawImpactOutline;
//   - with regen.generateLayouts: one layout per station segment from the template, named by
//     C3's LayoutNamer (E307, E603), its plan viewport (LockView keeps it, E608) and title
//     attributes; A3 renames and creates the layouts itself from the same name parts;
//   - Standard per-sheet table: on the segment's TargetLayout when it names one (A2, placed at
//     that layout's anchor; a missing layout is A2's E1017, never re-created elsewhere), else
//     on its generated layout (A3), else in model space at the segment when
//     Draw.TableAtEachSegment (A2);
//   - HCFCD table (6.16): on the TargetLayout when set, else in model space at the segment
//     when Draw.TableAtEachSegment (A2); never on a generated sheet;
//   - the master table when Draw.SummaryTable, in model space beside the run or on
//     modelOutput.masterTableLayout (A2; a missing layout is A2's E601 before any write).

using SheetVol.Civil3D.WritersModel;
using SheetVol.Civil3D.WritersPaper;
using SheetVol.Core;
using SheetVol.Core.Geometry;
using SheetVol.Core.Output;
using SheetVol.Core.Planning;
using SheetVol.Core.Tracking;
using SheetVol.Core.Views;
using SheetVol.Core.Volumes;

namespace SheetVol.Civil3D.Session;

/// <summary>Inputs to <see cref="OutputPlanner.Build"/>.</summary>
public sealed record OutputInputs(
    RunDefinition Run,
    PlanResult Plan,
    IReadOnlyList<SegmentDef> ManualSegments,
    ReconcileResult? Reconcile,
    IAlignmentGeometry Alignment,
    IReadOnlyDictionary<Guid, ViewSpec> Views,
    TemplateInfo? Template,
    IReadOnlyList<LayoutRef> Layouts,
    RunRecord? Prior,
    HcfcdTemplate? Hcfcd,
    QuantityFormatter? Formatter,
    GeometryOptions Geometry,
    SheetVolSettings Settings);

/// <summary>What the run will own after step 5.</summary>
/// <param name="Objects">Planned objects.</param>
/// <param name="Renames">Layout rename plan, or null when layouts are not generated (the fake paper writer applies it; A3 plans its own from the same names).</param>
/// <param name="SegmentLayoutHandles">Stored layout handle per segment.</param>
/// <param name="Problems">Notes and warnings (E603).</param>
public sealed record OutputPlan(
    IReadOnlyList<OwnedObject> Objects,
    LayoutRenamePlan? Renames,
    IReadOnlyDictionary<Guid, string> SegmentLayoutHandles,
    IReadOnlyList<Problem> Problems)
{
    /// <summary>A plan that owns nothing.</summary>
    public static OutputPlan Empty { get; } = new([], null, new Dictionary<Guid, string>(), []);
}

/// <summary>Builds the planned owned objects for one run.</summary>
public static class OutputPlanner
{
    /// <summary>Builds the plan. Throws <see cref="InvalidOperationException"/> only on a defect (a table builder refusing a reconciled result).</summary>
    public static OutputPlan Build(OutputInputs input)
    {
        var s = input.Settings;
        var run = input.Run;
        var draw = run.Draw ?? new DrawOptions();
        var alignment = input.Alignment;
        var problems = new List<Problem>();
        var objects = new List<OwnedObject>();
        var stationSegs = input.Plan.Segments.OrderBy(x => x.StartStation).ToList();
        var boundaries = input.Plan.Boundaries.ToDictionary(b => b.SegmentId);
        string Fmt(double raw) => double.IsFinite(raw) ? alignment.FormatStation(raw) : s.ModelOutput.ManualStationText;
        double ScaleOf(Guid id) => input.Views.TryGetValue(id, out var v) ? v.ModelUnitsPerPaperUnit
            : run.Overrides.TryGetValue(id, out var ov) && ov.ModelUnitsPerPaperUnit is { } sc ? sc
            : run.Views.ModelUnitsPerPaperUnit;
        var priorOwned = (input.Prior?.Owned ?? []).ToDictionary(o => o.Key, o => o.Handle);
        var layoutByHandle = input.Layouts.ToDictionary(l => l.Handle, l => l, StringComparer.OrdinalIgnoreCase);

        // Boundaries.
        if (draw.BoundaryPolylines)
            foreach (var seg in stationSegs)
                if (boundaries.TryGetValue(seg.Id, out var b))
                    objects.Add(new BoundaryObject(new OwnedKey(seg.Id, OwnedRole.Boundary), b));

        // Match marks at shared breaks (A2's planner: the segment's own offsets, its own view scale).
        var valid = stationSegs.Where(x => boundaries.ContainsKey(x.Id)).ToList();
        foreach (var mark in MatchMarkPlanner.Plan(valid, alignment, ScaleOf, s.ModelWriters, input.Geometry.StationTolerance, s.Report.TableCulture()))
        {
            objects.Add(new MatchMarkObject(mark.LineKey, mark));
            objects.Add(new MatchMarkObject(mark.TextKey, mark));
        }

        // Impact outline (HcfcdSegment preset; rev 3 D13): every ring of each analysis's
        // ImpactResult.Outlines, keyed (segment, analysis, ring). No rings, no outline: a segment
        // whose rings are gone owns none afterwards and A2 erases what it drew (E308).
        var rec = input.Reconcile;
        if (run.Preset == ReportPreset.HcfcdSegment && s.ModelWriters.DrawImpactOutline && rec is not null)
        {
            foreach (var seg in valid)
            {
                var impacts = rec.Analyses
                    .Select(a => (a.Analysis.Id, Impact: a.Rows.FirstOrDefault(r => r.SegmentId == seg.Id)?.Impact))
                    .Where(t => t.Impact is not null)
                    .Select(t => (t.Id, t.Impact!));
                foreach (var o in ImpactOutlines.Build(seg.Id, impacts)) objects.Add(new ImpactOutlineObject(o.Key, o));
            }
        }

        // Layouts, viewports, title attributes.
        var segmentLayout = new Dictionary<Guid, string>();
        var segmentLayoutHandles = new Dictionary<Guid, string>();
        LayoutRenamePlan? renames = null;
        if (input.Template is not null)
        {
            var namer = new LayoutNamer(s.LayoutNames);
            bool renumber = s.Paper.RenumberLayouts;
            var owned = new List<OwnedLayout>();
            var plannedCurrent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parts = new Dictionary<Guid, LayoutNameParts>();
            for (int i = 0; i < stationSegs.Count; i++)
            {
                var seg = stationSegs[i];
                parts[seg.Id] = Parts(run, i + 1, seg, Fmt);
                string? current = null;
                if (priorOwned.TryGetValue(new OwnedKey(seg.Id, OwnedRole.Layout), out var h) && layoutByHandle.TryGetValue(h, out var lr))
                {
                    current = lr.Name;
                    segmentLayoutHandles[seg.Id] = lr.Handle;
                    plannedCurrent.Add(current);
                }
                string requested = renumber || current is null ? namer.FormatName(parts[seg.Id]) : current;
                owned.Add(new OwnedLayout(seg.Id, current, requested));
            }
            var foreign = input.Layouts.Select(l => l.Name).Where(n => !plannedCurrent.Contains(n)).ToList();
            // LayoutNamer de-duplicates in list order. With renumbering off an existing layout keeps
            // its name, so existing layouts go first and a new layout never takes a kept name.
            if (!renumber) owned = [.. owned.Where(o => o.CurrentName is not null), .. owned.Where(o => o.CurrentName is null)];
            renames = namer.PlanRenames(owned, foreign);
            problems.AddRange(renames.Problems);
            var finalBySegment = owned.Select((o, i) => (o.SegmentId, renames.Assignments[i].Final)).ToDictionary(t => t.SegmentId, t => t.Final);

            for (int i = 0; i < stationSegs.Count; i++)
            {
                var seg = stationSegs[i];
                string name = finalBySegment[seg.Id];
                segmentLayout[seg.Id] = name;
                objects.Add(new LayoutObject(new OwnedKey(seg.Id, OwnedRole.Layout), name, i + 1, parts[seg.Id]));
                if (input.Views.TryGetValue(seg.Id, out var view))
                {
                    bool lockView = run.Overrides.TryGetValue(seg.Id, out var ov) && ov.LockView;
                    objects.Add(new ViewportObject(new OwnedKey(seg.Id, OwnedRole.Viewport), name, view, lockView));
                }
                var attributes = s.Paper.TitleAttributes ?? new Dictionary<string, string>();
                if (attributes.Count > 0)
                {
                    var values = attributes.ToDictionary(kv => kv.Key,
                        kv => LayoutNamer.FormatPattern(kv.Value, parts[seg.Id], s.Paper.SheetNumberFormat), StringComparer.Ordinal);
                    objects.Add(new TitleAttributesObject(new OwnedKey(seg.Id, OwnedRole.TitleAttributes), name, values));
                }
            }
        }

        // Tables.
        if (rec is not null && rec.Analyses.Count > 0)
        {
            bool hcfcd = run.Preset == ReportPreset.HcfcdSegment && input.Hcfcd is not null && input.Formatter is not null;
            foreach (var seg in stationSegs.Concat(input.ManualSegments))
            {
                if (!boundaries.TryGetValue(seg.Id, out var boundary)) continue;
                string? target = string.IsNullOrWhiteSpace(seg.TargetLayout) ? null : seg.TargetLayout;
                if (hcfcd)
                {
                    WritersModel.TablePlacement? hp = target is not null ? WritersModel.TablePlacement.InLayout(target)
                        : draw.TableAtEachSegment ? ModelTablePlacement.AtSegment(boundary, ScaleOf(seg.Id), s.ModelWriters)
                        : null;
                    if (hp is not null) objects.Add(Hcfcd(seg, rec, input, hp));
                    continue;
                }

                var table = SegmentTableBuilder.Build(rec, seg.Id, run.AreaUnit, Fmt, s.Report);
                if (!table.Succeeded) throw new InvalidOperationException("Segment table refused a reconciled result: " + string.Join(" | ", table.Problems.Select(p => p.Message)));
                if (target is not null)
                    objects.Add(Model(ModelTableSpec.ForSegment(table.Value, WritersModel.TablePlacement.InLayout(target))));
                else if (segmentLayout.TryGetValue(seg.Id, out var generated))
                    objects.Add(new PaperTableObject(new OwnedKey(seg.Id, OwnedRole.PaperTable, PaperTableWriter.Qualifier(table.Value)), generated, table.Value));
                else if (draw.TableAtEachSegment)
                    objects.Add(Model(ModelTableSpec.ForSegment(table.Value, ModelTablePlacement.AtSegment(boundary, ScaleOf(seg.Id), s.ModelWriters))));
            }

            if (draw.SummaryTable)
            {
                WritersModel.TablePlacement? placement = s.ModelOutput.MasterTableLayout is { } ml
                    ? WritersModel.TablePlacement.InLayout(ml)
                    : boundaries.Count > 0 ? ModelTablePlacement.MasterInModel(boundaries.Values, run.Views.ModelUnitsPerPaperUnit, s.ModelWriters) : null;
                if (placement is not null)
                {
                    var master = MasterTableBuilder.Build(rec, run.AreaUnit, Fmt, s.Report);
                    if (!master.Succeeded) throw new InvalidOperationException("Master table refused a reconciled result: " + string.Join(" | ", master.Problems.Select(p => p.Message)));
                    objects.Add(Model(ModelTableSpec.ForMaster(master.Value, placement)));
                }
            }
        }

        return new OutputPlan(objects, renames, segmentLayoutHandles, problems);

        static ModelTableObject Model(ModelTableSpec spec) => new(spec.Key, spec);
    }

    private static HcfcdTableObject Hcfcd(SegmentDef seg, ReconcileResult rec, OutputInputs input, WritersModel.TablePlacement placement)
    {
        var run = input.Run;
        var s = input.Settings;
        var template = input.Hcfcd!;
        var formatter = input.Formatter!;
        var values = new List<HcfcdMaterial>();
        foreach (var a in rec.Analyses)
        {
            var row = a.Rows.FirstOrDefault(r => r.SegmentId == seg.Id);
            // D10/D18: a station segment with a row but no impact (SV-E827) reports zero impact, never blank.
            var impact = row?.Impact ?? (row is not null && double.IsFinite(seg.StartStation)
                ? new ImpactResult(seg.Id, a.Analysis.Name, 0, 0, 0, [], [], false) : null);
            if (impact is not null) values.Add(new HcfcdMaterial(a.Analysis.Id, a.Analysis.Name, impact, a.Analysis.CutFactor));
        }
        var created = template.CreateTable(seg.Id, run.Analyses, run.AreaUnit, formatter.Units, s.Report);
        if (!created.Succeeded) throw new InvalidOperationException("HCFCD template refused the run's materials: " + string.Join(" | ", created.Problems.Select(p => p.Message)));
        var cells = created.Value.Rows.Select(r => (IReadOnlyList<string>)r.Cells).ToList();
        var bind = template.Bind(cells, seg.Id, values, run.AreaUnit, formatter, s.Report);
        var spec = new HcfcdTableSpec(seg.Id, template, run.Analyses, values, run.AreaUnit, placement);
        return new HcfcdTableObject(spec.Key, spec, OwnedSignatures.OfBound(bind.Bound));
    }

    private static LayoutNameParts Parts(RunDefinition run, int sheet, SegmentDef seg, Func<double, string> fmt) =>
        new(run.Name, sheet, seg.Label, fmt(seg.StartStation), fmt(seg.EndStation), run.AlignmentName);
}

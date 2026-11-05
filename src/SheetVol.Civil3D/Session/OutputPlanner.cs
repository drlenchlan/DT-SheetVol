// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4.
//
// Step 4 of the regen (6.10), after the views: what the run should own once applied.
// Pure: reads the plan, the reconciled volumes, the views and the drawing's layout list,
// writes nothing. Each planned object carries the signature C3's ChangeSet compares with
// the live object, so step 5 writes only what changed (E811).
//
// Placement rules (6.7, 6.8, 6.16, A14):
//   - boundary polyline per station segment when Draw.BoundaryPolylines;
//   - match marks at shared breaks when modelOutput.matchMarks;
//   - with regen.generateLayouts: one layout per station segment from the template, named
//     by C3's LayoutNamer and renumbered with a two-pass plan (E307, E603), its plan
//     viewport (LockView keeps it, E608) and title attributes;
//   - one table per segment: on the segment's TargetLayout when it names one (missing:
//     E1017, kept where it is, never re-created elsewhere), else on its generated layout,
//     else in model space at the segment when Draw.TableAtEachSegment;
//   - the master table when Draw.SummaryTable, in model space or modelOutput.masterTableLayout.

using System.Globalization;
using SheetVol.Core;
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
    double DrawingUnitsPerFoot,
    SheetVolSettings Settings,
    Func<OwnedKey, bool> LiveExists);   // a stored object still exists in the drawing (writer Inspect)

/// <summary>What the run will own after step 5.</summary>
/// <param name="Objects">Planned objects in apply order.</param>
/// <param name="Locked">Keys whose existing object is kept as is (LockView, missing target layout).</param>
/// <param name="Renames">Layout rename plan, or null when layouts are not generated.</param>
/// <param name="SegmentLayoutHandles">Stored layout handle per segment for the renames.</param>
/// <param name="Problems">Notes and warnings (E603, E1017).</param>
public sealed record OutputPlan(
    IReadOnlyList<OwnedObject> Objects,
    IReadOnlySet<OwnedKey> Locked,
    LayoutRenamePlan? Renames,
    IReadOnlyDictionary<Guid, string> SegmentLayoutHandles,
    IReadOnlyList<Problem> Problems);

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
        var locked = new HashSet<OwnedKey>();
        var stationSegs = input.Plan.Segments.OrderBy(x => x.StartStation).ToList();
        var boundaries = input.Plan.Boundaries.ToDictionary(b => b.SegmentId);
        double tol = s.Planning.StationToleranceFeet * input.DrawingUnitsPerFoot;
        string Fmt(double raw) => double.IsFinite(raw) ? alignment.FormatStation(raw) : s.ModelOutput.ManualStationText;
        var priorOwned = (input.Prior?.Owned ?? []).ToDictionary(o => o.Key, o => o.Handle);
        var layoutByHandle = input.Layouts.ToDictionary(l => l.Handle, l => l, StringComparer.OrdinalIgnoreCase);
        var layoutNames = new HashSet<string>(input.Layouts.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);

        // Boundaries.
        if (draw.BoundaryPolylines)
            foreach (var seg in stationSegs)
                if (boundaries.TryGetValue(seg.Id, out var b))
                    objects.Add(new BoundaryObject(new OwnedKey(seg.Id, OwnedRole.Boundary), b));

        // Match marks at shared breaks.
        if (s.ModelOutput.MatchMarks)
        {
            double height = s.ModelOutput.MatchMarkTextHeight * run.Views.ModelUnitsPerPaperUnit;
            for (int i = 0; i + 1 < stationSegs.Count; i++)
            {
                var a = stationSegs[i];
                var c = stationSegs[i + 1];
                if (Math.Abs(a.EndStation - c.StartStation) > tol) continue;
                double station = a.EndStation;
                Pt from = alignment.PointAt(station, Math.Min(a.OffsetLeft, c.OffsetLeft));
                Pt to = alignment.PointAt(station, Math.Max(a.OffsetRight, c.OffsetRight));
                string F(string label) => string.Format(CultureInfo.InvariantCulture, s.ModelOutput.MatchMarkTextTemplate, Fmt(station), label);
                objects.Add(new MatchMarkObject(new OwnedKey(a.Id, OwnedRole.MatchMark, MarkQualifiers.End), from, to, F(c.Label), height));
                objects.Add(new MatchMarkObject(new OwnedKey(c.Id, OwnedRole.MatchMark, MarkQualifiers.Start), from, to, F(a.Label), height));
            }
        }

        // Layouts, viewports, title attributes.
        var segmentLayout = new Dictionary<Guid, string>();
        var segmentLayoutHandles = new Dictionary<Guid, string>();
        LayoutRenamePlan? renames = null;
        if (input.Template is not null)
        {
            var namer = new LayoutNamer(s.LayoutNames);
            var owned = new List<OwnedLayout>();
            var plannedCurrent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < stationSegs.Count; i++)
            {
                var seg = stationSegs[i];
                string? current = null;
                if (priorOwned.TryGetValue(new OwnedKey(seg.Id, OwnedRole.Layout), out var h) && layoutByHandle.TryGetValue(h, out var lr))
                {
                    current = lr.Name;
                    segmentLayoutHandles[seg.Id] = lr.Handle;
                    plannedCurrent.Add(current);
                }
                string requested = s.Regen.RenumberLayouts || current is null
                    ? namer.FormatName(Parts(run, i + 1, seg, Fmt))
                    : current;
                owned.Add(new OwnedLayout(seg.Id, current, requested));
            }
            var foreign = input.Layouts.Select(l => l.Name).Where(n => !plannedCurrent.Contains(n)).ToList();
            // LayoutNamer de-duplicates in list order. With renumbering off an existing layout keeps
            // its name, so existing layouts go first and a new layout never takes a kept name.
            if (!s.Regen.RenumberLayouts) owned = [.. owned.Where(o => o.CurrentName is not null), .. owned.Where(o => o.CurrentName is null)];
            renames = namer.PlanRenames(owned, foreign);
            problems.AddRange(renames.Problems);
            var finalBySegment = owned.Select((o, i) => (o.SegmentId, renames.Assignments[i].Final)).ToDictionary(t => t.SegmentId, t => t.Final);

            for (int i = 0; i < stationSegs.Count; i++)
            {
                var seg = stationSegs[i];
                string name = finalBySegment[seg.Id];
                segmentLayout[seg.Id] = name;
                objects.Add(new LayoutObject(new OwnedKey(seg.Id, OwnedRole.Layout), name, i + 1));
                if (input.Views.TryGetValue(seg.Id, out var view))
                {
                    var key = new OwnedKey(seg.Id, OwnedRole.Viewport);
                    objects.Add(new ViewportObject(key, name, view));
                    if (run.Overrides.TryGetValue(seg.Id, out var ov) && ov.LockView) locked.Add(key);
                }
                var attributes = s.Template.TitleAttributes ?? new Dictionary<string, string>();
                if (attributes.Count > 0)
                {
                    var parts = Parts(run, i + 1, seg, Fmt);
                    var values = attributes.ToDictionary(kv => kv.Key,
                        kv => LayoutNamer.FormatPattern(kv.Value, parts, s.LayoutNames.SheetNumberFormat), StringComparer.Ordinal);
                    objects.Add(new TitleAttributesObject(new OwnedKey(seg.Id, OwnedRole.TitleAttributes), name, values));
                }
            }
        }

        // Tables.
        var rec = input.Reconcile;
        if (rec is not null && rec.Analyses.Count > 0)
        {
            foreach (var seg in stationSegs.Concat(input.ManualSegments))
            {
                TablePlacement? placement = null;
                bool keepOnly = false;
                if (!string.IsNullOrWhiteSpace(seg.TargetLayout))
                {
                    if (layoutNames.Contains(seg.TargetLayout))
                        placement = new TablePlacement(seg.TargetLayout, null, 1.0);
                    else
                    {
                        // Live check, not the record: a deleted layout took its table with it, and a
                        // locked plan for a missing object would be re-created (E1017 forbids that).
                        bool exists = input.LiveExists(new OwnedKey(seg.Id, OwnedRole.PaperTable));
                        problems.Add(new Problem(PipelineCodes.TargetLayoutMissing, Severity.Warning,
                            $"Sheet {seg.Label}: layout '{seg.TargetLayout}' is not in the drawing. "
                            + (exists ? "Its table stays where it is and is not updated." : "No table is placed; it is never re-created elsewhere."),
                            seg.Id, "Restore or rename the layout, or change the segment's target layout with SVDEFINE."));
                        if (!exists) continue;
                        placement = new TablePlacement(seg.TargetLayout, null, 1.0);
                        keepOnly = true;
                    }
                }
                else if (segmentLayout.TryGetValue(seg.Id, out var generated))
                    placement = new TablePlacement(generated, null, 1.0);
                else if (draw.TableAtEachSegment)
                    placement = new TablePlacement(null, ModelPoint(seg, boundaries, alignment, s, input.DrawingUnitsPerFoot), run.Views.ModelUnitsPerPaperUnit);
                if (placement is null) continue;

                var key = new OwnedKey(seg.Id, placement.LayoutName is null ? OwnedRole.ModelTable : OwnedRole.PaperTable);
                objects.Add(SegmentTable(key, seg, rec, input, placement, Fmt));
                if (keepOnly) locked.Add(key);
            }

            if (draw.SummaryTable)
            {
                TablePlacement? placement = null;
                if (s.ModelOutput.MasterTableLayout is { } ml)
                {
                    if (layoutNames.Contains(ml)) placement = new TablePlacement(ml, null, 1.0);
                    else problems.Add(new Problem(PipelineCodes.TargetLayoutMissing, Severity.Warning,
                        $"The master table layout '{ml}' is not in the drawing; the master table is not placed.",
                        Fix: "Create the layout or change modelOutput.masterTableLayout."));
                }
                else
                {
                    double gap = s.ModelOutput.ModelTableGapFeet * input.DrawingUnitsPerFoot;
                    placement = new TablePlacement(null, alignment.PointAt(alignment.StartStation, run.OffsetLeft - gap), run.Views.ModelUnitsPerPaperUnit);
                }
                if (placement is not null)
                {
                    var master = MasterTableBuilder.Build(rec, run.AreaUnit, Fmt, s.Report);
                    if (!master.Succeeded) throw new InvalidOperationException("Master table refused a reconciled result: " + string.Join(" | ", master.Problems.Select(p => p.Message)));
                    objects.Add(new TableObject(new OwnedKey(Guid.Empty, OwnedRole.MasterTable), master.Value, placement));
                }
            }
        }

        return new OutputPlan(objects, locked, renames, segmentLayoutHandles, problems);
    }

    private static OwnedObject SegmentTable(OwnedKey key, SegmentDef seg, ReconcileResult rec, OutputInputs input, TablePlacement placement, Func<double, string> fmt)
    {
        var run = input.Run;
        var s = input.Settings;
        if (run.Preset == ReportPreset.HcfcdSegment && input.Hcfcd is { } template && input.Formatter is { } formatter)
        {
            var values = new List<HcfcdMaterial>();
            foreach (var a in rec.Analyses)
            {
                var row = a.Rows.FirstOrDefault(r => r.SegmentId == seg.Id);
                if (row?.Impact is { } impact) values.Add(new HcfcdMaterial(a.Analysis.Id, a.Analysis.Name, impact, a.Analysis.CutFactor));
            }
            var created = template.CreateTable(seg.Id, run.Analyses, run.AreaUnit, formatter.Units, s.Report);
            if (!created.Succeeded) throw new InvalidOperationException("HCFCD template refused the run's materials: " + string.Join(" | ", created.Problems.Select(p => p.Message)));
            var cells = created.Value.Rows.Select(r => (IReadOnlyList<string>)r.Cells).ToList();
            var bind = template.Bind(cells, seg.Id, values, run.AreaUnit, formatter, s.Report);
            return new HcfcdTableObject(key, template, run.Analyses, values, run.AreaUnit, formatter, placement, OwnedSignatures.OfBound(bind.Bound));
        }

        var table = SegmentTableBuilder.Build(rec, seg.Id, run.AreaUnit, fmt, s.Report);
        if (!table.Succeeded) throw new InvalidOperationException("Segment table refused a reconciled result: " + string.Join(" | ", table.Problems.Select(p => p.Message)));
        return new TableObject(key, table.Value, placement);
    }

    private static Pt ModelPoint(SegmentDef seg, IReadOnlyDictionary<Guid, Boundary> boundaries, IAlignmentGeometry alignment, SheetVolSettings s, double unitsPerFoot)
    {
        double gap = s.ModelOutput.ModelTableGapFeet * unitsPerFoot;
        if (double.IsFinite(seg.StartStation)) return alignment.PointAt(seg.StartStation, seg.OffsetRight + gap);
        // Manual segment: no station; beside the polyline's highest-right vertex.
        var ring = boundaries.TryGetValue(seg.Id, out var b) ? b.Tessellated : [];
        if (ring.Count == 0) return new Pt(0, 0);
        double x = ring.Max(p => p.X), y = ring.Max(p => p.Y);
        return new Pt(x + gap, y);
    }

    private static LayoutNameParts Parts(RunDefinition run, int sheet, SegmentDef seg, Func<double, string> fmt) =>
        new(run.Name, sheet, seg.Label, fmt(seg.StartStation), fmt(seg.EndStation), run.AlignmentName);
}

/// <summary>Qualifiers of <see cref="OwnedRole.MatchMark"/> keys.</summary>
public static class MarkQualifiers
{
    /// <summary>Mark at the segment's start station.</summary>
    public const string Start = "start";
    /// <summary>Mark at the segment's end station.</summary>
    public const string End = "end";
}

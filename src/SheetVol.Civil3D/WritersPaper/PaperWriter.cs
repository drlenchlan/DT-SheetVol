// A3 work package (HANDOFF.md section 10, Wave 2). Owner: A3.
//
// The paper writer: layouts cloned from the template (P11), renamed by C3's two-pass plan
// (E307, E603), plan viewport view and clip (P12), annotation scale (P17), per-sheet table
// (P13), title-block attributes (6.7). A4 calls, in this order:
//
//   1. LoadTemplate()  outside the transaction, before any write: E601 fails here.
//   2. C3 ViewCalculator with template.PlanViewportWidth/Height; C2 SegmentTableBuilder.
//   3. Validate(tr, db, template, request)  inside the regen transaction, before any write:
//      E501, clip layer, annotation-scale name collision (E605), request sanity.
//   4. Apply(tr, db, template, request)  same transaction. Throws PaperWriterException
//      (SV-E690 or a row code) when the transaction must be aborted.
//   5. A4 commits, persists the handles in PaperApplyResult, disposes the template.
//
// Rules this file keeps: never clone the whole Layout object; Layout.Initialize() before
// any viewport is added (E614); the plan viewport (and any other viewport that is on in the
// template) is a new Viewport carrying the clone's properties, and the new layout's
// paper-space view is fitted to its content, because otherwise AutoCAD does not enter the
// viewport at first activation (MEASURED, see ReplaceWithNewViewport and FitOverallView);
// never set Viewport.On (refused in the creating transaction, P12, and not needed: R8);
// never delete a layout (E612); never change CTAB or CVPORT (E611); write nothing when the
// drawing already realizes the plan (E811).

using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Tracking;
using SheetVol.Core.Views;
using SheetVol.Core.Volumes;

namespace SheetVol.Civil3D.WritersPaper;

/// <summary>One object a run owns on paper, found by its owner tag.</summary>
public sealed record OwnedPaperObject(OwnedRole Role, Guid SegmentId, string Qualifier, string Handle, string LayoutName, string Signature);

/// <summary>Writes a run's layouts, viewports, clips, tables and title attributes. See the file header for the call order.</summary>
public sealed class PaperWriter
{
    private readonly PaperConfig _config;
    private readonly PlacementConfig _placement;
    private readonly LayoutNamer _namer;
    private readonly ViewportWriter _viewports;
    private readonly PaperTableWriter _tables;
    private readonly TitleBlockWriter _titles;

    /// <summary>Creates a writer.</summary>
    /// <param name="config">Paper settings.</param>
    /// <param name="placement">C2's placement config: the table anchor block name.</param>
    /// <param name="namer">C3's layout namer, built from the run's LayoutNameConfig.</param>
    public PaperWriter(PaperConfig config, PlacementConfig placement, LayoutNamer namer)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));
        _namer = namer ?? throw new ArgumentNullException(nameof(namer));
        _viewports = new ViewportWriter(config);
        _tables = new PaperTableWriter(config);
        _titles = new TitleBlockWriter(config);
    }

    /// <summary>Step 1: loads and validates the sheet template (E601). Writes nothing. Dispose the template after the regen.</summary>
    public Outcome<PaperTemplate> LoadTemplate() => PaperTemplate.Load(_config, _placement.TableAnchorBlock);

    /// <summary>Step 3: every check that needs the destination drawing, before any write. Errors mean: do not call Apply.</summary>
    public IReadOnlyList<Problem> Validate(Transaction tr, Database db, PaperTemplate template, PaperRunRequest run)
    {
        ArgumentNullException.ThrowIfNull(tr);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(run);
        var p = new List<Problem>(_config.Validate());
        if (run.RunId == Guid.Empty) p.Add(new Problem(PaperCodes.WriterFailed, Severity.Error, "Paper run request has an empty run id."));

        var seen = new HashSet<Guid>();
        var toAdd = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);   // annotation scales this run will add, by name
        foreach (var s in run.Sheets)
        {
            if (!seen.Add(s.SegmentId)) p.Add(new Problem(PaperCodes.WriterFailed, Severity.Error, $"Segment {s.SegmentId} is listed twice in the paper request.", s.SegmentId));
            var v = s.View;
            if (v is null) { p.Add(new Problem(PaperCodes.WriterFailed, Severity.Error, $"Segment {s.SegmentId} has no view.", s.SegmentId)); continue; }
            if (v.SegmentId != s.SegmentId) p.Add(new Problem(PaperCodes.WriterFailed, Severity.Error, $"Segment {s.SegmentId} carries the view of segment {v.SegmentId}.", s.SegmentId));
            if (!double.IsFinite(v.ModelCenter.X) || !double.IsFinite(v.ModelCenter.Y) || !double.IsFinite(v.TwistRadians))
                p.Add(new Problem(PaperCodes.WriterFailed, Severity.Error, $"Segment {s.SegmentId}: the view centre or twist is not a finite number.", s.SegmentId));
            if (!double.IsFinite(v.ModelUnitsPerPaperUnit) || v.ModelUnitsPerPaperUnit <= 0)
                p.Add(new Problem(ViewProblemCodes.InvalidScale, Severity.Error, $"Segment {s.SegmentId}: scale {v.ModelUnitsPerPaperUnit:R} model units per paper unit is not a positive number.", s.SegmentId));
            else if (_viewports.ScaleNameCollision(db, v.ModelUnitsPerPaperUnit, s.SegmentId) is { } collision)
                p.Add(collision);
            else if (_viewports.FindScale(db, v.ModelUnitsPerPaperUnit) is null)
            {
                string name = _config.AnnotationScaleName(v.ModelUnitsPerPaperUnit);
                if (toAdd.TryGetValue(name, out double other) && ViewportWriter.RelativeDifference(other, v.ModelUnitsPerPaperUnit) > _config.ScaleRelativeTolerance)
                    p.Add(new Problem(PaperCodes.AnnotationScale, Severity.Error,
                        $"Annotation scales 1:{other:R} and 1:{v.ModelUnitsPerPaperUnit:R} are both missing and would both be named '{name}'.", s.SegmentId,
                        "Use more decimals in annotationScaleNumberFormat, or round the scales."));
                else toAdd[name] = v.ModelUnitsPerPaperUnit;
            }
            if (_viewports.DesiredClip(v, new Pt(0, 0)) is { } relative
                && (relative.Count < 3 || relative.Any(q => !double.IsFinite(q.Point.X) || !double.IsFinite(q.Point.Y) || !double.IsFinite(q.Bulge))))
                p.Add(new Problem(PaperCodes.WriterFailed, Severity.Error, $"Segment {s.SegmentId}: the clip has {relative.Count} distinct vertices or a value that is not finite; at least 3 are needed.", s.SegmentId));
            if (s.Table is { } model)
            {
                p.AddRange(PaperTableWriter.CheckModel(model, s.SegmentId));
                p.AddRange(PaperTableWriter.CheckStyle(tr, db, model, s.SegmentId));
            }
            try { _ = _namer.FormatName(s.NameParts); }
            catch (FormatException ex) { p.Add(new Problem(PaperCodes.ConfigInvalid, Severity.Error, $"Layout name pattern: {ex.Message}", s.SegmentId)); }
        }

        if (_config.ClipLayer is { } clipLayer && run.Sheets.Any(s => s.View?.ClipInPaper is not null))
        {
            var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!layers.Has(clipLayer))
                p.Add(new Problem(PaperCodes.TemplateMissing, Severity.Error, $"Clip layer '{clipLayer}' named in paper config is not in this drawing.",
                    Fix: "Create the layer, or clear clipLayer to put clips on the plan viewport's layer."));
        }

        // A missing table style or layer repeats on every sheet; report each message once.
        return p.DistinctBy(x => (x.Code, x.Severity, x.Message)).ToList();
    }

    /// <summary>Step 4: renames, creates and updates. See the file header. Throws <see cref="PaperWriterException"/> for an abort.</summary>
    public PaperApplyResult Apply(Transaction tr, Database db, PaperTemplate template, PaperRunRequest run)
    {
        try
        {
            var validation = Validate(tr, db, template, run);
            if (validation.Any(x => x.Severity == Severity.Error))
                return new PaperApplyResult(false, [], [], null, validation, 0);
            using var scope = new WorkingDatabaseScope(db);
            return ApplyCore(tr, db, template, run, validation.ToList());
        }
        catch (PaperWriterException) { throw; }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            throw new PaperWriterException(PaperCodes.WriterFailed, $"Paper writer: AutoCAD {ex.ErrorStatus}: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new PaperWriterException(PaperCodes.WriterFailed, $"Paper writer: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    private PaperApplyResult ApplyCore(Transaction tr, Database db, PaperTemplate template, PaperRunRequest run, List<Problem> problems)
    {
        var writes = new WriteCounter();
        var lm = LayoutManager.Current;
        string key = _config.OwnerTagKey;

        // 1. Layouts this run already owns, by owner tag.
        var tagged = new List<(ObjectId Id, string Name, long Handle, Guid Segment)>();
        var allNames = new List<string>();
        var layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in layoutDict)
        {
            var lay = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            allNames.Add(lay.LayoutName);
            if (lay.ModelType) continue;
            var tag = OwnerTags.Read(tr, lay, key);
            if (tag is { Role: OwnedRole.Layout } && tag.RunId == run.RunId) tagged.Add((lay.ObjectId, lay.LayoutName, lay.Handle.Value, tag.SegmentId));
        }

        var orphans = new List<OrphanLayout>();
        var planned = run.Sheets.Select(s => s.SegmentId).ToHashSet();
        var existing = new Dictionary<Guid, (ObjectId Id, string Name)>();
        foreach (var s in run.Sheets)
        {
            var claims = tagged.Where(t => t.Segment == s.SegmentId).OrderBy(t => t.Handle).ToList();
            if (claims.Count == 0) continue;
            var keep = claims.FirstOrDefault(c => s.LayoutHandle is not null && string.Equals(c.Handle.ToString("X"), s.LayoutHandle, StringComparison.OrdinalIgnoreCase));
            if (keep.Id.IsNull) keep = claims[0];
            existing[s.SegmentId] = (keep.Id, keep.Name);
            foreach (var other in claims.Where(c => c.Id != keep.Id))
                orphans.Add(new OrphanLayout(s.SegmentId, other.Handle.ToString("X"), other.Name, $"also claims segment {s.SegmentId} (copied layout?); layout '{keep.Name}' is the one driven"));
        }
        foreach (var t in tagged.Where(t => !planned.Contains(t.Segment)))
            orphans.Add(new OrphanLayout(t.Segment, t.Handle.ToString("X"), t.Name, "its segment is no longer planned"));

        // 2. Names: C3's two-pass plan (E307, E603).
        var owned = run.Sheets.Select(s =>
        {
            string? current = existing.TryGetValue(s.SegmentId, out var x) ? x.Name : null;
            string requested = current is not null && !_config.RenumberLayouts ? current : _namer.FormatName(s.NameParts);
            return new OwnedLayout(s.SegmentId, current, requested);
        }).ToList();
        var ownedCurrent = new HashSet<string>(owned.Where(o => o.CurrentName is not null).Select(o => o.CurrentName!), StringComparer.OrdinalIgnoreCase);
        // Renumbering off: existing layouts are named first, so a new sheet's pattern name can
        // never take an existing layout's name and push it into a rename (stable order otherwise).
        var ordered = _config.RenumberLayouts ? owned : owned.OrderBy(o => o.CurrentName is null).ToList();
        var plan = _namer.PlanRenames(ordered, allNames.Where(n => !ownedCurrent.Contains(n)));
        problems.AddRange(plan.Problems);

        var renamed = new HashSet<Guid>();
        foreach (var r in plan.FirstPass) { lm.RenameLayout(r.From, r.To); writes.Add(); renamed.Add(r.SegmentId); }
        foreach (var r in plan.SecondPass) { lm.RenameLayout(r.From, r.To); writes.Add(); }

        // 3. Creates: Method S clone (P11) with Initialize() before the clone (E614).
        var fresh = new Dictionary<Guid, (ObjectId Layout, ObjectId PlanViewport)>();
        foreach (var c in plan.Creates)
            fresh[c.SegmentId] = CloneLayout(tr, template, c.Name, writes, problems, c.SegmentId);

        // 4. Content per sheet.
        var finalNames = plan.Assignments.ToDictionary(a => ordered[a.Index].SegmentId, a => a.Final);
        var results = new List<PaperSheetResult>();
        foreach (var s in run.Sheets)
        {
            var sheetProblems = new List<Problem>();
            bool layoutFresh = fresh.TryGetValue(s.SegmentId, out var made);
            ObjectId layoutId = layoutFresh ? made.Layout : existing[s.SegmentId].Id;
            string layoutName = finalNames[s.SegmentId];

            var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
            var layoutTag = new OwnerTag(run.RunId, s.SegmentId, OwnedRole.Layout, "", ContentSignatures.OfTexts([layoutName]), []);
            if (!OwnerTags.Matches(tr, layout, key, layoutTag))
            {
                layout = OwnerTags.OpenForWrite<Layout>(tr, layoutId);
                writes.Add();
                OwnerTags.Write(tr, layout, key, layoutTag);
            }
            var layoutChange = layoutFresh ? PaperChange.Created : renamed.Contains(s.SegmentId) ? PaperChange.Renamed : PaperChange.Unchanged;
            var block = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);

            // Plan viewport: the fresh clone, else the tagged one, else recreate it from the template.
            bool vpFresh = layoutFresh;
            ObjectId vpId = layoutFresh ? made.PlanViewport : FindTagged<Viewport>(tr, block, run.RunId, s.SegmentId, OwnedRole.Viewport, sheetProblems, layoutName);
            if (vpId.IsNull)
            {
                vpId = ClonePlanViewport(tr, template, block.ObjectId, writes, sheetProblems);
                vpFresh = true;
                sheetProblems.Add(new Problem(PaperCodes.OwnedObjectRecreated, Severity.Info,
                    $"Layout '{layoutName}' had no plan viewport of this run; one was cloned from the template.", s.SegmentId));
            }

            PaperChange vpChange, clipChange;
            ObjectId clipId = ObjectId.Null;
            var vpRead = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
            if (s.LockView && !vpFresh)
            {
                vpChange = PaperChange.Kept;
                clipChange = PaperChange.Kept;
                if (!vpRead.NonRectClipEntityId.IsNull && !vpRead.NonRectClipEntityId.IsErased) clipId = vpRead.NonRectClipEntityId;
                sheetProblems.Add(new Problem(PaperCodes.ViewportMoved, Severity.Info,
                    $"LockView is set for the sheet on layout '{layoutName}'; its viewport was left untouched.", s.SegmentId));
            }
            else
            {
                var anchor = ViewportWriter.AnchorFor(vpRead, vpFresh);
                if (!vpFresh && ViewportWriter.Distance(anchor, template.PlanViewportCenter) > _config.PaperTolerance)
                    sheetProblems.Add(new Problem(PaperCodes.ViewportMoved, Severity.Info,
                        $"The plan viewport on layout '{layoutName}' shows its view centre at paper ({anchor.X:0.######}, {anchor.Y:0.######}), not at the template's ({template.PlanViewportCenter.X:0.######}, {template.PlanViewportCenter.Y:0.######}); position and size kept, view re-applied.", s.SegmentId));
                vpChange = _viewports.ApplyView(tr, db, vpId, s.View, anchor, vpFresh, writes, sheetProblems, layoutName);
                clipChange = _viewports.ApplyClip(tr, db, vpId, s.View, anchor, vpFresh, run.RunId, writes, sheetProblems, layoutName, out clipId);
                var vpTag = new OwnerTag(run.RunId, s.SegmentId, OwnedRole.Viewport, "", ContentSignatures.Of(s.View), []);
                var vpNow = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
                if (!OwnerTags.Matches(tr, vpNow, key, vpTag))
                {
                    var w = OwnerTags.OpenForWrite<Viewport>(tr, vpId);
                    writes.Add();
                    OwnerTags.Write(tr, w, key, vpTag);
                    if (vpChange == PaperChange.Unchanged) vpChange = PaperChange.Updated;
                }
            }

            // Table at the layout's own anchor reference, else the template's anchor (E503).
            PaperChange tableChange = PaperChange.None;
            ObjectId tableId = ObjectId.Null;
            if (s.Table is { } model)
            {
                tableId = _tables.Find(tr, block, run.RunId, s.SegmentId, PaperTableWriter.Qualifier(model), sheetProblems);
                var anchorPt = tableId.IsNull ? LayoutAnchor(tr, block, sheetProblems, layoutName, s.SegmentId) ?? template.TableAnchor : default;
                tableChange = _tables.Apply(tr, db, block, ref tableId, model, anchorPt, layoutFresh, run.RunId, s.SegmentId, writes, sheetProblems, layoutName);
            }

            // Title attributes.
            var titleHandles = new List<string>();
            var values = _config.TitleAttributes.ToDictionary(kv => kv.Key, kv => LayoutNamer.FormatPattern(kv.Value, s.NameParts, _config.SheetNumberFormat), StringComparer.OrdinalIgnoreCase);
            var attrChange = _titles.Apply(tr, db, block, values, _placement.TableAnchorBlock, run.RunId, s.SegmentId, writes, sheetProblems, layoutName, titleHandles);

            problems.AddRange(sheetProblems);
            results.Add(new PaperSheetResult(s.SegmentId, layoutName, layout.Handle.ToString(), vpRead.Handle.ToString(),
                clipId.IsNull ? null : clipId.Handle.ToString(), tableId.IsNull ? null : tableId.Handle.ToString(), titleHandles,
                layoutChange, vpChange, clipChange, tableChange, attrChange, sheetProblems));
        }

        foreach (var o in orphans)
            problems.Add(new Problem(PaperCodes.OrphanLayout, Severity.Info, $"Layout '{o.Name}' ({o.Handle}) {o.Reason}; it is kept, never deleted.", o.SegmentId,
                "Delete it by hand or with SVPURGE if it is no longer wanted."));

        return new PaperApplyResult(true, results, orphans, plan, problems, writes.Count);
    }

    /// <summary>Method S (P11): CreateLayout, CopyFrom, Initialize, WblockCloneObjects of the paper-space entities without the overall viewport.</summary>
    internal (ObjectId Layout, ObjectId PlanViewport) CloneLayout(Transaction tr, PaperTemplate template, string name, WriteCounter writes, List<Problem> problems, Guid segmentId)
    {
        var lm = LayoutManager.Current;
        var layoutId = lm.CreateLayout(name);
        writes.Add();
        var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
        var blockBefore = layout.BlockTableRecordId;
        using var trS = template.Source.TransactionManager.StartTransaction();
        var source = (Layout)trS.GetObject(template.LayoutId, OpenMode.ForRead);
        layout.CopyFrom(source);                                   // plot settings only (P11)
        if (layout.BlockTableRecordId != blockBefore)
            throw new PaperWriterException(PaperCodes.WriterFailed, $"Layout.CopyFrom replaced the block of new layout '{name}'.");
        if (!string.Equals(layout.LayoutName, name, StringComparison.Ordinal)) layout.LayoutName = name;
        layout.Initialize();                                       // overall viewport first (E614)

        // Initialize gives the overall viewport only (MEASURED, P11). Anything else would be
        // AutoCAD's default floating viewport and must not compete with the plan viewport.
        var block = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        var listed = layout.GetViewports();
        ObjectId overall = listed is { Count: > 0 } ? listed[0] : ObjectId.Null;
        foreach (ObjectId id in block)
        {
            if (id == overall || id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Viewport extra) continue;
            extra.UpgradeOpen();
            extra.Erase();
            writes.Add();
            problems.Add(new Problem(PaperCodes.OtherViewports, Severity.Info, $"Layout.Initialize() on '{name}' added viewport {extra.Handle}; it was erased before the template was cloned.", segmentId));
        }

        using var map = new IdMapping();
        template.Source.WblockCloneObjects(new ObjectIdCollection(template.CloneIds.ToArray()), layout.BlockTableRecordId, map, DuplicateRecordCloning.Ignore, false);
        writes.Add();
        var planId = ReplaceWithNewViewport(tr, map[template.PlanViewportId].Value, writes, problems);
        foreach (var on in template.OtherViewportsOn) ReplaceWithNewViewport(tr, map[on].Value, writes, problems);   // E602: shown as in the template, never driven
        trS.Commit();
        FitOverallView(tr, layout, overall, writes);
        return (layoutId, planId);
    }

    /// <summary>
    /// Replaces the cloned plan viewport by a new Viewport that carries its properties, and
    /// erases the clone. MEASURED 2026-11-05 (tools/scripts/writersPaperNumbering.scr, cases R5
    /// and R6): a WblockClone of the template's plan viewport, in an initialized layout whose
    /// overall view covers it, was taken over at first activation (viewport number 1, the
    /// paper-space view, scale 0.444 instead of 0.02), and _.MSPACE found no model-space
    /// viewport. A new Viewport with the same layer, centre and size in the same place got
    /// number 2, kept its view, and _.MSPACE entered it. The clone stays the carrier of
    /// everything the viewport references (layers, frozen-layer list, visual style, clip
    /// entity), so the copy below only moves ids that already exist in this drawing.
    /// </summary>
    private static ObjectId ReplaceWithNewViewport(Transaction tr, ObjectId cloneId, WriteCounter writes, List<Problem> problems)
    {
        var clone = (Viewport)tr.GetObject(cloneId, OpenMode.ForWrite, false, true);
        var owner = (BlockTableRecord)tr.GetObject(clone.OwnerId, OpenMode.ForWrite);
        var vp = new Viewport();
        vp.SetDatabaseDefaults(clone.Database);
        vp.LayerId = clone.LayerId;
        vp.Color = clone.Color;
        vp.LinetypeId = clone.LinetypeId;
        vp.LinetypeScale = clone.LinetypeScale;
        vp.LineWeight = clone.LineWeight;
        vp.Transparency = clone.Transparency;
        vp.Visible = clone.Visible;
        if (!clone.Database.PlotStyleMode) vp.PlotStyleNameId = clone.PlotStyleNameId;
        vp.CenterPoint = clone.CenterPoint;
        vp.Width = clone.Width;
        vp.Height = clone.Height;
        var id = owner.AppendEntity(vp);
        tr.AddNewlyCreatedDBObject(vp, true);
        writes.Add();

        vp.ViewDirection = clone.ViewDirection;
        vp.ViewTarget = clone.ViewTarget;
        vp.TwistAngle = clone.TwistAngle;
        vp.CustomScale = clone.CustomScale;
        vp.ViewCenter = clone.ViewCenter;
        vp.ShadePlot = clone.ShadePlot;
        if (!clone.VisualStyleId.IsNull) vp.VisualStyleId = clone.VisualStyleId;
        vp.UcsPerViewport = clone.UcsPerViewport;
        var frozen = clone.GetFrozenLayers();
        if (frozen.Count > 0) vp.FreezeLayersInViewport(frozen.Cast<ObjectId>().ToList().GetEnumerator());
        var clipId = clone.NonRectClipEntityId;
        if (!clipId.IsNull && !clipId.IsErased)
        {
            // The new viewport gets its own copy of the template's clip. MEASURED (E802 check):
            // erasing a clipped viewport through the API erases its clip entity too, so sharing
            // the clone's clip would lose it when the clone is erased below.
            var original = (Entity)tr.GetObject(clipId, OpenMode.ForWrite, false, true);
            var copy = (Entity)original.Clone();
            owner.AppendEntity(copy);
            tr.AddNewlyCreatedDBObject(copy, true);
            vp.NonRectClipEntityId = copy.ObjectId;
            vp.NonRectClipOn = clone.NonRectClipOn;
            original.Erase();
        }
        var scale = PaperViewMath.SafeAnnotationScale(clone);
        if (scale is not null)
        {
            try { vp.AnnotationScale = scale; }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                problems.Add(new Problem(PaperCodes.AnnotationScale, Severity.Info,
                    $"Template viewport {clone.Handle}: its annotation scale '{scale.Name}' could not be assigned to the copy ({ex.ErrorStatus}); the copy has the drawing's default annotation scale."));
            }
        }
        vp.Locked = clone.Locked;
        clone.Erase();
        return id;
    }

    /// <summary>
    /// Points the new layout's overall (paper-space) viewport at the extents of what was cloned,
    /// as a first-activation zoom would. MEASURED 2026-11-05: Layout.Initialize() leaves the
    /// overall view at a fixed window (view centre (16.83, 12.00), view height 7.857 on this
    /// seat, with or without a paper size; S2's P11 layouts show the same numbers), and because
    /// the layout is already initialized, activation does not re-zoom it. A viewport outside
    /// that window gets no viewport number and _.MSPACE reports "There are no active Model space
    /// viewports" (writersPaperNumbering.scr: R2 not entered, R3 and R4 with this fit entered).
    /// Only the overall viewport's view changes; CenterPoint, Width and Height stay.
    /// </summary>
    private void FitOverallView(Transaction tr, Layout layout, ObjectId overallId, WriteCounter writes)
    {
        if (overallId.IsNull) return;
        var block = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        Extents3d? ext = null;
        foreach (ObjectId id in block)
        {
            if (id == overallId || id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity e) continue;
            Extents3d b;
            try { b = e.GeometricExtents; }
            catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }   // entities without extents (empty text) are skipped
            if (ext is Extents3d x) { x.AddExtents(b); ext = x; } else ext = b;
        }
        if (ext is not Extents3d box) return;
        var overall = (Viewport)tr.GetObject(overallId, OpenMode.ForWrite);
        writes.Add();
        double w = box.MaxPoint.X - box.MinPoint.X, h = box.MaxPoint.Y - box.MinPoint.Y;
        double aspect = overall.Width / overall.Height;
        double viewHeight = Math.Max(h, w / aspect) * _config.OverallViewMargin;
        if (!(viewHeight > 0) || !double.IsFinite(viewHeight)) return;
        overall.ViewDirection = Autodesk.AutoCAD.Geometry.Vector3d.ZAxis;
        overall.ViewTarget = Autodesk.AutoCAD.Geometry.Point3d.Origin;
        overall.TwistAngle = 0;
        overall.ViewCenter = new Autodesk.AutoCAD.Geometry.Point2d((box.MinPoint.X + box.MaxPoint.X) / 2, (box.MinPoint.Y + box.MaxPoint.Y) / 2);
        overall.CustomScale = overall.Height / viewHeight;
    }

    /// <summary>Clones the template's plan viewport alone into an existing layout (its owned viewport was erased).</summary>
    private static ObjectId ClonePlanViewport(Transaction tr, PaperTemplate template, ObjectId blockId, WriteCounter writes, List<Problem> problems)
    {
        using var map = new IdMapping();
        template.Source.WblockCloneObjects(new ObjectIdCollection([template.PlanViewportId]), blockId, map, DuplicateRecordCloning.Ignore, false);
        writes.Add();
        return ReplaceWithNewViewport(tr, map[template.PlanViewportId].Value, writes, problems);
    }

    /// <summary>The run's tagged object of a role on a layout, lowest handle first; Null when none.</summary>
    private ObjectId FindTagged<T>(Transaction tr, BlockTableRecord block, Guid runId, Guid segmentId, OwnedRole role, List<Problem> problems, string layoutName) where T : Entity
    {
        var found = new List<T>();
        var cls = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(T));
        foreach (ObjectId id in block)
        {
            if (id.IsErased || !id.ObjectClass.IsDerivedFrom(cls)) continue;
            var e = (T)tr.GetObject(id, OpenMode.ForRead);
            var tag = OwnerTags.Read(tr, e, _config.OwnerTagKey);
            if (tag is not null && tag.Role == role && tag.RunId == runId && tag.SegmentId == segmentId) found.Add(e);
        }
        if (found.Count == 0) return ObjectId.Null;
        var keep = found.OrderBy(e => e.Handle.Value).First();
        if (found.Count > 1)
            problems.Add(new Problem(PaperCodes.OtherViewports, Severity.Info,
                $"Layout '{layoutName}' has {found.Count} {role} objects of this sheet (copied?); {keep.Handle} is driven, the others are left alone.", segmentId));
        return keep.ObjectId;
    }

    /// <summary>The insertion point of the layout's own table anchor reference, or null (the template's anchor is then used, with a note when the layout has several).</summary>
    private Pt? LayoutAnchor(Transaction tr, BlockTableRecord block, List<Problem> problems, string layoutName, Guid segmentId)
    {
        var cls = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(BlockReference));
        var points = new List<Pt>();
        foreach (ObjectId id in block)
        {
            if (id.IsErased || !id.ObjectClass.IsDerivedFrom(cls)) continue;
            if (tr.GetObject(id, OpenMode.ForRead) is BlockReference br and not Table
                && string.Equals(PaperTemplate.EffectiveName(tr, br), _placement.TableAnchorBlock, StringComparison.OrdinalIgnoreCase))
                points.Add(new Pt(br.Position.X, br.Position.Y));
        }
        if (points.Count > 1)
            problems.Add(new Problem(PaperCodes.TableRecreated, Severity.Info,
                $"Layout '{layoutName}' has {points.Count} '{_placement.TableAnchorBlock}' references; the table is placed at the template's anchor instead.", segmentId));
        return points.Count == 1 ? points[0] : null;
    }

    /// <summary>
    /// Every object the run owns on paper, by owner tag, for A4's persistence and SVSTATUS.
    /// Read only.
    /// </summary>
    public static IReadOnlyList<OwnedPaperObject> FindOwned(Transaction tr, Database db, string ownerTagKey, Guid runId)
    {
        var list = new List<OwnedPaperObject>();
        var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in dict)
        {
            var lay = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            if (lay.ModelType) continue;
            if (OwnerTags.Read(tr, lay, ownerTagKey) is { } lt && lt.RunId == runId)
                list.Add(new OwnedPaperObject(lt.Role, lt.SegmentId, lt.Qualifier, lay.Handle.ToString(), lay.LayoutName, lt.Signature));
            var block = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in block)
            {
                if (id.IsErased) continue;
                var obj = tr.GetObject(id, OpenMode.ForRead);
                if (OwnerTags.Read(tr, obj, ownerTagKey) is { } t && t.RunId == runId)
                    list.Add(new OwnedPaperObject(t.Role, t.SegmentId, t.Qualifier, obj.Handle.ToString(), lay.LayoutName, t.Signature));
            }
        }
        return list;
    }

    /// <summary>LayoutManager works on the working database: switch to <c>db</c> for the call and restore.</summary>
    private sealed class WorkingDatabaseScope : IDisposable
    {
        private readonly Database? _previous;

        public WorkingDatabaseScope(Database db)
        {
            var current = HostApplicationServices.WorkingDatabase;
            if (!ReferenceEquals(current, db)) { _previous = current; HostApplicationServices.WorkingDatabase = db; }
        }

        public void Dispose()
        {
            if (_previous is not null) HostApplicationServices.WorkingDatabase = _previous;
        }
    }
}

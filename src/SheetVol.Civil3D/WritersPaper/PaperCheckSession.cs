// A3 work package. Owner: A3.
//
// SVWRITERSPAPER steps that run after the script changes the active space: REOPEN (second
// session, before any activation), ACTIVE (E614 after (setvar "CTAB" ...)), TRANS (the
// acedTrans oracle after _.MSPACE), E611 and E611CHECK (regen while a generated layout is
// current; the view survives _.PSPACE). The T18 plan is recomputed from the template on
// disk, so these steps also work in a fresh session.
//
// S2REPLICA and VPSTATE are the numbering probe behind PaperWriter.ReplaceWithNewViewport and
// FitOverallView (tools/scripts/writersPaperNumbering.scr, run after writersPaper.scr so the
// template exists). MEASURED 2026-11-05, 2026.2.3 Core Console, first activation then _.MSPACE:
//   R1  S2's P12A path and geometry                                   number 2, entered
//   R2  same path, viewport outside the window Initialize() leaves    number -1, not entered
//   R3  R2 with the overall view set to cover it                      number 2, entered
//   R4  as R3, only ViewCenter and CustomScale set                    number 2, entered
//   R5  raw Method S clone of the template's plan viewport, covered   not entered (number -1, or
//                                                                     taken over as number 1 with
//                                                                     the paper-space view)
//   R6  the writer's CloneLayout (clone replaced by a new Viewport)   number 2, entered
//   R8  new viewport, view covered, On never set                      number 2, entered
// So a headless-built viewport displays at first activation when it is a new Viewport and the
// overall view covers it; On does not matter. S2's P12 oracle worked because its viewport
// partly overlapped the window Initialize() leaves (R1 against R2).

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SheetVol.Core;
using SheetVol.Core.Tracking;
using SheetVol.Core.Views;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace SheetVol.Civil3D.WritersPaper;

public static partial class PaperCheckCommands
{
    private static PaperTemplate LoadCheckTemplate()
    {
        var loaded = CheckPlans.Writer().LoadTemplate();
        if (!loaded.Succeeded) throw new InvalidOperationException("template refused: " + string.Join(" | ", loaded.Problems.Select(p => $"{p.Code} {p.Message}")));
        return loaded.Value;
    }

    /// <summary>Second session: the saved T18 viewports as the opened document holds them, before any activation.</summary>
    private static void Reopen(Database db)
    {
        Say($"A3 REOPEN document '{db.Filename}' CTAB={AcApp.GetSystemVariable("CTAB")}");
        using var tr = db.TransactionManager.StartTransaction();
        foreach (var o in PaperWriter.FindOwned(tr, db, CheckPlans.Config().OwnerTagKey, CheckPlans.RunT18).Where(o => o.Role == OwnedRole.Viewport))
            Say($"A3 REOPEN {o.LayoutName} {VpLine(tr, (Viewport)tr.GetObject(Id(db, o.Handle), OpenMode.ForRead))}");
        tr.Commit();
    }

    /// <summary>The T18 sheet shown on the current layout, its plan viewport, the expected anchor and the template's other viewports; null when the layout is not a T18 sheet.</summary>
    private static (CheckSheet Sheet, ObjectId Viewport, ObjectId Layout, Pt Anchor, IReadOnlyList<OtherViewport> Others)? CurrentT18(Database db)
    {
        using var template = LoadCheckTemplate();
        var others = TemplateOthers(template);
        var plan = CheckPlans.T18(template.PlanViewportWidth, template.PlanViewportHeight);
        var lm = LayoutManager.Current;
        var layoutId = lm.GetLayoutId(lm.CurrentLayout);
        using var tr = db.TransactionManager.StartTransaction();
        var key = CheckPlans.Config().OwnerTagKey;
        var tag = OwnerTags.Read(tr, tr.GetObject(layoutId, OpenMode.ForRead), key);
        if (tag is null || tag.RunId != CheckPlans.RunT18) { tr.Commit(); return null; }
        var vp = PaperWriter.FindOwned(tr, db, key, CheckPlans.RunT18).Single(o => o.Role == OwnedRole.Viewport && o.SegmentId == tag.SegmentId);
        tr.Commit();
        return (plan.Single(s => s.Segment.Id == tag.SegmentId), Id(db, vp.Handle), layoutId, template.PlanViewportCenter, others);
    }

    /// <summary>E614 after activation: the plan viewport was not made the overall viewport, kept its view, and no default viewport was added.</summary>
    private static void Active(Database db)
    {
        string ctab = (string)AcApp.GetSystemVariable("CTAB");
        var found = CurrentT18(db);
        if (found is null) { Expect($"E614 {ctab}", false, "the current layout is not a T18 sheet"); return; }
        var (sheet, vpId, layoutId, anchor, others) = found.Value;
        using var tr = db.TransactionManager.StartTransaction();
        var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
        var block = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        var vps = block.Cast<ObjectId>().Where(id => tr.GetObject(id, OpenMode.ForRead) is Viewport).ToList();
        foreach (var id in vps) Say($"A3 ACTIVE {ctab} {VpLine(tr, (Viewport)tr.GetObject(id, OpenMode.ForRead))}");
        var listed = layout.GetViewports();
        var overall = listed is { Count: > 0 } ? listed[0] : ObjectId.Null;
        var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
        var spec = sheet.View;
        double targetErr = Dist(new Pt(vp.ViewTarget.X, vp.ViewTarget.Y), spec.ModelCenter);
        double twistErr = PaperViewMath.AngleDifference(vp.TwistAngle, spec.TwistRadians);
        double scaleErr = Math.Abs(vp.CustomScale * spec.ModelUnitsPerPaperUnit - 1);
        var lands = PaperViewMath.ModelToPaper(vp, spec.ModelCenter);
        Expect($"E614 {ctab} after activation", !overall.IsNull && overall != vpId && vps.Count == 2 + others.Count && targetErr <= 1e-9 && twistErr <= 1e-12 && scaleErr <= 1e-12 && Dist(lands, anchor) <= 1e-9,
            $"CTAB={ctab} GetViewports()[0]={(overall.IsNull ? "none" : overall.Handle.ToString())} plan={vp.Handle} (on={vp.On} number={vp.Number}); {vps.Count} viewport entities (overall + plan + {others.Count} other expected); " +
            $"target |d|={E(targetErr)} twist |d|={E(twistErr)} scale rel={E(scaleErr)}; model centre lands at {P(lands)} (anchor {P(anchor)})");
        CheckOthers($"E614 {ctab} after activation", tr, vps.Where(v => v != overall && v != vpId).ToList(), others, activated: true);
        tr.Commit();
    }

    /// <summary>The acedTrans oracle (WCS to DCS to PSDCS, AutoCAD's own transform) against C3's math, when the plan viewport is the active one.</summary>
    private static void Trans(Database db)
    {
        string ctab = (string)AcApp.GetSystemVariable("CTAB");
        int cvport = Convert.ToInt32(AcApp.GetSystemVariable("CVPORT"));
        var found = CurrentT18(db);
        if (found is null) { Expect($"TRANS {ctab}", false, "the current layout is not a T18 sheet"); return; }
        var (sheet, vpId, _, anchor, _) = found.Value;
        int number;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var v = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
            number = v.Number;
            Say($"A3 TRANS {ctab} CVPORT={cvport} {VpLine(tr, v)}");
            tr.Commit();
        }
        if (cvport != number || number < 2)
        {
            Expect($"P12 oracle {ctab}", false, $"_.MSPACE did not enter the plan viewport (CVPORT={cvport}, plan viewport number {number}): the E614 activation fix regressed; acedTrans not run");
            return;
        }
        var spec = sheet.View;
        double k = spec.ModelUnitsPerPaperUnit;
        var pts = new (string, Pt)[]
        {
            ("centre", spec.ModelCenter),
            ("start", sheet.Alignment.PointAt(sheet.Segment.StartStation, 0)),
            ("end", sheet.Alignment.PointAt(sheet.Segment.EndStation, 0)),
            ("start-left", sheet.Alignment.PointAt(sheet.Segment.StartStation, sheet.Segment.OffsetLeft)),
            ("end-right", sheet.Alignment.PointAt(sheet.Segment.EndStation, sheet.Segment.OffsetRight)),
        };
        double maxErr = 0;
        var parts = new List<string>();
        foreach (var (label, w) in pts)
        {
            var dcs = Trans(new Point3d(w.X, w.Y, 0), 0, 2);
            var ps = dcs is null ? null : Trans(dcs.Value, 2, 3);
            if (ps is null) { Expect($"TRANS {ctab} {label}", false, "acedTrans did not return RTNORM"); return; }
            var c3 = ViewMath.ToPaper(w, spec.ModelCenter, spec.TwistRadians, k);
            var expected = new Pt(anchor.X + c3.X, anchor.Y + c3.Y);
            var observed = new Pt(ps.Value.X, ps.Value.Y);
            double err = Dist(observed, expected);
            maxErr = Math.Max(maxErr, err);
            parts.Add($"{label} {P(w)} -> acedTrans {P(observed)} C3 {P(expected)}");
        }
        Expect($"P12 oracle {ctab}", maxErr <= 1e-6, $"max |acedTrans - C3| = {E(maxErr)}: {string.Join("; ", parts)}");
    }

    /// <summary>Probe: S2's P12A path and geometry (R1) against the same path with this check's geometry (R2).</summary>
    private static void S2Replica(Database db)
    {
        var lm = LayoutManager.Current;
        foreach (var (name, center, h) in new[] { ("R1", new Point3d(8.5, 5.5, 0), 7.0), ("R2", new Point3d(7, 4.5, 0), 5.0), ("R3", new Point3d(7, 4.5, 0), 5.0), ("R4", new Point3d(7, 4.5, 0), 5.0) })
        {
            ObjectId lid, vid;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                lid = lm.CreateLayout(name);
                var lay = (Layout)tr.GetObject(lid, OpenMode.ForWrite);
                lay.Initialize();
                if (name is "R3" or "R4")
                {
                    // Overall view over paper x 1..13, y 2..7: R3 sets the full view, R4 only ViewCenter and CustomScale.
                    var ov = (Viewport)tr.GetObject(lay.GetViewports()[0], OpenMode.ForWrite);
                    if (name == "R3") { ov.ViewDirection = Vector3d.ZAxis; ov.ViewTarget = Point3d.Origin; ov.TwistAngle = 0; }
                    ov.ViewCenter = new Point2d(7, 4.5);
                    ov.CustomScale = ov.Height / Math.Max(5.0, 12.0 * ov.Height / ov.Width);
                    Say($"A3 S2REPLICA {name} overall set: {VpLine(tr, ov)}");
                }
                tr.Commit();
            }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var btr = (BlockTableRecord)tr.GetObject(((Layout)tr.GetObject(lid, OpenMode.ForRead)).BlockTableRecordId, OpenMode.ForWrite);
                var vp = new Viewport();
                vp.SetDatabaseDefaults(db);
                vp.Layer = "SV-VPORT";
                vp.CenterPoint = center; vp.Width = 12; vp.Height = h;
                vid = btr.AppendEntity(vp);
                tr.AddNewlyCreatedDBObject(vp, true);
                vp.ViewDirection = Vector3d.ZAxis; vp.ViewTarget = new Point3d(5000, 3000, 0); vp.ViewCenter = Point2d.Origin; vp.TwistAngle = Math.PI / 6; vp.CustomScale = 0.02;
                tr.Commit();
            }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var vp = (Viewport)tr.GetObject(vid, OpenMode.ForWrite);
                try { vp.On = true; } catch (Autodesk.AutoCAD.Runtime.Exception ex) { Say($"A3 S2REPLICA {name} On threw {ex.ErrorStatus}"); }
                Say($"A3 S2REPLICA {name}: {VpLine(tr, vp)}");
                tr.Commit();
            }
        }

        // R5: a raw Method S clone (CreateLayout, CopyFrom, Initialize, WblockCloneObjects without the
        // overall viewport), overall view set as in R4, On in a later transaction.
        if (!File.Exists(CheckPlans.TemplatePath)) { Say($"A3 S2REPLICA R5 skipped: no {CheckPlans.TemplatePath} (run writersPaper.scr first)"); return; }
        using var template = LoadCheckTemplate();
        ObjectId planId;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var lid = lm.CreateLayout("R5");
            var lay = (Layout)tr.GetObject(lid, OpenMode.ForWrite);
            using var trS = template.Source.TransactionManager.StartTransaction();
            lay.CopyFrom((Layout)trS.GetObject(template.LayoutId, OpenMode.ForRead));
            lay.Initialize();
            using var map = new IdMapping();
            template.Source.WblockCloneObjects(new ObjectIdCollection(template.CloneIds.ToArray()), lay.BlockTableRecordId, map, DuplicateRecordCloning.Ignore, false);
            planId = map[template.PlanViewportId].Value;
            var ov = (Viewport)tr.GetObject(lay.GetViewports()[0], OpenMode.ForWrite);
            ov.ViewCenter = new Point2d(7, 4.5);
            ov.CustomScale = ov.Height / Math.Max(5.0, 12.0 * ov.Height / ov.Width);
            trS.Commit();
            tr.Commit();
        }
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var vp = (Viewport)tr.GetObject(planId, OpenMode.ForWrite);
            try { vp.On = true; } catch (Autodesk.AutoCAD.Runtime.Exception ex) { Say($"A3 S2REPLICA R5 On threw {ex.ErrorStatus}"); }
            Say($"A3 S2REPLICA R5: {VpLine(tr, vp)}");
            tr.Commit();
        }

        // R6: the writer's CloneLayout (clone, plan viewport replaced by a new Viewport, overall view fitted); no On.
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var vpId = CheckPlans.Writer().CloneLayout(tr, template, "R6", new WriteCounter(), [], Guid.Empty).PlanViewport;
            Say($"A3 S2REPLICA R6: {VpLine(tr, (Viewport)tr.GetObject(vpId, OpenMode.ForRead))}");
            tr.Commit();
        }

        // R8: R4's path (new viewport, overall view covering it) without On.
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var lid = lm.CreateLayout("R8");
            var lay = (Layout)tr.GetObject(lid, OpenMode.ForWrite);
            lay.Initialize();
            var ov = (Viewport)tr.GetObject(lay.GetViewports()[0], OpenMode.ForWrite);
            ov.ViewCenter = new Point2d(7, 4.5);
            ov.CustomScale = ov.Height / Math.Max(5.0, 12.0 * ov.Height / ov.Width);
            var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForWrite);
            var vp = new Viewport();
            vp.SetDatabaseDefaults(db);
            vp.Layer = "SV-VPORT";
            vp.CenterPoint = new Point3d(7, 4.5, 0); vp.Width = 12; vp.Height = 5;
            btr.AppendEntity(vp);
            tr.AddNewlyCreatedDBObject(vp, true);
            vp.ViewDirection = Vector3d.ZAxis; vp.ViewTarget = new Point3d(5000, 3000, 0); vp.ViewCenter = Point2d.Origin; vp.CustomScale = 0.02;
            Say($"A3 S2REPLICA R8: {VpLine(tr, vp)}");
            tr.Commit();
        }
    }

    /// <summary>Probe: every viewport on the current layout with CVPORT.</summary>
    private static void PrintViewportState(Database db)
    {
        string ctab = (string)AcApp.GetSystemVariable("CTAB");
        using var tr = db.TransactionManager.StartTransaction();
        var lay = (Layout)tr.GetObject(LayoutManager.Current.GetLayoutId(ctab), OpenMode.ForRead);
        foreach (ObjectId id in (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead))
            if (tr.GetObject(id, OpenMode.ForRead) is Viewport v) Say($"A3 VPSTATE {ctab} CVPORT={AcApp.GetSystemVariable("CVPORT")} {VpLine(tr, v)}");
        tr.Commit();
    }

    /// <summary>E611: regen while a generated layout is current; CTAB, CVPORT and TILEMODE stay.</summary>
    private static void E611(Database db)
    {
        string ctab0 = (string)AcApp.GetSystemVariable("CTAB");
        object cvport0 = AcApp.GetSystemVariable("CVPORT"), tile0 = AcApp.GetSystemVariable("TILEMODE");
        using var template = LoadCheckTemplate();
        var sheet = CheckPlans.OnS("E6", 1, CheckPlans.SegE6, 1100, template.PlanViewportWidth, template.PlanViewportHeight, 2, 4.0);
        var r = Run(db, CheckPlans.Writer(), template, new PaperRunRequest(CheckPlans.RunE6, [sheet.Request()]), out var c, out _);
        PrintResult("E611 regen while its layout is current", r, c);
        string ctab1 = (string)AcApp.GetSystemVariable("CTAB");
        object cvport1 = AcApp.GetSystemVariable("CVPORT"), tile1 = AcApp.GetSystemVariable("TILEMODE");
        var v = Read(db, Id(db, r.Sheets[0].ViewportHandle));
        Expect("E611 active space kept", r.Succeeded && ctab0 == ctab1 && Equals(cvport0, cvport1) && Equals(tile0, tile1) && Dist(v.Target, sheet.View.ModelCenter) <= 1e-9,
            $"CTAB {ctab0} -> {ctab1}, CVPORT {cvport0} -> {cvport1}, TILEMODE {tile0} -> {tile1}; target {P(v.Target)} (plan {P(sheet.View.ModelCenter)})");
    }

    /// <summary>After _.PSPACE: the view written while its layout was current survived.</summary>
    private static void E611Check(Database db)
    {
        using var template = LoadCheckTemplate();
        var sheet = CheckPlans.OnS("E6", 1, CheckPlans.SegE6, 1100, template.PlanViewportWidth, template.PlanViewportHeight, 2, 4.0);
        ObjectId vpId;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            vpId = Id(db, PaperWriter.FindOwned(tr, db, CheckPlans.Config().OwnerTagKey, CheckPlans.RunE6).Single(o => o.Role == OwnedRole.Viewport).Handle);
            tr.Commit();
        }
        var v = Read(db, vpId);
        Expect("E611 view kept after PSPACE", Dist(v.Target, sheet.View.ModelCenter) <= 1e-9,
            $"CTAB={AcApp.GetSystemVariable("CTAB")} CVPORT={AcApp.GetSystemVariable("CVPORT")}; {v.Line}");
    }
}

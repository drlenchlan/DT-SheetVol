// S2 probes P12 (create and update a viewport without making its layout current; the
// model-to-paper mapping, numerically) and P17 (annotation scale on a viewport; adding a
// missing scale). HANDOFF.md 5.2, 5.3, 6.6, E604, E605, E614.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using S = SheetVol.Civil3D.SelfTest.ProbeBSettings;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace SheetVol.Civil3D.SelfTest;

public static partial class ProbeBCommands
{
    private sealed record VpCase(string Layout, Point3d Target, Point2d ViewCenter, double Twist, double Scale, bool TwoPhase, string Note, bool Init = true);

    private static Point2d RotPlus(Point3d p, double a)
        => new(Math.Cos(a) * p.X - Math.Sin(a) * p.Y, Math.Sin(a) * p.X + Math.Cos(a) * p.Y);

    private static readonly VpCase[] P12Cases =
    {
        new("SV-P12A", S.ModelPoint, Point2d.Origin, S.Twist, S.Scale, true,
            "6.6 form: target=model point, viewCenter=(0,0), twist 30, created locked then updated, clipped"),
        new("SV-P12B", Point3d.Origin, new Point2d(S.ModelPoint.X, S.ModelPoint.Y), S.Twist, S.Scale, false,
            "claim test: target=(0,0,0), viewCenter=model point's WCS numbers, twist 30"),
        new("SV-P12B2", Point3d.Origin, RotPlus(S.ModelPoint, S.Twist), S.Twist, S.Scale, false,
            "claim test: target=(0,0,0), viewCenter=model point rotated by +twist, twist 30"),
        new("SV-P12C", S.ModelPoint, Point2d.Origin, 0.0, S.Scale, false,
            "control: target=model point, viewCenter=(0,0), twist 0"),
        new("SV-P12N", S.ModelPoint, Point2d.Origin, S.Twist, S.Scale, false,
            "negative control: as 6.6 form but the layout is NOT initialized", Init: false),
    };

    // ======================================================================
    // P12 setup: everything without making a layout current
    // ======================================================================

    [CommandMethod("SVPROBEB_P12", CommandFlags.Modal)]
    public static void P12() => Run("P12", (doc, db) =>
    {
        var lm = LayoutManager.Current;
        string startLayout = lm.CurrentLayout;
        Say($"P12 CTAB before={AcApp.GetSystemVariable("CTAB")} LayoutManager.CurrentLayout='{startLayout}' TILEMODE={AcApp.GetSystemVariable("TILEMODE")} LAYOUTCREATEVIEWPORT={AcApp.GetSystemVariable("LAYOUTCREATEVIEWPORT")}");

        // The known model points, drawn so the saved drawing can be eyeballed.
        using (var tr = db.TransactionManager.StartTransaction())
        {
            EnsureLayer(tr, db, S.VportLayer);
            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
            foreach (var p in new[] { S.ModelPoint, S.ModelPointEast })
            {
                var pt = new DBPoint(p);
                pt.SetDatabaseDefaults(db);
                ms.AppendEntity(pt);
                tr.AddNewlyCreatedDBObject(pt, true);
            }
            var ln = new Line(S.ModelPoint, S.ModelPointEast);
            ln.SetDatabaseDefaults(db);
            ms.AppendEntity(ln);
            tr.AddNewlyCreatedDBObject(ln, true);
            tr.Commit();
        }

        foreach (var c in P12Cases)
        {
            Say($"P12 --- {c.Layout}: {c.Note}");
            var layId = CreateLayoutHeadless(db, c.Layout, c.Init, $"P12 {c.Layout}");
            ObjectId vpId;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var btr = LayoutBlock(tr, layId, OpenMode.ForWrite);
                Say($"P12 {c.Layout} viewports in new layout block: {CountViewports(tr, btr)}; GetViewports().Count={((Layout)tr.GetObject(layId, OpenMode.ForRead)).GetViewports().Count}");
                var vp = new Viewport();
                vp.SetDatabaseDefaults(db);
                vp.Layer = S.VportLayer;
                vp.CenterPoint = S.VpPaperCenter;
                vp.Width = S.VpWidth;
                vp.Height = S.VpHeight;
                btr.AppendEntity(vp);
                tr.AddNewlyCreatedDBObject(vp, true);
                vp.ViewDirection = Vector3d.ZAxis;
                if (c.TwoPhase)
                {
                    // Phase 1: a deliberately different view, locked (E604).
                    vp.ViewTarget = Point3d.Origin;
                    vp.ViewCenter = Point2d.Origin;
                    vp.TwistAngle = 0.0;
                    vp.CustomScale = 1.0 / 100.0;
                    vp.Locked = true;
                }
                else
                {
                    ApplyView(vp, c);
                }
                Try($"P12 {c.Layout} create: vp.On=true", () => vp.On = true);
                vpId = vp.ObjectId;
                tr.Commit();
            }
            Say($"P12 {c.Layout} CTAB after create={AcApp.GetSystemVariable("CTAB")}");
            ReadBackVp(db, vpId, $"P12 {c.Layout} readback after create");
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForWrite);
                bool ok = Try($"P12 {c.Layout} second transaction: vp.On=true", () => vp.On = true);
                Say($"P12 {c.Layout} second transaction: On={vp.On} (setter {(ok ? "accepted" : "refused")})");
                tr.Commit();
            }

            if (c.TwoPhase)
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForWrite);
                    var t0 = vp.ViewTarget;
                    Try($"P12 {c.Layout} update while Locked: set ViewTarget", () => vp.ViewTarget = c.Target);
                    Say($"P12 {c.Layout} update while Locked: ViewTarget now {P(vp.ViewTarget)} (was {P(t0)}, changed={vp.ViewTarget != t0})");
                    vp.Locked = false;
                    ApplyView(vp, c);

                    // Non-rectangular clip: a closed polyline with one arc edge, inside the viewport.
                    var btr = (BlockTableRecord)tr.GetObject(vp.OwnerId, OpenMode.ForWrite);
                    var pl = new Polyline();
                    pl.SetDatabaseDefaults(db);
                    pl.Layer = S.VportLayer;
                    double cx = S.VpPaperCenter.X, cy = S.VpPaperCenter.Y;
                    pl.AddVertexAt(0, new Point2d(cx - 5.0, cy - 3.0), 0, 0, 0);
                    pl.AddVertexAt(1, new Point2d(cx + 5.0, cy - 3.0), 0.4, 0, 0);   // arc edge to the next vertex
                    pl.AddVertexAt(2, new Point2d(cx + 5.0, cy + 3.0), 0, 0, 0);
                    pl.AddVertexAt(3, new Point2d(cx - 4.0, cy + 2.0), 0, 0, 0);
                    pl.Closed = true;
                    btr.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    Say($"P12 {c.Layout} before clip: center={P(vp.CenterPoint)} w={F(vp.Width)} h={F(vp.Height)}");
                    Try($"P12 {c.Layout} set NonRectClipEntityId", () => vp.NonRectClipEntityId = pl.ObjectId);
                    Try($"P12 {c.Layout} set NonRectClipOn=true", () => vp.NonRectClipOn = true);
                    Say($"P12 {c.Layout} after clip: center={P(vp.CenterPoint)} w={F(vp.Width)} h={F(vp.Height)} clipOn={vp.NonRectClipOn} clipId==pline={vp.NonRectClipEntityId == pl.ObjectId}");
                    vp.Locked = true;
                    Try($"P12 {c.Layout} update: vp.On=true", () => vp.On = true);
                    tr.Commit();
                }
                ReadBackVp(db, vpId, $"P12 {c.Layout} readback after update");

                // The clip moved the viewport on paper; how did AutoCAD compensate ViewCenter?
                // H1 (DCS is paper-aligned): dViewCenter = dPaper / scale.
                // H2 (WCS offset): dViewCenter = R(-twist)(dPaper / scale).
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
                    var dP = vp.CenterPoint - S.VpPaperCenter;
                    var dC = vp.ViewCenter - c.ViewCenter;
                    var h1 = new Vector2d(dP.X / c.Scale, dP.Y / c.Scale);
                    var h2 = h1.RotateBy(-c.Twist);
                    Say($"P12 {c.Layout} clip compensation: dPaperCenter=({F(dP.X)}, {F(dP.Y)}) dViewCenter=({F(dC.X)}, {F(dC.Y)}) " +
                        $"H1 expects ({F6(h1.X)}, {F6(h1.Y)}) match={(dC - h1).Length < 1e-6} | H2 expects ({F6(h2.X)}, {F6(h2.Y)}) match={(dC - h2).Length < 1e-6}");
                    tr.Commit();
                }
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
                Say($"P12 {c.Layout} set vs read: target d={F(vp.ViewTarget.DistanceTo(c.Target))} viewCenter d={F(vp.ViewCenter.GetDistanceTo(c.ViewCenter))} " +
                    $"twist d={F(vp.TwistAngle - c.Twist)} customScale d={F(vp.CustomScale - c.Scale)} viewHeight - h/scale = {F(vp.ViewHeight - vp.Height / c.Scale)}");
                PrintPredictions(c.Layout, vp);
                tr.Commit();
            }
        }
        Say($"P12 CTAB at end={AcApp.GetSystemVariable("CTAB")} LayoutManager.CurrentLayout='{lm.CurrentLayout}' (unchanged={lm.CurrentLayout == startLayout})");
    });

    private static void ApplyView(Viewport vp, VpCase c)
    {
        vp.ViewDirection = Vector3d.ZAxis;
        vp.ViewTarget = c.Target;
        vp.ViewCenter = c.ViewCenter;
        vp.TwistAngle = c.Twist;
        vp.CustomScale = c.Scale;
    }

    private static void ReadBackVp(Autodesk.AutoCAD.DatabaseServices.Database db, ObjectId vpId, string tag)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
        Say($"{tag}: {VpSummary(vp)}");
        tr.Commit();
    }

    /// <summary>Predictions from the viewport's read-back values (a clip can move the
    /// viewport and its ViewCenter, so the values set are not the values to test).</summary>
    private static void PrintPredictions(string tag, Viewport vp)
    {
        foreach (var (name, w) in new[] { ("M", S.ModelPoint), ("M+100E", S.ModelPointEast) })
        {
            var h1 = PredictDcs(w, vp.ViewTarget, vp.ViewCenter, vp.TwistAngle, vp.CustomScale, vp.CenterPoint);
            var h1n = PredictDcs(w, vp.ViewTarget, vp.ViewCenter, vp.TwistAngle, vp.CustomScale, vp.CenterPoint, -1.0);
            var h2 = PredictWcsCenter(w, vp.ViewTarget, vp.ViewCenter, vp.TwistAngle, vp.CustomScale, vp.CenterPoint);
            Say($"P12 {tag} predict {name} {P(w)} -> H1 DCS(+twist) {P6(h1)} | H1' DCS(-twist) {P6(h1n)} | H2 WCS-center {P6(h2)}");
        }
    }

    // ======================================================================
    // P12 oracle: run after the script makes a P12 layout current (CTAB). Compares the
    // predictions with acedTrans WCS -> DCS -> PSDCS, AutoCAD's own transform.
    // ======================================================================

    [CommandMethod("SVPROBEB_P12TRANS", CommandFlags.Modal)]
    public static void P12Trans() => Run("P12TRANS", (doc, db) =>
    {
        var lm = LayoutManager.Current;
        string cur = lm.CurrentLayout;
        var c = P12Cases.FirstOrDefault(k => k.Layout == cur);
        Say($"P12T layout='{cur}' CTAB={AcApp.GetSystemVariable("CTAB")} TILEMODE={AcApp.GetSystemVariable("TILEMODE")} CVPORT={AcApp.GetSystemVariable("CVPORT")}");

        ObjectId vpId = ObjectId.Null;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var lay = (Layout)tr.GetObject(lm.GetLayoutId(cur), OpenMode.ForRead);
            Say($"P12T '{cur}' GetViewports().Count={lay.GetViewports().Count}");
            var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Viewport vp) continue;
                Say($"P12T '{cur}' after activation: Viewport {vp.Handle} {VpSummary(vp)}");
                if (vp.Layer == S.VportLayer) vpId = id;
            }
            tr.Commit();
        }
        if (c is null) { Say("P12T not a P12 layout: viewport list only"); return; }
        if (vpId.IsNull) { Say($"P12T no viewport on {S.VportLayer}"); return; }

        using (var tr = db.TransactionManager.StartTransaction())
        {
            var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForWrite);
            if (!vp.On) Try("P12T vp.On=true after activation", () => vp.On = true);
            Say($"P12T after activation, set vs read: target d={F(vp.ViewTarget.DistanceTo(c.Target))} viewCenter d={F(vp.ViewCenter.GetDistanceTo(c.ViewCenter))} " +
                $"twist d={F(vp.TwistAngle - c.Twist)} customScale d={F(vp.CustomScale - c.Scale)} On={vp.On} Number={vp.Number}");
            tr.Commit();
        }

        int number;
        Point3d paperCenter, target;
        Point2d viewCenter;
        double twist, scale;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
            number = vp.Number;
            paperCenter = vp.CenterPoint;
            target = vp.ViewTarget;
            viewCenter = vp.ViewCenter;
            twist = vp.TwistAngle;
            scale = vp.CustomScale;
            Say($"P12T read-back used for predictions: {VpSummary(vp)}");
            tr.Commit();
        }
        if (Convert.ToInt32(AcApp.GetSystemVariable("CVPORT")) != number)
            Try($"P12T set CVPORT={number}", () => AcApp.SetSystemVariable("CVPORT", number));
        int cvport = Convert.ToInt32(AcApp.GetSystemVariable("CVPORT"));
        Say($"P12T CVPORT now {cvport} (probe viewport Number={number})");
        if (cvport != number || number < 2) { Say("P12T ORACLE INVALID: the probe viewport is not the active model-space viewport"); return; }

        foreach (var (name, w) in new[] { ("M", S.ModelPoint), ("M+100E", S.ModelPointEast) })
        {
            var dcs = Trans(w, 0, 2);
            if (dcs is null) continue;
            var ps = Trans(dcs.Value, 2, 3);
            if (ps is null) continue;
            var obs = new Point2d(ps.Value.X, ps.Value.Y);
            var h1 = PredictDcs(w, target, viewCenter, twist, scale, paperCenter);
            var h1n = PredictDcs(w, target, viewCenter, twist, scale, paperCenter, -1.0);
            var h2 = PredictWcsCenter(w, target, viewCenter, twist, scale, paperCenter);
            Say($"P12T {c.Layout} {name}: WCS {P(w)} -> DCS {P(dcs.Value)} -> paper {P6(obs)}");
            Say($"P12T {c.Layout} {name}: |obs-H1|={F6(obs.GetDistanceTo(h1))} |obs-H1'|={F6(obs.GetDistanceTo(h1n))} |obs-H2|={F6(obs.GetDistanceTo(h2))}  " +
                $"H1 match={obs.GetDistanceTo(h1) < 1e-6} H1' match={obs.GetDistanceTo(h1n) < 1e-6} H2 match={obs.GetDistanceTo(h2) < 1e-6}");
        }
    });

    // ======================================================================
    // P17
    // ======================================================================

    private static AnnotationScale? FindScaleByRatio(ObjectContextCollection occ, double ratio)
    {
        foreach (ObjectContext oc in occ)
            if (oc is AnnotationScale a && a.PaperUnits > 0 && Math.Abs(a.DrawingUnits / a.PaperUnits - ratio) < 1e-9) return a;
        return null;
    }

    [CommandMethod("SVPROBEB_P17", CommandFlags.Modal)]
    public static void P17() => Run("P17", (doc, db) =>
    {
        var occ = db.ObjectContextManager.GetContextCollection("ACDB_ANNOTATIONSCALES");
        var all = new List<string>();
        foreach (ObjectContext oc in occ)
            if (oc is AnnotationScale a) all.Add($"'{a.Name}' {F(a.PaperUnits)}:{F(a.DrawingUnits)}");
        Say($"P17 scale list: {all.Count} scales; CANNOSCALE='{AcApp.GetSystemVariable("CANNOSCALE")}'; current context='{occ.CurrentContext?.Name}'");
        Say($"P17 scales: {string.Join("; ", all)}");

        var existing = FindScaleByRatio(occ, 1.0 / S.Scale);
        double existingRatio = 1.0 / S.Scale;
        if (existing is null)
        {
            foreach (ObjectContext oc in occ)
                if (oc is AnnotationScale a && a.PaperUnits > 0 && a.DrawingUnits / a.PaperUnits > 1.0) { existing = a; existingRatio = a.DrawingUnits / a.PaperUnits; break; }
        }
        Say($"P17 existing scale for the assign test: {(existing is null ? "none" : $"'{existing.Name}' ratio {F(existingRatio)}")}");
        var missingBefore = FindScaleByRatio(occ, S.MissingScaleRatio);
        Say($"P17 ratio {F(S.MissingScaleRatio)} before add: {(missingBefore is null ? "absent" : $"present as '{missingBefore.Name}'")}; HasContext('{S.MissingScaleName}')={occ.HasContext(S.MissingScaleName)}");

        var layId = CreateLayoutHeadless(db, "SV-P17", initialize: true, "P17");
        ObjectId vpE, vpM;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            EnsureLayer(tr, db, S.VportLayer);
            var btr = LayoutBlock(tr, layId, OpenMode.ForWrite);
            vpE = AddVp(tr, db, btr, new Point3d(4.5, 5.5, 0));
            vpM = AddVp(tr, db, btr, new Point3d(12.5, 5.5, 0));
            tr.Commit();
        }
        ReadBackVp(db, vpE, "P17 vpE as created");

        // Existing scale: CustomScale first, then AnnotationScale.
        if (existing is not null)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var vp = (Viewport)tr.GetObject(vpE, OpenMode.ForWrite);
            vp.CustomScale = 1.0 / existingRatio;
            Try($"P17 vpE AnnotationScale='{existing.Name}'", () => vp.AnnotationScale = existing);
            Say($"P17 vpE after assign: annoScale='{vp.AnnotationScale?.Name}' customScale={F(vp.CustomScale)}");
            tr.Commit();
        }

        // Missing scale: add it, then assign (E605).
        if (missingBefore is null)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var asc = new AnnotationScale { Name = S.MissingScaleName, PaperUnits = 1.0, DrawingUnits = S.MissingScaleRatio };
            Try($"P17 AddContext('{S.MissingScaleName}')", () => occ.AddContext(asc));
            tr.Commit();
        }
        var added = FindScaleByRatio(occ, S.MissingScaleRatio);
        Say($"P17 after add: ratio {F(S.MissingScaleRatio)} {(added is null ? "absent" : $"present as '{added.Name}' scale={F(added.Scale)}")}; HasContext={occ.HasContext(S.MissingScaleName)}; list count={occ.Cast<ObjectContext>().Count()}");
        if (added is not null)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var vp = (Viewport)tr.GetObject(vpM, OpenMode.ForWrite);
            vp.CustomScale = 1.0 / 20.0;   // deliberately not 1/37
            Try($"P17 vpM AnnotationScale='{added.Name}' while CustomScale=1/20", () => vp.AnnotationScale = added);
            Say($"P17 vpM order 1 (CustomScale 1/20, then annoScale 1:37): annoScale='{vp.AnnotationScale?.Name}' customScale={F(vp.CustomScale)} (1/37={F(1.0 / 37.0)})");
            vp.CustomScale = 1.0 / S.MissingScaleRatio;
            Say($"P17 vpM order 2 (then CustomScale 1/37): annoScale='{vp.AnnotationScale?.Name}' customScale={F(vp.CustomScale)}");
            tr.Commit();
        }
        ReadBackVp(db, vpE, "P17 vpE readback");
        ReadBackVp(db, vpM, "P17 vpM readback");
    });

    private static ObjectId AddVp(Transaction tr, Autodesk.AutoCAD.DatabaseServices.Database db, BlockTableRecord btr, Point3d center)
    {
        var vp = new Viewport();
        vp.SetDatabaseDefaults(db);
        vp.Layer = S.VportLayer;
        vp.CenterPoint = center;
        vp.Width = 6.0;
        vp.Height = 6.0;
        btr.AppendEntity(vp);
        tr.AddNewlyCreatedDBObject(vp, true);
        vp.ViewDirection = Vector3d.ZAxis;
        vp.ViewTarget = S.ModelPoint;
        vp.ViewCenter = Point2d.Origin;
        return vp.ObjectId;
    }
}

// A3 work package. Owner: A3.
//
// SVWRITERSPAPER BUILD, the edge-case rows: E602 (largest viewport), E604, E608, E609,
// E503, E502, E504, E505 and E615 on one sheet; E605; E307 and E612; E613.

using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SheetVol.Core;
using SheetVol.Core.Views;

namespace SheetVol.Civil3D.WritersPaper;

public static partial class PaperCheckCommands
{
    /// <summary>E602: a template layout with no SV-VPORT viewport drives its largest floating viewport; the other is untouched.</summary>
    private static void E602Largest(Database db)
    {
        var writer = CheckPlans.Writer(CheckPlans.Config(CheckPlans.LayoutLargest));
        var loaded = writer.LoadTemplate();
        if (!loaded.Succeeded) { Expect("E602 largest", false, string.Join(" | ", loaded.Problems.Select(p => $"{p.Code} {p.Message}"))); return; }
        using var template = loaded.Value;
        foreach (var n in template.Notes) Say($"A3 E602 template {n.Code} {n.Severity}: {n.Message}");
        var sheet = CheckPlans.OnS("E602", 1, CheckPlans.SegE602, 0, template.PlanViewportWidth, template.PlanViewportHeight, analyses: 0, clip: ClipMode.None, offset: 50);
        var r = Run(db, writer, template, new PaperRunRequest(CheckPlans.RunE602, [sheet.Request()]), out var c, out _);
        PrintResult("E602 largest", r, c);
        using var tr = db.TransactionManager.StartTransaction();
        var plan = (Viewport)tr.GetObject(Id(db, r.Sheets[0].ViewportHandle), OpenMode.ForRead);
        var block = (BlockTableRecord)tr.GetObject(plan.OwnerId, OpenMode.ForRead);
        var layout = (Layout)tr.GetObject(Id(db, r.Sheets[0].LayoutHandle), OpenMode.ForRead);
        var overall = layout.GetViewports()[0];
        var others = block.Cast<ObjectId>().Where(id => id != overall && id != plan.ObjectId && tr.GetObject(id, OpenMode.ForRead) is Viewport)
            .Select(id => (Viewport)tr.GetObject(id, OpenMode.ForRead)).ToList();
        bool planOk = !template.PlanChosenByLayer && Math.Abs(plan.Width - 10) < 1e-12 && Math.Abs(plan.Height - 4) < 1e-12 && Dist(new Pt(plan.ViewTarget.X, plan.ViewTarget.Y), sheet.View.ModelCenter) <= 1e-9;
        bool othersOk = others.Count == 1 && Dist(new Pt(others[0].ViewTarget.X, others[0].ViewTarget.Y), new Pt(999, 999)) <= 1e-9 && Math.Abs(others[0].CustomScale - 1.0 / 500.0) <= 1e-15;
        Expect("E602 largest viewport driven, other untouched", r.Succeeded && planOk && othersOk,
            $"driven {VpLine(tr, plan)} | other {(others.Count == 1 ? VpLine(tr, others[0]) : $"{others.Count} found")}");
        tr.Commit();
    }

    /// <summary>
    /// A template whose plan viewport is clipped: with clip mode None the new viewport keeps a live
    /// copy of the template's clip; with Boundary the copy is reshaped to the band in place.
    /// </summary>
    private static void E602Clipped(Database db)
    {
        var writer = CheckPlans.Writer(CheckPlans.Config(CheckPlans.LayoutClipped));
        var loaded = writer.LoadTemplate();
        if (!loaded.Succeeded) { Expect("E602 clipped template", false, string.Join(" | ", loaded.Problems.Select(p => $"{p.Code} {p.Message}"))); return; }
        using var template = loaded.Value;
        double w = template.PlanViewportWidth, h = template.PlanViewportHeight;
        var none = CheckPlans.OnS("CLIP", 1, CheckPlans.SegClippedNone, 0, w, h, analyses: 0, clip: ClipMode.None);
        var band = CheckPlans.OnS("CLIP", 2, CheckPlans.SegClippedBand, 500, w, h, analyses: 0);
        var r = Run(db, writer, template, new PaperRunRequest(CheckPlans.RunClipped, [none.Request(), band.Request()]), out var c, out _);
        PrintResult("E602 clipped template", r, c);
        var a = template.PlanViewportCenter;
        using var tr = db.TransactionManager.StartTransaction();
        var v1 = (Viewport)tr.GetObject(Id(db, r.Sheets[0].ViewportHandle), OpenMode.ForRead);
        var v2 = (Viewport)tr.GetObject(Id(db, r.Sheets[1].ViewportHandle), OpenMode.ForRead);
        var l1 = PaperViewMath.ModelToPaper(v1, none.View.ModelCenter);
        var l2 = PaperViewMath.ModelToPaper(v2, band.View.ModelCenter);
        Expect("E602 clipped template, clip mode None", r.Succeeded && ClipVertexCount(tr, v1) == 6 && Dist(l1, a) <= 1e-9,
            $"template plan viewport {F(w)}x{F(h)} at {P(a)}; {VpLine(tr, v1)}; model centre lands at {P(l1)}");
        Expect("E602 clipped template, clip mode Boundary", r.Succeeded && ClipVertexCount(tr, v2) == band.View.ClipInPaper!.Count && Dist(l2, a) <= 1e-9,
            $"{VpLine(tr, v2)}; model centre lands at {P(l2)}; plan clip {band.View.ClipInPaper!.Count} vertices");
        tr.Commit();
    }

    /// <summary>E604, E608 (move, same-view regen, re-apply, clipped ViewCenter kept, LockView), E609, E615, E503, E502, E504, E505 on sheet E6-1.</summary>
    private static void E6Rows(Database db, PaperWriter writer, PaperTemplate template)
    {
        double w = template.PlanViewportWidth, h = template.PlanViewportHeight;
        CheckSheet At(double start, int analyses = 2, double rate = 1.0, bool onlySecond = false)
            => CheckPlans.OnS("E6", 1, CheckPlans.SegE6, start, w, h, analyses, rate, onlySecond: onlySecond);
        PaperRunRequest Req(CheckSheet s, bool lockView = false) => new(CheckPlans.RunE6, [s.Request(lockView)]);
        var a0 = template.PlanViewportCenter;

        var s1 = At(500);
        var r1 = Run(db, writer, template, Req(s1), out var c1, out _);
        PrintResult("E6 create", r1, c1);
        var vpId = Id(db, r1.Sheets[0].ViewportHandle);
        var e615 = r1.Sheets[0].Problems.FirstOrDefault(p => p.Code == PaperCodes.TitleTagMissing);
        Expect("E615 E6-1", r1.Succeeded && e615 is not null && r1.Sheets[0].Attributes == PaperChange.Created, $"{e615?.Code} {e615?.Severity}: {e615?.Message}");

        // E604: a locked viewport is updated through the API and stays locked.
        Modify<Viewport>(db, vpId, v => v.Locked = true);
        var s2 = At(600);
        var r2 = Run(db, writer, template, Req(s2), out var c2, out _);
        PrintResult("E604 locked viewport", r2, c2);
        var v2 = Read(db, vpId);
        Expect("E604", r2.Sheets[0].Viewport == PaperChange.Updated && v2.Locked && Dist(v2.Target, s2.View.ModelCenter) <= 1e-9 && r2.Problems.Any(p => p.Code == PaperCodes.ViewportLocked),
            $"locked after={v2.Locked} target {P(v2.Target)} (plan {P(s2.View.ModelCenter)}); {r2.Problems.FirstOrDefault(p => p.Code == PaperCodes.ViewportLocked)?.Message}");
        Modify<Viewport>(db, vpId, v => v.Locked = false);

        // E608: the user moves the viewport on paper (MOVE takes the viewport and its clip).
        var d = new Vector3d(1.5, -0.5, 0);
        var before = Read(db, vpId);
        string moveNote = MoveViewport(db, vpId, d);
        var moved = Read(db, vpId);
        Say($"A3 E608 move by ({F(d.X)}, {F(d.Y)}): {moveNote}; anchor {P(before.Anchor)} -> {P(moved.Anchor)}; centre {P(before.Center)} -> {P(moved.Center)}; ViewCenter {P(before.ViewCenter)} -> {P(moved.ViewCenter)}");
        var expectedAnchor = new Pt(a0.X + d.X, a0.Y + d.Y);

        var r3 = Run(db, writer, template, Req(s2), out var c3, out _);
        PrintResult("E608 same view after move", r3, c3);
        var v3 = Read(db, vpId);
        Expect("E608 position kept, nothing re-applied when the view matches", c3.None && r3.Writes == 0 && Dist(v3.Center, moved.Center) == 0 && Dist(v3.ViewCenter, moved.ViewCenter) == 0,
            $"dbEvents={c3.Appended}/{c3.Modified}/{c3.Erased} centre {P(v3.Center)} ViewCenter {P(v3.ViewCenter)} (clipped viewport keeps its ViewCenter: {P(moved.ViewCenter)})");

        var s4 = At(700);
        var r4 = Run(db, writer, template, Req(s4), out var c4, out _);
        PrintResult("E608 new view after move", r4, c4);
        var v4 = Read(db, vpId);
        var lands = Lands(db, vpId, s4.View.ModelCenter);
        double clipErr = ClipError(db, vpId, s4.View, expectedAnchor);
        Expect("E608 view re-applied at the moved position", r4.Sheets[0].Viewport == PaperChange.Updated && Dist(lands, expectedAnchor) <= 1e-9 && clipErr <= 1e-9
                && Dist(v4.Target, s4.View.ModelCenter) <= 1e-9 && r4.Problems.Any(p => p.Code == PaperCodes.ViewportMoved),
            $"model centre lands at {P(lands)} (moved anchor {P(expectedAnchor)}, template {P(a0)}); clip |d|={E(clipErr)}; centre {P(v4.Center)} {F(v4.Width)}x{F(v4.Height)} ViewCenter {P(v4.ViewCenter)}; {r4.Problems.FirstOrDefault(p => p.Code == PaperCodes.ViewportMoved)?.Message}");

        // E608 LockView: even the view update is skipped.
        var s5 = At(800);
        var r5 = Run(db, writer, template, Req(s5, lockView: true), out var c5, out _);
        PrintResult("E608 LockView", r5, c5);
        var v5 = Read(db, vpId);
        Expect("E608 LockView", r5.Sheets[0].Viewport == PaperChange.Kept && Dist(v5.Target, s4.View.ModelCenter) <= 1e-9,
            $"viewport {r5.Sheets[0].Viewport}, target {P(v5.Target)} (still the previous {P(s4.View.ModelCenter)}, not {P(s5.View.ModelCenter)}); viewport and clip unopened, table and attributes followed the new stations");

        // E609: a per-viewport layer freeze survives a view update (update in place).
        ObjectId frozen;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            frozen = ((LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead))[CheckPlans.FrozenLayer];
            var v = (Viewport)tr.GetObject(vpId, OpenMode.ForWrite);
            v.FreezeLayersInViewport(new List<ObjectId> { frozen }.GetEnumerator());
            tr.Commit();
        }
        var s6 = At(900);
        var r6 = Run(db, writer, template, Req(s6), out var c6, out _);
        PrintResult("E609 frozen layer", r6, c6);
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var v = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
            var list = v.GetFrozenLayers().Cast<ObjectId>().ToList();
            Expect("E609", r6.Sheets[0].ViewportHandle == r1.Sheets[0].ViewportHandle && list.Contains(frozen) && Dist(new Pt(v.ViewTarget.X, v.ViewTarget.Y), s6.View.ModelCenter) <= 1e-9,
                $"viewport {v.Handle} (created as {r1.Sheets[0].ViewportHandle}) frozen in viewport: [{string.Join(", ", list.Select(id => ((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name))}] target {P(v.ViewTarget)}");
            tr.Commit();
        }

        // E503: the user erases the table; it is recreated at the anchor.
        var tableId = Id(db, r6.Sheets[0].TableHandle);
        Modify<Table>(db, tableId, t => t.Erase());
        var r7 = Run(db, writer, template, Req(s6), out var c7, out _);
        PrintResult("E503 table erased", r7, c7);
        tableId = Id(db, r7.Sheets[0].TableHandle);
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var t = (Table)tr.GetObject(tableId, OpenMode.ForRead);
            var (match, detail) = TableMatches(t, s6.Table!);
            Expect("E503", r7.Sheets[0].Table == PaperChange.Created && match && Dist(new Pt(t.Position.X, t.Position.Y), template.TableAnchor) <= 1e-9 && r7.Problems.Any(p => p.Code == PaperCodes.TableRecreated),
                $"new table {t.Handle} at {P(t.Position)} (anchor {P(template.TableAnchor)}); {detail}");
            tr.Commit();
        }

        // E502: the user moves the table and gives it another style; only text updates.
        var offset = new Vector3d(0.5, -0.25, 0);
        ObjectId standard;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            standard = ((DBDictionary)tr.GetObject(db.TableStyleDictionaryId, OpenMode.ForRead)).GetAt("Standard");
            var t = (Table)tr.GetObject(tableId, OpenMode.ForWrite);
            t.TransformBy(Matrix3d.Displacement(offset));
            t.TableStyle = standard;
            tr.Commit();
        }
        var s8 = At(900, rate: 2.0);
        var r8 = Run(db, writer, template, Req(s8), out var c8, out _);
        PrintResult("E502 table moved and restyled", r8, c8);
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var t = (Table)tr.GetObject(tableId, OpenMode.ForRead);
            var (match, detail) = TableMatches(t, s8.Table!);
            var expectedPos = new Pt(template.TableAnchor.X + offset.X, template.TableAnchor.Y + offset.Y);
            Expect("E502", r8.Sheets[0].Table == PaperChange.Updated && r8.Sheets[0].TableHandle == tableId.Handle.ToString() && match
                    && Dist(new Pt(t.Position.X, t.Position.Y), expectedPos) <= 1e-9 && t.TableStyle == standard,
                $"table {t.Handle} at {P(t.Position)} (user put it at {P(expectedPos)}), style '{((TableStyle)tr.GetObject(t.TableStyle, OpenMode.ForRead)).Name}'; {detail}");
            tr.Commit();
        }

        // E504: table on a locked layer, then on a frozen layer.
        string tableLayer = s8.Table!.Style.Layer;
        SetLayer(db, tableLayer, locked: true, frozen: false);
        var s9 = At(900, rate: 3.0);
        var r9 = Run(db, writer, template, Req(s9), out var c9, out _);
        PrintResult("E504 locked layer", r9, c9);
        var (m9, d9) = TableCheck(db, tableId, s9.Table!);
        var (l9, _) = LayerState(db, tableLayer);
        Expect("E504 locked", r9.Sheets[0].Table == PaperChange.Updated && m9 && l9 && r9.Problems.Any(p => p.Code == PaperCodes.TableOnLockedLayer),
            $"layer '{tableLayer}' locked after={l9}; {d9}; {r9.Problems.FirstOrDefault(p => p.Code == PaperCodes.TableOnLockedLayer)?.Message}");
        SetLayer(db, tableLayer, locked: false, frozen: true);
        var s10 = At(900, rate: 4.0);
        var r10 = Run(db, writer, template, Req(s10), out var c10, out _);
        PrintResult("E504 frozen layer", r10, c10);
        var (m10, d10) = TableCheck(db, tableId, s10.Table!);
        var (l10, f10) = LayerState(db, tableLayer);
        Expect("E504 frozen", r10.Sheets[0].Table == PaperChange.Updated && m10 && f10 && !l10, $"layer '{tableLayer}' frozen after={f10}; {d10}");
        SetLayer(db, tableLayer, locked: false, frozen: false);

        // E505: an analysis removed, then added back: the table shrinks and grows by one block in place.
        var s11 = At(900, analyses: 1, rate: 4.0);
        var r11 = Run(db, writer, template, Req(s11), out var c11, out _);
        PrintResult("E505 analysis removed", r11, c11);
        var (m11, d11) = TableCheck(db, tableId, s11.Table!);
        Expect("E505 shrink", r11.Sheets[0].TableHandle == tableId.Handle.ToString() && m11 && r11.Problems.Any(p => p.Code == PaperCodes.TableResized),
            $"{d11}; {r11.Problems.FirstOrDefault(p => p.Code == PaperCodes.TableResized)?.Message}");
        var s12 = At(900, analyses: 2, rate: 4.0);
        var r12 = Run(db, writer, template, Req(s12), out var c12, out _);
        PrintResult("E505 analysis added", r12, c12);
        var (m12, d12) = TableCheck(db, tableId, s12.Table!);
        Expect("E505 grow", r12.Sheets[0].TableHandle == tableId.Handle.ToString() && m12 && r12.Problems.Any(p => p.Code == PaperCodes.TableResized),
            $"{d12}; {r12.Problems.FirstOrDefault(p => p.Code == PaperCodes.TableResized)?.Message}");

        // E802 (owned object erased): the user erases the plan viewport; it is recreated from the
        // template at the template's position with a new clip, and no stray clip polyline is left.
        string? clipBefore = r12.Sheets[0].ClipHandle;
        Modify<Viewport>(db, vpId, v => v.Erase());
        bool clipGoneWithViewport;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            clipGoneWithViewport = Id(db, clipBefore).IsErased;
            tr.Commit();
        }
        Say($"A3 E802 erasing the clipped viewport {vpId.Handle} through the API also erased its clip polyline {clipBefore}: {clipGoneWithViewport}");
        var r13 = Run(db, writer, template, Req(s12), out var c13, out _);
        PrintResult("E802 plan viewport erased", r13, c13);
        var newVp = Id(db, r13.Sheets[0].ViewportHandle);
        var lands13 = Lands(db, newVp, s12.View.ModelCenter);
        double clip13 = ClipError(db, newVp, s12.View, a0);
        int clips;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            clips = PaperWriter.FindOwned(tr, db, CheckPlans.Config().OwnerTagKey, CheckPlans.RunE6).Count(o => o.Role == Core.Tracking.OwnedRole.Clip);
            tr.Commit();
        }
        Expect("E802 plan viewport recreated", r13.Sheets[0].Viewport == PaperChange.Created && newVp != vpId && r13.Sheets[0].Clip == PaperChange.Created && clips == 1
                && Dist(lands13, a0) <= 1e-9 && clip13 <= 1e-9 && r13.Problems.Any(p => p.Code == PaperCodes.OwnedObjectRecreated),
            $"viewport {r13.Sheets[0].ViewportHandle} (was {vpId.Handle}), clip {r13.Sheets[0].ClipHandle} (was {clipBefore}), owned clip polylines {clips}; model centre lands at {P(lands13)} (template anchor {P(a0)}); clip |d|={E(clip13)}");

        // E505 with rows inserted ahead of existing ones: the first analysis removed, then added back.
        var s14 = At(900, analyses: 2, rate: 4.0, onlySecond: true);
        var r14 = Run(db, writer, template, Req(s14), out var c14, out _);
        PrintResult("E505 first analysis removed", r14, c14);
        var (m14, d14) = TableCheck(db, tableId, s14.Table!);
        Expect("E505 shrink at the front", r14.Sheets[0].TableHandle == tableId.Handle.ToString() && m14, d14);
        var s15 = At(900, analyses: 2, rate: 4.0);
        var r15 = Run(db, writer, template, Req(s15), out var c15, out _);
        PrintResult("E505 first analysis added back", r15, c15);
        var (m15, d15) = TableCheck(db, tableId, s15.Table!);
        Expect("E505 grow at the front", r15.Sheets[0].TableHandle == tableId.Handle.ToString() && m15 && r15.Problems.Any(p => p.Code == PaperCodes.TableResized && p.Severity == Severity.Info),
            $"{d15}; {r15.Problems.FirstOrDefault(p => p.Code == PaperCodes.TableResized)?.Message}");
    }

    /// <summary>
    /// E608 with a clip that is not centred on the view (P12 caveat): the clip refit moves the
    /// viewport and its ViewCenter; a same-view regen keeps that ViewCenter; changed views keep the
    /// model centre on the anchor while AutoCAD refits the updated clip.
    /// </summary>
    private static void E608ClipRows(Database db, PaperWriter writer, PaperTemplate template)
    {
        double w = template.PlanViewportWidth, h = template.PlanViewportHeight;
        var a = template.PlanViewportCenter;
        var steps = new (string Label, CheckSheet Sheet)[]
        {
            ("create, clip off-centre", CheckPlans.Asymmetric("E608C", 1, CheckPlans.SegE608C, 0, 400, 50, w, h)),
            ("same view again", CheckPlans.Asymmetric("E608C", 1, CheckPlans.SegE608C, 0, 400, 50, w, h)),
            ("new view, clip centred", CheckPlans.Asymmetric("E608C", 1, CheckPlans.SegE608C, 100, 400, 50, w, h)),
            ("new view, clip off-centre again", CheckPlans.Asymmetric("E608C", 1, CheckPlans.SegE608C, 0, 400, 50, w, h)),
        };
        VpState? previous = null;
        foreach (var (label, sheet) in steps)
        {
            var r = Run(db, writer, template, new PaperRunRequest(CheckPlans.RunE608C, [sheet.Request()]), out var c, out _);
            PrintResult($"E608 clip {label}", r, c);
            var vpId = Id(db, r.Sheets[0].ViewportHandle);
            var v = Read(db, vpId);
            var lands = Lands(db, vpId, sheet.View.ModelCenter);
            double clipErr = ClipError(db, vpId, sheet.View, a);
            bool ok = Dist(lands, a) <= 1e-9 && clipErr <= 1e-9;
            if (label == "same view again" && previous is not null)
                ok &= c.None && r.Writes == 0 && Dist(v.ViewCenter, previous.ViewCenter) == 0;
            Expect($"E608 clip {label}", r.Succeeded && ok,
                $"model centre lands at {P(lands)} (anchor {P(a)}); clip |d|={E(clipErr)}; viewport centre {P(v.Center)} {F(v.Width)}x{F(v.Height)} ViewCenter {P(v.ViewCenter)}" +
                (previous is null ? "" : $" (was {P(previous.ViewCenter)})") + $"; dbEvents={c.Appended}/{c.Modified}/{c.Erased}");
            previous = v;
        }
    }

    /// <summary>E605: a missing annotation scale is added and assigned, CustomScale set too; the next regen finds it by ratio.</summary>
    private static void E605Rows(Database db, PaperWriter writer, PaperTemplate template)
    {
        var sheet = CheckPlans.OnS("E605", 1, CheckPlans.SegE605, 0, template.PlanViewportWidth, template.PlanViewportHeight,
            analyses: 0, scale: 37, clip: ClipMode.None, offset: 80, length: 400);
        int before = ScaleCount(db);
        var r = Run(db, writer, template, new PaperRunRequest(CheckPlans.RunE605, [sheet.Request()]), out var c, out _);
        PrintResult("E605 missing scale", r, c);
        int after = ScaleCount(db);
        var v = Read(db, Id(db, r.Sheets[0].ViewportHandle));
        Expect("E605", after == before + 1 && Math.Abs(v.AnnoRatio - 37) < 1e-9 && Math.Abs(v.CustomScale * 37 - 1) < 1e-12
                && r.Problems.Any(p => p.Code == PaperCodes.AnnotationScale),
            $"scales {before} -> {after}; viewport annotation scale '{v.AnnoName}' ratio {F(v.AnnoRatio)}, customScale {F(v.CustomScale)} (1/37 = {F(1.0 / 37)}); {r.Problems.FirstOrDefault(p => p.Code == PaperCodes.AnnotationScale)?.Message}");
        var again = Run(db, writer, template, new PaperRunRequest(CheckPlans.RunE605, [sheet.Request()]), out var c2, out _);
        Expect("E605 found by ratio on the next regen", c2.None && again.Writes == 0 && ScaleCount(db) == after, $"scales {ScaleCount(db)}, dbEvents={c2.Appended}/{c2.Modified}/{c2.Erased}");
    }

    /// <summary>E307 (two-pass rename on insert; handles kept), rename rollback on abort, E612 (orphan listed, not deleted).</summary>
    private static void E307Rows(Database db, PaperWriter writer, PaperTemplate template)
    {
        double w = template.PlanViewportWidth, h = template.PlanViewportHeight;
        var a = CheckPlans.OnS("REN", 1, CheckPlans.SegRenA, 0, w, h, analyses: 0);
        var b = CheckPlans.OnS("REN", 2, CheckPlans.SegRenB, 1000, w, h, analyses: 0);
        var r1 = Run(db, writer, template, new PaperRunRequest(CheckPlans.RunRen, [a.Request(), b.Request()]), out var c1, out _);
        PrintResult("E307 two sheets", r1, c1);

        var c = CheckPlans.OnS("REN", 2, CheckPlans.SegRenC, 500, w, h, analyses: 0);
        var b3 = CheckPlans.OnS("REN", 3, CheckPlans.SegRenB, 1000, w, h, analyses: 0);
        var r2 = Run(db, writer, template, new PaperRunRequest(CheckPlans.RunRen, [a.Request(), c.Request(), b3.Request()]), out var c2, out _);
        PrintResult("E307 sheet inserted", r2, c2);
        var plan = r2.RenamePlan!;
        bool twoPass = plan.FirstPass.Count == 1 && plan.FirstPass[0].From == "REN-2" && plan.SecondPass.Count == 1 && plan.SecondPass[0].To == "REN-3"
                    && plan.Creates.Count == 1 && plan.Creates[0].Name == "REN-2";
        bool handlesKept = r2.Sheets[0].LayoutHandle == r1.Sheets[0].LayoutHandle && r2.Sheets[2].LayoutHandle == r1.Sheets[1].LayoutHandle
                        && r2.Sheets[2].ViewportHandle == r1.Sheets[1].ViewportHandle;
        Expect("E307", twoPass && handlesKept && r2.Sheets[2].LayoutName == "REN-3" && r2.Sheets[2].Layout == PaperChange.Renamed && r2.Sheets[1].Layout == PaperChange.Created,
            $"names [{string.Join(", ", r2.Sheets.Select(s => $"{s.LayoutName} {s.LayoutHandle}/{s.Layout}"))}]; pass 1 [{string.Join(", ", plan.FirstPass.Select(x => $"{x.From}->{x.To}"))}] pass 2 [{string.Join(", ", plan.SecondPass.Select(x => $"{x.From}->{x.To}"))}]");

        // A rename inside an aborted transaction is rolled back (A4's atomicity, 6.10).
        using (var tr = db.TransactionManager.StartTransaction())
        {
            LayoutManager.Current.RenameLayout("REN-3", "REN-X");
            tr.Abort();
        }
        var lm = LayoutManager.Current;
        Expect("E307 rename rolled back by abort", lm.LayoutExists("REN-3") && !lm.LayoutExists("REN-X"), $"REN-3 exists={lm.LayoutExists("REN-3")}, REN-X exists={lm.LayoutExists("REN-X")}");

        // E612: the right-hand segment is removed; its layout is listed, never deleted.
        int layouts = LayoutCount(db);
        var r3 = Run(db, writer, template, new PaperRunRequest(CheckPlans.RunRen, [a.Request(), c.Request()]), out var c3, out _);
        PrintResult("E612 segment removed", r3, c3);
        var orphan = r3.Orphans.FirstOrDefault(o => o.SegmentId == CheckPlans.SegRenB);
        Expect("E612", orphan is not null && lm.LayoutExists("REN-3") && LayoutCount(db) == layouts && r3.Problems.Any(p => p.Code == PaperCodes.OrphanLayout),
            $"orphan {orphan?.Name} {orphan?.Handle}: {orphan?.Reason}; layouts {layouts} -> {LayoutCount(db)}");

        // RenumberLayouts off: an inserted sheet takes a free name; existing layouts keep theirs.
        var keepWriter = CheckPlans.Writer(CheckPlans.Config() with { RenumberLayouts = false });
        var ka = CheckPlans.OnS("KEEP", 1, CheckPlans.SegRenA, 0, w, h, analyses: 0);
        var kb = CheckPlans.OnS("KEEP", 2, CheckPlans.SegRenB, 1000, w, h, analyses: 0);
        var k1 = Run(db, keepWriter, template, new PaperRunRequest(CheckPlans.RunKeep, [ka.Request(), kb.Request()]), out var kc1, out _);
        PrintResult("E307 renumber off, two sheets", k1, kc1);
        var kc = CheckPlans.OnS("KEEP", 2, CheckPlans.SegRenC, 500, w, h, analyses: 0);
        var kb3 = CheckPlans.OnS("KEEP", 3, CheckPlans.SegRenB, 1000, w, h, analyses: 0);
        var k2 = Run(db, keepWriter, template, new PaperRunRequest(CheckPlans.RunKeep, [ka.Request(), kc.Request(), kb3.Request()]), out var kc2, out _);
        PrintResult("E307 renumber off, sheet inserted", k2, kc2);
        var kp = k2.RenamePlan!;
        Expect("E307 renumber off", kp.FirstPass.Count == 0 && kp.SecondPass.Count == 0 && k2.Sheets[0].LayoutName == "KEEP-1" && k2.Sheets[2].LayoutName == "KEEP-2"
                && k2.Sheets[2].LayoutHandle == k1.Sheets[1].LayoutHandle && k2.Sheets[1].Layout == PaperChange.Created && k2.Sheets[1].LayoutName != "KEEP-2",
            $"names [{string.Join(", ", k2.Sheets.Select(s => $"{s.LayoutName} {s.LayoutHandle}/{s.Layout}"))}]; renames {kp.FirstPass.Count + kp.SecondPass.Count}; {k2.Problems.FirstOrDefault(p => p.Code == ViewProblemCodes.LayoutNameChanged)?.Message}");
    }

    /// <summary>E613: 60 sheets in one regen, then an unchanged regen of all 60.</summary>
    private static void E613Rows(Database db, PaperWriter writer, PaperTemplate template)
    {
        const int count = 60;
        var sheets = Enumerable.Range(0, count).Select(i => CheckPlans.OnS("MANY", i + 1,
            Guid.Parse($"a3a3a3a3-0613-4000-8000-{i:x12}"), i * 25, template.PlanViewportWidth, template.PlanViewportHeight, analyses: 0, length: 300)).ToList();
        var req = new PaperRunRequest(CheckPlans.RunMany, sheets.Select(s => s.Request()).ToList());
        int before = LayoutCount(db);
        var r1 = Run(db, writer, template, req, out var c1, out var ms1);
        Say($"A3 E613 create {count} sheets: succeeded={r1.Succeeded} writes={r1.Writes} dbEvents appended={c1.Appended} modified={c1.Modified} in {F6(ms1)} ms ({F6(ms1 / count)} ms per sheet)");
        var r2 = Run(db, writer, template, req, out var c2, out var ms2);
        Say($"A3 E613 unchanged regen of {count} sheets: writes={r2.Writes} dbEvents={c2.Appended}/{c2.Modified}/{c2.Erased} in {F6(ms2)} ms");
        Expect("E613", r1.Succeeded && LayoutCount(db) == before + count && r2.Succeeded && c2.None && r2.Writes == 0,
            $"layouts {before} -> {LayoutCount(db)}; create {F6(ms1)} ms, unchanged regen {F6(ms2)} ms");
    }

    // ------------------------------------------------------------------ small helpers

    private sealed record VpState(Pt Center, double Width, double Height, Pt Target, Pt ViewCenter, Pt Anchor, bool Locked,
                                  double CustomScale, string? AnnoName, double AnnoRatio, string Line);

    /// <summary>A snapshot of a viewport's values, read in its own transaction.</summary>
    private static VpState Read(Database db, ObjectId vpId)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var v = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
        var anno = PaperViewMath.SafeAnnotationScale(v);
        var s = new VpState(new Pt(v.CenterPoint.X, v.CenterPoint.Y), v.Width, v.Height, new Pt(v.ViewTarget.X, v.ViewTarget.Y),
            new Pt(v.ViewCenter.X, v.ViewCenter.Y), PaperViewMath.Anchor(v), v.Locked, v.CustomScale, anno?.Name, PaperViewMath.Ratio(anno), VpLine(tr, v));
        tr.Commit();
        return s;
    }

    /// <summary>Where a model point lands on paper through the viewport (P12 mapping), read in its own transaction.</summary>
    private static Pt Lands(Database db, ObjectId vpId, Pt w)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var p = PaperViewMath.ModelToPaper((Viewport)tr.GetObject(vpId, OpenMode.ForRead), w);
        tr.Commit();
        return p;
    }

    private static void Modify<T>(Database db, ObjectId id, Action<T> change) where T : DBObject
    {
        using var tr = db.TransactionManager.StartTransaction();
        change((T)tr.GetObject(id, OpenMode.ForWrite, false, true));
        tr.Commit();
    }

    /// <summary>Moves the viewport as MOVE would: the viewport, and its clip polyline unless moving the viewport already moved it.</summary>
    private static string MoveViewport(Database db, ObjectId vpId, Vector3d d)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var v = (Viewport)tr.GetObject(vpId, OpenMode.ForWrite);
        var clipId = v.NonRectClipEntityId;
        var pl = (Polyline)tr.GetObject(clipId, OpenMode.ForRead);
        var first = pl.GetPoint2dAt(0);
        v.TransformBy(Matrix3d.Displacement(d));
        var after = pl.GetPoint2dAt(0);
        string note;
        if (after.GetDistanceTo(first) < 1e-12)
        {
            pl.UpgradeOpen();
            pl.TransformBy(Matrix3d.Displacement(d));
            note = "Viewport.TransformBy left the clip in place; the clip polyline was moved too";
        }
        else note = "Viewport.TransformBy moved the clip with it";
        tr.Commit();
        return note;
    }

    private static double ClipError(Database db, ObjectId vpId, ViewSpec spec, Pt anchor)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var v = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
        var pl = (Polyline)tr.GetObject(v.NonRectClipEntityId, OpenMode.ForRead);
        double err = pl.NumberOfVertices == spec.ClipInPaper!.Count ? 0 : double.PositiveInfinity;
        for (int i = 0; i < Math.Min(pl.NumberOfVertices, spec.ClipInPaper.Count); i++)
        {
            var q = pl.GetPoint2dAt(i);
            err = Math.Max(err, Dist(new Pt(q.X, q.Y), new Pt(anchor.X + spec.ClipInPaper[i].Point.X, anchor.Y + spec.ClipInPaper[i].Point.Y)));
        }
        tr.Commit();
        return err;
    }

    private static (bool, string) TableCheck(Database db, ObjectId tableId, Core.Output.TableModel model)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var r = TableMatches((Table)tr.GetObject(tableId, OpenMode.ForRead), model);
        tr.Commit();
        return r;
    }

    private static void SetLayer(Database db, string name, bool locked, bool frozen)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var l = (LayerTableRecord)tr.GetObject(((LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead))[name], OpenMode.ForWrite);
        l.IsLocked = locked;
        l.IsFrozen = frozen;
        tr.Commit();
    }

    private static (bool Locked, bool Frozen) LayerState(Database db, string name)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var l = (LayerTableRecord)tr.GetObject(((LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead))[name], OpenMode.ForRead);
        var s = (l.IsLocked, l.IsFrozen);
        tr.Commit();
        return s;
    }

    private static int ScaleCount(Database db) => db.ObjectContextManager.GetContextCollection("ACDB_ANNOTATIONSCALES").Cast<ObjectContext>().Count();
}

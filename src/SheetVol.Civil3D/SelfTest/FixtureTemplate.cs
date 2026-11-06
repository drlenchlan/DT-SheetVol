// FX work package. Owner: FX.
//
// The code-built test template (HANDOFF.md section 6.7, T15, T18): a layout with
// one floating viewport on layer SV-VPORT, 12 x 5 paper units, and a block
// reference named SV_TABLE_ANCHOR whose insertion point is the table's top-left.
// Lets S2 (P11) and A3 work without a LAN DWT. Plain AutoCAD objects only.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using static SheetVol.Civil3D.SelfTest.FixtureSpec;
using static SheetVol.Civil3D.SelfTest.FixtureSurfaces;

namespace SheetVol.Civil3D.SelfTest;

internal static class FixtureTemplate
{
    /// <summary>Adds the template layout to <paramref name="db"/>. LayoutManager works on the
    /// working database, so it is switched to <paramref name="db"/> for the call and restored.</summary>
    internal static TemplateFixture Build(Database db, Transaction tr)
    {
        var previous = HostApplicationServices.WorkingDatabase;
        bool switched = !ReferenceEquals(previous, db);
        if (switched) HostApplicationServices.WorkingDatabase = db;
        try
        {
            var lm = LayoutManager.Current;
            if (lm.LayoutExists(Names.TemplateLayout))
                throw new FixtureException(FixtureCodes.NameInUse, $"a layout named '{Names.TemplateLayout}' already exists");

            var layerId = EnsureLayer(db, tr, Names.ViewportLayer);
            var anchorDefId = EnsureAnchorBlock(db, tr, Names.TableAnchorBlock);

            ObjectId layoutId;
            try { layoutId = lm.CreateLayout(Names.TemplateLayout); }
            catch (System.Exception ex) { throw new FixtureException(FixtureCodes.BuildFailed, $"LayoutManager.CreateLayout('{Names.TemplateLayout}') threw {ex.GetType().Name}: {ex.Message}", ex); }

            // The layout is made current before the plan viewport is added, and the previous layout
            // is put back at the end. Two measured reasons (2026.2.3 Core Console, 2026-11-05):
            //  - Viewport.On = true on a layout that is not current throws eNotInPaperspace.
            //  - On a layout's first activation AutoCAD adopts the first viewport it finds as the
            //    layout's overall paper-space viewport: a 12 x 5 viewport added before activation
            //    came back 15.877 x 8.993 at scale 1.
            // Whether the engine can avoid activation is probe P12 (S2).
            var notes = new List<string>();
            string previousLayout = lm.CurrentLayout;
            ObjectId vpId, anchorId;
            try
            {
                lm.CurrentLayout = Names.TemplateLayout;

                var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                var paper = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
                var existing = layout.GetViewports();
                if (existing.Count == 0)
                {
                    // MEASURED 2026-11-05: in a side database (no document) activation does not
                    // initialize the layout, so it has no overall viewport. Append one first; the
                    // first viewport in a layout's block is the one AutoCAD treats as the overall
                    // paper-space viewport, so the plan viewport stays floating when the layout is
                    // first activated in a session. Proven by tools/scripts/fixtureTemplateCheck.scr.
                    var lim = layout.Limits;
                    double w = lim.MaxPoint.X - lim.MinPoint.X, h = lim.MaxPoint.Y - lim.MinPoint.Y;
                    if (!(w > 0 && h > 0))
                        throw new FixtureException(FixtureCodes.BuildFailed, $"new layout has no usable limits ({F(w)} x {F(h)}) to size its paper-space viewport");
                    var overall = new Viewport();
                    overall.SetDatabaseDefaults(db);
                    overall.LayerId = db.LayerZero;
                    overall.CenterPoint = new Point3d((lim.MinPoint.X + lim.MaxPoint.X) / 2, (lim.MinPoint.Y + lim.MaxPoint.Y) / 2, 0.0);
                    overall.Width = w;
                    overall.Height = h;
                    paper.AppendEntity(overall);
                    tr.AddNewlyCreatedDBObject(overall, true);
                    notes.Add($"no overall viewport after activation (no document); added a {F(w)} x {F(h)} paper-space viewport first");
                }
                // [0] is the overall paper-space viewport. Anything after it is a floating viewport
                // AutoCAD made in this brand-new layout (LAYOUTCREATEVIEWPORT); remove it so the
                // template holds exactly one floating viewport.
                for (int i = 1; i < existing.Count; i++)
                {
                    var extra = (Viewport)tr.GetObject(existing[i], OpenMode.ForWrite);
                    notes.Add($"erased auto-created viewport {extra.Handle} ({F(extra.Width)} x {F(extra.Height)}) in the new layout");
                    extra.Erase();
                }

                var vp = new Viewport();
                vp.SetDatabaseDefaults(db);
                vp.LayerId = layerId;
                vp.CenterPoint = new Point3d(Template.ViewportCenter.X, Template.ViewportCenter.Y, 0.0);
                vp.Width = Template.ViewportWidth;
                vp.Height = Template.ViewportHeight;
                vpId = paper.AppendEntity(vp);
                tr.AddNewlyCreatedDBObject(vp, true);
                vp.ViewDirection = Vector3d.ZAxis;
                vp.ViewTarget = Point3d.Origin;
                vp.ViewCenter = Point2d.Origin;
                vp.TwistAngle = 0.0;
                vp.CustomScale = 1.0 / Template.ModelUnitsPerPaperUnit;
                vp.On = true;

                var anchor = new BlockReference(new Point3d(Template.TableAnchor.X, Template.TableAnchor.Y, 0.0), anchorDefId);
                anchor.SetDatabaseDefaults(db);
                anchorId = paper.AppendEntity(anchor);
                tr.AddNewlyCreatedDBObject(anchor, true);
            }
            finally
            {
                if (!string.Equals(lm.CurrentLayout, previousLayout, StringComparison.OrdinalIgnoreCase))
                    lm.CurrentLayout = previousLayout;
            }

            // MEASURED 2026-11-05: in a side database AutoCAD initializes the layout only when the
            // previous layout is restored, adopting the first viewport as overall and adding its
            // default floating viewport (LAYOUTCREATEVIEWPORT). Keep the first viewport and the
            // plan viewport; erase anything else in this new layout.
            RemoveStrayViewports(tr, layoutId, vpId, notes);

            return new TemplateFixture(Names.TemplateLayout, layoutId, vpId, anchorId, anchorDefId, layerId,
                Template.ViewportWidth, Template.ViewportHeight, notes);
        }
        finally
        {
            if (switched) HostApplicationServices.WorkingDatabase = previous;
        }
    }

    /// <summary>MEASURED 2026-11-05 (2026.2.3 Core Console): the file passes its reread (plan
    /// viewport 12 x 5 at 1:50, on), but after reopening it and making SV-SHEET current the plan
    /// viewport's CustomScale read 0.014712 instead of 0.02. The in-document template
    /// (<see cref="Build"/> on an open drawing, then SAVEAS) kept 0.02 through save, reopen and
    /// activation. Cause not investigated; it is probe P12 territory.</summary>
    internal const string SideDatabaseScaleCaveat =
        "CAVEAT: a template written from a side database changed its plan viewport scale from 1:50 to about 1:68 on first activation after reopening (MEASURED 2026-11-05); when the scale must survive, run SVFIXTURE TEMPLATE in an open drawing and SAVEAS";

    /// <summary>Writes a drawing that holds only the template layout (plus Model), then reads it
    /// back and checks it: one paper layout, the overall viewport first, the plan viewport on
    /// SV-VPORT at 12 x 5, 1:50 and on, one anchor. See <see cref="SideDatabaseScaleCaveat"/>.</summary>
    internal static IReadOnlyList<string> WriteDrawing(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new FixtureException(FixtureCodes.BadInput, "no output path");
        path = Path.GetFullPath(path);
        if (File.Exists(path)) throw new FixtureException(FixtureCodes.NameInUse, $"'{path}' already exists; refusing to overwrite it");

        var notes = new List<string>();
        using (var db = new Database(true, true))
        {
            var previous = HostApplicationServices.WorkingDatabase;
            HostApplicationServices.WorkingDatabase = db;
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    notes.AddRange(Build(db, tr).Notes);
                    tr.Commit();
                }
                notes.Add($"after build: {ViewportCount(db, Names.TemplateLayout)} viewport entities in {Names.TemplateLayout}");
                // Drop every other paper layout so the file holds exactly one.
                foreach (var name in PaperLayoutNames(db).Where(n => !string.Equals(n, Names.TemplateLayout, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    LayoutManager.Current.DeleteLayout(name);
                    notes.Add($"after deleting {name}: {ViewportCount(db, Names.TemplateLayout)} viewport entities in {Names.TemplateLayout}");
                }
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = previous;
            }
            db.SaveAs(path, DwgVersion.Current);
        }
        notes.Add($"template written: {path}");

        // Read it back: the write is only done when the file says so. A failed reread removes
        // the file, so a wrong template never stays on disk looking finished.
        try { Reread(path, notes); }
        catch (FixtureException fx)
        {
            try { File.Delete(path); } catch (System.Exception del) { notes.Add($"could not delete the failed template: {del.Message}"); }
            throw new FixtureException(fx.Code, $"{fx.Message}; file removed. Notes: {string.Join(" | ", notes)}", fx);
        }
        notes.Add(SideDatabaseScaleCaveat);
        return notes;
    }

    private static int ViewportCount(Database db, string layoutName)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        var layout = (Layout)tr.GetObject(dict.GetAt(layoutName), OpenMode.ForRead);
        var paper = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        int n = 0;
        foreach (ObjectId id in paper) if (tr.GetObject(id, OpenMode.ForRead) is Viewport) n++;
        tr.Commit();
        return n;
    }

    private static void Reread(string path, List<string> notes)
    {
        using (var check = new Database(false, true))
        {
            check.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
            using var tr = check.TransactionManager.StartTransaction();
            var layouts = PaperLayoutNames(check);
            if (layouts.Count != 1 || !string.Equals(layouts[0], Names.TemplateLayout, StringComparison.OrdinalIgnoreCase))
                throw new FixtureException(FixtureCodes.CheckFailed, $"template reread: paper layouts [{string.Join(", ", layouts)}], expected [{Names.TemplateLayout}]");

            var dict = (DBDictionary)tr.GetObject(check.LayoutDictionaryId, OpenMode.ForRead);
            var layout = (Layout)tr.GetObject(dict.GetAt(Names.TemplateLayout), OpenMode.ForRead);
            var paper = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            // Expected: the overall paper-space viewport first, then the plan viewport on SV-VPORT
            // at 12 x 5, on, 1:50; and one anchor reference.
            int allViewports = 0, planViewports = 0, anchors = 0;
            bool planIsFirst = false;
            foreach (ObjectId id in paper)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead);
                if (ent is Viewport v)
                {
                    allViewports++;
                    bool plan = string.Equals(v.Layer, Names.ViewportLayer, StringComparison.OrdinalIgnoreCase);
                    notes.Add($"reread: viewport {allViewports} {v.Handle} layer {v.Layer} {F(v.Width)} x {F(v.Height)} at ({F(v.CenterPoint.X)}, {F(v.CenterPoint.Y)}), scale 1:{F(1.0 / v.CustomScale)}, on={v.On}");
                    if (!plan) continue;
                    planViewports++;
                    if (allViewports == 1) planIsFirst = true;
                    if (Math.Abs(v.Width - Template.ViewportWidth) > GeometryTolerance || Math.Abs(v.Height - Template.ViewportHeight) > GeometryTolerance
                        || Math.Abs(1.0 / v.CustomScale - Template.ModelUnitsPerPaperUnit) > GeometryTolerance || !v.On)
                        throw new FixtureException(FixtureCodes.CheckFailed,
                            $"template reread: plan viewport is {F(v.Width)} x {F(v.Height)} at 1:{F(1.0 / v.CustomScale)}, on={v.On}; expected {F(Template.ViewportWidth)} x {F(Template.ViewportHeight)} at 1:{F(Template.ModelUnitsPerPaperUnit)}, on");
                }
                else if (ent is BlockReference br && string.Equals(BlockName(tr, br), Names.TableAnchorBlock, StringComparison.OrdinalIgnoreCase))
                {
                    anchors++;
                    notes.Add($"reread: {Names.TableAnchorBlock} at ({F(br.Position.X)}, {F(br.Position.Y)})");
                }
            }
            if (allViewports != 2 || planViewports != 1 || planIsFirst || anchors != 1)
                throw new FixtureException(FixtureCodes.CheckFailed,
                    $"template reread: {allViewports} viewport entities, {planViewports} on {Names.ViewportLayer} (first in paper space: {planIsFirst}), {anchors} {Names.TableAnchorBlock}; expected 2 (overall + plan), 1, plan not first, 1");
            tr.Commit();
        }
    }

    // ------------------------------------------------------------ helpers

    private static void RemoveStrayViewports(Transaction tr, ObjectId layoutId, ObjectId planId, List<string> notes)
    {
        var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
        var paper = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        var viewports = new List<ObjectId>();
        foreach (ObjectId id in paper)
            if (tr.GetObject(id, OpenMode.ForRead) is Viewport) viewports.Add(id);
        // viewports[0] is the overall paper-space viewport; keep it and the plan viewport.
        foreach (var id in viewports.Skip(1).Where(id => id != planId))
        {
            var v = (Viewport)tr.GetObject(id, OpenMode.ForWrite);
            notes.Add($"erased auto-created viewport {v.Handle} ({F(v.Width)} x {F(v.Height)}) in the new layout");
            v.Erase();
        }
    }

    private static List<string> PaperLayoutNames(Database db)
    {
        var names = new List<string>();
        using var tr = db.TransactionManager.StartTransaction();
        var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in dict)
        {
            var layout = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            if (!layout.ModelType) names.Add(layout.LayoutName);
        }
        tr.Commit();
        return names;
    }

    private static ObjectId EnsureLayer(Database db, Transaction tr, string name)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(name)) return lt[name];
        lt.UpgradeOpen();
        var rec = new LayerTableRecord { Name = name };
        var id = lt.Add(rec);
        tr.AddNewlyCreatedDBObject(rec, true);
        return id;
    }

    /// <summary>The table anchor block (SV_TABLE_ANCHOR here; SVFIXTURE T passes the settings'
    /// name): a small cross at the block origin, so the anchor is visible and its insertion
    /// point is the table's top-left. An existing definition is reused.</summary>
    internal static ObjectId EnsureAnchorBlock(Database db, Transaction tr, string name)
    {
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        if (bt.Has(name)) return bt[name];
        bt.UpgradeOpen();
        var def = new BlockTableRecord { Name = name, Origin = Point3d.Origin };
        var defId = bt.Add(def);
        tr.AddNewlyCreatedDBObject(def, true);
        double a = Template.AnchorMarkSize;
        foreach (var line in new[] { new Line(new Point3d(-a, 0, 0), new Point3d(a, 0, 0)), new Line(new Point3d(0, -a, 0), new Point3d(0, a, 0)) })
        {
            line.SetDatabaseDefaults(db);
            line.LayerId = db.LayerZero;   // block content on layer 0 takes the reference's layer
            def.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);
        }
        return defId;
    }

    private static string BlockName(Transaction tr, BlockReference br)
    {
        var id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
        return ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
    }
}

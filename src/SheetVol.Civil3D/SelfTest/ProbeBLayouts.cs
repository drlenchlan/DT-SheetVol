// S2 probes P11 (import a layout from a DWT headless) and P20 (is layout creation rolled
// back by an aborted transaction, and by one UNDO). HANDOFF.md 5.3, 6.7.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using S = SheetVol.Civil3D.SelfTest.ProbeBSettings;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace SheetVol.Civil3D.SelfTest;

public static partial class ProbeBCommands
{
    // ======================================================================
    // P11
    // ======================================================================

    [CommandMethod("SVPROBEB_P11", CommandFlags.Modal)]
    public static void P11() => Run("P11", (doc, db) =>
    {
        Directory.CreateDirectory(S.OutDir);
        BuildTemplate();
        Say($"P11 template saved: {S.TemplatePath} ({new FileInfo(S.TemplatePath).Length} bytes)");

        using var src = OpenTemplate();
        var srcLayId = TemplateLayoutId(src);
        string srcPlot;
        using (var trS = src.TransactionManager.StartTransaction())
        {
            var srcLay = (Layout)trS.GetObject(srcLayId, OpenMode.ForRead);
            srcPlot = PlotSummary(srcLay);
            Say($"P11 source layout '{srcLay.LayoutName}' (reread from disk): {srcPlot}");
            Say($"P11 source GetViewports().Count={srcLay.GetViewports().Count}");
            foreach (var line in DescribeBlock(trS, srcLay.BlockTableRecordId)) Say("P11 source   " + line);
            trS.Commit();
        }

        string startLayout = LayoutManager.Current.CurrentLayout;
        Say($"P11 current layout before import: '{startLayout}'");

        // Method S: the Autodesk guide's sample shape (new layout + CopyFrom for the plot
        // settings + WblockCloneObjects of the paper-space entities), but the layout comes
        // from LayoutManager.CreateLayout and is never made current. SV-P11-0 skips
        // Layout.Initialize (negative control); SV-P11-1 and -2 (two segments) call it.
        ImportLayoutS(db, src, srcLayId, "SV-P11-0", commit: true, initialize: false);
        ImportLayoutS(db, src, srcLayId, "SV-P11-1", commit: true, initialize: true);
        ImportLayoutS(db, src, srcLayId, "SV-P11-2", commit: true, initialize: true);

        // Method W: clone the Layout object itself into the layout dictionary.
        var wId = ImportLayoutW(db, src, srcLayId, commit: true);
        if (!wId.IsNull)
        {
            Try("P11 W LayoutManager.RenameLayout(SV-TEMPLATE -> SV-P11-W)",
                () => LayoutManager.Current.RenameLayout(S.TemplateLayout, "SV-P11-W"));
            FixTabOrder(db, wId, "P11 W");
        }

        Say($"P11 current layout after import: '{LayoutManager.Current.CurrentLayout}' (unchanged={LayoutManager.Current.CurrentLayout == startLayout})");

        using (var tr = db.TransactionManager.StartTransaction())
        {
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict)
            {
                var lay = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                Say($"P11 layout key='{e.Key}' name='{lay.LayoutName}' tabOrder={lay.TabOrder} btr='{((BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead)).Name}'");
            }
            foreach (var name in new[] { "SV-P11-0", "SV-P11-1", "SV-P11-2", "SV-P11-W" })
            {
                if (!dict.Contains(name)) { Say($"P11 {name}: MISSING"); continue; }
                var lay = (Layout)tr.GetObject(dict.GetAt(name), OpenMode.ForRead);
                var plot = PlotSummary(lay);
                Say($"P11 {name} plot: {plot}");
                Say($"P11 {name} plot equals source: {plot == srcPlot}");
                foreach (var line in DescribeBlock(tr, lay.BlockTableRecordId)) Say($"P11 {name}   " + line);
            }
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var anchorDefs = new List<string>();
            foreach (ObjectId id in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (btr.Name.Contains(S.AnchorBlock, StringComparison.OrdinalIgnoreCase)) anchorDefs.Add(btr.Name);
            }
            Say($"P11 block definitions named like {S.AnchorBlock}: {anchorDefs.Count} [{string.Join(", ", anchorDefs)}]");
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            Say($"P11 layer {S.VportLayer} present: {lt.Has(S.VportLayer)}");
            tr.Commit();
        }
    });

    private static void BuildTemplate()
    {
        if (File.Exists(S.TemplatePath)) File.Delete(S.TemplatePath);
        using var src = new Database(true, true);
        var prev = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = src;   // LayoutManager works on the working database
        try
        {
            using (var tr = src.TransactionManager.StartTransaction())
            {
                EnsureLayer(tr, src, S.VportLayer);
                EnsureAnchorBlock(tr, src);
                tr.Commit();
            }
            var layId = LayoutManager.Current.CreateLayout(S.TemplateLayout);
            using (var tr = src.TransactionManager.StartTransaction())
            {
                var lay = (Layout)tr.GetObject(layId, OpenMode.ForWrite);
                var psv = PlotSettingsValidator.Current;
                psv.SetPlotConfigurationName(lay, S.PlotDevice, null);
                psv.RefreshLists(lay);
                var media = psv.GetCanonicalMediaNameList(lay).Cast<string>().ToList();
                var pick = media.FirstOrDefault(m => m.Contains(S.MediaHint, StringComparison.OrdinalIgnoreCase));
                Say($"P11 template: {media.Count} media for '{S.PlotDevice}'; picked '{pick}'");
                if (pick is null) throw new InvalidOperationException($"no media containing {S.MediaHint}");
                psv.SetCanonicalMediaName(lay, pick);
                psv.SetPlotPaperUnits(lay, PlotPaperUnit.Inches);
                psv.SetPlotType(lay, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
                psv.SetUseStandardScale(lay, true);
                psv.SetStdScaleType(lay, StdScaleType.StdScale1To1);
                psv.SetPlotRotation(lay, PlotRotation.Degrees000);
                var sheet = src.PlotStyleMode ? S.CtbSheet : S.StbSheet;
                var sheets = psv.GetPlotStyleSheetList().Cast<string>().ToList();
                Say($"P11 template: PlotStyleMode (true = color-dependent) = {src.PlotStyleMode}; '{sheet}' in style sheet list = {sheets.Contains(sheet, StringComparer.OrdinalIgnoreCase)} ({sheets.Count} sheets)");
                psv.SetCurrentStyleSheet(lay, sheet);

                var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForWrite);
                Say($"P11 template: viewports in layout block before Initialize: {CountViewports(tr, btr)}");
                Try("P11 template: Layout.Initialize()", () => lay.Initialize());
                Say($"P11 template: viewports in layout block after Initialize: {CountViewports(tr, btr)}; GetViewports().Count={lay.GetViewports().Count}");

                var vp = new Viewport();
                vp.SetDatabaseDefaults(src);
                vp.Layer = S.VportLayer;
                vp.CenterPoint = new Point3d(8.5, 5.5, 0);
                vp.Width = 15.0;
                vp.Height = 8.0;
                btr.AppendEntity(vp);
                tr.AddNewlyCreatedDBObject(vp, true);
                vp.ViewDirection = Vector3d.ZAxis;
                vp.ViewTarget = Point3d.Origin;
                vp.ViewCenter = Point2d.Origin;
                vp.CustomScale = S.Scale;
                Try("P11 template: plan viewport On=true (side database)", () => vp.On = true);

                var anchor = new BlockReference(new Point3d(1.0, 10.0, 0), EnsureAnchorBlock(tr, src));
                anchor.SetDatabaseDefaults(src);
                btr.AppendEntity(anchor);
                tr.AddNewlyCreatedDBObject(anchor, true);

                var title = new DBText { TextString = "SV TEMPLATE TITLE", Position = new Point3d(1.0, 0.75, 0), Height = 0.2 };
                title.SetDatabaseDefaults(src);
                btr.AppendEntity(title);
                tr.AddNewlyCreatedDBObject(title, true);
                tr.Commit();
            }
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = prev;
        }
        src.SaveAs(S.TemplatePath, DwgVersion.Current);
    }

    private static Database OpenTemplate()
    {
        var src = new Database(false, true);
        src.ReadDwgFile(S.TemplatePath, FileOpenMode.OpenForReadAndAllShare, true, "");
        src.CloseInput(true);
        return src;
    }

    private static ObjectId TemplateLayoutId(Database src)
    {
        using var trS = src.TransactionManager.StartTransaction();
        var dict = (DBDictionary)trS.GetObject(src.LayoutDictionaryId, OpenMode.ForRead);
        if (!dict.Contains(S.TemplateLayout)) throw new InvalidOperationException($"template has no layout '{S.TemplateLayout}'");
        var id = dict.GetAt(S.TemplateLayout);
        trS.Commit();
        return id;
    }

    /// <summary>Method S. Returns the new layout id, or Null when the step refused.</summary>
    private static ObjectId ImportLayoutS(Database db, Database src, ObjectId srcLayId, string newName, bool commit, bool initialize)
    {
        string tag = $"P11 S '{newName}'";
        using var trS = src.TransactionManager.StartTransaction();
        var srcLay = (Layout)trS.GetObject(srcLayId, OpenMode.ForRead);
        var srcBtr = (BlockTableRecord)trS.GetObject(srcLay.BlockTableRecordId, OpenMode.ForRead);

        // The overall (paper-space) viewport must not be cloned as a second plan viewport.
        // On an activated layout it is GetViewports()[0].
        var overall = ObjectId.Null;
        var vps = srcLay.GetViewports();
        if (vps.Count > 0) overall = vps[0];
        var ids = new ObjectIdCollection();
        foreach (ObjectId id in srcBtr)
        {
            if (id == overall) { Say($"{tag}: skipping source overall viewport {id.Handle}"); continue; }
            ids.Add(id);
        }

        using var tr = db.TransactionManager.StartTransaction();
        var layId = LayoutManager.Current.CreateLayout(newName);
        var lay = (Layout)tr.GetObject(layId, OpenMode.ForWrite);
        var btrBefore = lay.BlockTableRecordId;
        Say($"{tag}: CreateLayout -> name='{lay.LayoutName}' tabOrder={lay.TabOrder} plot: {PlotSummary(lay)}");
        lay.CopyFrom(srcLay);
        Say($"{tag}: after CopyFrom -> name='{lay.LayoutName}' tabOrder={lay.TabOrder} btrUnchanged={lay.BlockTableRecordId == btrBefore} plot: {PlotSummary(lay)}");
        if (lay.BlockTableRecordId != btrBefore)
            throw new InvalidOperationException("CopyFrom replaced the layout's block table record; transaction aborted");
        if (lay.LayoutName != newName)
        {
            lay.LayoutName = newName;
            Say($"{tag}: LayoutName reset to '{lay.LayoutName}'");
        }
        if (initialize) InitializeAndReport(tr, lay, tag);   // overall viewport first, after the paper size is known
        using (var map = new IdMapping())
        {
            src.WblockCloneObjects(ids, lay.BlockTableRecordId, map, DuplicateRecordCloning.Ignore, false);
            int cloned = 0;
            foreach (IdPair pair in map) if (pair.IsCloned && pair.IsPrimary) cloned++;
            Say($"{tag}: WblockCloneObjects {ids.Count} source entities -> {cloned} primary clones");
        }
        if (commit) { tr.Commit(); Say($"{tag}: committed"); }
        else { tr.Abort(); Say($"{tag}: aborted"); }
        trS.Commit();
        return layId;
    }

    /// <summary>Method W: clone the Layout object (its block, plot settings and contents
    /// come with it). Keeps the source name; the caller renames.</summary>
    private static ObjectId ImportLayoutW(Database db, Database src, ObjectId srcLayId, bool commit)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        if (dict.Contains(S.TemplateLayout))
        {
            Say($"P11 W: destination already holds '{S.TemplateLayout}'; Ignore cloning would skip it. Not cloning.");
            return ObjectId.Null;
        }
        ObjectId newId;
        using (var map = new IdMapping())
        {
            src.WblockCloneObjects(new ObjectIdCollection { srcLayId }, db.LayoutDictionaryId, map, DuplicateRecordCloning.Ignore, false);
            newId = map[srcLayId].Value;
        }
        var lay = (Layout)tr.GetObject(newId, OpenMode.ForRead);
        Say($"P11 W: cloned layout name='{lay.LayoutName}' tabOrder={lay.TabOrder} btr='{((BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead)).Name}' plot: {PlotSummary(lay)}");
        if (commit) { tr.Commit(); Say("P11 W: committed"); }
        else { tr.Abort(); Say("P11 W: aborted"); }
        return newId;
    }

    /// <summary>A whole-layout clone keeps the source TabOrder, which can collide with an
    /// existing layout. Moves the clone to the end.</summary>
    private static void FixTabOrder(Database db, ObjectId layId, string tag)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var lay = (Layout)tr.GetObject(layId, OpenMode.ForWrite);
        int max = 0, dup = 0;
        foreach (DBDictionaryEntry e in (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead))
        {
            if (e.Value == layId) continue;
            int o = ((Layout)tr.GetObject(e.Value, OpenMode.ForRead)).TabOrder;
            max = Math.Max(max, o);
            if (o == lay.TabOrder) dup++;
        }
        int before = lay.TabOrder;
        if (dup > 0) lay.TabOrder = max + 1;
        Say($"{tag}: TabOrder {before} shared with {dup} other layout(s); now {lay.TabOrder}");
        tr.Commit();
    }

    private static int CountViewports(Transaction tr, BlockTableRecord btr)
    {
        int n = 0;
        foreach (ObjectId id in btr) if (tr.GetObject(id, OpenMode.ForRead) is Viewport) n++;
        return n;
    }

    internal static IEnumerable<string> DescribeBlock(Transaction tr, ObjectId btrId)
    {
        var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
        foreach (ObjectId id in btr)
        {
            var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
            yield return ent switch
            {
                Viewport vp => $"Viewport {vp.Handle} " + VpSummary(vp),
                Table tb => $"Table {tb.Handle} {tb.Rows.Count}x{tb.Columns.Count} pos={P(tb.Position)}",   // Table derives from BlockReference
                BlockReference br => $"BlockReference {br.Handle} name={((BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead)).Name} layer={br.Layer} pos={P(br.Position)}",
                DBText t => $"DBText {t.Handle} '{t.TextString}' layer={t.Layer} pos={P(t.Position)} h={F(t.Height)}",
                _ => $"{ent.GetType().Name} {ent.Handle} layer={ent.Layer}",
            };
        }
    }

    // ======================================================================
    // P20
    // ======================================================================

    private static void P20Counts(Database db, string when)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var names = LayoutNames(tr, db);
        var blocks = PaperSpaceBlockNames(tr, db);
        Say($"P20 {when}: layoutDictionary={names.Count} LayoutManager.LayoutCount={LayoutManager.Current.LayoutCount} paperSpaceBlocks={blocks.Count}");
        Say($"P20 {when}: layouts=[{string.Join(", ", names)}]");
        Say($"P20 {when}: paperSpaceBlocks=[{string.Join(", ", blocks)}]");
        tr.Commit();
    }

    [CommandMethod("SVPROBEB_P20A", CommandFlags.Modal)]
    public static void P20A() => Run("P20A", (doc, db) =>
    {
        var lm = LayoutManager.Current;
        P20Counts(db, "before");

        // 1. LayoutManager.CreateLayout inside a transaction that is aborted.
        ObjectId cId;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            cId = lm.CreateLayout("SV-P20-ABORT-C");
            Say($"P20 abort-C inside transaction: layoutDictionary={LayoutDictCount(tr, db)} exists={lm.LayoutExists("SV-P20-ABORT-C")}");
            tr.Abort();
        }
        Say($"P20 abort-C after Abort: exists={lm.LayoutExists("SV-P20-ABORT-C")} id.IsErased={cId.IsErased} id.IsValid={cId.IsValid}");
        P20Counts(db, "after abort-C");

        // 2. Method S import inside a transaction that is aborted.
        if (!File.Exists(S.TemplatePath)) { Say("P20 template missing; run SVPROBEB_P11 first"); return; }
        using (var src = OpenTemplate())
        {
            var srcLayId = TemplateLayoutId(src);
            ObjectId sId = ObjectId.Null;
            Try("P20 abort-S", () => sId = ImportLayoutS(db, src, srcLayId, "SV-P20-ABORT-S", commit: false, initialize: true));
            Say($"P20 abort-S after Abort: exists={lm.LayoutExists("SV-P20-ABORT-S")} id.IsErased={sId.IsErased} id.IsValid={sId.IsValid}");
            P20Counts(db, "after abort-S");

            // 3. Method W clone inside a transaction that is aborted.
            ObjectId wId = ObjectId.Null;
            Try("P20 abort-W", () => wId = ImportLayoutW(db, src, srcLayId, commit: false));
            Say($"P20 abort-W after Abort: exists({S.TemplateLayout})={lm.LayoutExists(S.TemplateLayout)} id.IsErased={wId.IsErased} id.IsValid={wId.IsValid}");
            P20Counts(db, "after abort-W");
        }
    });

    [CommandMethod("SVPROBEB_P20B", CommandFlags.Modal)]
    public static void P20B() => Run("P20B", (doc, db) =>
    {
        var lm = LayoutManager.Current;
        Say($"P20 UNDOCTL={AcApp.GetSystemVariable("UNDOCTL")} (bit 1 = undo enabled)");
        P20Counts(db, "before committed creates");
        using (var tr = db.TransactionManager.StartTransaction())
        {
            lm.CreateLayout("SV-P20-UNDO-C");
            tr.Commit();
        }
        using (var src = OpenTemplate())
        {
            var srcLayId = TemplateLayoutId(src);
            ImportLayoutS(db, src, srcLayId, "SV-P20-UNDO-S", commit: true, initialize: true);
            var wId = ImportLayoutW(db, src, srcLayId, commit: true);
            if (!wId.IsNull)
            {
                Try("P20 rename W", () => lm.RenameLayout(S.TemplateLayout, "SV-P20-UNDO-W"));
                FixTabOrder(db, wId, "P20 W");
            }
        }
        P20Counts(db, "after committed creates (one command)");
    });

    [CommandMethod("SVPROBEB_P20C", CommandFlags.Modal)]
    public static void P20C() => Run("P20C", (doc, db) =>
    {
        var lm = LayoutManager.Current;
        foreach (var n in new[] { "SV-P20-UNDO-C", "SV-P20-UNDO-S", "SV-P20-UNDO-W" })
            Say($"P20 after UNDO 1: {n} exists={lm.LayoutExists(n)}");
        P20Counts(db, "after UNDO 1");
    });
}

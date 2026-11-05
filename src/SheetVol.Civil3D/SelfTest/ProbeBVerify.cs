// S2 save and reopen check for P11, P12, P13, P17, P24. SVPROBEB_SAVE snapshots every
// probe artifact in the live drawing and saves it to %TEMP%\sheetvol\probeB-out.dwg;
// SVPROBEB_VERIFY reads that file back with ReadDwgFile into a side database, takes the
// same snapshot and diffs the two key by key.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using S = SheetVol.Civil3D.SelfTest.ProbeBSettings;

namespace SheetVol.Civil3D.SelfTest;

public static partial class ProbeBCommands
{
    private static SortedDictionary<string, string>? _liveSnapshot;

    [CommandMethod("SVPROBEB_SAVE", CommandFlags.Modal)]
    public static void Save() => Run("SAVE", (doc, db) =>
    {
        using (var tr = db.TransactionManager.StartTransaction())
        {
            _liveSnapshot = Snapshot(tr, db);
            tr.Commit();
        }
        Say($"SAVE live snapshot: {_liveSnapshot.Count} keys");
        Directory.CreateDirectory(S.OutDir);
        if (File.Exists(S.OutPath)) File.Delete(S.OutPath);
        db.SaveAs(S.OutPath, DwgVersion.Current);
        var fi = new FileInfo(S.OutPath);
        Say($"SAVE wrote {fi.FullName} ({fi.Length} bytes, {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss})");

        // Second session (E614): open the saved file as the document and activate layouts
        // the way a user's first click would. Generated here so the layout list matches.
        var lines = new List<string> { "(command \"_.NETLOAD\" (getenv \"SHEETVOL_DLL\"))", "SVPROBEB_REOPEN" };
        foreach (var name in new[] { "SV-P12A", "SV-P12B2", "SV-P12C", "SV-P11-1", "SV-P11-W", "SV-P13", "SV-P17", "SV-P24" })
            lines.AddRange(new[] { $"(setvar \"CTAB\" \"{name}\")", "SVPROBEB_P12TRANS" });
        lines.AddRange(new[] { "(setvar \"CTAB\" \"SV-P12A\")", "_.MSPACE", "SVPROBEB_P12TRANS", "_.QUIT", "_Y", "" });
        File.WriteAllText(S.ReopenScriptPath, string.Join("\n", lines), new System.Text.UTF8Encoding(false));
        Say($"SAVE wrote second-session script {S.ReopenScriptPath}");
    });

    /// <summary>Second session, before any activation: every SV-* layout as the document
    /// read it from disk.</summary>
    [CommandMethod("SVPROBEB_REOPEN", CommandFlags.Modal | CommandFlags.NoUndoMarker)]   // read-only (S3, E804)
    public static void Reopen() => Run("REOPEN", (doc, db) =>
    {
        Say($"REOPEN document '{db.Filename}' CTAB={Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("CTAB")}");
        using var tr = db.TransactionManager.StartTransaction();
        foreach (DBDictionaryEntry e in (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead))
        {
            if (!e.Key.StartsWith(S.LayoutPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var lay = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            foreach (var line in DescribeBlock(tr, lay.BlockTableRecordId))
                if (line.StartsWith("Viewport", StringComparison.Ordinal)) Say($"REOPEN {e.Key} {line}");
        }
        tr.Commit();
    });

    [CommandMethod("SVPROBEB_VERIFY", CommandFlags.Modal | CommandFlags.NoUndoMarker)]   // reads the saved copy in a side database (S3, E804)
    public static void Verify() => Run("VERIFY", (doc, db) =>
    {
        if (_liveSnapshot is null) { Say("VERIFY no live snapshot in this session; run SVPROBEB_SAVE first"); return; }
        using var rdb = new Database(false, true);
        rdb.ReadDwgFile(S.OutPath, FileOpenMode.OpenForReadAndAllShare, true, "");
        rdb.CloseInput(true);
        SortedDictionary<string, string> file;
        using (var tr = rdb.TransactionManager.StartTransaction())
        {
            file = Snapshot(tr, rdb);

            // P11: plot settings of each import against the template on disk.
            using var src = OpenTemplate();
            string srcPlot;
            using (var trS = src.TransactionManager.StartTransaction())
            {
                srcPlot = PlotSummary((Layout)trS.GetObject(TemplateLayoutId(src), OpenMode.ForRead));
                trS.Commit();
            }
            var dict = (DBDictionary)tr.GetObject(rdb.LayoutDictionaryId, OpenMode.ForRead);
            foreach (var name in new[] { "SV-P11-0", "SV-P11-1", "SV-P11-2", "SV-P11-W" })
            {
                if (!dict.Contains(name)) { Say($"VERIFY P11 {name}: MISSING in reopened file"); continue; }
                var lay = (Layout)tr.GetObject(dict.GetAt(name), OpenMode.ForRead);
                var plot = PlotSummary(lay);
                Say($"VERIFY P11 {name} reopened plot: {plot}");
                Say($"VERIFY P11 {name} reopened plot equals template: {plot == srcPlot}");
                foreach (var line in DescribeBlock(tr, lay.BlockTableRecordId)) Say($"VERIFY P11 {name}   " + line);
            }

            // P24: binding by label on the reopened tables.
            foreach (var (owner, t) in Tables(tr, rdb))
            {
                if (t.Rows.Count < 3 || FindRowByLabel(t, LblArea) < 0) continue;
                var parts = new List<string>();
                foreach (var label in new[] { LblLength, LblArea, LblMaterial })
                {
                    int r = FindRowByLabel(t, label);
                    int c = r < 0 ? -1 : ValueCol(t, r);
                    parts.Add(r < 0 ? $"'{label}' not found" : $"'{label}' row {r} -> '{Plain(t, r, c)}'" + (label == LblMaterial ? $" / '{Plain(t, r, c + 1)}'" : ""));
                }
                Say($"VERIFY P24 {owner} table {t.Handle}: {string.Join("; ", parts)}");
            }
            tr.Commit();
        }

        int equal = 0;
        var diffs = new List<string>();
        foreach (var k in _liveSnapshot.Keys.Union(file.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            bool inLive = _liveSnapshot.TryGetValue(k, out var lv);
            bool inFile = file.TryGetValue(k, out var fv);
            if (inLive && inFile && lv == fv) { equal++; continue; }
            diffs.Add(!inFile ? $"only live: {k} = {lv}" : !inLive ? $"only file: {k} = {fv}" : $"differs: {k}: live '{lv}' file '{fv}'");
        }
        Say($"VERIFY snapshot: live {_liveSnapshot.Count} keys, file {file.Count} keys, equal {equal}, different {diffs.Count}");
        foreach (var d in diffs) Say("VERIFY DIFF " + d);
        foreach (var kv in file) Say($"VERIFY file {kv.Key} = {kv.Value}");
    });

    private static IEnumerable<(string Owner, Table Table)> Tables(Transaction tr, Database db)
    {
        foreach (ObjectId id in (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))
        {
            var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (!btr.IsLayout) continue;
            string owner = btr.Name;
            if (!btr.LayoutId.IsNull) owner = ((Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead)).LayoutName;
            foreach (ObjectId eid in btr)
                if (tr.GetObject(eid, OpenMode.ForRead) is Table t) yield return (owner, t);
        }
    }

    /// <summary>Key/value picture of every probe artifact: SV-* layouts (plot settings and
    /// every entity), model space, and the annotation scale list. Keys use handles, which
    /// survive save and reopen. Runtime-only values (viewport Number) are left out.</summary>
    internal static SortedDictionary<string, string> Snapshot(Transaction tr, Database db)
    {
        var s = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in dict)
        {
            var lay = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            bool probe = e.Key.StartsWith(S.LayoutPrefix, StringComparison.OrdinalIgnoreCase);
            if (!probe && !lay.ModelType) continue;
            string k = lay.ModelType ? "*Model" : e.Key;
            if (probe)
            {
                s[$"{k}|plot.device"] = lay.PlotConfigurationName;
                s[$"{k}|plot.media"] = lay.CanonicalMediaName;
                s[$"{k}|plot.styleSheet"] = lay.CurrentStyleSheet;
                s[$"{k}|plot.rotation"] = lay.PlotRotation.ToString();
                s[$"{k}|plot.units"] = lay.PlotPaperUnits.ToString();
                s[$"{k}|plot.type"] = lay.PlotType.ToString();
                s[$"{k}|plot.std"] = $"{lay.UseStandardScale}/{lay.StdScaleType}";
                s[$"{k}|plot.paperSize"] = P(lay.PlotPaperSize);
                s[$"{k}|tabOrder"] = lay.TabOrder.ToString();
            }
            var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in btr) SnapEntity(s, tr, k, (Entity)tr.GetObject(id, OpenMode.ForRead));
        }
        var occ = db.ObjectContextManager.GetContextCollection("ACDB_ANNOTATIONSCALES");
        foreach (ObjectContext oc in occ)
            if (oc is AnnotationScale a) s[$"scales|{a.Name}"] = $"{F(a.PaperUnits)}:{F(a.DrawingUnits)}";
        return s;
    }

    private static void SnapEntity(SortedDictionary<string, string> s, Transaction tr, string layout, Entity ent)
    {
        string k = $"{layout}|{ent.GetType().Name}#{ent.Handle}";
        s[$"{k}.layer"] = ent.Layer;
        switch (ent)
        {
            case Viewport vp:
                s[$"{k}.center"] = P(vp.CenterPoint);
                s[$"{k}.size"] = $"{F(vp.Width)} x {F(vp.Height)}";
                s[$"{k}.target"] = P(vp.ViewTarget);
                s[$"{k}.viewCenter"] = P(vp.ViewCenter);
                s[$"{k}.twist"] = F(vp.TwistAngle);
                s[$"{k}.customScale"] = F(vp.CustomScale);
                s[$"{k}.viewHeight"] = F(vp.ViewHeight);
                s[$"{k}.on"] = vp.On.ToString();
                s[$"{k}.locked"] = vp.Locked.ToString();
                s[$"{k}.clipOn"] = vp.NonRectClipOn.ToString();
                s[$"{k}.clipEntity"] = vp.NonRectClipEntityId.IsNull ? "null" : vp.NonRectClipEntityId.Handle.ToString();
                try { s[$"{k}.annoScale"] = vp.AnnotationScale?.Name ?? "(null)"; }
                catch (System.Exception ex) { s[$"{k}.annoScale"] = "threw " + ex.GetType().Name; }
                break;
            case Table tb:   // before BlockReference: Table derives from it
                s[$"{k}.size"] = $"{tb.Rows.Count}x{tb.Columns.Count}";
                s[$"{k}.position"] = P(tb.Position);
                for (int r = 0; r < tb.Rows.Count; r++)
                    for (int c = 0; c < tb.Columns.Count; c++)
                        s[$"{k}.cell[{r},{c}]"] = $"'{tb.Cells[r, c].TextString}'{MergeText(tb, r, c)}";
                break;
            case BlockReference br:
                s[$"{k}.block"] = ((BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead)).Name;
                s[$"{k}.position"] = P(br.Position);
                break;
            case DBText t:
                s[$"{k}.text"] = t.TextString;
                s[$"{k}.position"] = P(t.Position);
                break;
            case DBPoint pt:
                s[$"{k}.position"] = P(pt.Position);
                break;
            case Polyline pl:
                var v = new List<string>();
                for (int i = 0; i < pl.NumberOfVertices; i++) v.Add($"{P(pl.GetPoint2dAt(i))}b{F(pl.GetBulgeAt(i))}");
                s[$"{k}.vertices"] = (pl.Closed ? "closed " : "open ") + string.Join(" ", v);
                break;
        }
    }
}

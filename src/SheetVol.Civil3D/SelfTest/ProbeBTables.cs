// S2 probes P13 (AutoCAD Table created in a layout headless, updated in a second
// transaction) and P24 (HCFCD-style table with merged cells: find a row by its label,
// update one cell without disturbing the others, model space and layout). HANDOFF.md
// 5.3, 6.16, T31.
//
// The HCFCD layout below is a stand-in built from 6.16 and A13 (bound rows 6 to 9,
// 1-based). Dan's real table (HANDOFF.md section 13) is still open.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace SheetVol.Civil3D.SelfTest;

public static partial class ProbeBCommands
{
    // ---------- shared table helpers ----------

    private static string MergeText(Table t, int r, int c)
    {
        var cell = t.Cells[r, c];
        if (cell.IsMerged != true) return "-";
        var m = cell.GetMergeRange();
        return $"[{m.TopRow},{m.LeftColumn}..{m.BottomRow},{m.RightColumn}]";
    }

    private static string MergeList(Table t)
    {
        var seen = new HashSet<string>();
        for (int r = 0; r < t.Rows.Count; r++)
            for (int c = 0; c < t.Columns.Count; c++)
            {
                var m = MergeText(t, r, c);
                if (m != "-") seen.Add(m);
            }
        return seen.Count == 0 ? "none" : string.Join(" ", seen);
    }

    /// <summary>Cells inside a merge other than its top-left anchor. Cell.IsMerged reports
    /// false on these (MEASURED, P13), so they are found from the anchors' ranges.</summary>
    private static HashSet<(int, int)> CoveredCells(Table t)
    {
        var covered = new HashSet<(int, int)>();
        for (int r = 0; r < t.Rows.Count; r++)
            for (int c = 0; c < t.Columns.Count; c++)
            {
                if (t.Cells[r, c].IsMerged != true) continue;
                var m = t.Cells[r, c].GetMergeRange();
                for (int rr = m.TopRow; rr <= m.BottomRow; rr++)
                    for (int cc = m.LeftColumn; cc <= m.RightColumn; cc++)
                        if (rr != m.TopRow || cc != m.LeftColumn) covered.Add((rr, cc));
            }
        return covered;
    }

    private static string Plain(Table t, int r, int c)
        => t.Cells[r, c].GetTextString(FormatOption.IgnoreMtextFormat) ?? "";

    private static IEnumerable<string> Grid(Table t)
    {
        for (int r = 0; r < t.Rows.Count; r++)
        {
            var cells = new List<string>();
            for (int c = 0; c < t.Columns.Count; c++)
                cells.Add($"'{Plain(t, r, c)}'{(MergeText(t, r, c) == "-" ? "" : MergeText(t, r, c))}");
            yield return $"r{r}: " + string.Join(" | ", cells);
        }
    }

    private static Dictionary<(int, int), string> CellSnap(Table t)
    {
        var d = new Dictionary<(int, int), string>();
        for (int r = 0; r < t.Rows.Count; r++)
            for (int c = 0; c < t.Columns.Count; c++)
                d[(r, c)] = $"{t.Cells[r, c].TextString}{MergeText(t, r, c)}";
        return d;
    }

    private static List<string> CellDiff(Dictionary<(int, int), string> a, Dictionary<(int, int), string> b)
    {
        var diffs = new List<string>();
        foreach (var k in a.Keys.Union(b.Keys).OrderBy(k => k.Item1).ThenBy(k => k.Item2))
        {
            a.TryGetValue(k, out var va);
            b.TryGetValue(k, out var vb);
            if (va != vb) diffs.Add($"({k.Item1},{k.Item2}) '{va}' -> '{vb}'");
        }
        return diffs;
    }

    // ======================================================================
    // P13
    // ======================================================================

    [CommandMethod("SVPROBEB_P13", CommandFlags.Modal)]
    public static void P13() => Run("P13", (doc, db) =>
    {
        var layId = CreateLayoutHeadless(db, "SV-P13", initialize: true, "P13");
        ObjectId tid;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var btr = LayoutBlock(tr, layId, OpenMode.ForWrite);
            var t = new Table();
            t.SetDatabaseDefaults(db);
            t.TableStyle = db.Tablestyle;
            t.Position = new Point3d(1.0, 10.0, 0);
            t.SetSize(4, 3);
            t.SetRowHeight(0.3);
            t.SetColumnWidth(2.0);
            btr.AppendEntity(t);
            tr.AddNewlyCreatedDBObject(t, true);
            Say($"P13 table style='{((TableStyle)tr.GetObject(t.TableStyle, OpenMode.ForRead)).Name}' size={t.Rows.Count}x{t.Columns.Count} merges after SetSize: {MergeList(t)}");
            Try("P13 cell(0,0).Style / cell(1,0).Style / cell(2,0).Style",
                () => Say($"P13 row styles: r0='{t.Cells[0, 0].Style}' r1='{t.Cells[1, 0].Style}' r2='{t.Cells[2, 0].Style}'"));
            for (int c = 0; c < t.Columns.Count; c++)
            {
                var cell = t.Cells[0, c];
                var m = cell.GetMergeRange();
                Say($"P13 title row cell (0,{c}): IsMerged={cell.IsMerged} GetMergeRange=[{m.TopRow},{m.LeftColumn}..{m.BottomRow},{m.RightColumn}]");
            }
            var covered = CoveredCells(t);
            Say($"P13 covered (non-anchor) cells: {covered.Count} [{string.Join(" ", covered.Select(k => $"({k.Item1},{k.Item2})"))}]");
            for (int r = 0; r < t.Rows.Count; r++)
                for (int c = 0; c < t.Columns.Count; c++)
                    if (!covered.Contains((r, c))) t.Cells[r, c].TextString = $"P13 R{r}C{c}";   // anchors and plain cells only
            t.GenerateLayout();
            Say($"P13 created in transaction 1: handle={t.Handle} height={F(t.Height)} width={F(t.Width)} CTAB={AcApp.GetSystemVariable("CTAB")}");
            foreach (var line in Grid(t)) Say("P13 tx1 " + line);
            tid = t.ObjectId;
            tr.Commit();
        }

        Dictionary<(int, int), string> before;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var t = (Table)tr.GetObject(tid, OpenMode.ForWrite);
            before = CellSnap(t);
            t.Cells[2, 1].TextString = "P13 UPDATED";
            tr.Commit();
        }
        Say("P13 transaction 2 wrote cell (2,1) and committed");

        using (var tr = db.TransactionManager.StartTransaction())
        {
            var t = (Table)tr.GetObject(tid, OpenMode.ForRead);
            var diffs = CellDiff(before, CellSnap(t));
            Say($"P13 transaction 3 readback: {diffs.Count} cell(s) changed: {string.Join("; ", diffs)}");
            foreach (var line in Grid(t)) Say("P13 tx3 " + line);
            tr.Commit();
        }
    });

    // ======================================================================
    // P24
    // ======================================================================

    private sealed record HRow(string[] Texts, (int From, int To)[] Merges);

    internal const string LblLength = "TOTAL IMPACTED LENGTH (BELOW OHWM)";
    internal const string LblArea = "TOTAL IMPACTED AREA (BELOW OHWM)";
    internal const string LblMaterial = "SEDIMENT";
    private const string LblCenter = "CENTER COORDINATES";
    private const string LblWetlands = "WETLANDS";

    // Row 6 carries MTEXT bold formatting on its label, as a user's in-place edit would.
    private static readonly HRow[] HcfcdRows =
    {
        new(new[] { "SEGMENT 1  STA 10+00 TO 15+00", "", "" }, new[] { (0, 2) }),
        new(new[] { LblCenter, "N XX  E XX", "" }, new[] { (1, 2) }),
        new(new[] { "LATITUDE / LONGITUDE", "XX N  XX W", "" }, new[] { (1, 2) }),
        new(new[] { "OTHER AQUATIC FEATURES", "", "" }, new[] { (0, 2) }),
        new(new[] { LblWetlands, "XX AC", "" }, new[] { (1, 2) }),
        new(new[] { @"{\fArial|b1|i0|c0|p34;" + LblLength + "}", "XX LF", "" }, new[] { (1, 2) }),
        new(new[] { LblArea, "XX AC", "" }, new[] { (1, 2) }),
        new(new[] { "TOTAL CUT MATERIAL", "", "" }, new[] { (0, 2) }),
        new(new[] { LblMaterial, "XX CY", "XX CY/LF" }, Array.Empty<(int, int)>()),
    };

    private static ObjectId BuildHcfcd(Transaction tr, Database db, BlockTableRecord owner, Point3d pos, string tag)
    {
        var t = new Table();
        t.SetDatabaseDefaults(db);
        t.TableStyle = db.Tablestyle;
        t.Position = pos;
        t.SetSize(HcfcdRows.Length, 3);
        t.SetRowHeight(0.3);
        t.Columns[0].Width = 3.5;
        t.Columns[1].Width = 1.5;
        t.Columns[2].Width = 1.5;
        owner.AppendEntity(t);
        tr.AddNewlyCreatedDBObject(t, true);
        Say($"{tag} merges after SetSize (table style default): {MergeList(t)}");
        for (int r = 0; r < t.Rows.Count; r++)
            for (int c = 0; c < t.Columns.Count; c++)
                if (t.Cells[r, c].IsMerged == true) t.UnmergeCells(t.Cells[r, c].GetMergeRange());
        Say($"{tag} merges after unmerging all: {MergeList(t)}");
        for (int r = 0; r < HcfcdRows.Length; r++)
        {
            foreach (var (from, to) in HcfcdRows[r].Merges) t.MergeCells(CellRange.Create(t, r, from, r, to));
            for (int c = 0; c < 3; c++)
            {
                bool covered = HcfcdRows[r].Merges.Any(m => c > m.From && c <= m.To);
                if (!covered) t.Cells[r, c].TextString = HcfcdRows[r].Texts[c];
            }
        }
        t.GenerateLayout();
        return t.ObjectId;
    }

    private static int FindRowByLabel(Table t, string label)
    {
        for (int r = 0; r < t.Rows.Count; r++)
            if (Plain(t, r, 0).Trim().StartsWith(label, StringComparison.OrdinalIgnoreCase)) return r;
        return -1;
    }

    /// <summary>First column right of the label's merge range.</summary>
    private static int ValueCol(Table t, int r)
    {
        var cell = t.Cells[r, 0];
        return (cell.IsMerged == true ? cell.GetMergeRange().RightColumn : 0) + 1;
    }

    /// <summary>Writes values by label; returns the not-found labels.</summary>
    private static List<string> WriteBound(Table t, (string Label, string[] Values)[] items, string tag)
    {
        var missing = new List<string>();
        foreach (var (label, values) in items)
        {
            int r = FindRowByLabel(t, label);
            if (r < 0) { missing.Add(label); Say($"{tag} label '{label}' not found; skipped"); continue; }
            int c0 = ValueCol(t, r);
            for (int i = 0; i < values.Length && c0 + i < t.Columns.Count; i++)
                t.Cells[r, c0 + i].TextString = values[i];
            Say($"{tag} '{label}' found at row {r}; wrote {values.Length} value(s) from column {c0}");
        }
        return missing;
    }

    [CommandMethod("SVPROBEB_P24", CommandFlags.Modal)]
    public static void P24() => Run("P24", (doc, db) =>
    {
        RunHcfcd(db, SymbolUtilityServices.GetBlockModelSpaceId(db), new Point3d(0, -100, 0), "P24 model");
        var layId = CreateLayoutHeadless(db, "SV-P24", initialize: true, "P24");
        ObjectId btrId;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            btrId = ((Layout)tr.GetObject(layId, OpenMode.ForRead)).BlockTableRecordId;
            tr.Commit();
        }
        RunHcfcd(db, btrId, new Point3d(1.0, 10.0, 0), "P24 layout");
        Say($"P24 CTAB at end={AcApp.GetSystemVariable("CTAB")}");
    });

    private static void RunHcfcd(Database db, ObjectId ownerId, Point3d pos, string tag)
    {
        ObjectId tid;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForWrite);
            tid = BuildHcfcd(tr, db, owner, pos, tag);
            tr.Commit();
        }

        using (var tr = db.TransactionManager.StartTransaction())
        {
            var t = (Table)tr.GetObject(tid, OpenMode.ForRead);
            Say($"{tag} created: handle={t.Handle} owner={((BlockTableRecord)tr.GetObject(t.OwnerId, OpenMode.ForRead)).Name} {t.Rows.Count}x{t.Columns.Count} merges: {MergeList(t)}");
            foreach (var line in Grid(t)) Say($"{tag} tx1 {line}");
            var cell = t.Cells[5, 0];
            Say($"{tag} row 5 label reads: TextString='{cell.TextString}' | GetTextString(FormatOptionNone)='{cell.GetTextString(FormatOption.FormatOptionNone)}' | GetTextString(IgnoreMtextFormat)='{cell.GetTextString(FormatOption.IgnoreMtextFormat)}'");
            Say($"{tag} merged non-anchor cell (6,2): TextString='{t.Cells[6, 2].TextString}' IsMerged={t.Cells[6, 2].IsMerged}");
            tr.Commit();
        }

        // Update exactly one cell, found by label.
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var t = (Table)tr.GetObject(tid, OpenMode.ForWrite);
            var before = CellSnap(t);
            WriteBound(t, new[] { (LblArea, new[] { "1.23 AC" }) }, $"{tag} update-one:");
            var diffs = CellDiff(before, CellSnap(t));
            Say($"{tag} update-one: {diffs.Count} cell(s) changed: {string.Join("; ", diffs)}");
            tr.Commit();
        }

        // Bind the remaining values (a regen).
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var t = (Table)tr.GetObject(tid, OpenMode.ForWrite);
            var before = CellSnap(t);
            WriteBound(t, new[] { (LblLength, new[] { "1,234 LF" }), (LblMaterial, new[] { "567 CY", "0.46 CY/LF" }) }, $"{tag} bind:");
            var diffs = CellDiff(before, CellSnap(t));
            Say($"{tag} bind: {diffs.Count} cell(s) changed: {string.Join("; ", diffs)}");
            tr.Commit();
        }

        // The user deletes the WETLANDS row and types into the center-coordinates value (T31).
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var t = (Table)tr.GetObject(tid, OpenMode.ForWrite);
            int w = FindRowByLabel(t, LblWetlands);
            t.DeleteRows(w, 1);
            int cr = FindRowByLabel(t, LblCenter);
            t.Cells[cr, ValueCol(t, cr)].TextString = "N 13,812,345.67  E 3,101,234.56 (TYPED)";
            Say($"{tag} user edit: deleted row {w} ({LblWetlands}); typed into row {cr}; now {t.Rows.Count} rows; merges: {MergeList(t)}");
            tr.Commit();
        }

        // Regen again: rows have shifted; binding by label must still land.
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var t = (Table)tr.GetObject(tid, OpenMode.ForWrite);
            var before = CellSnap(t);
            var missing = WriteBound(t, new[]
            {
                (LblLength, new[] { "1,300 LF" }),
                (LblArea, new[] { "1.25 AC" }),
                (LblMaterial, new[] { "600 CY", "0.46 CY/LF" }),
            }, $"{tag} rebind:");
            var diffs = CellDiff(before, CellSnap(t));
            Say($"{tag} rebind: missing labels={missing.Count}; {diffs.Count} cell(s) changed: {string.Join("; ", diffs)}");
            foreach (var line in Grid(t)) Say($"{tag} final {line}");
            Say($"{tag} final handle={t.Handle} (created as same object, never recreated)");
            tr.Commit();
        }
    }
}

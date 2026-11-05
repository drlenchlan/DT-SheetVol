// A2 model writers: AutoCAD Table rendering from C2's TableModel. Owner: A2.
// Owner-agnostic (model space or a layout's paper space), so A3 can reuse it for paper tables.
//
// Rules from probes P13 and P24 (docs/api-findings.md section B.2, rules 6 and 7):
//   - the table style may merge row 0 on SetSize: unmerge everything first, then apply the
//     model's merges;
//   - Cell.IsMerged is true only on a merge's top-left cell; covered cells accept text silently
//     and store it hidden, so text is written only to anchors and unmerged cells;
//   - labels and cell text are read with GetTextString(FormatOption.IgnoreMtextFormat).

using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Tracking;
using AcCellRange = Autodesk.AutoCAD.DatabaseServices.CellRange;
using ModelCellRange = SheetVol.Core.Output.CellRange;

namespace SheetVol.Civil3D.WritersModel;

/// <summary>What an in-place table update changed.</summary>
/// <param name="RowsInserted">Rows inserted (an analysis block added, E505).</param>
/// <param name="RowsDeleted">Rows deleted (an analysis block removed, E505).</param>
/// <param name="CellsWritten">Cells whose text was rewritten.</param>
/// <param name="MergesChanged">Merges removed plus merges added.</param>
public sealed record TableUpdateStats(int RowsInserted, int RowsDeleted, int CellsWritten, int MergesChanged);

/// <summary>Creates AutoCAD tables from <see cref="TableModel"/>s and updates them in place.</summary>
public static class TableRenderer
{
    /// <summary>
    /// Creates a table in <paramref name="ownerBlockId"/> (model space or a layout block), sized
    /// from its text, with the model's merges and text, and puts <paramref name="corner"/> on
    /// <paramref name="anchor"/>. Style, layer and text height come from the caller (config).
    /// </summary>
    public static Table Create(
        Transaction tr, Database db, ObjectId ownerBlockId, TableModel model,
        ObjectId tableStyleId, ObjectId layerId, double textHeight, TableLayoutConfig layout,
        Pt anchor, AnchorCorner corner)
    {
        var t = new Table();
        t.SetDatabaseDefaults(db);
        t.TableStyle = tableStyleId;
        t.LayerId = layerId;
        t.Position = new Point3d(anchor.X, anchor.Y, 0);
        t.SetSize(model.Rows.Count, model.ColumnCount);
        var widths = ColumnWidths(model, textHeight, layout);
        for (int c = 0; c < model.ColumnCount; c++) t.Columns[c].Width = widths[c];
        for (int r = 0; r < model.Rows.Count; r++) t.Rows[r].Height = textHeight * layout.RowHeightFactor;

        var owner = (BlockTableRecord)tr.GetObject(ownerBlockId, OpenMode.ForWrite);
        owner.AppendEntity(t);
        tr.AddNewlyCreatedDBObject(t, true);

        UnmergeAll(t);
        for (int r = 0; r < model.Rows.Count; r++)
            for (int c = 0; c < model.ColumnCount; c++)
                t.Cells[r, c].TextHeight = textHeight;
        foreach (var m in model.Merges) t.MergeCells(AcCellRange.Create(t, m.FirstRow, m.FirstColumn, m.LastRow, m.LastColumn));
        var covered = Covered(model.Merges);
        for (int r = 0; r < model.Rows.Count; r++)
            for (int c = 0; c < model.ColumnCount; c++)
                if (!covered.Contains((r, c)) && model.Cell(r, c).Length > 0) t.Cells[r, c].TextString = Escape(model.Cell(r, c));
        t.GenerateLayout();

        if (corner == AnchorCorner.BottomLeft) t.Position = new Point3d(anchor.X, anchor.Y + t.Height, 0);
        else if (corner == AnchorCorner.TopRight) t.Position = new Point3d(anchor.X - t.Width, anchor.Y, 0);
        return t;
    }

    /// <summary>
    /// Updates a table in place to match <paramref name="model"/>: rows are inserted or deleted
    /// where <paramref name="oldRowKeys"/> and <paramref name="newRowKeys"/> differ (so an
    /// analysis block grows or shrinks the table without moving the rest, E505), merges are
    /// corrected, and only cells whose plain text differs are rewritten. Position, table style,
    /// layer and column widths are never touched (E502). The table must be open for write.
    /// </summary>
    public static TableUpdateStats Update(Table t, TableModel model, IReadOnlyList<string> oldRowKeys, IReadOnlyList<string> newRowKeys,
        double textHeightForNewRows, TableLayoutConfig layout)
    {
        double newRowHeight = textHeightForNewRows * layout.RowHeightFactor;
        int inserted = 0, deleted = 0, written = 0, mergesChanged = 0;

        // Columns: generated tables keep their column count; this only corrects a user edit.
        if (t.Columns.Count < model.ColumnCount)
            t.InsertColumns(t.Columns.Count, t.Columns[t.Columns.Count - 1].Width, model.ColumnCount - t.Columns.Count);
        else if (t.Columns.Count > model.ColumnCount)
            t.DeleteColumns(model.ColumnCount, t.Columns.Count - model.ColumnCount);

        // Rows: align old and new keys; delete the unmatched old rows bottom-up, then insert the
        // unmatched new rows top-down. When the stored keys no longer describe the table (a user
        // added or deleted rows), fall back to resizing at the end.
        if (oldRowKeys.Count == t.Rows.Count)
        {
            var (keepOld, keepNew) = Lcs(oldRowKeys, newRowKeys);
            for (int i = oldRowKeys.Count - 1; i >= 0; i--)
                if (!keepOld.Contains(i)) { t.DeleteRows(i, 1); deleted++; }
            for (int j = 0; j < newRowKeys.Count; j++)
                if (!keepNew.Contains(j)) { InsertRow(t, j, textHeightForNewRows, newRowHeight); inserted++; }
        }
        else
        {
            while (t.Rows.Count > model.Rows.Count) { t.DeleteRows(t.Rows.Count - 1, 1); deleted++; }
            while (t.Rows.Count < model.Rows.Count) { InsertRow(t, t.Rows.Count, textHeightForNewRows, newRowHeight); inserted++; }
        }

        // Merges: remove what the model does not have, write text, then add what is missing.
        var planned = model.Merges.Select(Key).ToHashSet();
        foreach (var live in LiveMerges(t).Where(m => !planned.Contains(Key(m))).ToList())
        {
            t.UnmergeCells(AcCellRange.Create(t, live.FirstRow, live.FirstColumn, live.LastRow, live.LastColumn));
            mergesChanged++;
        }
        var covered = Covered(model.Merges);
        for (int r = 0; r < model.Rows.Count; r++)
            for (int c = 0; c < model.ColumnCount; c++)
            {
                if (covered.Contains((r, c))) continue;
                string want = model.Cell(r, c);
                if (Shows(t, r, c, want)) continue;
                t.Cells[r, c].TextString = Escape(want);
                written++;
            }
        var present = LiveMerges(t).Select(Key).ToHashSet();
        foreach (var m in model.Merges.Where(m => !present.Contains(Key(m))))
        {
            // Text left in a cell about to be merged over would be kept hidden (P13): clear it first.
            for (int r = m.FirstRow; r <= m.LastRow; r++)
                for (int c = m.FirstColumn; c <= m.LastColumn; c++)
                    if ((r != m.FirstRow || c != m.FirstColumn) && Plain(t, r, c).Length > 0) { t.Cells[r, c].TextString = ""; written++; }
            t.MergeCells(AcCellRange.Create(t, m.FirstRow, m.FirstColumn, m.LastRow, m.LastColumn));
            mergesChanged++;
        }
        t.GenerateLayout();
        return new TableUpdateStats(inserted, deleted, written, mergesChanged);
    }

    /// <summary>Plain cell text (MTEXT formatting removed), never null (P24).</summary>
    public static string Plain(Table t, int row, int column) => t.Cells[row, column].GetTextString(FormatOption.IgnoreMtextFormat) ?? "";

    /// <summary>Cell text as written: cell text is MTEXT, so backslashes and braces are escaped to show literally.</summary>
    public static string Escape(string text) => MatchTextWriter.Escape(text);

    /// <summary>The cell's raw MTEXT contents, never null.</summary>
    public static string Raw(Table t, int row, int column) => t.Cells[row, column].GetTextString(FormatOption.FormatOptionNone) ?? "";

    /// <summary>
    /// True when the cell shows <paramref name="want"/>: its plain text equals it (a user's bold
    /// or colour on the same words still counts), or its raw contents equal the escaped text.
    /// The second test is needed because the plain read drops escaped braces (MEASURED: written
    /// <c>EG \{v2\}</c> reads back plain as <c>EG v2</c>).
    /// </summary>
    public static bool Shows(Table t, int row, int column, string want)
        => Plain(t, row, column) == want || Raw(t, row, column) == Escape(want);

    /// <summary>Every cell's plain text, row by row.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> ReadCells(Table t)
    {
        var rows = new List<IReadOnlyList<string>>(t.Rows.Count);
        for (int r = 0; r < t.Rows.Count; r++)
        {
            var cells = new string[t.Columns.Count];
            for (int c = 0; c < cells.Length; c++) cells[c] = Plain(t, r, c);
            rows.Add(cells);
        }
        return rows;
    }

    /// <summary>The table's merges, read from the anchors (P13: only the anchor reports IsMerged).</summary>
    public static IReadOnlyList<ModelCellRange> LiveMerges(Table t)
    {
        var list = new List<ModelCellRange>();
        for (int r = 0; r < t.Rows.Count; r++)
            for (int c = 0; c < t.Columns.Count; c++)
            {
                var cell = t.Cells[r, c];
                if (cell.IsMerged != true) continue;
                var m = cell.GetMergeRange();
                if (m.TopRow == r && m.LeftColumn == c) list.Add(new ModelCellRange(m.TopRow, m.LeftColumn, m.BottomRow, m.RightColumn));
            }
        return list;
    }

    /// <summary>Cells inside a merge other than its top-left anchor.</summary>
    public static HashSet<(int Row, int Column)> Covered(IEnumerable<ModelCellRange> merges)
    {
        var covered = new HashSet<(int, int)>();
        foreach (var m in merges)
            for (int r = m.FirstRow; r <= m.LastRow; r++)
                for (int c = m.FirstColumn; c <= m.LastColumn; c++)
                    if (r != m.FirstRow || c != m.FirstColumn) covered.Add((r, c));
        return covered;
    }

    /// <summary>
    /// Row keys of a model: kind, analysis and segment plus an occurrence number, so identical
    /// rows (several footers) stay distinct. Stored in the table's ownership tag (E505).
    /// </summary>
    public static IReadOnlyList<string> RowKeys(TableModel model)
    {
        var seen = new Dictionary<string, int>();
        var keys = new List<string>(model.Rows.Count);
        foreach (var row in model.Rows)
        {
            string baseKey = $"{row.Kind}|{row.AnalysisId?.ToString("D") ?? ""}|{row.SegmentId?.ToString("D") ?? ""}";
            seen.TryGetValue(baseKey, out int n);
            seen[baseKey] = n + 1;
            keys.Add(baseKey + "#" + n.ToString(CultureInfo.InvariantCulture));
        }
        return keys;
    }

    /// <summary>Content signature of a model as it would be drawn: row keys, size, text (covered cells blank) and merges.</summary>
    public static string Signature(TableModel model)
    {
        var covered = Covered(model.Merges);
        var texts = new List<string?> { "table", N(model.Rows.Count), N(model.ColumnCount) };
        texts.AddRange(RowKeys(model));
        texts.Add("|cells|");
        for (int r = 0; r < model.Rows.Count; r++)
            for (int c = 0; c < model.ColumnCount; c++)
                texts.Add(covered.Contains((r, c)) ? "" : model.Cell(r, c));
        texts.Add("|merges|");
        texts.AddRange(model.Merges.Select(Key).Order(StringComparer.Ordinal));
        return ContentSignatures.OfTexts(texts);
    }

    /// <summary>
    /// Content signature of a live table with the row keys from its tag, comparable with
    /// <see cref="Signature(TableModel)"/>: a cell that <see cref="Shows"/> the model's text at the
    /// same position contributes that text, any other cell its plain text.
    /// </summary>
    public static string Signature(Table t, IReadOnlyList<string> rowKeys, TableModel model)
    {
        var merges = LiveMerges(t);
        var covered = Covered(merges);   // hidden text under a merge is not shown, so it is not content
        var texts = new List<string?> { "table", N(t.Rows.Count), N(t.Columns.Count) };
        texts.AddRange(rowKeys);
        texts.Add("|cells|");
        for (int r = 0; r < t.Rows.Count; r++)
            for (int c = 0; c < t.Columns.Count; c++)
            {
                if (covered.Contains((r, c))) { texts.Add(""); continue; }
                bool atModel = r < model.Rows.Count && c < model.ColumnCount;
                texts.Add(atModel && Shows(t, r, c, model.Cell(r, c)) ? model.Cell(r, c) : Plain(t, r, c));
            }
        texts.Add("|merges|");
        texts.AddRange(merges.Select(Key).Order(StringComparer.Ordinal));
        return ContentSignatures.OfTexts(texts);
    }

    /// <summary>Checks a model is renderable: at least one row and column, every row the column count wide, merges inside the table and not overlapping.</summary>
    public static IEnumerable<string> Check(TableModel model)
    {
        if (model.ColumnCount < 1 || model.Rows.Count < 1) { yield return "has no rows or no columns"; yield break; }
        for (int r = 0; r < model.Rows.Count; r++)
            if (model.Rows[r].Cells.Count != model.ColumnCount) yield return $"row {r} has {model.Rows[r].Cells.Count} cells; the table has {model.ColumnCount} columns";
        var seen = new HashSet<(int, int)>();
        foreach (var m in model.Merges)
        {
            if (m.FirstRow < 0 || m.FirstColumn < 0 || m.LastRow < m.FirstRow || m.LastColumn < m.FirstColumn || m.LastRow >= model.Rows.Count || m.LastColumn >= model.ColumnCount)
            {
                yield return $"merge {Key(m)} is outside the table or reversed";
                continue;
            }
            for (int r = m.FirstRow; r <= m.LastRow; r++)
                for (int c = m.FirstColumn; c <= m.LastColumn; c++)
                    if (!seen.Add((r, c))) yield return $"merges overlap at row {r}, column {c}";
        }
    }

    /// <summary>
    /// Column widths from the text at creation: the longest unmerged text in each column, then
    /// widened evenly where a merged cell needs more than its columns give. Factors from config.
    /// </summary>
    public static double[] ColumnWidths(TableModel model, double textHeight, TableLayoutConfig layout)
    {
        double Need(string text) => (text.Length * layout.CharacterWidthFactor + 2 * layout.CellMarginFactor) * textHeight;
        var widths = Enumerable.Repeat(layout.MinimumColumnWidthFactor * textHeight, model.ColumnCount).ToArray();
        var inMerge = new HashSet<(int, int)>();
        foreach (var m in model.Merges.Where(m => m.LastColumn > m.FirstColumn))
            for (int r = m.FirstRow; r <= m.LastRow; r++)
                for (int c = m.FirstColumn; c <= m.LastColumn; c++) inMerge.Add((r, c));
        for (int r = 0; r < model.Rows.Count; r++)
            for (int c = 0; c < model.ColumnCount; c++)
                if (!inMerge.Contains((r, c))) widths[c] = Math.Max(widths[c], Need(model.Cell(r, c)));
        foreach (var m in model.Merges.Where(m => m.LastColumn > m.FirstColumn))
        {
            double have = 0;
            for (int c = m.FirstColumn; c <= m.LastColumn; c++) have += widths[c];
            double need = Need(model.Cell(m.FirstRow, m.FirstColumn));
            if (need <= have) continue;
            double extra = (need - have) / (m.LastColumn - m.FirstColumn + 1);
            for (int c = m.FirstColumn; c <= m.LastColumn; c++) widths[c] += extra;
        }
        return widths;
    }

    private static void UnmergeAll(Table t)
    {
        foreach (var m in LiveMerges(t))
            t.UnmergeCells(AcCellRange.Create(t, m.FirstRow, m.FirstColumn, m.LastRow, m.LastColumn));
    }

    private static void InsertRow(Table t, int index, double textHeight, double rowHeight)
    {
        if (index > 0)
        {
            t.InsertRowsAndInherit(index, index - 1, 1);   // formatting from the row above, so a restyled table stays consistent (E502)
            return;
        }
        t.InsertRows(0, t.Rows.Count > 0 ? t.Rows[0].Height : rowHeight, 1);
        for (int c = 0; c < t.Columns.Count; c++) t.Cells[0, c].TextHeight = textHeight;
    }

    /// <summary>Indices kept on each side by a longest common subsequence of the two key lists.</summary>
    private static (HashSet<int> Old, HashSet<int> New) Lcs(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var len = new int[a.Count + 1, b.Count + 1];
        for (int i = a.Count - 1; i >= 0; i--)
            for (int j = b.Count - 1; j >= 0; j--)
                len[i, j] = a[i] == b[j] ? len[i + 1, j + 1] + 1 : Math.Max(len[i + 1, j], len[i, j + 1]);
        var keepA = new HashSet<int>();
        var keepB = new HashSet<int>();
        for (int i = 0, j = 0; i < a.Count && j < b.Count;)
        {
            if (a[i] == b[j]) { keepA.Add(i++); keepB.Add(j++); }
            else if (len[i + 1, j] >= len[i, j + 1]) i++;
            else j++;
        }
        return (keepA, keepB);
    }

    private static string Key(ModelCellRange m) => $"[{m.FirstRow},{m.FirstColumn}..{m.LastRow},{m.LastColumn}]";

    private static string N(int v) => v.ToString(CultureInfo.InvariantCulture);
}

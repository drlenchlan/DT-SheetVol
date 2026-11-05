// A3 work package. Owner: A3.
//
// Paper-space tables from C2's TableModel (HANDOFF.md 6.7, 6.8, P13, E501-E505).
//
// Create once at the anchor with the configured style, layer and text height; afterwards
// update cell text only (E502: position, style, layer, widths and heights the user set are
// kept). Rows are identified by keys (RowKind, analysis, ordinal) stored in the table's
// owner tag, so an analysis added or removed grows or shrinks the table by whole blocks in
// place (E505) and the handle never changes. Text is compared as plain text
// (GetTextString(IgnoreMtextFormat), P24) so formatting the user applied to a cell whose
// value did not change survives, and an unchanged regen writes nothing (E811). Only merge
// anchors are written (P13: covered cells accept text silently and store it hidden).

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Tracking;
using AcCellRange = Autodesk.AutoCAD.DatabaseServices.CellRange;
using ModelRange = SheetVol.Core.Output.CellRange;

namespace SheetVol.Civil3D.WritersPaper;

/// <summary>Content signatures and row keys for paper tables.</summary>
public static class PaperTableSignatures
{
    /// <summary>Row keys of a model, top to bottom: kind, analysis id ("-" when none), ordinal among rows of that kind and analysis.</summary>
    public static IReadOnlyList<string> RowKeys(TableModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var counts = new Dictionary<(RowKind, Guid?), int>();
        var keys = new List<string>(model.Rows.Count);
        foreach (var r in model.Rows)
        {
            counts.TryGetValue((r.Kind, r.AnalysisId), out int n);
            counts[(r.Kind, r.AnalysisId)] = n + 1;
            keys.Add($"{r.Kind}|{(r.AnalysisId is Guid g ? g.ToString("N") : "-")}|{n}");
        }
        return keys;
    }

    /// <summary>Signature of what the writer puts in a table: size, merges and every cell's text (covered cells as written in the model, which C2 leaves empty).</summary>
    public static string Of(TableModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var texts = new List<string?> { $"size:{model.Rows.Count}x{model.ColumnCount}" };
        texts.AddRange(model.Merges.Select(m => $"merge:{m.FirstRow},{m.FirstColumn},{m.LastRow},{m.LastColumn}"));
        foreach (var r in model.Rows) texts.AddRange(r.Cells);
        return ContentSignatures.OfTexts(texts);
    }
}

/// <summary>Creates and updates one paper table. Used by <see cref="PaperWriter"/>.</summary>
internal sealed class PaperTableWriter(PaperConfig config)
{
    /// <summary>Owner-tag qualifier for a table of this kind.</summary>
    internal static string Qualifier(TableModel model) => model.Kind.ToString();

    /// <summary>Problems with the model's style and layer in <paramref name="db"/> (E501), empty when both exist.</summary>
    internal static IEnumerable<Problem> CheckStyle(Transaction tr, Database db, TableModel model, Guid segmentId)
    {
        var styles = (DBDictionary)tr.GetObject(db.TableStyleDictionaryId, OpenMode.ForRead);
        if (!styles.Contains(model.Style.TableStyle))
            yield return new Problem(PaperCodes.TableStyleMissing, Severity.Error,
                $"Table style '{model.Style.TableStyle}' named in config is not in this drawing.", segmentId,
                "Create the table style in the drawing (or its template), or correct the table style name in config.");
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (!layers.Has(model.Style.Layer))
            yield return new Problem(PaperCodes.TableStyleMissing, Severity.Error,
                $"Table layer '{model.Style.Layer}' named in config is not in this drawing.", segmentId,
                "Create the layer in the drawing (or its template), or correct the table layer name in config.");
    }

    /// <summary>Structural checks on a model; empty when it can be rendered.</summary>
    internal static IEnumerable<Problem> CheckModel(TableModel model, Guid segmentId)
    {
        if (model.ColumnCount < 1 || model.Rows.Count < 1)
            yield return new Problem(PaperCodes.WriterFailed, Severity.Error, $"Table model for segment {segmentId} has {model.Rows.Count} rows and {model.ColumnCount} columns.", segmentId);
        else if (model.Rows.Any(r => r.Cells.Count != model.ColumnCount))
            yield return new Problem(PaperCodes.WriterFailed, Severity.Error, $"Table model for segment {segmentId} has a row whose cell count differs from {model.ColumnCount}.", segmentId);
        foreach (var m in model.Merges)
            if (m.FirstRow < 0 || m.FirstColumn < 0 || m.LastRow >= model.Rows.Count || m.LastColumn >= model.ColumnCount || m.LastRow < m.FirstRow || m.LastColumn < m.FirstColumn)
                yield return new Problem(PaperCodes.WriterFailed, Severity.Error, $"Table model for segment {segmentId} has a merge outside the table.", segmentId);
    }

    /// <summary>The owned table of this kind on the layout, or Null. A second claimant is reported and left alone.</summary>
    internal ObjectId Find(Transaction tr, BlockTableRecord layoutBlock, Guid runId, Guid segmentId, string qualifier, List<Problem> problems)
    {
        var found = new List<Table>();
        foreach (ObjectId id in layoutBlock)
        {
            if (id.IsErased || !id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Table)))) continue;
            var t = (Table)tr.GetObject(id, OpenMode.ForRead);
            var tag = OwnerTags.Read(tr, t, config.OwnerTagKey);
            if (tag is { Role: OwnedRole.PaperTable } && tag.RunId == runId && tag.SegmentId == segmentId && tag.Qualifier == qualifier) found.Add(t);
        }
        if (found.Count == 0) return ObjectId.Null;
        var keep = found.OrderBy(t => t.Handle.Value).First();
        foreach (var other in found.Where(t => t != keep))
            problems.Add(new Problem(PaperCodes.TableRebuilt, Severity.Warning,
                $"Table {other.Handle} also claims this sheet's {qualifier} table (copied?); table {keep.Handle} is updated and {other.Handle} is left alone.", segmentId));
        return keep.ObjectId;
    }

    /// <summary>
    /// Creates the table at <paramref name="anchor"/> when <paramref name="tableId"/> is Null,
    /// else brings the existing table's rows, merges and text in line with the model.
    /// </summary>
    internal PaperChange Apply(Transaction tr, Database db, BlockTableRecord layoutBlock, ref ObjectId tableId, TableModel model, Pt anchor,
                               bool layoutFresh, Guid runId, Guid segmentId, WriteCounter writes, List<Problem> problems, string layoutName)
    {
        var keys = PaperTableSignatures.RowKeys(model);
        var tag = new OwnerTag(runId, segmentId, OwnedRole.PaperTable, Qualifier(model), PaperTableSignatures.Of(model), keys);
        var covered = Covered(model);

        if (tableId.IsNull)
        {
            tableId = Create(tr, db, layoutBlock, model, anchor, covered, writes);
            var created = OwnerTags.OpenForWrite<Table>(tr, tableId);
            OwnerTags.Write(tr, created, config.OwnerTagKey, tag);
            if (!layoutFresh)
                problems.Add(new Problem(PaperCodes.TableRecreated, Severity.Info,
                    $"The sheet table on layout '{layoutName}' was missing; it was created at the anchor ({anchor.X:R}, {anchor.Y:R}).", segmentId));
            return PaperChange.Created;
        }

        var t = (Table)tr.GetObject(tableId, OpenMode.ForRead);
        var stored = OwnerTags.Read(tr, t, config.OwnerTagKey)?.Extras;
        bool sameShape = stored is not null && stored.SequenceEqual(keys) && t.Rows.Count == model.Rows.Count && t.Columns.Count == model.ColumnCount;
        if (sameShape && MergesMatch(t, model) && TextsMatch(t, model, covered))
        {
            if (OwnerTags.Matches(tr, t, config.OwnerTagKey, tag)) return PaperChange.Unchanged;
            t = OwnerTags.OpenForWrite<Table>(tr, tableId);
            writes.Add();
            OwnerTags.Write(tr, t, config.OwnerTagKey, tag);
            return PaperChange.Updated;
        }

        t = OwnerTags.OpenForWrite<Table>(tr, tableId);
        writes.Add();
        if (LayerLocked(tr, t.LayerId))
            problems.Add(new Problem(PaperCodes.TableOnLockedLayer, Severity.Info,
                $"The sheet table on layout '{layoutName}' is on locked layer '{t.Layer}'; it was updated and the layer stays locked.", segmentId));

        bool structural = false;
        if (!sameShape)
        {
            structural = true;
            int added = 0, removed = 0;
            bool reshaped = stored is not null && stored.Count == t.Rows.Count && t.Columns.Count == model.ColumnCount
                            && Reshape(t, stored, keys, out added, out removed);
            if (reshaped)
            {
                if (added + removed > 0)
                    problems.Add(new Problem(PaperCodes.TableResized, Severity.Info,
                        $"The sheet table on layout '{layoutName}' grew by {added} and shrank by {removed} row(s) in place to follow the analyses.", segmentId));
            }
            else
            {
                Rebuild(t, model);
                problems.Add(new Problem(PaperCodes.TableRebuilt, Severity.Warning,
                    $"The rows or columns of the sheet table on layout '{layoutName}' were changed by hand; it was rebuilt in place, keeping its position, style and layer.", segmentId));
            }
        }

        SyncMerges(t, model);
        WriteTexts(t, model, covered);
        if (structural) t.GenerateLayout();
        OwnerTags.Write(tr, t, config.OwnerTagKey, tag);
        return PaperChange.Updated;
    }

    private ObjectId Create(Transaction tr, Database db, BlockTableRecord layoutBlock, TableModel model, Pt anchor, HashSet<(int, int)> covered, WriteCounter writes)
    {
        var styles = (DBDictionary)tr.GetObject(db.TableStyleDictionaryId, OpenMode.ForRead);
        if (!layoutBlock.IsWriteEnabled) layoutBlock.UpgradeOpen();
        var t = new Table();
        t.SetDatabaseDefaults(db);
        t.TableStyle = styles.GetAt(model.Style.TableStyle);
        t.Layer = model.Style.Layer;
        t.Position = new Point3d(anchor.X, anchor.Y, 0.0);
        t.SetSize(model.Rows.Count, model.ColumnCount);
        t.SetRowHeight(config.TableRowHeight);
        for (int c = 0; c < model.ColumnCount; c++) t.Columns[c].Width = config.ColumnWidth(c);
        var id = layoutBlock.AppendEntity(t);
        tr.AddNewlyCreatedDBObject(t, true);
        writes.Add();

        UnmergeAll(t);
        foreach (var m in model.Merges) t.MergeCells(AcCellRange.Create(t, m.FirstRow, m.FirstColumn, m.LastRow, m.LastColumn));
        for (int r = 0; r < model.Rows.Count; r++)
            for (int c = 0; c < model.ColumnCount; c++)
            {
                t.Cells[r, c].TextHeight = model.Style.TextHeight;
                if (!covered.Contains((r, c))) t.Cells[r, c].TextString = model.Cell(r, c);
            }
        t.GenerateLayout();
        return id;
    }

    /// <summary>
    /// Deletes rows whose key the model no longer has and inserts the model's new rows at their
    /// places, inheriting format from a row of the same kind (E505). False when the surviving
    /// rows are not in model order (analyses reordered): the caller rebuilds instead.
    /// </summary>
    private static bool Reshape(Table t, IReadOnlyList<string> stored, IReadOnlyList<string> model, out int added, out int removed)
    {
        added = removed = 0;
        var wanted = new HashSet<string>(model);
        var live = stored.ToList();
        var surviving = live.Where(wanted.Contains).ToList();
        int mi = 0;
        foreach (var k in surviving)
        {
            while (mi < model.Count && model[mi] != k) mi++;
            if (mi == model.Count) return false;
            mi++;
        }
        if (surviving.Count == 0) return false;

        for (int i = live.Count - 1; i >= 0; i--)
            if (!wanted.Contains(live[i])) { t.DeleteRows(i, 1); live.RemoveAt(i); removed++; }

        for (int i = 0; i < model.Count; i++)
        {
            if (i < live.Count && live[i] == model[i]) continue;
            string kind = model[i].Split('|')[0];
            int inherit = live.FindIndex(k => k.Split('|')[0] == kind);
            if (inherit < 0) inherit = Math.Max(0, Math.Min(i, live.Count) - 1);
            t.InsertRowsAndInherit(i, inherit, 1);
            live.Insert(i, model[i]);
            added++;
        }
        return live.SequenceEqual(model);
    }

    /// <summary>Resizes the table to the model in place and drops every merge; position, style and layer stay.</summary>
    private static void Rebuild(Table t, TableModel model)
    {
        int oldRows = t.Rows.Count;
        UnmergeAll(t);
        if (t.Rows.Count != model.Rows.Count || t.Columns.Count != model.ColumnCount) t.SetSize(model.Rows.Count, model.ColumnCount);
        for (int r = oldRows; r < model.Rows.Count; r++)
            for (int c = 0; c < model.ColumnCount; c++)
                t.Cells[r, c].TextHeight = model.Style.TextHeight;
    }

    private static void UnmergeAll(Table t)
    {
        for (int r = 0; r < t.Rows.Count; r++)
            for (int c = 0; c < t.Columns.Count; c++)
                if (t.Cells[r, c].IsMerged == true) t.UnmergeCells(t.Cells[r, c].GetMergeRange());
    }

    private static HashSet<(int, int, int, int)> LiveMerges(Table t)
    {
        var set = new HashSet<(int, int, int, int)>();
        for (int r = 0; r < t.Rows.Count; r++)
            for (int c = 0; c < t.Columns.Count; c++)
                if (t.Cells[r, c].IsMerged == true)
                {
                    var m = t.Cells[r, c].GetMergeRange();
                    set.Add((m.TopRow, m.LeftColumn, m.BottomRow, m.RightColumn));
                }
        return set;
    }

    private static HashSet<(int, int, int, int)> ModelMerges(TableModel model)
        => model.Merges.Select(m => (m.FirstRow, m.FirstColumn, m.LastRow, m.LastColumn)).ToHashSet();

    private static bool MergesMatch(Table t, TableModel model) => LiveMerges(t).SetEquals(ModelMerges(model));

    private static void SyncMerges(Table t, TableModel model)
    {
        var live = LiveMerges(t);
        var want = ModelMerges(model);
        foreach (var m in live.Where(m => !want.Contains(m)))
            t.UnmergeCells(AcCellRange.Create(t, m.Item1, m.Item2, m.Item3, m.Item4));
        foreach (var m in want.Where(m => !live.Contains(m)))
            t.MergeCells(AcCellRange.Create(t, m.Item1, m.Item2, m.Item3, m.Item4));
    }

    private static string Plain(Table t, int r, int c) => t.Cells[r, c].GetTextString(FormatOption.IgnoreMtextFormat) ?? "";

    private static bool TextsMatch(Table t, TableModel model, HashSet<(int, int)> covered)
    {
        for (int r = 0; r < model.Rows.Count; r++)
            for (int c = 0; c < model.ColumnCount; c++)
                if (!covered.Contains((r, c)) && Plain(t, r, c) != model.Cell(r, c)) return false;
        return true;
    }

    private static void WriteTexts(Table t, TableModel model, HashSet<(int, int)> covered)
    {
        for (int r = 0; r < model.Rows.Count; r++)
            for (int c = 0; c < model.ColumnCount; c++)
                if (!covered.Contains((r, c)) && Plain(t, r, c) != model.Cell(r, c))
                    t.Cells[r, c].TextString = model.Cell(r, c);
    }

    /// <summary>Cells inside a model merge other than its top-left anchor.</summary>
    internal static HashSet<(int, int)> Covered(TableModel model)
    {
        var set = new HashSet<(int, int)>();
        foreach (ModelRange m in model.Merges)
            for (int r = m.FirstRow; r <= m.LastRow; r++)
                for (int c = m.FirstColumn; c <= m.LastColumn; c++)
                    if (r != m.FirstRow || c != m.FirstColumn) set.Add((r, c));
        return set;
    }

    private static bool LayerLocked(Transaction tr, ObjectId layerId)
        => !layerId.IsNull && ((LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead)).IsLocked;
}

// A2 model writers: per-sheet tables, the master table and the HCFCD label-bound tables.
// Owner: A2.
//
// Created once at the placement anchor, then only cell text (and, for generated tables, the
// analysis blocks) changes: position, style and layer the user set are kept (6.7, E502). An
// erased table is recreated at the anchor (E503). Style, layer and text height come from C2's
// config; a missing style or layer is SV-E501 before anything is written (E501).

using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Tracking;
using SheetVol.Core.Volumes;

namespace SheetVol.Civil3D.WritersModel;

/// <summary>Placement resolution shared by the table writers (6.7, A14, E601, E1017).</summary>
internal static class TablePlacements
{
    /// <summary>
    /// The owner block and anchor for a table about to be created, or null with a problem: an
    /// SV-E601 error for the master table (the run fails before writing), an SV-E1017 warning
    /// for a segment's table (that table is skipped, never created elsewhere).
    /// </summary>
    public static (ObjectId Owner, Pt Anchor, AnchorCorner Corner)? Resolve(
        Transaction tr, Database db, OwnedKey key, TablePlacement p, ModelWritersConfig config, ReportConfig report, List<Problem> problems)
    {
        if (p.LayoutName is null)
        {
            if (p.Anchor is Pt a && double.IsFinite(a.X) && double.IsFinite(a.Y))
                return (SymbolUtilityServices.GetBlockModelSpaceId(db), a, p.Corner);
            problems.Add(new Problem(ModelWriterCodes.InvalidInput, Severity.Error, $"Model-space table {key} has no anchor point.", SegmentOf(key)));
            return null;
        }

        var block = DrawingNames.LayoutBlock(tr, db, p.LayoutName);
        Pt? anchor = block is null ? null
            : DrawingNames.AnchorPoint(tr, block.Value, report.Placement.TableAnchorBlock)
              ?? (config.Tables.TablePaperPoint is { } pp ? new Pt(pp.X, pp.Y) : null);
        if (block is not null && anchor is not null) return (block.Value, anchor.Value, AnchorCorner.TopLeft);

        string what = block is null
            ? $"layout '{p.LayoutName}' does not exist"
            : $"layout '{p.LayoutName}' has no {report.Placement.TableAnchorBlock} block and modelWriters.tables.tablePaperPoint is not set";
        if (key.Role == OwnedRole.MasterTable)
            problems.Add(new Problem(ModelWriterCodes.LayoutOrAnchorMissing, Severity.Error,
                $"The master table cannot be created: {what}. Nothing was written.",
                Fix: "Create the layout or its anchor block, set the table paper point, or place the master table in model space."));
        else
            problems.Add(new Problem(ModelWriterCodes.TableLayoutGone, Severity.Warning,
                $"The table of segment {key.SegmentId:D} is gone and cannot be created: {what}. It is not re-created anywhere else.",
                key.SegmentId, "Restore the layout (or its anchor), or clear the segment's target layout."));
        return null;
    }

    public static Guid? SegmentOf(OwnedKey key) => key.SegmentId == Guid.Empty ? null : key.SegmentId;

    /// <summary>The config path naming a table kind's style settings, for SV-E501 messages.</summary>
    public static string StylePath(TableKind kind) => kind switch
    {
        TableKind.Segment => "segmentTable.style",
        TableKind.Master => "masterTable.style",
        _ => "hcfcd.style",
    };
}

/// <summary>
/// Per-sheet tables (role ModelTable, qualifier "standard") and the master table (role
/// MasterTable) rendered from C2's table models. Analysis blocks grow or shrink in place (E505)
/// using the row keys stored in the table's ownership tag.
/// </summary>
public sealed class ModelTableWriter : OwnedWriter<ModelTableSpec, Table>
{
    private readonly Dictionary<(string Style, string Layer), (ObjectId Style, ObjectId Layer)> _resources = [];
    private readonly Dictionary<OwnedKey, (ObjectId Owner, Pt Anchor, AnchorCorner Corner)> _placements = [];
    private readonly Dictionary<OwnedKey, IReadOnlyList<string>> _liveRowKeys = [];

    /// <summary>A writer in the caller's context.</summary>
    public ModelTableWriter(ModelWriteContext ctx) : base(ctx) { }

    /// <inheritdoc/>
    protected override string What => "table";

    /// <inheritdoc/>
    protected override string RecreatedCode => ModelWriterCodes.TableRecreated;

    /// <inheritdoc/>
    public override bool Owns(OwnedKey key)
        => (key.Role == OwnedRole.ModelTable && key.Qualifier == OwnedQualifiers.StandardTable) || key.Role == OwnedRole.MasterTable;

    /// <inheritdoc/>
    protected override IEnumerable<Problem> Validate(OwnedKey key, ModelTableSpec spec)
    {
        foreach (string why in TableRenderer.Check(spec.Model))
            yield return new Problem(ModelWriterCodes.InvalidInput, Severity.Error, $"Table model for {key} {why}.", TablePlacements.SegmentOf(key));
        bool kindOk = key.Role == OwnedRole.MasterTable ? spec.Model.Kind == TableKind.Master : spec.Model.Kind == TableKind.Segment;
        if (!kindOk)
            yield return new Problem(ModelWriterCodes.InvalidInput, Severity.Error, $"Table model kind {spec.Model.Kind} does not match key {key}.", TablePlacements.SegmentOf(key));
        if (!double.IsFinite(spec.Placement.TextScale) || spec.Placement.TextScale <= 0)
            yield return new Problem(ModelWriterCodes.InvalidInput, Severity.Error, $"Table {key} has an invalid text scale.", TablePlacements.SegmentOf(key));
    }

    /// <inheritdoc/>
    protected override void ResolveResources(IReadOnlyList<(OwnedKey Key, ModelTableSpec Spec)> planned, List<Problem> problems)
    {
        foreach (var group in planned.GroupBy(p => (p.Spec.Model.Style.TableStyle, p.Spec.Model.Style.Layer)))
        {
            string path = TablePlacements.StylePath(group.First().Spec.Model.Kind);
            var style = DrawingNames.TableStyle(Tr, Db, group.Key.TableStyle, path + ".tableStyle", problems);
            var layer = DrawingNames.Layer(Tr, Db, group.Key.Layer, path + ".layer", problems);
            if (style is not null && layer is not null) _resources[group.Key] = (style.Value, layer.Value);
        }
    }

    /// <inheritdoc/>
    protected override (string Planned, string? Live) Signatures(OwnedKey key, ModelTableSpec spec, Table? live, List<Problem> notes)
    {
        string planned = TableRenderer.Signature(spec.Model);
        if (live is null) return (planned, null);
        var rowKeys = OwnerTag.Read(Tr, live, Ctx.Config.OwnerTagKey)?.RowKeys ?? [];
        _liveRowKeys[key] = rowKeys;
        return (planned, TableRenderer.Signature(live, rowKeys, spec.Model));
    }

    /// <inheritdoc/>
    protected override bool PrepareCreate(OwnedKey key, ModelTableSpec spec, bool recreate, List<Problem> problems)
    {
        var placed = TablePlacements.Resolve(Tr, Db, key, spec.Placement, Ctx.Config, Ctx.Report, problems);
        if (placed is null) return false;
        _placements[key] = placed.Value;
        return true;
    }

    /// <inheritdoc/>
    protected override Table Create(OwnedKey key, ModelTableSpec spec, LayerGuard guard, List<Problem> notes)
    {
        var (owner, anchor, corner) = _placements[key];
        var (style, layer) = _resources[(spec.Model.Style.TableStyle, spec.Model.Style.Layer)];
        guard.Allow(layer);
        return TableRenderer.Create(Tr, Db, owner, spec.Model, style, layer, spec.Model.Style.TextHeight * spec.Placement.TextScale,
            Ctx.Config.Tables, anchor, corner);
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<string> TagRowKeys(ModelTableSpec spec) => TableRenderer.RowKeys(spec.Model);

    /// <inheritdoc/>
    protected override void Update(Table t, OwnedKey key, ModelTableSpec spec, List<Problem> notes)
    {
        var newKeys = TableRenderer.RowKeys(spec.Model);
        var oldKeys = _liveRowKeys.TryGetValue(key, out var k) ? k : [];
        var stats = TableRenderer.Update(t, spec.Model, oldKeys, newKeys, spec.Model.Style.TextHeight * spec.Placement.TextScale, Ctx.Config.Tables);
        if (stats.RowsInserted > 0 || stats.RowsDeleted > 0)
            notes.Add(new Problem(ModelWriterCodes.TableResized, Severity.Info,
                $"Table {Describe(key)} ({t.Handle}) updated in place: {stats.RowsInserted} row(s) inserted, {stats.RowsDeleted} deleted.",
                TablePlacements.SegmentOf(key)));
        if (!oldKeys.SequenceEqual(newKeys)) new OwnerTag(Ctx.RunName, key, newKeys).Write(Tr, t, Ctx.Config.OwnerTagKey);
    }
}

/// <summary>
/// HCFCD label-bound tables (6.16), role ModelTable, qualifier "hcfcd". Created once from the
/// template with one material row per analysis (E1018); afterwards only the bound cells are
/// written, found by their row labels with <c>GetTextString(IgnoreMtextFormat)</c> (P24), so
/// typed cells and rows the user added or removed are kept (E1016) and a missing label skips
/// only its own value (E1015).
/// </summary>
public sealed class HcfcdTableWriter : OwnedWriter<HcfcdTableSpec, Table>
{
    private readonly Dictionary<OwnedKey, (ObjectId Owner, Pt Anchor, AnchorCorner Corner)> _placements = [];
    private readonly Dictionary<OwnedKey, TableModel> _createModels = [];
    private readonly Dictionary<OwnedKey, IReadOnlyList<CellWrite>> _writes = [];
    private ObjectId _styleId, _layerId;
    private QuantityUnits? _units;
    private QuantityFormatter? _formatter;

    /// <summary>A writer in the caller's context.</summary>
    public HcfcdTableWriter(ModelWriteContext ctx) : base(ctx) { }

    /// <inheritdoc/>
    protected override string What => "HCFCD table";

    /// <inheritdoc/>
    protected override string RecreatedCode => ModelWriterCodes.TableRecreated;

    /// <inheritdoc/>
    public override bool Owns(OwnedKey key) => key.Role == OwnedRole.ModelTable && key.Qualifier == OwnedQualifiers.HcfcdTable;

    /// <inheritdoc/>
    protected override IEnumerable<Problem> Validate(OwnedKey key, HcfcdTableSpec spec)
    {
        if (!double.IsFinite(spec.Placement.TextScale) || spec.Placement.TextScale <= 0)
            yield return new Problem(ModelWriterCodes.InvalidInput, Severity.Error, $"HCFCD table {key} has an invalid text scale.", spec.SegmentId);
        if (spec.Analyses.Count == 0)
            yield return new Problem(ModelWriterCodes.InvalidInput, Severity.Error, $"HCFCD table {key} has no analyses.", spec.SegmentId);
    }

    /// <inheritdoc/>
    protected override void ResolveResources(IReadOnlyList<(OwnedKey Key, HcfcdTableSpec Spec)> planned, List<Problem> problems)
    {
        var s = Ctx.Report.Hcfcd.Style;
        var style = DrawingNames.TableStyle(Tr, Db, s.TableStyle, "hcfcd.style.tableStyle", problems);
        var layer = DrawingNames.Layer(Tr, Db, s.Layer, "hcfcd.style.layer", problems);
        if (style is not null) _styleId = style.Value;
        if (layer is not null) _layerId = layer.Value;
        var units = QuantityUnits.Create(Ctx.Units, Ctx.Report.Volumes);
        if (!units.Succeeded) { problems.AddRange(units.Problems); return; }
        _units = units.Value;
        _formatter = new QuantityFormatter(_units, Ctx.Report);
    }

    /// <inheritdoc/>
    protected override (string Planned, string? Live) Signatures(OwnedKey key, HcfcdTableSpec spec, Table? live, List<Problem> notes)
    {
        if (live is null) return ("create", null);
        var cells = TableRenderer.ReadCells(live);
        var bind = spec.Template.Bind(cells, spec.SegmentId, spec.Materials, spec.AreaUnit, _formatter!, Ctx.Report);
        notes.AddRange(bind.Problems);
        var covered = TableRenderer.Covered(TableRenderer.LiveMerges(live));
        int labelColumn = spec.Template.Definition.LabelColumn;
        var writable = new List<CellWrite>();
        foreach (var w in bind.Bound)
        {
            if (covered.Contains((w.Row, w.Column)))
                notes.Add(new Problem(ModelWriterCodes.BoundCellCovered, Severity.Warning,
                    $"HCFCD table {live.Handle}: the value for row '{cells[w.Row][labelColumn]}' falls in a merged cell (row {w.Row + 1}, column {w.Column + 1}); it was skipped.",
                    spec.SegmentId, "Unmerge that cell so the value can be written."));
            else writable.Add(w);
        }
        _writes[key] = writable.Where(w => cells[w.Row][w.Column] != w.Text).ToList();
        string planned = ContentSignatures.OfTexts(writable.Select(w => $"{w.Row},{w.Column}={w.Text}"));
        string liveSig = ContentSignatures.OfTexts(writable.Select(w => $"{w.Row},{w.Column}={cells[w.Row][w.Column]}"));
        return (planned, liveSig);
    }

    /// <summary>
    /// Builds the first-creation model and binds the values into it here, in the plan, so a bind
    /// error (an invalid impact value) stops the apply before anything is written, and the
    /// columns and the anchor corner are sized with the values in place.
    /// </summary>
    protected override bool PrepareCreate(OwnedKey key, HcfcdTableSpec spec, bool recreate, List<Problem> problems)
    {
        var model = spec.Template.CreateTable(spec.SegmentId, spec.Analyses, spec.AreaUnit, _units!, Ctx.Report);
        if (!model.Succeeded) { problems.AddRange(model.Problems); return false; }
        problems.AddRange(model.Problems);
        var bind = spec.Template.Bind(model.Value.Rows.Select(r => r.Cells).ToList(), spec.SegmentId, spec.Materials, spec.AreaUnit, _formatter!, Ctx.Report);
        problems.AddRange(bind.Problems);
        if (bind.Problems.Any(p => p.Severity == Severity.Error)) return false;
        var placed = TablePlacements.Resolve(Tr, Db, key, spec.Placement, Ctx.Config, Ctx.Report, problems);
        if (placed is null) return false;

        var covered = TableRenderer.Covered(model.Value.Merges);
        var cells = model.Value.Rows.Select(r => r.Cells.ToArray()).ToList();
        foreach (var w in bind.Bound.Where(w => !covered.Contains((w.Row, w.Column)))) cells[w.Row][w.Column] = w.Text;
        _createModels[key] = model.Value with { Rows = model.Value.Rows.Select((r, i) => r with { Cells = cells[i] }).ToList() };
        _placements[key] = placed.Value;
        return true;
    }

    /// <inheritdoc/>
    protected override Table Create(OwnedKey key, HcfcdTableSpec spec, LayerGuard guard, List<Problem> notes)
    {
        var (owner, anchor, corner) = _placements[key];
        guard.Allow(_layerId);
        return TableRenderer.Create(Tr, Db, owner, _createModels[key], _styleId, _layerId, Ctx.Report.Hcfcd.Style.TextHeight * spec.Placement.TextScale,
            Ctx.Config.Tables, anchor, corner);
    }

    /// <inheritdoc/>
    protected override void Update(Table t, OwnedKey key, HcfcdTableSpec spec, List<Problem> notes)
    {
        foreach (var w in _writes[key]) t.Cells[w.Row, w.Column].TextString = TableRenderer.Escape(w.Text);
        t.GenerateLayout();
    }
}

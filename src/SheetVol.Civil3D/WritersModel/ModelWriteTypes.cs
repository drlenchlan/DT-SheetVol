// A2 model writers: the inputs A4 hands over and the results it gets back. Owner: A2.
// No Autodesk type appears in a spec; the drawing side is confined to the writers.

using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Tracking;
using SheetVol.Core.Volumes;

namespace SheetVol.Civil3D.WritersModel;

/// <summary>Qualifiers of the owned keys the model writers use (Core.Tracking.OwnedKey).</summary>
public static class OwnedQualifiers
{
    /// <summary>Per-sheet table of the Standard preset placed in model space.</summary>
    public const string StandardTable = "standard";

    /// <summary>HCFCD label-bound table placed in model space.</summary>
    public const string HcfcdTable = "hcfcd";

    /// <summary>Line of the match mark at a segment's start.</summary>
    public const string StartLine = "start-line";

    /// <summary>Text of the match mark at a segment's start.</summary>
    public const string StartText = "start-text";

    /// <summary>Line of the match mark at a segment's end.</summary>
    public const string EndLine = "end-line";

    /// <summary>Text of the match mark at a segment's end.</summary>
    public const string EndText = "end-text";

    /// <summary>Impacted-area outline number <paramref name="index"/> of a segment.</summary>
    public static string Outline(int index) => index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Which end of a segment a match mark sits at.</summary>
public enum MatchEnd
{
    /// <summary>The segment's begin station; the mark points to the previous sheet.</summary>
    Start,

    /// <summary>The segment's end station; the mark points to the next sheet.</summary>
    End,
}

/// <summary>
/// One match-line mark (6.8): a line from the left to the right offset point at a break,
/// plus its text on the segment's own side of the line.
/// </summary>
/// <param name="SegmentId">The segment that owns the mark.</param>
/// <param name="End">Which end of the segment.</param>
/// <param name="Left">Left offset point at the break, WCS.</param>
/// <param name="Right">Right offset point at the break, WCS.</param>
/// <param name="Inside">Unit vector from the line toward the segment's interior; the text is offset this way.</param>
/// <param name="Text">The mark text, already formatted.</param>
/// <param name="ModelUnitsPerPaperUnit">The segment's view scale; text height and gap from config are multiplied by it.</param>
public sealed record MatchMarkSpec(Guid SegmentId, MatchEnd End, Pt Left, Pt Right, Pt Inside, string Text, double ModelUnitsPerPaperUnit)
{
    /// <summary>The line's owned key.</summary>
    public OwnedKey LineKey => new(SegmentId, OwnedRole.MatchMark, End == MatchEnd.Start ? OwnedQualifiers.StartLine : OwnedQualifiers.EndLine);

    /// <summary>The text's owned key.</summary>
    public OwnedKey TextKey => new(SegmentId, OwnedRole.MatchMark, End == MatchEnd.Start ? OwnedQualifiers.StartText : OwnedQualifiers.EndText);
}

/// <summary>One closed outline of impacted area below OHWM (6.15), drawn as a polyline.</summary>
/// <param name="SegmentId">The segment.</param>
/// <param name="Index">Zero-based number of the outline within the segment.</param>
/// <param name="Outline">Closed outline with bulges, no repeated closing vertex.</param>
public sealed record ImpactOutlineSpec(Guid SegmentId, int Index, IReadOnlyList<PolyVertex> Outline)
{
    /// <summary>The owned key.</summary>
    public OwnedKey Key => new(SegmentId, OwnedRole.ImpactOutline, OwnedQualifiers.Outline(Index));
}

/// <summary>Which corner of a new table sits on the anchor point.</summary>
public enum AnchorCorner
{
    /// <summary>Top-left (the 6.7 layout anchor).</summary>
    TopLeft,

    /// <summary>Bottom-left (a model-space table above its segment).</summary>
    BottomLeft,

    /// <summary>Top-right (the model-space master table left of the run).</summary>
    TopRight,
}

/// <summary>
/// Where a table goes when it is created or recreated (E503). An existing table is never
/// moved (6.7, E502): placement is read only on creation.
/// </summary>
/// <param name="LayoutName">Null for model space; otherwise the layout whose paper space receives the table.</param>
/// <param name="Anchor">Model-space anchor point (WCS). Null on a layout: the layout's anchor block or the configured paper point is used.</param>
/// <param name="Corner">Which table corner sits on the anchor.</param>
/// <param name="TextScale">Multiplier on the configured paper text height: the view scale in model space, 1 on a layout.</param>
public sealed record TablePlacement(string? LayoutName, Pt? Anchor, AnchorCorner Corner, double TextScale)
{
    /// <summary>A model-space placement.</summary>
    public static TablePlacement InModel(Pt anchor, AnchorCorner corner, double modelUnitsPerPaperUnit)
        => new(null, anchor, corner, modelUnitsPerPaperUnit);

    /// <summary>A placement on a named layout at its anchor block or the configured paper point (6.7).</summary>
    public static TablePlacement InLayout(string layoutName) => new(layoutName, null, AnchorCorner.TopLeft, 1.0);
}

/// <summary>A per-sheet or master table rendered from a C2 <see cref="TableModel"/>.</summary>
/// <param name="Key">Owned key: (segment, ModelTable, "standard") or (Guid.Empty, MasterTable, "").</param>
/// <param name="Model">C2's table model.</param>
/// <param name="Placement">Where to create it.</param>
public sealed record ModelTableSpec(OwnedKey Key, TableModel Model, TablePlacement Placement)
{
    /// <summary>A per-sheet table placed in model space at its segment (A14: the segment names no layout).</summary>
    public static ModelTableSpec ForSegment(TableModel model, TablePlacement placement)
        => new(new OwnedKey(model.SegmentId ?? throw new ArgumentException("A per-sheet table model needs its segment id.", nameof(model)),
                            OwnedRole.ModelTable, OwnedQualifiers.StandardTable), model, placement);

    /// <summary>The run's master table, in model space or on a named layout.</summary>
    public static ModelTableSpec ForMaster(TableModel model, TablePlacement placement)
        => new(new OwnedKey(Guid.Empty, OwnedRole.MasterTable), model, placement);
}

/// <summary>An HCFCD label-bound table for one segment (6.16).</summary>
/// <param name="SegmentId">The segment.</param>
/// <param name="Template">The loaded template.</param>
/// <param name="Analyses">The run's analyses in order; one material row each when the table is first created (E1018).</param>
/// <param name="Materials">The values to bind: one per analysis that has an impact result for this segment.</param>
/// <param name="AreaUnit">The run's area unit.</param>
/// <param name="Placement">Where to create it.</param>
public sealed record HcfcdTableSpec(
    Guid SegmentId, HcfcdTemplate Template, IReadOnlyList<AnalysisDef> Analyses, IReadOnlyList<HcfcdMaterial> Materials,
    AreaUnit AreaUnit, TablePlacement Placement)
{
    /// <summary>The owned key.</summary>
    public OwnedKey Key => new(SegmentId, OwnedRole.ModelTable, OwnedQualifiers.HcfcdTable);
}

/// <summary>
/// Everything the model writers draw for one run. A4 passes every list on every regen, empty
/// when a feature is off, so objects no longer planned are erased (E308).
/// </summary>
/// <param name="Boundaries">Station-segment boundaries to draw. Manual segments are the user's own polylines and are not passed.</param>
/// <param name="ImpactOutlines">Impacted-area outlines (HcfcdSegment preset); empty otherwise.</param>
/// <param name="MatchMarks">Match marks; empty when disabled.</param>
/// <param name="Tables">Per-sheet model-space tables and the master table.</param>
/// <param name="HcfcdTables">HCFCD tables placed by the model writer.</param>
public sealed record ModelWritePlan(
    IReadOnlyList<Boundary> Boundaries,
    IReadOnlyList<ImpactOutlineSpec> ImpactOutlines,
    IReadOnlyList<MatchMarkSpec> MatchMarks,
    IReadOnlyList<ModelTableSpec> Tables,
    IReadOnlyList<HcfcdTableSpec> HcfcdTables)
{
    /// <summary>A plan that draws nothing (and so erases everything the model writers own for the run).</summary>
    public static ModelWritePlan Empty { get; } = new([], [], [], [], []);
}

/// <summary>One owned object as persisted: its key and handle (6.9).</summary>
/// <param name="SegmentId">Segment, or <see cref="Guid.Empty"/> for run-level objects.</param>
/// <param name="Role">Role.</param>
/// <param name="Qualifier">Qualifier.</param>
/// <param name="Handle">Handle as upper-case hex.</param>
public sealed record StoredObjectRef(Guid SegmentId, OwnedRole Role, string Qualifier, string Handle)
{
    /// <summary>The owned key.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public OwnedKey Key => new(SegmentId, Role, Qualifier);

    /// <summary>Builds a reference from a key and handle.</summary>
    public static StoredObjectRef From(OwnedKey key, string handle) => new(key.SegmentId, key.Role, key.Qualifier, handle);
}

/// <summary>The drawing and settings the writers work in. The transaction belongs to the caller (A4), who commits or aborts it.</summary>
/// <param name="Transaction">The caller's open transaction (6.10 step 5).</param>
/// <param name="Database">The drawing.</param>
/// <param name="RunName">Run name, written into every ownership tag.</param>
/// <param name="Config">A2 config.</param>
/// <param name="Report">C2 config: table styles, layers, text heights, anchor block name, formats.</param>
/// <param name="Units">Drawing units from the adapter, for HCFCD values.</param>
public sealed record ModelWriteContext(
    Transaction Transaction, Database Database, string RunName,
    ModelWritersConfig Config, ReportConfig Report, DrawingUnits Units);

/// <summary>Result of one apply.</summary>
/// <param name="Succeeded">False when an error stopped the apply before any write.</param>
/// <param name="Changes">The fate of every owned object of the model writers' roles, except a table whose creation was skipped with an SV-E1017 warning (that table has no entry here and none in <paramref name="Objects"/>; the warning names it).</param>
/// <param name="Objects">Owned objects of the model writers' roles after the apply, for the persistence record.</param>
/// <param name="Problems">Errors (when failed), warnings and notes.</param>
public sealed record ModelWriteResult(
    bool Succeeded, IReadOnlyList<ObjectChange> Changes, IReadOnlyList<StoredObjectRef> Objects, IReadOnlyList<Problem> Problems)
{
    /// <summary>Changes that wrote to the database.</summary>
    public IEnumerable<ObjectChange> Writes => Changes.Where(c => c.Kind is ChangeKind.Create or ChangeKind.Update or ChangeKind.Erase);

    /// <summary>The roles the model writers own; stored objects of any other role are A3's and are left alone.</summary>
    public static IReadOnlyList<OwnedRole> Roles { get; } =
        [OwnedRole.Boundary, OwnedRole.MatchMark, OwnedRole.ModelTable, OwnedRole.MasterTable, OwnedRole.ImpactOutline];
}

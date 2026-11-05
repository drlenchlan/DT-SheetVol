// A3 work package. Owner: A3.
//
// What A4 hands the paper writer and what it gets back. No Autodesk types cross this
// surface except handles as strings, so A4's persistence (6.9) can store them as they are.

using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Views;

namespace SheetVol.Civil3D.WritersPaper;

/// <summary>One sheet: one segment's layout, plan viewport, clip, table and title attributes.</summary>
/// <param name="SegmentId">The segment the sheet shows.</param>
/// <param name="NameParts">Tokens for the layout name pattern (C3 LayoutNamer) and the title-attribute patterns.</param>
/// <param name="View">C3's view. Its PaperWidth/Height are the template size the view was fitted to; the writer never resizes the viewport from them.</param>
/// <param name="Table">C2's per-sheet table, or null for no paper table on this sheet.</param>
/// <param name="LockView">Per-segment LockView (E608): an existing viewport is left untouched, view and clip included. A missing viewport is still created.</param>
/// <param name="LayoutHandle">The layout handle A4 stored for this segment, or null. A hint: the owner tag decides.</param>
public sealed record PaperSheetRequest(
    Guid SegmentId,
    LayoutNameParts NameParts,
    ViewSpec View,
    TableModel? Table,
    bool LockView = false,
    string? LayoutHandle = null);

/// <summary>One run's paper output.</summary>
/// <param name="RunId">The run's identity, written into every owner tag. RunDefinition has no GUID; A4 supplies a stable one.</param>
/// <param name="Sheets">Sheets in sheet order (layout numbering follows it).</param>
public sealed record PaperRunRequest(Guid RunId, IReadOnlyList<PaperSheetRequest> Sheets);

/// <summary>What happened to one owned paper object.</summary>
public enum PaperChange
{
    /// <summary>Nothing to do: the object is not part of this sheet (no table, no clip, no attributes).</summary>
    None,
    /// <summary>Created (or recreated after the user erased it).</summary>
    Created,
    /// <summary>Changed in place; handle kept.</summary>
    Updated,
    /// <summary>Already realizes the plan; nothing written.</summary>
    Unchanged,
    /// <summary>LockView: left untouched whatever its content (E608).</summary>
    Kept,
    /// <summary>Layout renamed (E307); content handled separately.</summary>
    Renamed,
}

/// <summary>Result for one sheet. Handles are hexadecimal strings as AutoCAD prints them.</summary>
public sealed record PaperSheetResult(
    Guid SegmentId,
    string LayoutName,
    string LayoutHandle,
    string ViewportHandle,
    string? ClipHandle,
    string? TableHandle,
    IReadOnlyList<string> TitleBlockHandles,
    PaperChange Layout,
    PaperChange Viewport,
    PaperChange Clip,
    PaperChange Table,
    PaperChange Attributes,
    IReadOnlyList<Problem> Problems);

/// <summary>A layout tagged for this run that the plan no longer holds, or a second layout claiming a planned segment (E612). Listed, never deleted.</summary>
public sealed record OrphanLayout(Guid SegmentId, string Handle, string Name, string Reason);

/// <summary>Outcome of <see cref="PaperWriter.Apply"/>.</summary>
/// <param name="Succeeded">False when validation refused the run; nothing was written.</param>
/// <param name="Sheets">One per requested sheet, in request order (empty when refused).</param>
/// <param name="Orphans">Layouts listed per E612.</param>
/// <param name="RenamePlan">C3's two-pass plan that was applied (null when refused).</param>
/// <param name="Problems">Every problem, run level and per sheet.</param>
/// <param name="Writes">Objects the writer created, erased or opened for write. Zero means the drawing already realized the plan (E811).</param>
public sealed record PaperApplyResult(
    bool Succeeded,
    IReadOnlyList<PaperSheetResult> Sheets,
    IReadOnlyList<OrphanLayout> Orphans,
    LayoutRenamePlan? RenamePlan,
    IReadOnlyList<Problem> Problems,
    int Writes);

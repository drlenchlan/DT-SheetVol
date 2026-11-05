// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4.
//
// The ports the regen pipeline needs from the adapters (A1), the model writers (A2) and
// the paper writers (A3). A1-A3 are built in parallel; the orchestrator wires their real
// implementations at integration by setting EngineBindings.Current. SVSELFTEST runs the
// same pipeline against the in-memory fakes in SelfTest/SelfTestFakes.cs, so a real
// factory drops in by swapping one object.
//
// Rules every implementation follows (HANDOFF.md 6.13, 6.10):
//   - Every call happens inside the caller's transaction and under its document lock.
//     Implementations never start, commit or abort a transaction of their own, never
//     dispose objects obtained from tr.GetObject, and never lock the document. The
//     pipeline commits once at the end of step 5; any failure before that aborts the
//     transaction and the drawing is untouched (6.10, E801, T20, P20).
//   - Failures that belong to the user's data come back as Problems with a stable SV-Exxx
//     code (Outcome failures or WriteResult problems). Exceptions are for defects; the
//     pipeline turns them into SV-E801 and aborts.
//   - Writers update only what they own and only what they set: cell text, view, clip,
//     attribute values. Never a table or viewport position the user changed (6.7, E502,
//     E608). Objects on locked layers are unlocked, written and relocked; frozen layers
//     stay frozen (E504, E604).
//   - Signatures: Inspect returns the content signature of the LIVE object computed with
//     the OwnedSignatures function for its role, so the pipeline's ChangeSet (C3) compares
//     like with like and an unchanged regen writes nothing (E811, T19). A writer that
//     cannot read an object back returns a null signature, which forces an update.

using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Planning;
using SheetVol.Core.Tracking;
using SheetVol.Core.Volumes;

namespace SheetVol.Civil3D.Session;

// ============================================================================
// A1: adapters
// ============================================================================

/// <summary>Creates the drawing's source adapters for one operation (A1). The swap point for SVSELFTEST.</summary>
public interface IAdapterFactory
{
    /// <summary>Opens the sources of <paramref name="db"/> inside <paramref name="tr"/>.</summary>
    IDrawingSources Open(Database db, Transaction tr, SheetVolSettings settings);
}

/// <summary>
/// Everything the pipeline reads from the drawing (A1). Lives for one transaction, and so does
/// everything it returns: alignment geometry, triangle sources and volume sources may hold
/// objects opened in that transaction and are used only while it is open (A1's adapters throw
/// after it ends). Callers copy out plain values (stations, strings, PlanResult) before the
/// transaction closes. Names are the user's names; handles are hex strings as AutoCAD prints them.
/// </summary>
public interface IDrawingSources
{
    /// <summary>Unit detection: the Civil 3D drawing-settings unit, never INSUNITS (P2, E216, E606).</summary>
    Outcome<DrawingUnits> Units();

    /// <summary>Alignment names in the drawing, native and data-shortcut references.</summary>
    IReadOnlyList<string> AlignmentNames();

    /// <summary>TIN surface names usable as base or comparison: not volume or grid surfaces (E402), not the tool's own volume surfaces.</summary>
    IReadOnlyList<string> TinSurfaceNames();

    /// <summary>
    /// The alignment, resolved by <paramref name="storedHandle"/> first and then by name (E803).
    /// Fails with SV-E213 when missing or the reference is broken, SV-E214 when entities are
    /// disconnected or unsolved, SV-E404 when a reference is stale. Geometry absorbs the probe
    /// quirks (P7 Clockwise, azimuths to radians CCW from +X).
    /// </summary>
    Outcome<AlignmentSource> Alignment(string name, string? storedHandle);

    /// <summary>A manual segment's polyline in WCS with bulges (6.3). Fails with SV-E311 when erased or not a polyline.</summary>
    Outcome<ManualOutline> ManualOutline(string polylineHandle);

    /// <summary>
    /// A base or comparison TIN surface. Out of date and native: rebuilt when
    /// <paramref name="rebuildIfOutOfDate"/>, else SV-E404 naming it. Stale reference: SV-E404,
    /// never computed on (E404, E415). Grid or volume surface: SV-E402. No triangles: SV-E403.
    /// </summary>
    Outcome<SurfaceSource> Surface(string name, string? storedHandle, bool rebuildIfOutOfDate);

    /// <summary>
    /// Ensures the owned TinVolumeSurface for one base/comparison pair (6.5): reuses the one it
    /// owns (by handle, E412), recreates it when erased or renamed (name collision resolved with
    /// a suffix), writes the plan's factors, and ALWAYS calls Rebuild() (P18). Writes happen in
    /// the caller's transaction and are rolled back with it. The returned volume source applies
    /// the P1-P3 mapping once: raw unadjusted cubic drawing units, cut and fill both &gt;= 0,
    /// |x| &lt; 1e-6 clamped to 0 (P18 sign noise).
    /// </summary>
    Outcome<OwnedVolumeSurface> EnsureVolumeSurface(OwnedSurfacePlan plan, string? storedHandle);

    /// <summary>Read-only state of an owned volume surface for the status fingerprint (6.11): no rebuild, no writes. Null when it does not exist.</summary>
    AnalysisSurfaceState? ReadVolumeSurfaceState(Guid analysisId, OwnedSurfacePlan plan, string? storedHandle);
}

/// <summary>A resolved alignment.</summary>
/// <param name="Geometry">Contract geometry (6.2).</param>
/// <param name="Handle">Its handle now.</param>
/// <param name="IsReference">True for a data-shortcut reference.</param>
public sealed record AlignmentSource(IAlignmentGeometry Geometry, string Handle, bool IsReference);

/// <summary>A resolved TIN surface.</summary>
/// <param name="Name">Surface name.</param>
/// <param name="Handle">Its handle now.</param>
/// <param name="Triangles">Visible triangles only (P23, E1013).</param>
/// <param name="IsReference">True for a data-shortcut reference.</param>
/// <param name="Rebuilt">True when it was out of date and rebuilt (E404 with RebuildSources on).</param>
public sealed record SurfaceSource(string Name, string Handle, ITriangleSource Triangles, bool IsReference, bool Rebuilt);

/// <summary>An owned volume surface after Ensure.</summary>
/// <param name="Name">Its name now (SV-&lt;base&gt;-&lt;comparison&gt;, suffixed on collision, E412).</param>
/// <param name="Handle">Its handle now.</param>
/// <param name="Volumes">Bounded volumes and datum-method 2D area (6.5).</param>
/// <param name="Created">True when it was created or recreated in this transaction (E412).</param>
public sealed record OwnedVolumeSurface(string Name, string Handle, IVolumeSource Volumes, bool Created);

// ============================================================================
// A2 / A3: writers and persistence
// ============================================================================

/// <summary>Creates the writers and the persistence store for one operation (A2, A3).</summary>
public interface IWriterFactory
{
    /// <summary>Model-space writer (A2): Boundary, MatchMark, ModelTable, MasterTable, ImpactOutline.</summary>
    IModelWriter Model(Database db, Transaction tr, SheetVolSettings settings);

    /// <summary>Paper-space writer (A3): Layout, Viewport (with its clip), PaperTable, TitleAttributes.</summary>
    IPaperWriter Paper(Database db, Transaction tr, SheetVolSettings settings);

    /// <summary>Persistence store (A2, 6.9).</summary>
    IPersistenceStore Store(Database db, Transaction tr, SheetVolSettings settings);
}

/// <summary>What A2 and A3 have in common: owned objects by key.</summary>
public interface IOwnedObjectWriter
{
    /// <summary>The roles this writer owns. Every <see cref="OwnedRole"/> is owned by exactly one writer.</summary>
    IReadOnlySet<OwnedRole> Roles { get; }

    /// <summary>
    /// Checks, before any write, that everything the planned objects need exists: table
    /// styles and layers (E501), template file, layout, plan viewport and anchor (E601),
    /// target layouts (E1017). Read-only. An Error fails the run before step 5.
    /// </summary>
    IReadOnlyList<Problem> Preflight(IReadOnlyList<OwnedObject> planned);

    /// <summary>Live state of a stored object: exists (E802) and its content signature (E811).</summary>
    StoredObject Inspect(OwnedKey key, string? handle);

    /// <summary>
    /// Creates (<paramref name="existingHandle"/> null) or updates in place. Returns the handle,
    /// whether anything was modified, and per-object notes (E1015 missing label, E615 missing
    /// attribute tag). An Error problem aborts the run.
    /// </summary>
    WriteResult Write(OwnedObject planned, string? existingHandle);

    /// <summary>Erases an object that is no longer planned (E308). Layouts are never erased here (E612).</summary>
    void Erase(OwnedKey key, string handle);
}

/// <summary>Model-space writer (A2).</summary>
public interface IModelWriter : IOwnedObjectWriter
{
}

/// <summary>Paper-space writer (A3).</summary>
public interface IPaperWriter : IOwnedObjectWriter
{
    /// <summary>The template's plan viewport size and anchor (6.7). Read-only. Fails with SV-E601 naming what is missing.</summary>
    Outcome<TemplateInfo> Template();

    /// <summary>Every paper layout in the drawing with its handle (E603 de-duplication, E307 renames, E612 orphans).</summary>
    IReadOnlyList<LayoutRef> Layouts();

    /// <summary>Renames one layout. The pipeline calls it with a collision-free two-pass plan (C3 LayoutNamer, E307).</summary>
    void RenameLayout(string handle, string newName);

    /// <summary>Deletes a layout. Only SVPURGE and a confirmed SVCLEAR call this; regen never deletes a layout (6.7, E612).</summary>
    void DeleteLayout(string handle);
}

/// <summary>The template's plan viewport (6.7).</summary>
/// <param name="ViewportWidth">Paper units.</param>
/// <param name="ViewportHeight">Paper units.</param>
/// <param name="HasTableAnchor">True when the layout holds the anchor block; otherwise template.tablePaperPoint is used.</param>
public sealed record TemplateInfo(double ViewportWidth, double ViewportHeight, bool HasTableAnchor);

/// <summary>A paper layout.</summary>
public sealed record LayoutRef(string Name, string Handle);

/// <summary>Result of one write.</summary>
/// <param name="Handle">The object's handle (new on create).</param>
/// <param name="Modified">False when the writer found nothing to change.</param>
/// <param name="Problems">Notes and warnings for this object.</param>
public sealed record WriteResult(string Handle, bool Modified, IReadOnlyList<Problem> Problems);

/// <summary>Persistence (6.9): one record per run in the drawing.</summary>
public interface IPersistenceStore
{
    /// <summary>Run names stored in the drawing, in ordinal order.</summary>
    IReadOnlyList<string> RunNames();

    /// <summary>Reads a run. A stored schema newer than the plugin's refuses modification and reports both versions (E105).</summary>
    StoredRunLoad Load(string runName);

    /// <summary>Creates or replaces the run's record. <see cref="RunRecordCodec"/> gives the chunked strings.</summary>
    void Save(RunRecord record);

    /// <summary>Removes the run's record (SVCLEAR).</summary>
    void Delete(string runName);
}

/// <summary>Result of reading a stored run.</summary>
/// <param name="Exists">A record with that name is stored.</param>
/// <param name="Record">The record; null when absent or refused.</param>
/// <param name="CanModify">False when the stored schema is newer (E105) or unreadable.</param>
/// <param name="Problems">Why it was refused.</param>
public sealed record StoredRunLoad(bool Exists, RunRecord? Record, bool CanModify, IReadOnlyList<Problem> Problems)
{
    /// <summary>No record by that name.</summary>
    public static StoredRunLoad Missing { get; } = new(false, null, true, []);
}

/// <summary>One owned object by key and handle (6.9 per-segment handles).</summary>
public sealed record OwnedHandle(OwnedKey Key, string Handle);

/// <summary>A source reference by kind, name and handle (6.9: "by handle and name", E803).</summary>
/// <param name="Kind">One of <see cref="SourceKinds"/>.</param>
/// <param name="Name">Object name.</param>
/// <param name="Handle">Handle at the last successful regen.</param>
public sealed record SourceHandle(string Kind, string Name, string Handle);

/// <summary>Source kinds in <see cref="SourceHandle"/>.</summary>
public static class SourceKinds
{
    /// <summary>The run's alignment.</summary>
    public const string Alignment = "alignment";
    /// <summary>A base or comparison surface.</summary>
    public const string Surface = "surface";
    /// <summary>An owned volume surface; Name is SV-base-comparison as created.</summary>
    public const string VolumeSurface = "volumeSurface";
}

/// <summary>A layout whose segment no longer exists (E308, E612). Listed by SVSTATUS, deleted only by SVPURGE.</summary>
public sealed record OrphanLayout(Guid SegmentId, string Handle, string Name, DateTimeOffset SinceUtc);

/// <summary>
/// The persisted run (6.9). <see cref="Run"/> carries KnownSegments = the last applied plan
/// (C1: persist only after a successful regen). <see cref="Fingerprint"/> is null until the
/// first successful regen. <see cref="LastProblems"/> are the last regen's warnings and notes
/// for SVSTATUS; they are not part of the record signature.
/// </summary>
public sealed record RunRecord(
    RunDefinition Run,
    RunFingerprint? Fingerprint,
    IReadOnlyList<OwnedHandle> Owned,
    IReadOnlyList<SourceHandle> Sources,
    IReadOnlyList<OrphanLayout> Orphans,
    DateTimeOffset? LastRegenUtc,
    IReadOnlyList<Problem> LastProblems)
{
    /// <summary>A new run with nothing applied yet.</summary>
    public static RunRecord New(RunDefinition run) => new(run, null, [], [], [], null, []);

    /// <summary>Handle of a source by kind and name, or null.</summary>
    public string? SourceHandleOf(string kind, string name) =>
        Sources.FirstOrDefault(s => s.Kind == kind && string.Equals(s.Name, name, StringComparison.Ordinal))?.Handle;
}

// ============================================================================
// Planned objects: what a writer is asked to create or update
// ============================================================================

/// <summary>Where a table goes: a layout (top-left at the anchor block or template.tablePaperPoint) or model space.</summary>
/// <param name="LayoutName">Target layout; null for model space.</param>
/// <param name="ModelPoint">Model-space top-left, WCS; null on a layout.</param>
/// <param name="ModelScale">Model units per paper unit: model-space text height = config height x this (C2 note).</param>
public sealed record TablePlacement(string? LayoutName, Pt? ModelPoint, double ModelScale);

/// <summary>One object the regen plans to own. <see cref="Signature"/> uses <see cref="OwnedSignatures"/>.</summary>
public abstract record OwnedObject(OwnedKey Key)
{
    /// <summary>Content signature of what would be written.</summary>
    public abstract string Signature { get; }
}

/// <summary>Model-space boundary polyline, closed, true arcs (6.4). Layer: modelOutput.boundaryLayer.</summary>
public sealed record BoundaryObject(OwnedKey Key, Boundary Boundary) : OwnedObject(Key)
{
    /// <inheritdoc />
    public override string Signature => ContentSignatures.Of(Boundary);
}

/// <summary>Match-line mark (6.8): a line across the band at a shared break and its text. Layer and height from modelOutput.</summary>
/// <param name="Key">Segment, MatchMark, "start" or "end".</param>
/// <param name="From">Left offset point at the break, WCS.</param>
/// <param name="To">Right offset point at the break, WCS.</param>
/// <param name="Text">Formatted text.</param>
/// <param name="TextHeight">Model-space text height, drawing units.</param>
public sealed record MatchMarkObject(OwnedKey Key, Pt From, Pt To, string Text, double TextHeight) : OwnedObject(Key)
{
    /// <inheritdoc />
    public override string Signature => OwnedSignatures.OfMatchMark(From, To, Text, TextHeight);
}

/// <summary>A per-sheet or master table (6.8). Created once, afterwards cell text only (E502). Grows or shrinks by analysis block in place (E505).</summary>
public sealed record TableObject(OwnedKey Key, TableModel Table, TablePlacement Placement) : OwnedObject(Key)
{
    /// <inheritdoc />
    public override string Signature => OwnedSignatures.OfTable(Table);
}

/// <summary>
/// An HCFCD label-bound table (6.16). First creation: <c>Template.CreateTable(segment, Materials, AreaUnit, Formatter.Units, config)</c>,
/// then <c>Template.Bind</c>. Later regens: <c>Template.Bind</c> on the live cells and write only <c>Changes</c> (E811, E1015, E1016).
/// Values only for analyses whose impact was computed (D10).
/// </summary>
public sealed record HcfcdTableObject(
    OwnedKey Key, HcfcdTemplate Template, IReadOnlyList<AnalysisDef> Materials, IReadOnlyList<HcfcdMaterial> Values,
    AreaUnit AreaUnit, QuantityFormatter Formatter, TablePlacement Placement, string BoundSignature) : OwnedObject(Key)
{
    /// <inheritdoc />
    public override string Signature => BoundSignature;
}

/// <summary>A layout cloned from the template (6.7, P11). Signature covers the name; renames go through <see cref="IPaperWriter.RenameLayout"/>.</summary>
public sealed record LayoutObject(OwnedKey Key, string Name, int SheetNumber) : OwnedObject(Key)
{
    /// <inheritdoc />
    public override string Signature => OwnedSignatures.OfLayout(Name);
}

/// <summary>
/// The plan viewport on a layout: view target, twist, scale, clip (6.6, P12). Paper position and size are never changed (E608).
/// The signature covers <see cref="ViewSpec.PaperWidth"/>/<see cref="ViewSpec.PaperHeight"/>, which are the TEMPLATE viewport
/// size the view was fitted to. Inspect must compute the live signature with that same template size (or return the signature
/// recorded when the view was applied), never the live paper size, so a viewport the user resized is not rewritten every regen.
/// </summary>
public sealed record ViewportObject(OwnedKey Key, string LayoutName, ViewSpec View) : OwnedObject(Key)
{
    /// <inheritdoc />
    public override string Signature => ContentSignatures.Of(View);
}

/// <summary>Title-block attribute values on a layout (6.7). A missing tag is skipped with a note (E615).</summary>
public sealed record TitleAttributesObject(OwnedKey Key, string LayoutName, IReadOnlyDictionary<string, string> Values) : OwnedObject(Key)
{
    /// <inheritdoc />
    public override string Signature => OwnedSignatures.OfAttributes(Values);
}

/// <summary>Content signatures shared by the pipeline (planned) and the writers (live), built on C3's <see cref="ContentSignatures"/>.</summary>
public static class OwnedSignatures
{
    /// <summary>All cell texts row by row (cells read with FormatOption.IgnoreMtextFormat, P24). Position and style are not part of it (E502).</summary>
    public static string OfTable(TableModel table) => ContentSignatures.OfTexts(table.Rows.SelectMany(r => r.Cells));

    /// <summary>Cell texts read from a live table, row by row.</summary>
    public static string OfCells(IEnumerable<IReadOnlyList<string>> rows) => ContentSignatures.OfTexts(rows.SelectMany(r => r));

    /// <summary>The bound cells' texts in row then column order (6.16).</summary>
    public static string OfBound(IEnumerable<CellWrite> bound) =>
        ContentSignatures.OfTexts(bound.OrderBy(c => c.Row).ThenBy(c => c.Column).Select(c => c.Text));

    /// <summary>A layout's name.</summary>
    public static string OfLayout(string name) => ContentSignatures.OfTexts([name]);

    /// <summary>Attribute values, sorted by tag (ordinal).</summary>
    public static string OfAttributes(IReadOnlyDictionary<string, string> values) =>
        ContentSignatures.OfTexts(values.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));

    /// <summary>A match mark: both ends, text and height.</summary>
    public static string OfMatchMark(Pt from, Pt to, string text, double height) =>
        ContentSignatures.OfTexts([text, R(from.X), R(from.Y), R(to.X), R(to.Y), R(height)]);

    private static string R(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

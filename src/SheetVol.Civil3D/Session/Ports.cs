// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4; writer ports reshaped at the
// Wave 2 integration.
//
// The ports the regen pipeline needs from the adapters (A1), the model writers (A2) and
// the paper writers (A3). The real implementations live in Civil3D/Integration and are
// installed at assembly load (EngineStartup). SVSELFTEST runs the same pipeline against
// the in-memory fakes in SelfTest/SelfTestFakes.cs, so a factory drops in by swapping one
// object.
//
// Rules every implementation follows (HANDOFF.md 6.13, 6.10):
//   - Every call happens inside the caller's transaction and under its document lock.
//     Implementations never start, commit or abort the caller's transaction, never
//     dispose objects obtained from tr.GetObject, and never lock the document. The
//     pipeline commits once at the end of step 5; any failure before that aborts the
//     transaction and the drawing is untouched (6.10, E801, T20, P20).
//   - Failures that belong to the user's data come back as Problems with a stable SV-Exxx
//     code (Outcome failures, WriterResult problems). Exceptions are for defects; the
//     pipeline turns them into SV-E823 and aborts.
//   - Writers are whole-run appliers (A2's ModelWriter.Apply, A3's PaperWriter.Apply): each
//     plans read-only first, refuses with no writes on any error, and then writes only what
//     the live objects show has changed, so an unchanged regen writes nothing (E811, T19).
//     They update only what they own and only what they set: cell text, view, clip,
//     attribute values; never a table or viewport position the user changed (6.7, E502,
//     E608). Objects on locked layers are unlocked or force-opened, written and relocked;
//     frozen layers stay frozen (E504, E604).
//   - Ordering inside step 5 (RegenPipeline): model writer first, paper writer last. A3's
//     apply replaces the template's cloned viewports by new ones and erases the clones; A1
//     measured that erasing an object created in the same transaction and then aborting can
//     crash the host (0xC0000005), so nothing that can fail runs after the paper writer.

using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Civil3D.WritersModel;
using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Planning;
using SheetVol.Core.Tracking;
using SheetVol.Core.Views;
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
/// objects opened in that transaction and are used only while it is open (they throw after it
/// ends). Callers copy out plain values (stations, strings, PlanResult) before the transaction
/// closes. Names are the user's names; handles are hex strings as AutoCAD prints them.
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
    /// The alignment, resolved by name and then by <paramref name="storedHandle"/> (E803).
    /// Fails with SV-E213 when missing or the reference is broken or stale, SV-E214 when
    /// entities are disconnected or unsolved. Geometry absorbs the probe quirks (P7 Clockwise,
    /// azimuths to radians CCW from +X).
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

    /// <summary>Paper-space writer (A3): Layout, Viewport, Clip, PaperTable, TitleAttributes. Dispose it after the regen (it holds the template's side database).</summary>
    IPaperWriter Paper(Database db, Transaction tr, SheetVolSettings settings);

    /// <summary>Persistence store (6.9).</summary>
    IPersistenceStore Store(Database db, Transaction tr, SheetVolSettings settings);
}

/// <summary>What one regen asks the writers to realize.</summary>
/// <param name="Run">The run as applied (KnownSegments = this plan).</param>
/// <param name="RunId">The run id in every owner tag (<see cref="Ownership.RunIds.For"/>).</param>
/// <param name="Output">The planned objects (<see cref="OutputPlanner"/>).</param>
/// <param name="Prior">The record of the last successful regen, or null.</param>
/// <param name="Units">Drawing units (HCFCD values).</param>
/// <param name="NowUtc">The regen's timestamp (orphan dates).</param>
public sealed record WriteRequest(RunDefinition Run, Guid RunId, OutputPlan Output, RunRecord? Prior, DrawingUnits Units, DateTimeOffset NowUtc);

/// <summary>Counts of what a writer did, for SVREGEN's summary and the self-test.</summary>
/// <param name="Created">Objects created.</param>
/// <param name="Updated">Objects updated in place.</param>
/// <param name="Erased">Objects erased.</param>
/// <param name="Renamed">Layout renames (both passes).</param>
/// <param name="Orphaned">Layouts that became orphans in this regen.</param>
/// <param name="Writes">Database writes the writer made (objects created, erased or opened for write). Zero means the drawing already realized the plan (E811).</param>
public sealed record ApplyCounts(int Created, int Updated, int Erased, int Renamed, int Orphaned, int Writes)
{
    /// <summary>Nothing done.</summary>
    public static ApplyCounts None { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>Two writers' counts together.</summary>
    public ApplyCounts Plus(ApplyCounts other) => new(Created + other.Created, Updated + other.Updated, Erased + other.Erased,
        Renamed + other.Renamed, Orphaned + other.Orphaned, Writes + other.Writes);
}

/// <summary>What one writer did in one regen.</summary>
/// <param name="Succeeded">False when the writer refused; it then wrote nothing of its own.</param>
/// <param name="Owned">Every object of this writer's roles the run owns after the apply, for the record (6.9).</param>
/// <param name="NewOrphans">Layouts the run owns that the plan no longer holds (E308, E612); listed, never deleted.</param>
/// <param name="Counts">What was written.</param>
/// <param name="Problems">Errors (when refused), warnings and notes.</param>
public sealed record WriterResult(bool Succeeded, IReadOnlyList<OwnedHandle> Owned, IReadOnlyList<OrphanLayout> NewOrphans, ApplyCounts Counts, IReadOnlyList<Problem> Problems)
{
    /// <summary>A refusal: nothing written by this writer.</summary>
    public static WriterResult Refused(IReadOnlyList<Problem> problems) => new(false, [], [], ApplyCounts.None, problems);
}

/// <summary>What A2 and A3 have in common.</summary>
public interface IOwnedObjectWriter
{
    /// <summary>The roles this writer owns. Every <see cref="OwnedRole"/> is owned by exactly one writer.</summary>
    IReadOnlySet<OwnedRole> Roles { get; }

    /// <summary>
    /// Live state of one stored object, read only: it exists, is not erased and carries the run's
    /// owner tag for <paramref name="key"/> (E802, SVSTATUS); its signature describes the live
    /// content so the self-test can compare drawing states (T20).
    /// </summary>
    StoredObject Inspect(string runName, OwnedKey key, string? handle);

    /// <summary>
    /// Applies the run's planned objects of this writer's roles against what <see cref="WriteRequest.Prior"/>
    /// recorded. Plans read-only first; any error refuses the whole apply with no writes of its own.
    /// </summary>
    WriterResult Apply(WriteRequest request);
}

/// <summary>Model-space writer (A2).</summary>
public interface IModelWriter : IOwnedObjectWriter
{
    /// <summary>SVCLEAR: erases every model-space object <paramref name="record"/> lists that still exists.</summary>
    WriterResult Clear(string runName, RunRecord record);
}

/// <summary>Paper-space writer (A3). Holds the loaded template until disposed.</summary>
public interface IPaperWriter : IOwnedObjectWriter, IDisposable
{
    /// <summary>Loads the sheet template (6.7): plan viewport size and anchor. Read-only. Fails with SV-E601 naming what is missing.</summary>
    Outcome<TemplateInfo> Template();

    /// <summary>Every paper layout in the drawing with its handle (E603 de-duplication, E612 orphans).</summary>
    IReadOnlyList<LayoutRef> Layouts();

    /// <summary>Every check that needs the destination drawing, before any write (E501, clip layer, E605 name clash). Read-only.</summary>
    IReadOnlyList<Problem> Validate(WriteRequest request);

    /// <summary>Deletes a layout. Only SVPURGE and a confirmed SVCLEAR call this; regen never deletes a layout (6.7, E612).</summary>
    void DeleteLayout(string handle);
}

/// <summary>The template's plan viewport (6.7).</summary>
/// <param name="ViewportWidth">Paper units.</param>
/// <param name="ViewportHeight">Paper units.</param>
/// <param name="HasTableAnchor">True when the layout holds the anchor block; otherwise <c>paper.tablePaperPoint</c> is used.</param>
public sealed record TemplateInfo(double ViewportWidth, double ViewportHeight, bool HasTableAnchor);

/// <summary>A paper layout.</summary>
public sealed record LayoutRef(string Name, string Handle);

/// <summary>Persistence (6.9): one record per run in the drawing.</summary>
public interface IPersistenceStore
{
    /// <summary>Run names stored in the drawing, in ordinal order.</summary>
    IReadOnlyList<string> RunNames();

    /// <summary>Reads a run. A stored schema newer than the plugin's refuses modification and reports both versions (E105).</summary>
    StoredRunLoad Load(string runName);

    /// <summary>Creates or replaces the run's record. Content equal to what is stored rewrites only the timestamp (E811).</summary>
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

/// <summary>Source kinds in <see cref="SourceHandle"/>: A2's store kinds, plus the owned volume surface.</summary>
public static class SourceKinds
{
    /// <summary>The run's alignment.</summary>
    public const string Alignment = WritersModel.SourceKinds.Alignment;

    /// <summary>A base or comparison surface.</summary>
    public const string Surface = WritersModel.SourceKinds.Surface;

    /// <summary>An owned volume surface; Name is SV-base-comparison as planned (the stored handle finds a suffixed or renamed one, E412).</summary>
    public const string VolumeSurface = "VolumeSurface";
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
// Planned objects: what the writers are asked to realize
// ============================================================================

/// <summary>
/// One object the regen plans to own. <see cref="Signature"/> uses <see cref="OwnedSignatures"/>;
/// the fake writers compare it with what they stored, the real writers compare the live object
/// themselves. Model-space objects carry A2's spec, paper objects what A3's sheet request needs.
/// </summary>
public abstract record OwnedObject(OwnedKey Key)
{
    /// <summary>Content signature of what would be written.</summary>
    public abstract string Signature { get; }
}

/// <summary>Model-space boundary polyline, closed, true arcs (6.4). Layer: modelWriters.boundaryLayer.</summary>
public sealed record BoundaryObject(OwnedKey Key, Boundary Boundary) : OwnedObject(Key)
{
    /// <inheritdoc />
    public override string Signature => ContentSignatures.Of(Boundary);
}

/// <summary>One half of a match-line mark (6.8): A2's spec makes a line and a text, each its own key (<see cref="MatchMarkSpec.LineKey"/>, <see cref="MatchMarkSpec.TextKey"/>).</summary>
public sealed record MatchMarkObject(OwnedKey Key, MatchMarkSpec Spec) : OwnedObject(Key)
{
    /// <inheritdoc />
    public override string Signature => OwnedSignatures.OfMatchMark(Spec.Left, Spec.Right, Spec.Text, Spec.ModelUnitsPerPaperUnit);
}

/// <summary>An impacted-area outline below OHWM (6.15), HcfcdSegment preset.</summary>
public sealed record ImpactOutlineObject(OwnedKey Key, ImpactOutlineSpec Spec) : OwnedObject(Key)
{
    /// <inheritdoc />
    public override string Signature => ContentSignatures.OfPolyline(Spec.Outline);
}

/// <summary>A per-sheet table in model space or on a named layout, or the master table (6.8), written by A2. Created once, afterwards cell text only (E502); grows or shrinks by analysis block in place (E505).</summary>
public sealed record ModelTableObject(OwnedKey Key, ModelTableSpec Spec) : OwnedObject(Key)
{
    /// <inheritdoc />
    public override string Signature => OwnedSignatures.OfTable(Spec.Model);
}

/// <summary>
/// An HCFCD label-bound table (6.16), written by A2: created once from the template with one
/// material row per analysis, afterwards only the bound cells (E1015, E1016, E1018).
/// <paramref name="BoundSignature"/> is the bound cells' text as first created.
/// </summary>
public sealed record HcfcdTableObject(OwnedKey Key, HcfcdTableSpec Spec, string BoundSignature) : OwnedObject(Key)
{
    /// <inheritdoc />
    public override string Signature => BoundSignature;
}

/// <summary>A layout cloned from the template (6.7, P11). <paramref name="Parts"/> feed the layout name pattern and the title attributes (C3).</summary>
public sealed record LayoutObject(OwnedKey Key, string Name, int SheetNumber, LayoutNameParts Parts) : OwnedObject(Key)
{
    /// <inheritdoc />
    public override string Signature => OwnedSignatures.OfLayout(Name);
}

/// <summary>
/// The plan viewport on a layout: view target, twist, scale, clip (6.6, P12). Paper position and
/// size are never changed (E608); <paramref name="LockView"/> keeps an existing viewport as it is.
/// </summary>
public sealed record ViewportObject(OwnedKey Key, string LayoutName, ViewSpec View, bool LockView) : OwnedObject(Key)
{
    /// <inheritdoc />
    public override string Signature => ContentSignatures.Of(View);
}

/// <summary>The per-sheet table on a generated layout (6.8), written by A3 at the template's anchor.</summary>
public sealed record PaperTableObject(OwnedKey Key, string LayoutName, TableModel Table) : OwnedObject(Key)
{
    /// <inheritdoc />
    public override string Signature => OwnedSignatures.OfTable(Table);
}

/// <summary>Title-block attribute values on a layout (6.7). A missing tag is skipped with a note (E615).</summary>
public sealed record TitleAttributesObject(OwnedKey Key, string LayoutName, IReadOnlyDictionary<string, string> Values) : OwnedObject(Key)
{
    /// <inheritdoc />
    public override string Signature => OwnedSignatures.OfAttributes(Values);
}

/// <summary>Content signatures shared by the planner and the fake writers, built on C3's <see cref="ContentSignatures"/>.</summary>
public static class OwnedSignatures
{
    /// <summary>All cell texts row by row. Position and style are not part of it (E502).</summary>
    public static string OfTable(TableModel table) => ContentSignatures.OfTexts(table.Rows.SelectMany(r => r.Cells));

    /// <summary>The bound cells' texts in row then column order (6.16).</summary>
    public static string OfBound(IEnumerable<CellWrite> bound) =>
        ContentSignatures.OfTexts(bound.OrderBy(c => c.Row).ThenBy(c => c.Column).Select(c => c.Text));

    /// <summary>A layout's name.</summary>
    public static string OfLayout(string name) => ContentSignatures.OfTexts([name]);

    /// <summary>Attribute values, sorted by tag (ordinal).</summary>
    public static string OfAttributes(IReadOnlyDictionary<string, string> values) =>
        ContentSignatures.OfTexts(values.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));

    /// <summary>A match mark: both ends, text and scale.</summary>
    public static string OfMatchMark(Pt from, Pt to, string text, double scale) =>
        ContentSignatures.OfTexts([text, R(from.X), R(from.Y), R(to.X), R(to.Y), R(scale)]);

    private static string R(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

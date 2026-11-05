// A2 persistence store (HANDOFF.md 6.9). Owner: A2.
//
// Named Objects Dictionary -> DBDictionary "SHEETVOL" (ReportConfig.Persistence.DictionaryName)
// -> one Xrecord per run, keyed "RUN-" + 24 hex digits of SHA-256 of the run name. Layout:
//   (1,   "SHEETVOL-RUN")   marker
//   (90,  schemaVersion)    read first: newer than this plugin refuses the run (E105)
//   (302, timestamp)        UTC, ISO 8601 round-trip; the only value an unchanged regen rewrites (E811)
//   (300, chunk) ...        the RunDefinition JSON from C2's RunPersistence, <= 250 chars each
//   (301, chunk) ...        the state JSON: owned-object handles, source references by handle
//                           and name, orphans, last fingerprint, schemaVersion
// Handles survive SAVEAS (E809). Copies of owned objects pasted into another drawing arrive
// without this dictionary and stay plain objects (E810).

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Tracking;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace SheetVol.Civil3D.WritersModel;

/// <summary>Kinds of source the run references (6.9: by handle and by name).</summary>
public static class SourceKinds
{
    /// <summary>A Civil 3D alignment; re-found by name (E803).</summary>
    public const string Alignment = "Alignment";

    /// <summary>A Civil 3D surface (TIN or the owned volume surface); re-found by name (E803).</summary>
    public const string Surface = "Surface";

    /// <summary>A layout; re-found by name.</summary>
    public const string Layout = "Layout";

    /// <summary>A manual segment's polyline; handle only (it has no name). A dead handle is the E311 "erased" case.</summary>
    public const string Polyline = "Polyline";
}

/// <summary>A source the run reads, stored by handle and name (6.9).</summary>
/// <param name="Kind">One of <see cref="SourceKinds"/>.</param>
/// <param name="Name">Name at the last regen; empty for kinds without names.</param>
/// <param name="Handle">Handle at the last regen.</param>
public sealed record SourceRef(string Kind, string Name, string Handle);

/// <summary>An object left behind by a removed segment and kept for SVSTATUS to list (E612: layouts are never auto-deleted).</summary>
/// <param name="SegmentId">The removed segment.</param>
/// <param name="Role">Role (Layout, Viewport, Clip, TitleAttributes).</param>
/// <param name="Qualifier">Qualifier.</param>
/// <param name="Handle">Handle.</param>
/// <param name="Name">Layout name when known, for the listing.</param>
public sealed record OrphanRef(Guid SegmentId, OwnedRole Role, string Qualifier, string Handle, string? Name)
{
    /// <summary>The owned key.</summary>
    [JsonIgnore] public OwnedKey Key => new(SegmentId, Role, Qualifier);
}

/// <summary>Everything persisted for one run.</summary>
/// <param name="Run">The run definition (KnownSegments = the last successfully applied plan).</param>
/// <param name="Objects">Every owned object's handle: the model writers' and A3's.</param>
/// <param name="Sources">Source references by handle and name.</param>
/// <param name="Orphans">Objects left behind by removed segments (E612).</param>
/// <param name="Fingerprint">Last source fingerprint (6.11), or null before the first regen.</param>
public sealed record RunRecord(
    RunDefinition Run,
    IReadOnlyList<StoredObjectRef> Objects,
    IReadOnlyList<SourceRef> Sources,
    IReadOnlyList<OrphanRef> Orphans,
    string? Fingerprint);

/// <summary>Result of reading a run from the store.</summary>
/// <param name="Record">The record; null when there is none or it was refused.</param>
/// <param name="CanModify">False when the record is newer than this plugin (E105) or malformed; nothing may write to the run.</param>
/// <param name="StoredSchemaVersion">Schema version found, or null.</param>
/// <param name="TimestampUtc">When the record was last written, or null.</param>
/// <param name="Problems">Why the record was refused.</param>
public sealed record RunStoreReadResult(RunRecord? Record, bool CanModify, int? StoredSchemaVersion, DateTime? TimestampUtc, IReadOnlyList<Problem> Problems)
{
    /// <summary>No record for this run yet: writable, nothing stored.</summary>
    public static RunStoreReadResult None { get; } = new(null, true, null, null, []);
}

/// <summary>What a store write did.</summary>
public enum StoreWriteKind
{
    /// <summary>A new record.</summary>
    Created,

    /// <summary>The record's content changed.</summary>
    Updated,

    /// <summary>Content unchanged; only the timestamp was rewritten (E811).</summary>
    TimestampOnly,

    /// <summary>Nothing written: the stored schema is newer (E105) or the input is invalid.</summary>
    Refused,
}

/// <summary>Result of a store write.</summary>
public sealed record RunStoreWriteResult(StoreWriteKind Kind, string Key, int Chunks, int LongestString, IReadOnlyList<Problem> Problems);

/// <summary>Result of resolving a source reference (E803).</summary>
/// <param name="Id">The object, or <see cref="ObjectId.Null"/> when it cannot be found.</param>
/// <param name="Ref">The reference to store from now on (handle and name repaired).</param>
/// <param name="Problem">SV-E803 warning when found by name; SV-E540 error when not found; null when the handle held.</param>
public sealed record SourceResolution(ObjectId Id, SourceRef Ref, Problem? Problem);

/// <summary>The 6.9 persistence store. Every method uses the caller's transaction.</summary>
public static class RunStore
{
    /// <summary>The record schema this plugin reads and writes: C2's run schema version.</summary>
    public const int SchemaVersion = RunPersistence.PluginSchemaVersion;

    private const string Marker = "SHEETVOL-RUN";
    private const int MarkerCode = 1, VersionCode = 90, TimestampCode = 302, RunChunkCode = 300, StateChunkCode = 301;

    /// <summary>The Xrecord key for a run name.</summary>
    public static string KeyFor(string runName)
        => "RUN-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runName)))[..24];

    /// <summary>Reads one run. No dictionary or no record gives <see cref="RunStoreReadResult.None"/>.</summary>
    public static RunStoreReadResult Read(Transaction tr, Database db, string runName, ReportConfig report)
    {
        var dict = Dictionary(tr, db, report, forWrite: false);
        if (dict is null) return RunStoreReadResult.None;
        string key = KeyFor(runName);
        if (!dict.Contains(key)) return RunStoreReadResult.None;
        if (tr.GetObject(dict.GetAt(key), OpenMode.ForRead) is not Xrecord xr) return Refused(null, $"Store entry {key} is not an Xrecord.");
        var result = Parse(xr, key);
        if (result.Record is not null && !string.Equals(result.Record.Run.Name, runName, StringComparison.Ordinal))
            return Refused(result.StoredSchemaVersion, $"Store record {key} holds run '{result.Record.Run.Name}', not '{runName}'.");
        return result;
    }

    /// <summary>Reads every record in the store, by key, including refused ones (for SVSTATUS).</summary>
    public static IReadOnlyList<(string Key, RunStoreReadResult Result)> ReadAll(Transaction tr, Database db, ReportConfig report)
    {
        var list = new List<(string, RunStoreReadResult)>();
        var dict = Dictionary(tr, db, report, forWrite: false);
        if (dict is null) return list;
        foreach (DBDictionaryEntry e in dict)
            if (tr.GetObject(e.Value, OpenMode.ForRead) is Xrecord xr) list.Add((e.Key, Parse(xr, e.Key)));
        return list;
    }

    /// <summary>
    /// Writes a run's record. Refuses when the stored record's schema is newer (E105). When the
    /// content equals what is stored, only the timestamp changes (E811).
    /// </summary>
    public static RunStoreWriteResult Write(Transaction tr, Database db, RunRecord record, DateTime utcNow, ReportConfig report)
    {
        string key = KeyFor(record.Run.Name);
        var configProblems = report.Validate();
        if (configProblems.Count > 0) return new RunStoreWriteResult(StoreWriteKind.Refused, key, 0, 0, configProblems);

        var content = Content(record, report.Persistence.ChunkLength);
        int chunks = content.Count, longest = content.Max(v => v.Value is string s ? s.Length : 0);
        var dict = Dictionary(tr, db, report, forWrite: false);
        if (dict is not null && dict.Contains(key))
        {
            // Never overwrite what Read would refuse: a newer schema (E105), a malformed record, or another run's record.
            if (tr.GetObject(dict.GetAt(key), OpenMode.ForRead) is not Xrecord xr)
                return new RunStoreWriteResult(StoreWriteKind.Refused, key, 0, 0, Refused(null, $"Store entry {key} is not an Xrecord.").Problems);
            var existing = Parse(xr, key);
            if (!existing.CanModify) return new RunStoreWriteResult(StoreWriteKind.Refused, key, 0, 0, existing.Problems);
            if (!string.Equals(existing.Record!.Run.Name, record.Run.Name, StringComparison.Ordinal))
                return new RunStoreWriteResult(StoreWriteKind.Refused, key, 0, 0,
                    Refused(existing.StoredSchemaVersion, $"Store record {key} holds run '{existing.Record.Run.Name}', not '{record.Run.Name}'.").Problems);
            var stored = xr.Data?.AsArray() ?? [];
            bool same = stored.Where(v => v.TypeCode != TimestampCode).Select(Text)
                .SequenceEqual(content.Where(v => v.TypeCode != TimestampCode).Select(Text));
            xr.UpgradeOpen();
            xr.Data = Buffer(content, utcNow);
            return new RunStoreWriteResult(same ? StoreWriteKind.TimestampOnly : StoreWriteKind.Updated, key, chunks, longest, []);
        }

        dict ??= Dictionary(tr, db, report, forWrite: true)!;
        if (!dict.IsWriteEnabled) dict.UpgradeOpen();
        var rec = new Xrecord { Data = Buffer(content, utcNow) };
        dict.SetAt(key, rec);
        tr.AddNewlyCreatedDBObject(rec, true);
        return new RunStoreWriteResult(StoreWriteKind.Created, key, chunks, longest, []);
    }

    /// <summary>Removes a run's record (SVCLEAR). Refuses a record newer than this plugin (E105). Returns true when a record was removed.</summary>
    public static bool Delete(Transaction tr, Database db, string runName, ReportConfig report, List<Problem> problems)
    {
        var dict = Dictionary(tr, db, report, forWrite: false);
        string key = KeyFor(runName);
        if (dict is null || !dict.Contains(key)) return false;
        var obj = tr.GetObject(dict.GetAt(key), OpenMode.ForRead);
        int? v = (obj as Xrecord)?.Data?.AsArray().Where(t => t.TypeCode == VersionCode).Select(t => t.Value as int?).FirstOrDefault();
        if (v > SchemaVersion) { problems.Add(Newer(v.Value)); return false; }
        obj.UpgradeOpen();
        obj.Erase();   // a malformed record is removable: clearing it is the documented fix
        return true;
    }

    /// <summary>
    /// Resolves a source: by handle first (a renamed source keeps its handle and the stored name
    /// is refreshed); when the handle is dead or points at something else, by name with an
    /// SV-E803 warning (E803); not found or ambiguous is SV-E540.
    /// </summary>
    public static SourceResolution ResolveSource(Transaction tr, Database db, SourceRef source)
    {
        ObjectId id = Handles.Resolve(db, source.Handle);
        if (!id.IsNull && Kind(tr.GetObject(id, OpenMode.ForRead), source.Kind) is { } liveName)
            return new SourceResolution(id, source with { Name = liveName }, null);

        if (source.Kind == SourceKinds.Polyline || string.IsNullOrEmpty(source.Name))
            return new SourceResolution(ObjectId.Null, source, new Problem(ModelWriterCodes.SourceMissing, Severity.Error,
                $"{source.Kind} {source.Handle} referenced by the run no longer exists.", Fix: "Redraw it and attach it to the run again."));

        var hits = Candidates(tr, db, source.Kind).Where(c => string.Equals(c.Name, source.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 1)
        {
            var repaired = source with { Handle = Handles.Of(hits[0].Id), Name = hits[0].Name };
            return new SourceResolution(hits[0].Id, repaired, new Problem(ModelWriterCodes.SourceFoundByName, Severity.Warning,
                $"{source.Kind} '{source.Name}': stored handle {source.Handle} is no longer valid; found by name as {repaired.Handle} and the reference repaired."));
        }
        string why = hits.Count == 0 ? "no object has that name" : $"{hits.Count} objects have that name";
        return new SourceResolution(ObjectId.Null, source, new Problem(ModelWriterCodes.SourceMissing, Severity.Error,
            $"{source.Kind} '{source.Name}' (stored handle {source.Handle}) cannot be found: the handle is dead and {why}.",
            Fix: $"Restore the {source.Kind.ToLowerInvariant()} or redefine the run with SVDEFINE."));
    }

    /// <summary>
    /// The orphan list for the next record (E612): previous orphans whose objects still exist,
    /// plus new ones, minus any whose key is planned again; de-duplicated by key and handle.
    /// </summary>
    public static IReadOnlyList<OrphanRef> MergeOrphans(Database db, IEnumerable<OrphanRef> previous, IEnumerable<OrphanRef> added, IEnumerable<OwnedKey> plannedAgain)
    {
        var planned = plannedAgain.ToHashSet();
        return previous.Concat(added)
            .Where(o => !planned.Contains(o.Key) && !Handles.Resolve(db, o.Handle).IsNull)
            .GroupBy(o => (o.Key, o.Handle)).Select(g => g.Last())
            .ToList();
    }

    /// <summary>Signature of a record's content without its timestamp, for Core.Tracking's RecordChanged.</summary>
    public static string RecordSignature(RunRecord record, ReportConfig report)
        => ContentSignatures.OfTexts(Content(record, report.Persistence.ChunkLength).Select(Text));

    // ------------------------------------------------------------------ internals

    private sealed record StoredState(
        int SchemaVersion,
        IReadOnlyList<StoredObjectRef> Objects,
        IReadOnlyList<SourceRef> Sources,
        IReadOnlyList<OrphanRef> Orphans,
        string? Fingerprint);

    private static List<TypedValue> Content(RunRecord record, int chunkLength)
    {
        var state = new StoredState(SchemaVersion, record.Objects, record.Sources, record.Orphans, record.Fingerprint);
        var list = new List<TypedValue> { new(MarkerCode, Marker), new(VersionCode, SchemaVersion) };
        list.AddRange(RunPersistence.Chunk(RunPersistence.Serialize(record.Run), chunkLength).Select(c => new TypedValue(RunChunkCode, c)));
        list.AddRange(RunPersistence.Chunk(JsonSerializer.Serialize(state, StateOptions), chunkLength).Select(c => new TypedValue(StateChunkCode, c)));
        return list;
    }

    private static ResultBuffer Buffer(List<TypedValue> content, DateTime utcNow)
    {
        var rb = new ResultBuffer();
        foreach (var v in content)
        {
            rb.Add(v);
            if (v.TypeCode == VersionCode)
                rb.Add(new TypedValue(TimestampCode, DateTime.SpecifyKind(utcNow, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture)));
        }
        return rb;
    }

    private static RunStoreReadResult Parse(Xrecord xr, string key)
    {
        var values = xr.Data?.AsArray() ?? [];
        if (!values.Any(v => v.TypeCode == MarkerCode && Marker.Equals(v.Value as string)))
            return Refused(null, $"Store record {key} has no SheetVol marker.");
        int? version = values.Where(v => v.TypeCode == VersionCode).Select(v => v.Value as int?).FirstOrDefault();
        if (version is null) return Refused(null, $"Store record {key} has no schema version.");
        if (version > SchemaVersion)
            return new RunStoreReadResult(null, false, version, null, [Newer(version.Value)]);

        DateTime? stamp = null;
        string? stampText = values.Where(v => v.TypeCode == TimestampCode).Select(v => v.Value as string).FirstOrDefault();
        if (stampText is not null && DateTime.TryParse(stampText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)) stamp = t;

        var run = RunPersistence.FromChunks(values.Where(v => v.TypeCode == RunChunkCode).Select(v => v.Value as string));
        if (!run.CanModify || run.Run is null)
            return new RunStoreReadResult(null, false, run.StoredSchemaVersion ?? version, stamp, run.Problems);

        var joined = RunPersistence.Join(values.Where(v => v.TypeCode == StateChunkCode).Select(v => v.Value as string));
        if (!joined.Succeeded) return new RunStoreReadResult(null, false, version, stamp, joined.Problems);
        StoredState? state;
        try
        {
            state = JsonSerializer.Deserialize<StoredState>(joined.Value, StateOptions);
        }
        catch (JsonException ex)
        {
            return Refused(version, $"Store record {key}: the state JSON could not be read: {ex.Message}");
        }
        if (state is null) return Refused(version, $"Store record {key}: the state JSON is empty.");
        if (state.SchemaVersion > SchemaVersion) return new RunStoreReadResult(null, false, state.SchemaVersion, stamp, [Newer(state.SchemaVersion)]);
        return new RunStoreReadResult(new RunRecord(run.Run, state.Objects, state.Sources, state.Orphans, state.Fingerprint), true, version, stamp, []);
    }

    private static DBDictionary? Dictionary(Transaction tr, Database db, ReportConfig report, bool forWrite)
    {
        string name = report.Persistence.DictionaryName;
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (nod.Contains(name)) return (DBDictionary)tr.GetObject(nod.GetAt(name), OpenMode.ForRead);
        if (!forWrite) return null;
        nod.UpgradeOpen();
        var dict = new DBDictionary();
        nod.SetAt(name, dict);
        tr.AddNewlyCreatedDBObject(dict, true);
        return dict;
    }

    private static string? Kind(DBObject obj, string kind) => kind switch
    {
        SourceKinds.Alignment => obj is CivAlignment a ? a.Name : null,
        SourceKinds.Surface => obj is CivSurface s ? s.Name : null,
        SourceKinds.Layout => obj is Layout l && !l.ModelType ? l.LayoutName : null,
        SourceKinds.Polyline => obj is Polyline ? "" : null,
        _ => null,
    };

    private static IEnumerable<(ObjectId Id, string Name)> Candidates(Transaction tr, Database db, string kind)
    {
        switch (kind)
        {
            case SourceKinds.Alignment:
            {
                var civ = CivilDocument.GetCivilDocument(db);
                if (civ is null) yield break;
                foreach (ObjectId id in civ.GetAlignmentIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is CivAlignment a) yield return (id, a.Name);
                break;
            }
            case SourceKinds.Surface:
            {
                var civ = CivilDocument.GetCivilDocument(db);
                if (civ is null) yield break;
                foreach (ObjectId id in civ.GetSurfaceIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is CivSurface s) yield return (id, s.Name);
                break;
            }
            case SourceKinds.Layout:
                foreach (DBDictionaryEntry e in (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead))
                    if (tr.GetObject(e.Value, OpenMode.ForRead) is Layout l && !l.ModelType) yield return (e.Value, l.LayoutName);
                break;
        }
    }

    private static string Text(TypedValue v) => v.TypeCode.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToString(v.Value, CultureInfo.InvariantCulture);

    private static Problem Newer(int stored) => new(OutputCodes.StoredSchemaNewer, Severity.Error,
        $"This run was saved with schema version {stored}; this plugin reads version {SchemaVersion}. The run is left unchanged.",
        Fix: "Update SheetVol to the version that saved this drawing.");

    private static RunStoreReadResult Refused(int? version, string message) => new(null, false, version, null,
        [new Problem(OutputCodes.PersistedJsonInvalid, Severity.Error, message, Fix: "Clear the run (SVCLEAR) and redefine it with SVDEFINE.")]);

    private static readonly JsonSerializerOptions StateOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
}

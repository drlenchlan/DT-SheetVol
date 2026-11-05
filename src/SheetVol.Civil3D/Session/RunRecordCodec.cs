// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4.
//
// The persisted run record (6.9) as JSON strings for an Xrecord. The run definition
// itself goes through C2's RunPersistence, so the schema guard (E105), the schema 1 to 2
// upgrade and the "missing member is an error" rules are C2's; this codec only wraps the
// definition with the handles, fingerprint, orphans and timestamp the pipeline keeps.
// A2's real store writes ToChunks to its Xrecord and hands the strings back to FromChunks.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Tracking;

namespace SheetVol.Civil3D.Session;

/// <summary>Run record to and from chunked JSON (6.9).</summary>
public static class RunRecordCodec
{
    private const string RunProperty = "run";
    private const string RecordProperty = "record";

    private sealed record Envelope(
        RunFingerprint? Fingerprint,
        IReadOnlyList<OwnedHandle> Owned,
        IReadOnlyList<SourceHandle> Sources,
        IReadOnlyList<OrphanLayout> Orphans,
        DateTimeOffset? LastRegenUtc,
        IReadOnlyList<Problem> LastProblems);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>The record as one JSON document: <c>{"run": &lt;RunPersistence JSON&gt;, "record": {...}}</c>.</summary>
    public static string Serialize(RunRecord record)
    {
        var root = new JsonObject
        {
            [RunProperty] = JsonNode.Parse(RunPersistence.Serialize(record.Run)),
            [RecordProperty] = JsonSerializer.SerializeToNode(
                new Envelope(record.Fingerprint, record.Owned, record.Sources, record.Orphans, record.LastRegenUtc, record.LastProblems), Options),
        };
        return root.ToJsonString();
    }

    /// <summary>Serialized and chunked to at most <see cref="PersistenceConfig.ChunkLength"/> characters per string.</summary>
    public static IReadOnlyList<string> ToChunks(RunRecord record, PersistenceConfig config) =>
        RunPersistence.Chunk(Serialize(record), config.ChunkLength);

    /// <summary>Joins stored strings and reads the record with C2's schema guard.</summary>
    public static StoredRunLoad FromChunks(IEnumerable<string?> chunks)
    {
        var joined = RunPersistence.Join(chunks);
        return joined.Succeeded ? Deserialize(joined.Value) : new StoredRunLoad(true, null, false, joined.Problems);
    }

    /// <summary>Reads one record document.</summary>
    public static StoredRunLoad Deserialize(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return Refused($"Stored run record is not JSON: {ex.Message}");
        }
        if (root is not JsonObject obj || obj[RunProperty] is not JsonNode runNode)
            return Refused("Stored run record has no 'run' member.");

        var load = RunPersistence.Deserialize(runNode.ToJsonString());
        if (load.Run is null) return new StoredRunLoad(true, null, false, load.Problems);

        Envelope? env = null;
        if (obj[RecordProperty] is JsonNode recordNode)
        {
            try
            {
                env = recordNode.Deserialize<Envelope>(Options);
            }
            catch (JsonException ex)
            {
                return Refused($"Stored run record could not be read: {ex.Message}");
            }
        }
        var record = env is null
            ? RunRecord.New(load.Run)
            : new RunRecord(load.Run, env.Fingerprint, env.Owned ?? [], env.Sources ?? [], env.Orphans ?? [], env.LastRegenUtc, env.LastProblems ?? []);
        return new StoredRunLoad(true, record, load.CanModify, load.Problems);
    }

    /// <summary>
    /// Signature of a record without its timestamp and last problems (C3 ChangeSet "record
    /// changed"): definition, fingerprint, owned keys and handles, sources, orphans.
    /// </summary>
    public static string Signature(RunRecord record)
    {
        var texts = new List<string?>
        {
            RunPersistence.Serialize(record.Run),
            record.Fingerprint?.Value,
        };
        texts.AddRange(record.Owned
            .OrderBy(o => o.Key.SegmentId).ThenBy(o => o.Key.Role).ThenBy(o => o.Key.Qualifier, StringComparer.Ordinal)
            .Select(o => $"{o.Key.SegmentId:D}|{o.Key.Role}|{o.Key.Qualifier}|{o.Handle}"));
        texts.AddRange(record.Sources.OrderBy(s => s.Kind, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => $"{s.Kind}|{s.Name}|{s.Handle}"));
        texts.AddRange(record.Orphans.OrderBy(o => o.Handle, StringComparer.Ordinal).Select(o => $"{o.SegmentId:D}|{o.Handle}"));
        return ContentSignatures.OfTexts(texts);
    }

    private static StoredRunLoad Refused(string message) =>
        new(true, null, false, [new Problem(OutputCodes.PersistedJsonInvalid, Severity.Error, message, Fix: "Redefine the run with SVDEFINE.")]);
}

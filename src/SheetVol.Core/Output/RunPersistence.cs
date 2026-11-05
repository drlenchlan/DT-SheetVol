using System.Text.Json;
using System.Text.Json.Serialization;
using SheetVol.Core.Volumes;

namespace SheetVol.Core.Output;

/// <summary>Result of reading a stored run.</summary>
/// <param name="Run">The run; null when it could not be read or its schema is newer than this plugin's.</param>
/// <param name="CanModify">True only when the run was read and its schema is not newer (E105).</param>
/// <param name="StoredSchemaVersion">The schema version found in storage; null when none could be read.</param>
/// <param name="PluginSchemaVersion">The schema version this plugin reads and writes.</param>
/// <param name="Problems">Why the run was refused, if it was.</param>
public sealed record RunLoadResult(
    RunDefinition? Run, bool CanModify, int? StoredSchemaVersion, int PluginSchemaVersion, IReadOnlyList<Problem> Problems);

/// <summary>
/// Persistence helpers for HANDOFF.md 6.9: <see cref="RunDefinition"/> to and from JSON with
/// System.Text.Json (never BinaryFormatter), chunking into Xrecord strings of at most 250
/// characters, and the schema guard: a stored schema newer than the plugin's refuses
/// modification and reports both versions (E105). The Xrecord itself is written by the model
/// writer (A2); this class never touches the drawing.
/// </summary>
public static class RunPersistence
{
    /// <summary>The run schema version this plugin reads and writes. Matches the <see cref="RunDefinition.SchemaVersion"/> default.</summary>
    public const int PluginSchemaVersion = 1;

    /// <summary>The 6.9 ceiling for one Xrecord string.</summary>
    public const int MaxChunkLength = 250;

    private const string SchemaProperty = "schemaVersion";

    /// <summary>
    /// Serializes a run, stamping <see cref="PluginSchemaVersion"/>. Output is ASCII (non-ASCII
    /// characters are \u-escaped), so one character is one byte in the Xrecord.
    /// </summary>
    public static string Serialize(RunDefinition run)
        => JsonSerializer.Serialize(run with { SchemaVersion = PluginSchemaVersion }, Options);

    /// <summary>
    /// Reads a run from JSON. The schema version is read first: newer than the plugin's refuses
    /// the run and reports both versions (E105); missing or malformed is an SV-E533 error.
    /// Missing required members and nulls in non-nullable members are errors, never defaults.
    /// </summary>
    public static RunLoadResult Deserialize(string json)
    {
        int? stored;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Refused(null, "Stored run is not a JSON object.");
            stored = doc.RootElement.TryGetProperty(SchemaProperty, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)
                ? n : null;
        }
        catch (JsonException ex)
        {
            return Refused(null, $"Stored run JSON is malformed: {ex.Message}");
        }

        if (stored is null)
            return Refused(null, $"Stored run has no integer '{SchemaProperty}'.");
        if (stored < 1)
            return Refused(stored, $"Stored run has schema version {stored}; versions start at 1.");
        if (stored > PluginSchemaVersion)
            return new RunLoadResult(null, false, stored, PluginSchemaVersion,
            [
                new Problem(OutputCodes.StoredSchemaNewer, Severity.Error,
                    $"This run was saved with schema version {stored}; this plugin reads version {PluginSchemaVersion}. The run is left unchanged.",
                    Fix: "Update SheetVol to the version that saved this drawing."),
            ]);

        try
        {
            var run = JsonSerializer.Deserialize<RunDefinition>(json, Options);
            if (run is null) return Refused(stored, "Stored run JSON is null.");
            return new RunLoadResult(run, true, stored, PluginSchemaVersion, []);
        }
        catch (JsonException ex)
        {
            return Refused(stored, $"Stored run could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Splits text into chunks of at most <paramref name="maxLength"/> characters (1 to 250, 6.9),
    /// never splitting a surrogate pair. Empty text gives one empty chunk so a stored run always
    /// has at least one string.
    /// </summary>
    public static IReadOnlyList<string> Chunk(string text, int maxLength = MaxChunkLength)
    {
        if (maxLength < 2 || maxLength > MaxChunkLength)
            throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, $"Chunk length must be 2 to {MaxChunkLength}.");
        if (text.Length == 0) return [""];
        var chunks = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            int len = Math.Min(maxLength, text.Length - i);
            if (i + len < text.Length && char.IsHighSurrogate(text[i + len - 1])) len--;
            chunks.Add(text.Substring(i, len));
            i += len;
        }
        return chunks;
    }

    /// <summary>Joins stored chunks in order. A null chunk, a chunk over 250 characters or no chunks at all is an SV-E533 error.</summary>
    public static Outcome<string> Join(IEnumerable<string?> chunks)
    {
        var list = chunks.ToList();
        if (list.Count == 0)
            return Outcome<string>.Fail(new Problem(OutputCodes.PersistedJsonInvalid, Severity.Error, "Stored run has no data strings."));
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is null)
                return Outcome<string>.Fail(new Problem(OutputCodes.PersistedJsonInvalid, Severity.Error, $"Stored run string {i + 1} is missing."));
            if (list[i]!.Length > MaxChunkLength)
                return Outcome<string>.Fail(new Problem(OutputCodes.PersistedJsonInvalid, Severity.Error,
                    $"Stored run string {i + 1} has {list[i]!.Length} characters; the limit is {MaxChunkLength}."));
        }
        return Outcome<string>.Ok(string.Concat(list));
    }

    /// <summary>Serializes a run and chunks it for an Xrecord.</summary>
    public static IReadOnlyList<string> ToChunks(RunDefinition run, PersistenceConfig config)
        => Chunk(Serialize(run), config.ChunkLength);

    /// <summary>Joins stored chunks and reads the run, with the schema guard.</summary>
    public static RunLoadResult FromChunks(IEnumerable<string?> chunks)
    {
        var joined = Join(chunks);
        return joined.Succeeded
            ? Deserialize(joined.Value)
            : new RunLoadResult(null, false, null, PluginSchemaVersion, joined.Problems);
    }

    private static RunLoadResult Refused(int? stored, string message)
        => new(null, false, stored, PluginSchemaVersion,
               [new Problem(OutputCodes.PersistedJsonInvalid, Severity.Error, message, Fix: "Redefine the run with SVDEFINE.")]);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
}

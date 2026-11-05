// Wave 2 integration. The one owner-tag format for every object SheetVol creates or fills in a
// drawing: A2's model-space objects (boundaries, impact outlines, match marks, model and master
// tables, HCFCD tables) and A3's layouts, plan viewports, clips, paper tables and title-block
// references. It replaces two records that differed in layout: A2's "SHEETVOL-OWNER" (group
// codes, keyed by run name) and A3's "SV-OWNER-1" (text fields, keyed by run GUID). A3's layout
// is the one kept, so tags its checks already wrote stay readable.
//
// An Xrecord under the configured key in the object's extension dictionary, one DxfCode.Text per
// field, each at most 250 characters (6.9):
//   [0] schema "SV-OWNER-1"   [1] run id (RunIds.For(run name))   [2] segment GUID
//   [3] role (C3 OwnedRole)   [4] qualifier   [5] signature of what was written, empty when the
//   writer compares the live object instead (A2)   [6..] extras (a table's row keys, E505)
// A copy pasted into another drawing carries the tag as inert data and is a plain object there
// (E810). The run id is derived from the run name, so it is the same on every seat and in every
// session; RunDefinition has no GUID of its own (contract revision 2).

using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Core.Tracking;

namespace SheetVol.Civil3D.Ownership;

/// <summary>A decoded owner tag.</summary>
/// <param name="RunId">The owning run's id (<see cref="RunIds.For"/>).</param>
/// <param name="Key">Segment, role and qualifier of the object.</param>
/// <param name="Signature">Signature of what the writer wrote, or empty when the writer compares the live object.</param>
/// <param name="Extras">Role-specific values (a table's row keys).</param>
public sealed record OwnerTag(Guid RunId, OwnedKey Key, string Signature, IReadOnlyList<string> Extras)
{
    /// <summary>Schema marker in field 0. A tag with another marker is not SheetVol's and is ignored.</summary>
    public const string Schema = "SV-OWNER-1";

    /// <summary>Longest string one TypedValue may carry (6.9).</summary>
    public const int MaxField = 250;

    /// <summary>Builds a tag from its parts.</summary>
    public OwnerTag(Guid runId, Guid segmentId, OwnedRole role, string qualifier, string signature, IReadOnlyList<string> extras)
        : this(runId, new OwnedKey(segmentId, role, qualifier), signature, extras)
    {
    }

    /// <summary>The segment, or <see cref="Guid.Empty"/> for a run-level object.</summary>
    public Guid SegmentId => Key.SegmentId;

    /// <summary>The role.</summary>
    public OwnedRole Role => Key.Role;

    /// <summary>The qualifier.</summary>
    public string Qualifier => Key.Qualifier;

    /// <summary>True when this tag names the given run and key.</summary>
    public bool Matches(Guid runId, OwnedKey key) => RunId == runId && Key == key;

    /// <summary>True when every field equals <paramref name="other"/>'s.</summary>
    public bool SameAs(OwnerTag other) =>
        RunId == other.RunId && Key == other.Key && Signature == other.Signature && Extras.SequenceEqual(other.Extras);

    /// <summary>Reads the tag on <paramref name="obj"/>, or null when it carries none, it is malformed, or it is not SheetVol's.</summary>
    public static OwnerTag? Read(Transaction tr, DBObject obj, string tagKey)
    {
        var dictId = obj.ExtensionDictionary;
        if (dictId.IsNull || dictId.IsErased) return null;
        if (tr.GetObject(dictId, OpenMode.ForRead) is not DBDictionary dict || !dict.Contains(tagKey)) return null;
        if (tr.GetObject(dict.GetAt(tagKey), OpenMode.ForRead) is not Xrecord xr) return null;
        using var rb = xr.Data;
        if (rb is null) return null;
        var v = rb.AsArray().Select(t => t.Value as string).ToArray();
        if (v.Length < 6 || v[0] != Schema) return null;
        if (!Guid.TryParse(v[1], out var run) || !Guid.TryParse(v[2], out var seg)) return null;
        if (!Enum.TryParse<OwnedRole>(v[3], out var role)) return null;
        return new OwnerTag(run, new OwnedKey(seg, role, v[4] ?? ""), v[5] ?? "", v.Skip(6).Select(s => s ?? "").ToArray());
    }

    /// <summary>True when <paramref name="obj"/> already carries a tag equal to <paramref name="tag"/>.</summary>
    public static bool Matches(Transaction tr, DBObject obj, string tagKey, OwnerTag tag)
        => Read(tr, obj, tagKey) is { } existing && existing.SameAs(tag);

    /// <summary>
    /// Writes this tag on <paramref name="obj"/> unless an equal tag is already there, so an
    /// unchanged regen writes nothing (E811). Returns true when it wrote. When a write is needed
    /// <paramref name="obj"/> must already be open for write; the caller opens it (with force-open
    /// on a locked layer where the writer allows that).
    /// </summary>
    /// <exception cref="InvalidOperationException">A field is longer than <see cref="MaxField"/>, or the object is not open for write when a write is needed.</exception>
    public bool Write(Transaction tr, DBObject obj, string tagKey)
    {
        if (Matches(tr, obj, tagKey, this)) return false;

        var fields = new List<string> { Schema, RunId.ToString("D"), SegmentId.ToString("D"), Role.ToString(), Qualifier, Signature };
        fields.AddRange(Extras);
        foreach (var f in fields)
            if (f.Length > MaxField) throw new InvalidOperationException($"Owner tag field is {f.Length} characters; the limit is {MaxField}.");
        if (!obj.IsWriteEnabled)
            throw new InvalidOperationException($"Owner tag write on {obj.GetType().Name} {obj.Handle}: the object is not open for write (writer defect).");

        if (obj.ExtensionDictionary.IsNull) obj.CreateExtensionDictionary();
        var dict = (DBDictionary)tr.GetObject(obj.ExtensionDictionary, OpenMode.ForWrite);
        var rb = new ResultBuffer(fields.Select(f => new TypedValue((int)DxfCode.Text, f)).ToArray());
        if (dict.Contains(tagKey) && tr.GetObject(dict.GetAt(tagKey), OpenMode.ForWrite) is Xrecord xr)
        {
            xr.Data = rb;
        }
        else
        {
            var nx = new Xrecord { Data = rb };
            dict.SetAt(tagKey, nx);
            tr.AddNewlyCreatedDBObject(nx, true);
        }
        return true;
    }

    /// <summary>Writes <paramref name="tag"/> on <paramref name="obj"/>; see the instance <see cref="Write(Transaction, DBObject, string)"/>.</summary>
    public static bool Write(Transaction tr, DBObject obj, string tagKey, OwnerTag tag) => tag.Write(tr, obj, tagKey);

    /// <summary>Opens <paramref name="id"/> for write, also when its layer is locked (E504, E604). The layer's state is not changed.</summary>
    public static T OpenForWrite<T>(Transaction tr, ObjectId id) where T : DBObject
        => (T)tr.GetObject(id, OpenMode.ForWrite, false, true);
}

/// <summary>The run id every owner tag carries: derived from the run name, stable across seats and sessions.</summary>
public static class RunIds
{
    private const string Namespace = "SheetVol.Run:";

    /// <summary>
    /// The id of the run named <paramref name="runName"/> (ordinal: names differing only in case
    /// are different runs, as the persistence store keys them). SHA-256 of the UTF-8 name with a
    /// fixed prefix, first 16 bytes, version and variant bits set as an RFC 9562 version 8 UUID.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    public static Guid For(string runName)
    {
        if (string.IsNullOrEmpty(runName)) throw new ArgumentException("A run id needs a run name.", nameof(runName));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Namespace + runName));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);   // version 8
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // RFC 9562 variant
        return new Guid(bytes, bigEndian: true);
    }
}

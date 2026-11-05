// A3 work package. Owner: A3.
//
// The owner tag: an Xrecord under PaperConfig.OwnerTagKey in the extension dictionary of
// every object the paper writers create or fill. It carries the run GUID, the segment
// GUID, the role (C3's OwnedRole), a qualifier, the signature of what was written, and
// role-specific extras (a paper table's row keys). A4's persistence maps handles with it,
// and the writers find their own objects with it when a stored handle is missing.
//
// Layout: one TypedValue (DxfCode.Text) per field, each under 250 characters (6.9):
//   [0] schema marker  [1] run GUID  [2] segment GUID  [3] role  [4] qualifier
//   [5] signature      [6..] extras

using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Core.Tracking;

namespace SheetVol.Civil3D.WritersPaper;

/// <summary>A decoded owner tag.</summary>
public sealed record OwnerTag(Guid RunId, Guid SegmentId, OwnedRole Role, string Qualifier, string Signature, IReadOnlyList<string> Extras);

/// <summary>Reads and writes owner tags.</summary>
public static class OwnerTags
{
    /// <summary>Schema marker in field 0. A tag with another marker is not ours and is ignored.</summary>
    public const string Schema = "SV-OWNER-1";

    /// <summary>Longest string one TypedValue may carry (6.9).</summary>
    internal const int MaxField = 250;

    /// <summary>Reads the tag on <paramref name="obj"/>, or null when it has none or it is not ours.</summary>
    public static OwnerTag? Read(Transaction tr, DBObject obj, string key)
    {
        var dictId = obj.ExtensionDictionary;
        if (dictId.IsNull || dictId.IsErased) return null;
        var dict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead);
        if (!dict.Contains(key)) return null;
        if (tr.GetObject(dict.GetAt(key), OpenMode.ForRead) is not Xrecord xr) return null;
        using var rb = xr.Data;
        if (rb is null) return null;
        var v = rb.AsArray().Select(t => t.Value as string).ToArray();
        if (v.Length < 6 || v[0] != Schema) return null;
        if (!Guid.TryParse(v[1], out var run) || !Guid.TryParse(v[2], out var seg)) return null;
        if (!Enum.TryParse<OwnedRole>(v[3], out var role)) return null;
        return new OwnerTag(run, seg, role, v[4] ?? "", v[5] ?? "", v.Skip(6).Select(s => s ?? "").ToArray());
    }

    /// <summary>True when <paramref name="obj"/> already carries a tag equal to <paramref name="tag"/>.</summary>
    public static bool Matches(Transaction tr, DBObject obj, string key, OwnerTag tag)
        => Read(tr, obj, key) is { } existing && Same(existing, tag);

    /// <summary>
    /// Writes <paramref name="tag"/> on <paramref name="obj"/> unless an equal tag is already
    /// there. Returns true when it wrote. When a write is needed <paramref name="obj"/> must
    /// already be open for write: the caller opens it, with force-open on a locked layer.
    /// </summary>
    public static bool Write(Transaction tr, DBObject obj, string key, OwnerTag tag)
    {
        if (Matches(tr, obj, key, tag)) return false;

        var fields = new List<string> { Schema, tag.RunId.ToString("D"), tag.SegmentId.ToString("D"), tag.Role.ToString(), tag.Qualifier, tag.Signature };
        fields.AddRange(tag.Extras);
        foreach (var f in fields)
            if (f.Length > MaxField) throw new PaperWriterException(PaperCodes.WriterFailed, $"Owner tag field is {f.Length} characters; the limit is {MaxField}.");

        if (!obj.IsWriteEnabled)
            throw new PaperWriterException(PaperCodes.WriterFailed, $"Owner tag write on {obj.GetType().Name} {obj.Handle}: the object is not open for write (writer bug).");
        if (obj.ExtensionDictionary.IsNull) obj.CreateExtensionDictionary();
        var dict = (DBDictionary)tr.GetObject(obj.ExtensionDictionary, OpenMode.ForWrite);
        var rb = new ResultBuffer(fields.Select(f => new TypedValue((int)DxfCode.Text, f)).ToArray());
        if (dict.Contains(key) && tr.GetObject(dict.GetAt(key), OpenMode.ForWrite) is Xrecord xr)
        {
            xr.Data = rb;
        }
        else
        {
            var nx = new Xrecord { Data = rb };
            dict.SetAt(key, nx);
            tr.AddNewlyCreatedDBObject(nx, true);
        }
        return true;
    }

    /// <summary>Opens <paramref name="id"/> for write, also when its layer is locked (E504, E604). The layer's state is not changed.</summary>
    internal static T OpenForWrite<T>(Transaction tr, ObjectId id) where T : DBObject
        => (T)tr.GetObject(id, OpenMode.ForWrite, false, true);

    private static bool Same(OwnerTag a, OwnerTag b)
        => a.RunId == b.RunId && a.SegmentId == b.SegmentId && a.Role == b.Role
        && a.Qualifier == b.Qualifier && a.Signature == b.Signature && a.Extras.SequenceEqual(b.Extras);
}

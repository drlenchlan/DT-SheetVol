// A2 model writers: small drawing helpers shared by the writers and the store. Owner: A2.
// The caller supplies the transaction; nothing here commits, and nothing disposes an
// object obtained from tr.GetObject (HANDOFF.md 6.13).

using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Tracking;

namespace SheetVol.Civil3D.WritersModel;

/// <summary>Handles as stored text and back.</summary>
public static class Handles
{
    /// <summary>The object's handle as upper-case hex.</summary>
    public static string Of(ObjectId id) => id.Handle.ToString();

    /// <summary>
    /// Resolves stored hex to a live, non-erased object id; <see cref="ObjectId.Null"/> when the
    /// text is not a handle or the object is gone (E802).
    /// </summary>
    public static ObjectId Resolve(Database db, string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || !long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value) || value <= 0)
            return ObjectId.Null;
        if (!db.TryGetObjectId(new Handle(value), out ObjectId id)) return ObjectId.Null;
        return id.IsNull || id.IsErased || !id.IsValid ? ObjectId.Null : id;
    }
}

/// <summary>
/// The ownership tag: an Xrecord on the entity's extension dictionary naming the run, the
/// segment GUID, the role and qualifier, and for generated tables the row keys that let a
/// table grow or shrink in place (E505). A copy of the entity in another drawing carries the
/// tag as inert data and is a plain object there (E810).
/// </summary>
public sealed record OwnerTag(string RunName, OwnedKey Key, IReadOnlyList<string> RowKeys)
{
    private const string Marker = "SHEETVOL-OWNER";
    private const int Version = 1;
    private const int MarkerCode = 1, VersionCode = 90, RunCode = 300, SegmentCode = 301, RoleCode = 302, QualifierCode = 303, RowKeyCode = 304;

    /// <summary>True when this tag names the given run and key.</summary>
    public bool Matches(string runName, OwnedKey key) => string.Equals(RunName, runName, StringComparison.Ordinal) && Key == key;

    /// <summary>Reads the tag, or null when the entity carries none or it is malformed.</summary>
    public static OwnerTag? Read(Transaction tr, DBObject obj, string tagKey)
    {
        if (obj.ExtensionDictionary.IsNull) return null;
        var dict = (DBDictionary)tr.GetObject(obj.ExtensionDictionary, OpenMode.ForRead);
        if (!dict.Contains(tagKey)) return null;
        if (tr.GetObject(dict.GetAt(tagKey), OpenMode.ForRead) is not Xrecord xr || xr.Data is null) return null;
        string? marker = null, run = null, seg = null, role = null, qual = null;
        int? version = null;
        var rows = new List<string>();
        foreach (TypedValue tv in xr.Data)
        {
            switch (tv.TypeCode)
            {
                case MarkerCode: marker = tv.Value as string; break;
                case VersionCode: version = tv.Value is int v ? v : null; break;
                case RunCode: run = tv.Value as string; break;
                case SegmentCode: seg = tv.Value as string; break;
                case RoleCode: role = tv.Value as string; break;
                case QualifierCode: qual = tv.Value as string; break;
                case RowKeyCode: rows.Add(tv.Value as string ?? ""); break;
            }
        }
        if (marker != Marker || version is null || version > Version || run is null || qual is null
            || !Guid.TryParse(seg, out Guid segId) || !Enum.TryParse(role, out OwnedRole r)) return null;
        return new OwnerTag(run, new OwnedKey(segId, r, qual), rows);
    }

    /// <summary>Writes or replaces the tag. The entity must be open for write and in the database.</summary>
    public void Write(Transaction tr, DBObject obj, string tagKey)
    {
        if (obj.ExtensionDictionary.IsNull) obj.CreateExtensionDictionary();
        var dict = (DBDictionary)tr.GetObject(obj.ExtensionDictionary, OpenMode.ForWrite);
        var data = new ResultBuffer(
            new TypedValue(MarkerCode, Marker),
            new TypedValue(VersionCode, Version),
            new TypedValue(RunCode, RunName),
            new TypedValue(SegmentCode, Key.SegmentId.ToString("D")),
            new TypedValue(RoleCode, Key.Role.ToString()),
            new TypedValue(QualifierCode, Key.Qualifier));
        foreach (string row in RowKeys) data.Add(new TypedValue(RowKeyCode, row));
        if (dict.Contains(tagKey))
        {
            var xr = (Xrecord)tr.GetObject(dict.GetAt(tagKey), OpenMode.ForWrite);
            xr.Data = data;
        }
        else
        {
            var xr = new Xrecord { Data = data };
            dict.SetAt(tagKey, xr);
            tr.AddNewlyCreatedDBObject(xr, true);
        }
    }
}

/// <summary>
/// Unlocks a locked layer for the duration of one apply and relocks it before the caller
/// commits (E504). Frozen layers are never thawed. Only layers that actually stood in the way
/// of a write are touched, so a regen with nothing to write leaves every layer alone (E811).
/// </summary>
public sealed class LayerGuard
{
    private readonly Transaction _tr;
    private readonly List<ObjectId> _unlocked = [];
    private readonly HashSet<ObjectId> _frozenNoted = [];

    /// <summary>Starts a guard on the caller's transaction.</summary>
    public LayerGuard(Transaction tr) => _tr = tr;

    /// <summary>Notes raised: one per layer unlocked, one per frozen layer written on.</summary>
    public List<Problem> Notes { get; } = [];

    /// <summary>Makes the layer writable for an entity edit or creation; notes a frozen layer.</summary>
    public void Allow(ObjectId layerId)
    {
        if (layerId.IsNull) return;
        var layer = (LayerTableRecord)_tr.GetObject(layerId, OpenMode.ForRead);
        if (layer.IsLocked && !_unlocked.Contains(layerId))
        {
            layer.UpgradeOpen();
            layer.IsLocked = false;
            _unlocked.Add(layerId);
            Notes.Add(new Problem(ModelWriterCodes.LayerLockedOrFrozen, Severity.Info,
                $"Layer '{layer.Name}' was locked; it was unlocked for the update and relocked."));
        }
        if (layer.IsFrozen && _frozenNoted.Add(layerId))
            Notes.Add(new Problem(ModelWriterCodes.LayerLockedOrFrozen, Severity.Info,
                $"Layer '{layer.Name}' is frozen; its objects were updated and the layer was left frozen."));
    }

    /// <summary>Relocks every layer this guard unlocked. Returns the layer names.</summary>
    public IReadOnlyList<string> Relock()
    {
        var names = new List<string>();
        foreach (ObjectId id in _unlocked)
        {
            var layer = (LayerTableRecord)_tr.GetObject(id, OpenMode.ForWrite);
            layer.IsLocked = true;
            names.Add(layer.Name);
        }
        _unlocked.Clear();
        return names;
    }
}

/// <summary>Name lookups that fail with SV-E501 or SV-E601 instead of falling back (E501, E601, 6.13).</summary>
public static class DrawingNames
{
    /// <summary>The layer's id, or null with an SV-E501 problem naming it and the setting.</summary>
    public static ObjectId? Layer(Transaction tr, Database db, string name, string setting, List<Problem> problems)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(name)) return lt[name];
        problems.Add(Missing("Layer", name, setting));
        return null;
    }

    /// <summary>The table style's id, or null with an SV-E501 problem.</summary>
    public static ObjectId? TableStyle(Transaction tr, Database db, string name, string setting, List<Problem> problems)
    {
        var dict = (DBDictionary)tr.GetObject(db.TableStyleDictionaryId, OpenMode.ForRead);
        if (dict.Contains(name)) return dict.GetAt(name);
        problems.Add(Missing("Table style", name, setting));
        return null;
    }

    /// <summary>The text style's id, or null with an SV-E501 problem.</summary>
    public static ObjectId? TextStyle(Transaction tr, Database db, string name, string setting, List<Problem> problems)
    {
        var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        if (tst.Has(name)) return tst[name];
        problems.Add(Missing("Text style", name, setting));
        return null;
    }

    /// <summary>The paper-space block of a named layout, or null when no layout has that name.</summary>
    public static ObjectId? LayoutBlock(Transaction tr, Database db, string layoutName)
    {
        var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in layouts)
        {
            if (!string.Equals(e.Key, layoutName, StringComparison.OrdinalIgnoreCase)) continue;
            var layout = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            return layout.ModelType ? null : layout.BlockTableRecordId;
        }
        return null;
    }

    /// <summary>Insertion point of the first reference to the anchor block in a block, or null (6.7).</summary>
    public static Pt? AnchorPoint(Transaction tr, ObjectId blockId, string anchorBlockName)
    {
        var btr = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
        foreach (ObjectId id in btr)
        {
            if (id.ObjectClass.DxfName != "INSERT") continue;
            var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
            var def = (BlockTableRecord)tr.GetObject(br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord, OpenMode.ForRead);
            if (string.Equals(def.Name, anchorBlockName, StringComparison.OrdinalIgnoreCase))
                return new Pt(br.Position.X, br.Position.Y);
        }
        return null;
    }

    private static Problem Missing(string what, string name, string setting)
        => new(ModelWriterCodes.MissingStyleOrLayer, Severity.Error,
               $"{what} '{name}' named by config setting {setting} is not in the drawing. Nothing was written.",
               Fix: $"Add {what.ToLowerInvariant()} '{name}' to the drawing (normally from the LAN template), or change {setting}.");
}

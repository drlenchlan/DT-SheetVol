// A1 adapters. Owner: A1.

using Autodesk.AutoCAD.DatabaseServices;

namespace SheetVol.Civil3D.Adapters;

/// <summary>
/// The mark that says SheetVol created and owns a volume surface (HANDOFF.md 6.5: never edit a
/// volume surface the tool does not own). An Xrecord under <see cref="AdapterConfig.OwnershipKey"/>
/// in the surface's extension dictionary holding, as text: the marker, the tag schema, the name
/// the tool gave the surface, and the base and comparison handles at creation. A surface whose
/// current name differs from the tagged name was renamed by a user (E412).
/// </summary>
/// <param name="Name">The name SheetVol gave the surface.</param>
/// <param name="BaseHandle">Base surface handle when the surface was created.</param>
/// <param name="ComparisonHandle">Comparison surface handle when the surface was created.</param>
public sealed record OwnershipTag(string Name, string BaseHandle, string ComparisonHandle)
{
    /// <summary>Tag layout version, the second string of the Xrecord.</summary>
    public const string Schema = "1";

    /// <summary>Reads the tag, or null when the object carries none (not owned).</summary>
    public static OwnershipTag? Read(Transaction tr, DBObject obj, AdapterConfig cfg)
    {
        if (obj.ExtensionDictionary.IsNull || obj.ExtensionDictionary.IsErased) return null;
        if (tr.GetObject(obj.ExtensionDictionary, OpenMode.ForRead) is not DBDictionary dict || !dict.Contains(cfg.OwnershipKey)) return null;
        if (tr.GetObject(dict.GetAt(cfg.OwnershipKey), OpenMode.ForRead) is not Xrecord xr) return null;
        using var rb = xr.Data;
        if (rb is null) return null;
        var values = rb.AsArray().Select(v => v.Value as string).ToArray();
        if (values.Length < 5 || values[0] != cfg.OwnershipMarker || values[1] != Schema) return null;
        return new OwnershipTag(values[2] ?? "", values[3] ?? "", values[4] ?? "");
    }

    /// <summary>Writes (or replaces) the tag. <paramref name="obj"/> must be open for write.</summary>
    public void Write(Transaction tr, DBObject obj, AdapterConfig cfg)
    {
        if (obj.ExtensionDictionary.IsNull) obj.CreateExtensionDictionary();
        var dict = (DBDictionary)tr.GetObject(obj.ExtensionDictionary, OpenMode.ForWrite);
        using var data = new ResultBuffer(
            new TypedValue((int)DxfCode.Text, cfg.OwnershipMarker),
            new TypedValue((int)DxfCode.Text, Schema),
            new TypedValue((int)DxfCode.Text, Name),
            new TypedValue((int)DxfCode.Text, BaseHandle),
            new TypedValue((int)DxfCode.Text, ComparisonHandle));
        if (dict.Contains(cfg.OwnershipKey))
        {
            var existing = (Xrecord)tr.GetObject(dict.GetAt(cfg.OwnershipKey), OpenMode.ForWrite);
            existing.Data = data;
            return;
        }
        var xr = new Xrecord { Data = data };
        dict.SetAt(cfg.OwnershipKey, xr);
        tr.AddNewlyCreatedDBObject(xr, true);
    }
}

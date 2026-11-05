using Autodesk.AutoCAD.DatabaseServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace SheetVol.UI.Host;

/// <summary>
/// The two Civil 3D reads the editor side needs: an alignment's name and
/// <c>Alignment.StationOffset</c>. Civil's <c>Alignment</c> derives from a type in
/// <c>AecBaseMgd.dll</c>, which <c>SheetVol.UI.csproj</c> does not reference (CS0012), and the
/// work package may not change the csproj. Until the orchestrator adds that reference, the
/// members are reached through <c>dynamic</c>; the type test uses reflection. With the
/// reference in place, replace the bodies with a cast to <c>Alignment</c> and direct calls.
/// The objects are real Civil 3D alignments opened in a transaction, so the calls bind to the
/// same members a typed call would.
/// </summary>
internal static class CivilReads
{
    /// <summary>The Civil 3D alignment type, for <c>PromptEntityOptions.AddAllowedClass</c>.</summary>
    public static Type AlignmentType => typeof(CivilAlignment);

    /// <summary>True when <paramref name="obj"/> is a Civil 3D alignment.</summary>
    public static bool IsAlignment(DBObject obj) => typeof(CivilAlignment).IsInstanceOfType(obj);

    /// <summary>The alignment's name. <paramref name="alignment"/> must pass <see cref="IsAlignment"/>.</summary>
    public static string Name(DBObject alignment) => (string)((dynamic)alignment).Name;

    /// <summary>Raw station and offset of a WCS point. Throws <c>Autodesk.Civil.PointNotOnEntityException</c> when the point is not beside the alignment.</summary>
    public static (double Station, double Offset) StationOffset(DBObject alignment, double easting, double northing)
    {
        double station = 0, offset = 0;
        ((dynamic)alignment).StationOffset(easting, northing, ref station, ref offset);
        return (station, offset);
    }
}

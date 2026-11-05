using Autodesk.AutoCAD.DatabaseServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace SheetVol.UI.Host;

/// <summary>
/// The two Civil 3D reads the editor side needs: an alignment's name and
/// <c>Alignment.StationOffset</c>, typed since <c>SheetVol.UI.csproj</c> references
/// <c>AecBaseMgd.dll</c> (where Civil's <c>Alignment</c> base type lives; it was reached
/// through <c>dynamic</c> before that reference existed).
/// </summary>
internal static class CivilReads
{
    /// <summary>The Civil 3D alignment type, for <c>PromptEntityOptions.AddAllowedClass</c>.</summary>
    public static Type AlignmentType => typeof(CivilAlignment);

    /// <summary>True when <paramref name="obj"/> is a Civil 3D alignment.</summary>
    public static bool IsAlignment(DBObject obj) => obj is CivilAlignment;

    /// <summary>The alignment's name. <paramref name="alignment"/> must pass <see cref="IsAlignment"/>.</summary>
    public static string Name(DBObject alignment) => ((CivilAlignment)alignment).Name;

    /// <summary>Raw station and offset of a WCS point. Throws <c>Autodesk.Civil.PointNotOnEntityException</c> when the point is not beside the alignment.</summary>
    public static (double Station, double Offset) StationOffset(DBObject alignment, double easting, double northing)
    {
        double station = 0, offset = 0;
        ((CivilAlignment)alignment).StationOffset(easting, northing, ref station, ref offset);
        return (station, offset);
    }
}

// Wave 2 integration. The world edits SVSELFTEST Installed makes in the real drawing
// (SelfTest/SelfTestFakes.cs ISelfTestWorld): T12 moves a Fixture A pyramid apex, T14
// lengthens alignment S, T17 adds a north-running alignment. Each edit is its own committed
// transaction, as a user's edit would be; the regen that follows sees it through the adapters.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using SheetVol.Civil3D.SelfTest;
using SheetVol.Core;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivAlignmentLine = Autodesk.Civil.DatabaseServices.AlignmentLine;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;

namespace SheetVol.Civil3D.Integration;

/// <summary>World edits against the active drawing's fixture objects.</summary>
public sealed class CivilSelfTestWorld : ISelfTestWorld
{
    /// <inheritdoc />
    /// <remarks>The vertex at the apex (FindVertexAtXY returns the nearest vertex, S3) gets the new elevation and the TIN is rebuilt; IsOutOfDate stays false (P18), so the owned volume surface's always-Rebuild is what picks it up.</remarks>
    public void SetPyramidApex(string surfaceName, double centerX, double apexZ) => Edit(tr =>
    {
        var tin = Tin(tr, surfaceName);
        tin.UpgradeOpen();
        using (tin.SetVertexElevation(tin.FindVertexAtXY(centerX, 0), apexZ)) { }
        tin.Rebuild();
    });

    /// <inheritdoc />
    /// <remarks>Alignment S is one fixed line (FX); its second pass-through point moves to the new end.</remarks>
    public void LengthenAlignment(string alignmentName, double newEndStation) => Edit(tr =>
    {
        var al = Alignment(tr, alignmentName) ?? throw new InvalidOperationException($"Alignment '{alignmentName}' is not in the drawing.");
        al.UpgradeOpen();
        var line = al.Entities.GetEntityByOrder(al.Entities.Count - 1) as CivAlignmentLine
                   ?? throw new InvalidOperationException($"The last entity of '{alignmentName}' is not a line.");
        var a = line.PassThroughPoint1;
        var b = line.PassThroughPoint2;
        double extra = newEndStation - al.EndingStation, len = a.GetDistanceTo(b);
        line.PassThroughPoint2 = new Point2d(b.X + (b.X - a.X) / len * extra, b.Y + (b.Y - a.Y) / len * extra);
    });

    /// <inheritdoc />
    public void CreateStraightAlignment(string name, Pt from, Pt to) => Edit(tr =>
    {
        if (Alignment(tr, name) is not null) return;
        var db = AcApp.DocumentManager.MdiActiveDocument.Database;
        var civ = CivilDocument.GetCivilDocument(db);
        var style = civ.Styles.AlignmentStyles.Count > 0 ? civ.Styles.AlignmentStyles[0] : ObjectId.Null;
        var labels = civ.Styles.LabelSetStyles.AlignmentLabelSetStyles.Count > 0 ? civ.Styles.LabelSetStyles.AlignmentLabelSetStyles[0] : ObjectId.Null;
        var id = CivAlignment.Create(civ, name, ObjectId.Null, db.LayerZero, style, labels);
        var al = (CivAlignment)tr.GetObject(id, OpenMode.ForWrite);
        al.Entities.AddFixedLine(new Point3d(from.X, from.Y, 0), new Point3d(to.X, to.Y, 0));
    });

    private static void Edit(Action<Transaction> edit)
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("No drawing is open.");
        using var docLock = doc.LockDocument();
        using var tr = doc.Database.TransactionManager.StartTransaction();
        edit(tr);
        tr.Commit();
    }

    private static CivTinSurface Tin(Transaction tr, string name)
    {
        var civ = CivilDocument.GetCivilDocument(AcApp.DocumentManager.MdiActiveDocument.Database);
        foreach (ObjectId id in civ.GetSurfaceIds())
            if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is CivTinSurface s && s.Name == name) return s;
        throw new InvalidOperationException($"TIN surface '{name}' is not in the drawing.");
    }

    private static CivAlignment? Alignment(Transaction tr, string name)
    {
        var civ = CivilDocument.GetCivilDocument(AcApp.DocumentManager.MdiActiveDocument.Database);
        foreach (ObjectId id in civ.GetAlignmentIds())
            if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is CivAlignment a && a.Name == name) return a;
        return null;
    }
}

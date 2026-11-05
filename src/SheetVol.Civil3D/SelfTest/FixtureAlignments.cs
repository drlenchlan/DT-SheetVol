// FX work package. Owner: FX.
//
// Alignments S, C, K and SP (HANDOFF.md section 8; design values and derivations in
// FixtureSpec). Each is siteless, starts at (0,0) with station 0, and is checked
// after creation: entity count, start and end station, and centreline points at
// known stations through PointLocation with offset 0. Offsets and the meaning of
// Clockwise are probe P7 (S1) and are reported, not checked, here.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using static SheetVol.Civil3D.SelfTest.FixtureSpec;
using static SheetVol.Civil3D.SelfTest.FixtureSurfaces;

namespace SheetVol.Civil3D.SelfTest;

internal static class FixtureAlignments
{
    internal static AlignmentFixture BuildS(Database db, Transaction tr, CivilDocument civ)
    {
        var (al, notes) = NewAlignment(db, tr, civ, Names.AlignmentS);
        al.Entities.AddFixedLine(P(S.StartX, 0), P(S.EndX, 0));

        CheckShape(al, 1, S.Length, notes);
        CheckPoint(al, 0.0, (S.StartX, 0), notes);
        CheckPoint(al, 1000.0, (1000.0, 0), notes);
        CheckPoint(al, S.Length, (S.EndX, 0), notes);
        return Result(al, notes);
    }

    internal static AlignmentFixture BuildC(Database db, Transaction tr, CivilDocument civ)
    {
        var (al, notes) = NewAlignment(db, tr, civ, Names.AlignmentC);
        // Two tangents meeting at the PI, then a free curve of radius R between them.
        var pi = P(C.PiX, 0);
        double outLen = C.CurveTangent + C.TangentOut;
        var end = P(C.PiX + outLen * Math.Cos(C.OutDirection), outLen * Math.Sin(C.OutDirection));
        var t1 = al.Entities.AddFixedLine(P(0, 0), pi);
        var t2 = al.Entities.AddFixedLine(pi, end);
        al.Entities.AddFreeCurve(t1.EntityId, t2.EntityId, C.Radius, CurveParamType.Radius, false, CurveType.Compound);

        CheckShape(al, 3, C.Length, notes);
        CheckPoint(al, 0.0, (0, 0), notes);
        CheckPoint(al, C.ArcStartStation, (C.TangentIn, 0), notes);
        CheckPoint(al, C.ArcEndStation, C.ArcEnd, notes);
        CheckPoint(al, C.Length, C.End, notes);
        return Result(al, notes);
    }

    internal static AlignmentFixture BuildK(Database db, Transaction tr, CivilDocument civ)
    {
        var (al, notes) = NewAlignment(db, tr, civ, Names.AlignmentK);
        al.Entities.AddFixedLine(P(0, 0), P(K.AnglePoint.X, K.AnglePoint.Y));
        al.Entities.AddFixedLine(P(K.AnglePoint.X, K.AnglePoint.Y), P(K.End.X, K.End.Y));

        CheckShape(al, 2, K.Length, notes);
        CheckPoint(al, 0.0, (0, 0), notes);
        CheckPoint(al, K.AnglePointStation, K.AnglePoint, notes);
        CheckPoint(al, K.Length, K.End, notes);
        return Result(al, notes);
    }

    internal static AlignmentFixture BuildSP(Database db, Transaction tr, CivilDocument civ)
    {
        var (al, notes) = NewAlignment(db, tr, civ, Names.AlignmentSP);
        var t1 = al.Entities.AddFixedLine(P(0, 0), P(SP.Pi.X, SP.Pi.Y));
        var t2 = al.Entities.AddFixedLine(P(SP.Pi.X, SP.Pi.Y), P(SP.End.X, SP.End.Y));
        al.Entities.AddFreeSCS(t1.EntityId, t2.EntityId, SP.SpiralIn, SP.SpiralOut, SpiralParamType.Length,
            SP.Radius, false, SpiralType.Clothoid);

        CheckShape(al, 3, SP.Length, notes, SP.StationTolerance);
        CheckPoint(al, 0.0, (0, 0), notes);
        CheckPoint(al, al.EndingStation, SP.End, notes);   // the out-tangent's far end is fixed
        CheckPoint(al, SP.TsStation, (SP.TsStation, 0), notes, SP.StationTolerance);   // TS is on the x-axis
        notes.Note($"{al.Name}: analytic TS {F(SP.TsStation)}, SC {F(SP.ScStation)}, CS {F(SP.CsStation)}, ST {F(SP.StStation)}, end {F(SP.Length)}");
        return Result(al, notes);
    }

    // ------------------------------------------------------------ helpers

    private static (Alignment Al, FixtureChecks Checks) NewAlignment(Database db, Transaction tr, CivilDocument civ, string name)
    {
        foreach (ObjectId id in civ.GetAlignmentIds())
        {
            if (tr.GetObject(id, OpenMode.ForRead) is Alignment a && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                throw new FixtureException(FixtureCodes.NameInUse, $"an alignment named '{name}' already exists; build fixtures into a drawing without it");
        }

        var checks = new FixtureChecks();
        var styleId = PickStyle(civ.Styles.AlignmentStyles, Styles.AlignmentStyle, "alignment style", checks);
        var labelSetId = PickStyle(civ.Styles.LabelSetStyles.AlignmentLabelSetStyles, Styles.AlignmentLabelSet, "alignment label set", checks);

        ObjectId alId;
        try { alId = Alignment.Create(civ, name, ObjectId.Null, db.LayerZero, styleId, labelSetId); }
        catch (System.Exception ex) { throw new FixtureException(FixtureCodes.BuildFailed, $"Alignment.Create('{name}') threw {ex.GetType().Name}: {ex.Message}", ex); }
        return ((Alignment)tr.GetObject(alId, OpenMode.ForWrite), checks);
    }

    private static ObjectId PickStyle(TreeNodeCollectionBase styles, string preferred, string what, FixtureChecks checks)
    {
        if (styles.Contains(preferred)) return styles[preferred];
        if (styles.Count == 0)
            throw new FixtureException(FixtureCodes.BuildFailed, $"the drawing has no {what}; open a Civil 3D drawing or template first");
        var id = styles[0];
        checks.Note($"no {what} named '{preferred}'; used the first one in the drawing");
        return id;
    }

    private static void CheckShape(Alignment al, int entities, double length, FixtureChecks checks, double tolerance = GeometryTolerance)
    {
        int count = al.Entities.Count;
        string line = $"{al.Name}: {count} entities, stations {al.StartingStation.ToString("R", Inv)}..{al.EndingStation.ToString("R", Inv)} (expected {entities} entities, 0..{F(length)}, end diff {al.EndingStation - length:E2})";
        bool ok = count == entities && Math.Abs(al.StartingStation) <= GeometryTolerance && Math.Abs(al.EndingStation - length) <= tolerance;
        if (ok) checks.Pass(line); else checks.Fail(line);
    }

    private static void CheckPoint(Alignment al, double station, (double X, double Y) expected, FixtureChecks checks, double tolerance = GeometryTolerance)
    {
        double e = 0, n = 0;
        try { al.PointLocation(station, 0.0, ref e, ref n); }
        catch (System.Exception ex)
        {
            checks.Fail($"{al.Name}: PointLocation({F(station)}, 0) threw {ex.GetType().Name}: {ex.Message}");
            return;
        }
        double d = Math.Sqrt((e - expected.X) * (e - expected.X) + (n - expected.Y) * (n - expected.Y));
        if (d > tolerance)
            checks.Fail($"{al.Name}: station {F(station)} is at ({F(e)}, {F(n)}), expected ({F(expected.X)}, {F(expected.Y)}), off by {d:E2}");
        else
            checks.Pass($"{al.Name}: sta {F(station)} -> ({F(e)}, {F(n)}) ok");
    }

    private static AlignmentFixture Result(Alignment al, FixtureChecks checks)
    {
        checks.ThrowIfFailed($"Alignment {al.Name}");
        return new(al.Name, al.ObjectId, al.StartingStation, al.EndingStation, checks.Passed);
    }

    private static Point3d P(double x, double y) => new(x, y, 0.0);
}

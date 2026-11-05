// FX work package. Owner: FX.
//
// TIN surfaces for Fixtures A, B, D and H (HANDOFF.md section 8), built entirely
// from 2-point standard breaklines (the EG rectangle included), so every vertex is
// in the surface definition and the triangulation is forced where it matters. Each
// is then checked against the analytic surface: vertex, triangle and hidden-triangle
// counts, and elevation probes. Any mismatch fails the build (SV-E192): a fixture
// that is silently wrong would make every test that uses it wrong.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using static SheetVol.Civil3D.SelfTest.FixtureSpec;

namespace SheetVol.Civil3D.SelfTest;

internal static class FixtureSurfaces
{
    // ------------------------------------------------------------ fixtures

    internal static SurfaceFixture BuildA(Database db, Transaction tr, CivilDocument civ)
    {
        var checks = new FixtureChecks();
        var (egId, reused) = EnsureEg(db, tr, civ, checks);

        var fg = NewTin(db, tr, civ, Names.FgA, checks);
        AddPerimeter(fg, EgZ);
        foreach (var p in Pyramids)
        {
            double h = PyramidHalfBase;
            var corners = new[]
            {
                new Point3d(p.CenterX - h, -h, EgZ), new Point3d(p.CenterX + h, -h, EgZ),
                new Point3d(p.CenterX + h, h, EgZ), new Point3d(p.CenterX - h, h, EgZ),
            };
            var apex = new Point3d(p.CenterX, 0.0, p.ApexZ);
            for (int i = 0; i < 4; i++)
            {
                AddSegment(fg, corners[i], corners[(i + 1) % 4]);   // base edge
                AddSegment(fg, apex, corners[i]);                  // apex to corner
            }
        }
        fg.Rebuild();

        CheckCounts(fg, Names.FgA, FgAPoints, FgATriangles, checks);
        foreach (var p in Pyramids)
        {
            double h = PyramidHalfBase;
            CheckZ(fg, Names.FgA, p.CenterX, 0.0, p.ApexZ, checks);                              // apex
            CheckZ(fg, Names.FgA, p.CenterX + h / 2, 0.0, EgZ + p.Height / 2, checks);           // halfway down a face
            CheckZ(fg, Names.FgA, p.CenterX - h / 2, h / 4, EgZ + p.Height / 2, checks);         // same, west face off-axis
            CheckZ(fg, Names.FgA, p.CenterX, h, EgZ, checks);                                    // base edge midpoint
            CheckZ(fg, Names.FgA, p.CenterX, 2 * h, EgZ, checks);                                // outside the base
        }
        CheckZ(fg, Names.FgA, 750.0, 0.0, EgZ, checks);                                          // between pyramids

        checks.ThrowIfFailed("Fixture A");
        return new SurfaceFixture("A", egId, Names.EG, fg.ObjectId, Names.FgA, reused, checks.Passed);
    }

    internal static SurfaceFixture BuildB(Database db, Transaction tr, CivilDocument civ)
    {
        var checks = new FixtureChecks();
        var (egId, reused) = EnsureEg(db, tr, civ, checks);

        var fg = NewTin(db, tr, civ, Names.FgB, checks);
        AddPerimeter(fg, EgZ);
        AddSegment(fg, new Point3d(RidgeFromX, -RidgeHalfWidth, EgZ), new Point3d(RidgeToX, -RidgeHalfWidth, EgZ));
        AddSegment(fg, new Point3d(RidgeFromX, 0.0, RidgeCrestZ), new Point3d(RidgeToX, 0.0, RidgeCrestZ));
        AddSegment(fg, new Point3d(RidgeFromX, RidgeHalfWidth, EgZ), new Point3d(RidgeToX, RidgeHalfWidth, EgZ));
        fg.Rebuild();

        CheckCounts(fg, Names.FgB, FgBPoints, FgBTriangles, checks);
        foreach (var x in new[] { 0.0, 1000.0, 2000.0 })
        {
            CheckZ(fg, Names.FgB, x, 0.0, RidgeCrestZ, checks);
            CheckZ(fg, Names.FgB, x, 15.0, (EgZ + RidgeCrestZ) / 2, checks);
            CheckZ(fg, Names.FgB, x, -15.0, (EgZ + RidgeCrestZ) / 2, checks);
            CheckZ(fg, Names.FgB, x, RidgeHalfWidth, EgZ, checks);
            CheckZ(fg, Names.FgB, x, -100.0, EgZ, checks);
        }

        checks.ThrowIfFailed("Fixture B");
        return new SurfaceFixture("B", egId, Names.EG, fg.ObjectId, Names.FgB, reused, checks.Passed);
    }

    internal static SurfaceFixture BuildD(Database db, Transaction tr, CivilDocument civ)
    {
        var checks = new FixtureChecks();
        var (egId, reused) = EnsureEg(db, tr, civ, checks);

        var fg = NewTin(db, tr, civ, Names.FgD, checks);
        AddPerimeter(fg, EgZ + SlabRaise);
        fg.Rebuild();

        CheckCounts(fg, Names.FgD, FgDPoints, FgDTriangles, checks);
        CheckZ(fg, Names.FgD, 0.0, 0.0, EgZ + SlabRaise, checks);
        CheckZ(fg, Names.FgD, 2100.0, -250.0, EgZ + SlabRaise, checks);

        checks.ThrowIfFailed("Fixture D");
        return new SurfaceFixture("D", egId, Names.EG, fg.ObjectId, Names.FgD, reused, checks.Passed);
    }

    internal static SurfaceFixture BuildH(Database db, Transaction tr, CivilDocument civ)
    {
        var checks = new FixtureChecks();
        RequireFree(tr, civ, Names.EgH);
        RequireFree(tr, civ, Names.FgH);

        var eg = NewTin(db, tr, civ, Names.EgH, checks);
        foreach (var y in EgHLines)
            AddSegment(eg, new Point3d(ChannelFromX, y, EgHZ(y)), new Point3d(ChannelToX, y, EgHZ(y)));
        eg.Rebuild();

        var fg = NewTin(db, tr, civ, Names.FgH, checks);
        foreach (var (y, vertices) in FgHLines)
            for (int i = 0; i + 1 < vertices.Length; i++)
                AddSegment(fg, new Point3d(vertices[i].X, y, vertices[i].Z), new Point3d(vertices[i + 1].X, y, vertices[i + 1].Z));
        fg.Rebuild();

        CheckCounts(eg, Names.EgH, EgHPoints, EgHTriangles, checks);
        CheckCounts(fg, Names.FgH, FgHPoints, FgHTriangles, checks);
        // EG_H across every strip, at both ends of the reach and in the middle.
        foreach (var x in new[] { 250.0, 1000.0, 1700.0 })
            foreach (var y in new[] { -70.0, -35.0, -13.0, -5.0, 5.0, 13.0, 25.0, 35.0, 70.0 })
                CheckZ(eg, Names.EgH, x, y, EgHZ(y), checks);
        // FG_H in the sediment reach: bed 94; between |y| = 10 and 16 it rises 4 over 6 ft
        // (94 + (|y| - 10) x 4/6, so 96 at |y| = 13 and 98 at 16); EG_H beyond.
        static double FgSediment(double y)
        {
            double a = Math.Abs(y);
            if (a <= ChannelHalfBed) return SedimentBedZ;
            if (a <= SedimentHalfWidth) return SedimentBedZ + (a - ChannelHalfBed) * (SedimentMeetZ - SedimentBedZ) / (SedimentHalfWidth - ChannelHalfBed);
            return EgHZ(y);
        }
        foreach (var x in new[] { 250.0, 1000.0 })
            foreach (var y in new[] { -70.0, -35.0, -13.0, -5.0, 5.0, 13.0, 25.0, 35.0, 70.0 })
                CheckZ(fg, Names.FgH, x, y, FgSediment(y), checks);
        // Past the transition FG_H equals EG_H.
        foreach (var y in new[] { -70.0, -35.0, -13.0, -5.0, 5.0, 13.0, 25.0, 35.0, 70.0 })
            CheckZ(fg, Names.FgH, 1700.0, y, EgHZ(y), checks);

        checks.ThrowIfFailed("Fixture H");
        return new SurfaceFixture("H", eg.ObjectId, Names.EgH, fg.ObjectId, Names.FgH, false, checks.Passed);
    }

    // ------------------------------------------------------------ volume surfaces

    internal static VolumeFixture BuildVolume(Database db, Transaction tr, CivilDocument civ, string baseName, string comparisonName, string volumeName)
    {
        var baseId = FindSurface(tr, civ, baseName);
        var compId = FindSurface(tr, civ, comparisonName);
        if (baseId.IsNull) throw new FixtureException(FixtureCodes.BadInput, $"base surface '{baseName}' not found");
        if (compId.IsNull) throw new FixtureException(FixtureCodes.BadInput, $"comparison surface '{comparisonName}' not found");
        RequireFree(tr, civ, volumeName);

        ObjectId volId;
        try { volId = TinVolumeSurface.Create(volumeName, baseId, compId); }
        catch (System.Exception ex) { throw new FixtureException(FixtureCodes.BuildFailed, $"TinVolumeSurface.Create('{volumeName}') threw {ex.GetType().Name}: {ex.Message}", ex); }

        var vol = (TinVolumeSurface)tr.GetObject(volId, OpenMode.ForWrite);
        if (vol.IsOutOfDate) vol.Rebuild();
        return ReadVolume(vol, baseName, comparisonName);
    }

    internal static VolumeFixture ReadVolume(TinVolumeSurface vol, string baseName, string comparisonName)
    {
        var props = vol.GetVolumeProperties();
        int triangles = vol.GetTinProperties().NumberOfTriangles;
        return new VolumeFixture(vol.Name, vol.ObjectId, baseName, comparisonName,
            props.UnadjustedCutVolume, props.UnadjustedFillVolume, props.UnadjustedNetVolume, triangles);
    }

    /// <summary>The analytic whole-surface expectation for a fixture pair, in words, or null.</summary>
    internal static string? ExpectedWholeSurface(string baseName, string comparisonName)
    {
        static string Cy(double ft3) => (ft3 / CubicFeetPerCubicYard).ToString("0.###", Inv);
        return (baseName, comparisonName) switch
        {
            (Names.EG, Names.FgA) => $"cut {ACutFt3.ToString("0.###", Inv)} ({Cy(ACutFt3)} CY), fill {AFillFt3.ToString("0.###", Inv)} ({Cy(AFillFt3)} CY)",
            (Names.EG, Names.FgB) => $"cut 0, fill {BFillFanFt3.ToString("0.###", Inv)} ({Cy(BFillFanFt3)} CY) with the ridge ends fanned to the near EG corners; prism alone {BFillPrismFt3.ToString("0.###", Inv)}",
            (Names.EG, Names.FgD) => $"cut 0, fill {DFillFt3.ToString("0.###", Inv)} ({Cy(DFillFt3)} CY)",
            (Names.EgH, Names.FgH) => $"cut {HCutMinFt3.ToString("0.###", Inv)}..{HCutMaxFt3.ToString("0.###", Inv)} ({Cy(HCutMinFt3)}..{Cy(HCutMaxFt3)} CY), fill 0",
            _ => null,
        };
    }

    /// <summary>Fixture base/comparison pairs, in build order.</summary>
    internal static readonly (string Base, string Comparison)[] Pairs =
    {
        (Names.EG, Names.FgA), (Names.EG, Names.FgB), (Names.EG, Names.FgD), (Names.EgH, Names.FgH),
    };

    internal static string VolumeName(string baseName, string comparisonName) => $"{Names.VolumePrefix}{baseName}-{comparisonName}";

    // ------------------------------------------------------------ helpers

    internal static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>Reuse an existing EG only when it is the fixture EG: a TIN with the 4 corner
    /// points, flat at 100, on the section 8 extents. Anything else named EG is a collision.</summary>
    private static (ObjectId Id, bool Reused) EnsureEg(Database db, Transaction tr, CivilDocument civ, FixtureChecks checks)
    {
        var existing = FindSurface(tr, civ, Names.EG);
        if (!existing.IsNull)
        {
            var s = tr.GetObject(existing, OpenMode.ForRead) as TinSurface
                ?? throw new FixtureException(FixtureCodes.NameInUse, $"a surface named '{Names.EG}' exists and is not a TIN surface");
            var g = s.GetGeneralProperties();
            bool same = g.NumberOfPoints == EgPoints
                && Math.Abs(g.MinimumElevation - EgZ) <= GeometryTolerance && Math.Abs(g.MaximumElevation - EgZ) <= GeometryTolerance
                && Math.Abs(g.MinimumCoordinateX - EgCorners[0].X) <= GeometryTolerance && Math.Abs(g.MaximumCoordinateX - EgCorners[2].X) <= GeometryTolerance
                && Math.Abs(g.MinimumCoordinateY - EgCorners[0].Y) <= GeometryTolerance && Math.Abs(g.MaximumCoordinateY - EgCorners[2].Y) <= GeometryTolerance;
            if (!same)
                throw new FixtureException(FixtureCodes.NameInUse,
                    $"a surface named '{Names.EG}' exists and is not the fixture EG ({g.NumberOfPoints} points, z {g.MinimumElevation}..{g.MaximumElevation})");
            checks.Note($"{Names.EG}: reused the fixture EG already in the drawing");
            return (existing, true);
        }

        var eg = NewTin(db, tr, civ, Names.EG, checks);
        AddPerimeter(eg, EgZ);
        eg.Rebuild();
        CheckCounts(eg, Names.EG, EgPoints, EgTriangles, checks);
        CheckZ(eg, Names.EG, 0.0, 0.0, EgZ, checks);
        CheckZ(eg, Names.EG, 2100.0, 250.0, EgZ, checks);
        return (eg.ObjectId, false);
    }

    /// <summary>Creates an empty TIN and switches off every build option that could drop
    /// triangles or points (maximum triangle length, maximum angle, elevation exclusions), so
    /// the fixture does not depend on the drawing's surface creation defaults. Any default
    /// that was on is noted in the checks.</summary>
    private static TinSurface NewTin(Database db, Transaction tr, CivilDocument civ, string name, FixtureChecks checks)
    {
        RequireFree(tr, civ, name);
        ObjectId id;
        try { id = TinSurface.Create(db, name); }
        catch (System.Exception ex) { throw new FixtureException(FixtureCodes.BuildFailed, $"TinSurface.Create('{name}') threw {ex.GetType().Name}: {ex.Message}", ex); }
        var s = (TinSurface)tr.GetObject(id, OpenMode.ForWrite);

        var o = s.BuildOptions;
        var on = new List<string>();
        if (o.UseMaximumTriangleLength) on.Add($"maximum triangle length {F(o.MaximumTriangleLength)}");
        if (o.UseMaximumAngle) on.Add($"maximum angle {F(o.MaximumAngleBetweenAdjacentTinLines)}");
        if (o.ExecludeMaximumElevation) on.Add($"exclude above {F(o.MaximumElevation)}");
        if (o.ExecludeMinimumElevation) on.Add($"exclude below {F(o.MinimumElevation)}");
        if (on.Count > 0) checks.Note($"{name}: drawing default build options switched off: {string.Join(", ", on)}");
        o.UseMaximumTriangleLength = false;
        o.UseMaximumAngle = false;
        o.ExecludeMaximumElevation = false;
        o.ExecludeMinimumElevation = false;
        return s;
    }

    /// <summary>The EG rectangle as four 2-point breaklines at elevation <paramref name="z"/>.
    /// Corners go in through the definition, not as AddVertices edits. MEASURED 2026-11-05 in the
    /// 2026.2.3 Core Console: with the corners as an AddVertices edit FG_A came out non-convex
    /// (42 triangles instead of 52, FindElevationAtXY "Point Outside Surface" near the long
    /// edges); with this perimeter it is 52 and every probe matches. Consistent with edits being
    /// applied after the breakline definition (ASSUMED mechanism; not probed further).</summary>
    private static void AddPerimeter(TinSurface s, double z)
    {
        for (int i = 0; i < EgCorners.Length; i++)
        {
            var a = EgCorners[i];
            var b = EgCorners[(i + 1) % EgCorners.Length];
            AddSegment(s, new Point3d(a.X, a.Y, z), new Point3d(b.X, b.Y, z));
        }
    }

    internal static ObjectId FindSurface(Transaction tr, CivilDocument civ, string name)
    {
        foreach (ObjectId id in civ.GetSurfaceIds())
        {
            if (tr.GetObject(id, OpenMode.ForRead) is CivSurface s && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                return id;
        }
        return ObjectId.Null;
    }

    private static void RequireFree(Transaction tr, CivilDocument civ, string name)
    {
        if (!FindSurface(tr, civ, name).IsNull)
            throw new FixtureException(FixtureCodes.NameInUse, $"a surface named '{name}' already exists; build fixtures into a drawing without it");
    }

    private static void AddSegment(TinSurface s, Point3d a, Point3d b)
    {
        s.BreaklinesDefinition.AddStandardBreaklines(new Point3dCollection { a, b },
            Breaklines.MidOrdinate, Breaklines.MaximumDistance, Breaklines.WeedingDistance, Breaklines.WeedingAngle);
    }

    private static void CheckCounts(TinSurface s, string name, int points, int triangles, FixtureChecks checks)
    {
        int gotPoints = s.GetGeneralProperties().NumberOfPoints;
        int gotTriangles = s.GetTinProperties().NumberOfTriangles;
        int hidden = 0;
        foreach (TinSurfaceTriangle t in s.GetTriangles(true)) if (!t.IsVisible) hidden++;
        string line = $"{name}: {gotPoints} points, {gotTriangles} triangles, {hidden} hidden (expected {points}, {triangles}, 0)";
        if (gotPoints == points && gotTriangles == triangles && hidden == 0) { checks.Pass(line); return; }
        checks.Fail(line + "; " + DescribeTriangles(s, 80));
    }

    /// <summary>The surface's triangles as "(x,y,z)(x,y,z)(x,y,z)", invisible ones marked, for diagnosis.</summary>
    internal static string DescribeTriangles(TinSurface s, int max)
    {
        var parts = new List<string>();
        int n = 0;
        foreach (TinSurfaceTriangle t in s.GetTriangles(true))
        {
            if (n++ >= max) { parts.Add("..."); break; }
            static string V(TinSurfaceVertex v) => $"({F(v.Location.X)},{F(v.Location.Y)},{F(v.Location.Z)})";
            parts.Add((t.IsVisible ? "" : "hidden ") + V(t.Vertex1) + V(t.Vertex2) + V(t.Vertex3));
        }
        return $"triangles incl. hidden: {string.Join(" ", parts)}";
    }

    private static void CheckZ(CivSurface s, string name, double x, double y, double expected, FixtureChecks checks)
    {
        double z;
        try { z = s.FindElevationAtXY(x, y); }
        catch (System.Exception ex)
        {
            checks.Fail($"{name}: FindElevationAtXY({F(x)}, {F(y)}) threw {ex.GetType().Name}: {ex.Message}");
            return;
        }
        if (Math.Abs(z - expected) > GeometryTolerance)
            checks.Fail($"{name}: z at ({F(x)}, {F(y)}) is {z.ToString("R", Inv)}, expected {F(expected)}");
        else
            checks.Pass($"{name}: z({F(x)}, {F(y)}) = {F(z)} ok");
    }

    internal static string F(double v) => v.ToString("0.######", Inv);
}

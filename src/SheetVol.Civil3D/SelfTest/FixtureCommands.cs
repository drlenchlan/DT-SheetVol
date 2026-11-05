// FX work package. Owner: FX.
//
// SVFIXTURE <name>   builds one fixture into the current drawing so a script can
//                    drive it: A, B, D, H, S, C, K, SP, TEMPLATE, ALL (all of those),
//                    VOLUMES (an FX-VOL volume surface per fixture pair present, with
//                    raw totals and the analytic expectation), TEMPLATEDWG <path>
//                    (writes a one-layout template drawing and rereads it).
// SVFIXTUREREPORT    read-only listing of every surface (TIN counts, volume totals),
//                    alignment (stations, entities, geometry points, a PointLocation
//                    offset sample for P7) and paper layout (viewports, table anchors).
//
// Output lines start with "SVFX" so a log can be grepped. Failures print one line
// with the SV-E19x code and the transaction is aborted (HANDOFF.md 6.13).

using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using static SheetVol.Civil3D.SelfTest.FixtureSurfaces;

[assembly: CommandClass(typeof(SheetVol.Civil3D.SelfTest.FixtureCommands))]

namespace SheetVol.Civil3D.SelfTest;

public static class FixtureCommands
{
    [CommandMethod("SVFIXTURE", CommandFlags.Modal)]
    public static void BuildFixture()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc is null) return;
        var ed = doc.Editor;
        void Say(string s) => ed.WriteMessage("\n" + s);

        var pr = ed.GetString(new PromptStringOptions("\nFixture [A/B/D/H/S/C/K/SP/TEMPLATE/ALL/VOLUMES/TEMPLATEDWG]: ") { AllowSpaces = false });
        if (pr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(pr.StringResult))
        {
            Say($"{FixtureCodes.BadInput} SVFIXTURE: no fixture name given.");
            return;
        }
        var name = pr.StringResult.Trim().ToUpperInvariant();

        string? path = null;
        if (name == "TEMPLATEDWG")
        {
            var pp = ed.GetString(new PromptStringOptions("\nTemplate drawing path: ") { AllowSpaces = true });
            if (pp.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(pp.StringResult))
            {
                Say($"{FixtureCodes.BadInput} SVFIXTURE TEMPLATEDWG: no path given.");
                return;
            }
            path = pp.StringResult.Trim().Trim('"');
        }

        var targets = name == "ALL" ? Fixtures.BuildNames : new[] { name };
        foreach (var target in targets)
        {
            try
            {
                foreach (var line in Build(doc.Database, target, path)) Say("SVFX " + line);
            }
            catch (FixtureException fx)
            {
                Say($"{fx.Code} SVFIXTURE {target}: {fx.Message}");
                if (name == "ALL") Say($"SVFX ALL stopped at {target}; fixtures built before it are kept.");
                return;
            }
            catch (System.Exception ex)
            {
                Say($"{FixtureCodes.BuildFailed} SVFIXTURE {target}: {ex.GetType().FullName}: {ex.Message}");
                return;
            }
        }
    }

    [CommandMethod("SVFIXTUREREPORT", CommandFlags.Modal)]
    public static void Report()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc is null) return;
        var ed = doc.Editor;
        void Say(string s) => ed.WriteMessage("\nSVFX " + s);
        var db = doc.Database;

        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var civ = CivilDocument.GetCivilDocument(db);
            Say($"drawing: INSUNITS={db.Insunits}  Civil drawing units={civ.Settings.DrawingSettings.UnitZoneSettings.DrawingUnits}");

            var surfaceIds = civ.GetSurfaceIds();
            Say($"surfaces: {surfaceIds.Count}");
            foreach (ObjectId id in surfaceIds)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not CivSurface s) continue;
                var g = s.GetGeneralProperties();
                switch (s)
                {
                    case TinVolumeSurface v:
                    {
                        var p = v.GetVolumeProperties();
                        string baseName = SurfaceName(tr, p.BaseSurface), compName = SurfaceName(tr, p.ComparisonSurface);
                        Say($"  {v.Name}: TinVolumeSurface base={baseName} comparison={compName} triangles={v.GetTinProperties().NumberOfTriangles}");
                        Say($"    UnadjustedCutVolume={R(p.UnadjustedCutVolume)} UnadjustedFillVolume={R(p.UnadjustedFillVolume)} UnadjustedNetVolume={R(p.UnadjustedNetVolume)}");
                        Say($"    AdjustedCut={R(p.AdjustedCutVolume)} AdjustedFill={R(p.AdjustedFillVolume)} CutFactor={R(p.CutFactor)} FillFactor={R(p.FillFactor)}");
                        var expected = Fixtures.ExpectedWholeSurface(baseName, compName);
                        if (expected is not null) Say($"    expected (analytic, ft3): {expected}");
                        break;
                    }
                    case TinSurface t:
                        Say($"  {t.Name}: TinSurface points={g.NumberOfPoints} triangles={t.GetTinProperties().NumberOfTriangles} z={F(g.MinimumElevation)}..{F(g.MaximumElevation)} x={F(g.MinimumCoordinateX)}..{F(g.MaximumCoordinateX)} y={F(g.MinimumCoordinateY)}..{F(g.MaximumCoordinateY)}");
                        break;
                    default:
                        Say($"  {s.Name}: {s.GetType().Name} points={g.NumberOfPoints}");
                        break;
                }
            }

            var alignmentIds = civ.GetAlignmentIds();
            Say($"alignments: {alignmentIds.Count}");
            foreach (ObjectId id in alignmentIds)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Alignment al) continue;
                Say($"  {al.Name}: stations {F(al.StartingStation)}..{F(al.EndingStation)} length={F(al.Length)} entities={al.Entities.Count}");
                for (int i = 0; i < al.Entities.Count; i++)
                {
                    var e = al.Entities.GetEntityByOrder(i);
                    Say($"    entity {i}: {e.EntityType} id={e.EntityId} subentities={e.SubEntityCount}");
                    for (int j = 0; j < e.SubEntityCount; j++)
                    {
                        var sub = e[j];
                        string extra = sub switch
                        {
                            AlignmentSubEntityArc a => $" R={F(a.Radius)} Clockwise={a.Clockwise} center=({F(a.CenterPoint.X)}, {F(a.CenterPoint.Y)}) delta={F(a.Delta)}",
                            AlignmentSubEntitySpiral sp => $" Rin={F(sp.RadiusIn)} Rout={F(sp.RadiusOut)} A={F(sp.A)} direction={sp.Direction} {sp.SpiralDefinition}",
                            _ => "",
                        };
                        Say($"      {sub.SubEntityType} {F(sub.StartStation)}..{F(sub.EndStation)} ({F(sub.StartPoint.X)}, {F(sub.StartPoint.Y)})->({F(sub.EndPoint.X)}, {F(sub.EndPoint.Y)}){extra}");
                    }
                }
                var geo = al.GetStationSet(StationTypes.GeometryPoint);
                Say($"    geometry points: {string.Join(", ", geo.Select(st => $"{F(st.RawStation)} {st.GeometryStationType}"))}");
                // P7 data, not a check: where does a negative offset land? Contracts say negative = left.
                double mid = (al.StartingStation + al.EndingStation) / 2, e1 = 0, n1 = 0, e2 = 0, n2 = 0;
                al.PointLocation(mid, 0.0, ref e1, ref n1);
                al.PointLocation(mid, -10.0, ref e2, ref n2);
                Say($"    PointLocation({F(mid)}, 0) = ({F(e1)}, {F(n1)}); PointLocation({F(mid)}, -10) = ({F(e2)}, {F(n2)})");
            }

            var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            Say($"layouts: {layouts.Count} (incl. Model)");
            foreach (DBDictionaryEntry entry in layouts)
            {
                var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                if (layout.ModelType) continue;
                var paper = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                int viewports = 0;
                var lines = new List<string>();
                foreach (ObjectId id in paper)
                {
                    var obj = tr.GetObject(id, OpenMode.ForRead);
                    if (obj is Viewport v)
                    {
                        viewports++;
                        lines.Add($"    viewport {viewports} {v.Handle} layer {v.Layer}: {F(v.Width)} x {F(v.Height)} at ({F(v.CenterPoint.X)}, {F(v.CenterPoint.Y)}) CustomScale={R(v.CustomScale)} target=({F(v.ViewTarget.X)}, {F(v.ViewTarget.Y)}) twist={F(v.TwistAngle)} on={v.On}");
                    }
                    else if (obj is BlockReference br)
                    {
                        var def = (BlockTableRecord)tr.GetObject(br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord, OpenMode.ForRead);
                        if (string.Equals(def.Name, FixtureSpec.Names.TableAnchorBlock, StringComparison.OrdinalIgnoreCase))
                            lines.Add($"    {def.Name} at ({F(br.Position.X)}, {F(br.Position.Y)})");
                    }
                }
                Say($"  {layout.LayoutName}: {viewports} viewport entit{(viewports == 1 ? "y" : "ies")} in paper space");
                foreach (var l in lines) Say(l);
            }
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\n{FixtureCodes.BuildFailed} SVFIXTUREREPORT: {ex.GetType().FullName}: {ex.Message}");
        }
    }

    private static IEnumerable<string> Build(Database db, string name, string? path)
    {
        switch (name)
        {
            case "A": return Surface(Fixtures.CreateA(db));
            case "B": return Surface(Fixtures.CreateB(db));
            case "D": return Surface(Fixtures.CreateD(db));
            case "H": return Surface(Fixtures.CreateH(db));
            case "S": return Align(Fixtures.CreateAlignmentS(db));
            case "C": return Align(Fixtures.CreateAlignmentC(db));
            case "K": return Align(Fixtures.CreateAlignmentK(db));
            case "SP": return Align(Fixtures.CreateAlignmentSP(db));
            case "TEMPLATE":
            {
                var t = Fixtures.CreateTemplate(db);
                var lines = new List<string> { $"TEMPLATE: layout {t.LayoutName}, plan viewport {t.ViewportId.Handle} {F(t.ViewportWidth)} x {F(t.ViewportHeight)} on {FixtureSpec.Names.ViewportLayer}, {FixtureSpec.Names.TableAnchorBlock} reference {t.AnchorReferenceId.Handle}" };
                lines.AddRange(t.Notes.Select(n => "  " + n));
                return lines;
            }
            case "TEMPLATEDWG":
                return Fixtures.WriteTemplateDrawing(path!).Select(n => "TEMPLATEDWG: " + n).ToList();
            case "VOLUMES":
            {
                var made = Fixtures.CreateFixtureVolumes(db);
                var lines = new List<string> { $"VOLUMES: {made.Count} volume surface(s) created" };
                foreach (var v in made)
                {
                    lines.Add($"{v.Name}: base={v.BaseName} comparison={v.ComparisonName} triangles={v.Triangles}");
                    lines.Add($"  UnadjustedCutVolume={R(v.UnadjustedCut)} UnadjustedFillVolume={R(v.UnadjustedFill)} UnadjustedNetVolume={R(v.UnadjustedNet)}");
                    var expected = Fixtures.ExpectedWholeSurface(v.BaseName, v.ComparisonName);
                    if (expected is not null) lines.Add($"  expected (analytic, ft3): {expected}");
                }
                return lines;
            }
            default:
                throw new FixtureException(FixtureCodes.BadInput, $"unknown fixture '{name}'. Use A, B, D, H, S, C, K, SP, TEMPLATE, ALL, VOLUMES or TEMPLATEDWG.");
        }
    }

    private static IEnumerable<string> Surface(SurfaceFixture f)
    {
        var lines = new List<string> { $"{f.Fixture}: base {f.BaseName} ({(f.BaseReused ? "reused" : "created")}), comparison {f.ComparisonName}" };
        lines.AddRange(f.Checks.Where(c => !c.EndsWith(" ok", StringComparison.Ordinal)).Select(c => "  " + c));
        int probes = f.Checks.Count(c => c.EndsWith(" ok", StringComparison.Ordinal));
        lines.Add($"  {probes} elevation probes matched the analytic surface within {FixtureSpec.GeometryTolerance:E0}");
        return lines;
    }

    private static IEnumerable<string> Align(AlignmentFixture f)
    {
        var lines = new List<string> { $"{f.Name}: alignment stations {F(f.StartStation)}..{F(f.EndStation)}" };
        lines.AddRange(f.Checks.Select(c => "  " + c));
        return lines;
    }

    private static string SurfaceName(Transaction tr, ObjectId id) =>
        id.IsNull ? "(none)" : (tr.GetObject(id, OpenMode.ForRead) as CivSurface)?.Name ?? "(not a surface)";

    private static string R(double v) => v.ToString("R", Inv);
}

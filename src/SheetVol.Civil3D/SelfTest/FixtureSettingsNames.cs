// FX work package (FX-T, 2026-11-06). Owner: FX.
//
// SVFIXTURE T: creates in the drawing every name the effective settings expect the LAN
// template to supply, so a regen on a fixture drawing can complete. MEASURED on Dan's seat
// 2026-11-06: SVDIALOG Regenerate on a bare SVFIXTURE A + S drawing failed SV-E501 on the
// boundary, match-line and table layers and the table style, and warned SV-E443 / SV-E444
// on the owned-surface style and layer.
//
// Every name is read from SheetVolSettings (the settings file, or its built-in defaults);
// no name is written in this file. The walk below covers every settings property that
// names a layer, surface style, table style, text style or block in the drawing:
//   adapters.ownedSurfaceStyle / ownedSurfaceLayer (A1, AnalysisResolver: E443 / E444)
//   modelWriters.boundaryLayer / impactOutlineLayer / matchMarks.layer / matchMarks.textStyle
//     (A2, DrawingNames: E501)
//   report.segmentTable / masterTable / hcfcd .style.tableStyle and .style.layer (C2 names;
//     A2 TableWriters and A3 PaperTableWriter resolve them in TableStyleDictionaryId, so
//     they are AutoCAD TableStyles, not Civil 3D table styles: E501 / E6xx)
//   report.placement.tableAnchorBlock (the anchor block definition)
//   paper.planViewportLayer, paper.clipLayer (A3; clipLayer only when set: E601)
// Not walked: layout names (paper.templateLayout is in the template file;
// modelOutput.masterTableLayout is a layout, not a style), dictionary and Xrecord keys.
//
// Existing objects are never opened for write. A null setting (the lookup is switched
// off) is listed as "not set". One transaction; any failure aborts all of it.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;
using SheetVol.Civil3D.Session;

namespace SheetVol.Civil3D.SelfTest;

internal static class FixtureSettingsNames
{
    internal const string Created = "created", Present = "present", NotSet = "not set";

    internal const string Layer = "layer", SurfaceStyle = "surface style", TableStyle = "table style",
        TextStyle = "text style", Block = "block";

    /// <summary>Every drawing-resident name in <paramref name="s"/>: kind, settings key (from the
    /// settings file root, camelCase as the file spells it), and the name (null when the setting
    /// is null, which switches its lookup off).</summary>
    internal static IReadOnlyList<(string Kind, string Key, string? Name)> Expected(SheetVolSettings s) =>
    [
        (SurfaceStyle, "adapters.ownedSurfaceStyle", s.Adapters.OwnedSurfaceStyle),
        (Layer, "adapters.ownedSurfaceLayer", s.Adapters.OwnedSurfaceLayer),
        (Layer, "modelWriters.boundaryLayer", s.ModelWriters.BoundaryLayer),
        (Layer, "modelWriters.impactOutlineLayer", s.ModelWriters.ImpactOutlineLayer),
        (Layer, "modelWriters.matchMarks.layer", s.ModelWriters.MatchMarks.Layer),
        (TextStyle, "modelWriters.matchMarks.textStyle", s.ModelWriters.MatchMarks.TextStyle),
        (TableStyle, "report.segmentTable.style.tableStyle", s.Report.SegmentTable.Style.TableStyle),
        (Layer, "report.segmentTable.style.layer", s.Report.SegmentTable.Style.Layer),
        (TableStyle, "report.masterTable.style.tableStyle", s.Report.MasterTable.Style.TableStyle),
        (Layer, "report.masterTable.style.layer", s.Report.MasterTable.Style.Layer),
        (TableStyle, "report.hcfcd.style.tableStyle", s.Report.Hcfcd.Style.TableStyle),
        (Layer, "report.hcfcd.style.layer", s.Report.Hcfcd.Style.Layer),
        (Block, "report.placement.tableAnchorBlock", s.Report.Placement.TableAnchorBlock),
        (Layer, "paper.planViewportLayer", s.Paper.PlanViewportLayer),
        (Layer, "paper.clipLayer", s.Paper.ClipLayer),
    ];

    internal static SettingsNamesFixture Build(Database db, Transaction tr, CivilDocument civ, SheetVolSettings settings, string source)
    {
        // One result per (kind, name); AutoCAD and Civil 3D names compare case-insensitively.
        // A name several keys share (the three table layers, by default) is one line with every key.
        var groups = new List<(string Kind, string? Name, List<string> Keys)>();
        foreach (var (kind, key, name) in Expected(settings))
        {
            var g = groups.FindIndex(x => x.Kind == kind && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (g >= 0) groups[g].Keys.Add(key);
            else groups.Add((kind, name, new List<string> { key }));
        }

        var results = new List<SettingsNameResult>();
        foreach (var (kind, name, keys) in groups)
        {
            if (name is null) { results.Add(new SettingsNameResult(kind, "", NotSet, keys)); continue; }
            if (string.IsNullOrWhiteSpace(name))
                throw new FixtureException(FixtureCodes.BadInput, $"{string.Join(", ", keys)} is blank in settings {source}");
            bool exists = Exists(db, tr, civ, kind, name);
            if (!exists) Create(db, tr, civ, kind, name);
            results.Add(new SettingsNameResult(kind, name, exists ? Present : Created, keys));
        }

        // The write is done when the drawing says so: every expected name now resolves the way
        // the engine resolves it, and every created surface style shows nothing.
        var checks = new FixtureChecks();
        foreach (var r in results.Where(r => r.Status != NotSet))
        {
            if (Exists(db, tr, civ, r.Kind, r.Name)) checks.Pass($"{r.Kind} '{r.Name}' resolves");
            else checks.Fail($"{r.Kind} '{r.Name}' ({string.Join(", ", r.Keys)}) does not resolve after the build");
            if (r.Kind == SurfaceStyle && r.Status == Created)
                CheckNoDisplay(tr, civ, r.Name, checks);
        }
        checks.ThrowIfFailed("SVFIXTURE T");
        return new SettingsNamesFixture(source, results);
    }

    // ------------------------------------------------------------ lookups (as the engine does them)

    private static bool Exists(Database db, Transaction tr, CivilDocument civ, string kind, string name) => kind switch
    {
        Layer => ((LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead)).Has(name),
        TextStyle => ((TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead)).Has(name),
        Block => ((BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead)).Has(name),
        TableStyle => ((DBDictionary)tr.GetObject(db.TableStyleDictionaryId, OpenMode.ForRead)).Contains(name),
        SurfaceStyle => civ.Styles.SurfaceStyles.Contains(name),
        _ => throw new FixtureException(FixtureCodes.BuildFailed, $"unknown name kind '{kind}'"),
    };

    // ------------------------------------------------------------ creators

    private static void Create(Database db, Transaction tr, CivilDocument civ, string kind, string name)
    {
        switch (kind)
        {
            case Layer:
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
                var rec = new LayerTableRecord { Name = name };
                lt.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
                break;
            }
            case TextStyle:
            {
                // A copy of the drawing's current text style's font, so no font name is written here.
                var current = (TextStyleTableRecord)tr.GetObject(db.Textstyle, OpenMode.ForRead);
                var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForWrite);
                var rec = new TextStyleTableRecord
                {
                    Name = name,
                    FileName = current.FileName,
                    BigFontFileName = current.BigFontFileName,
                    TextSize = 0.0,
                    XScale = current.XScale,
                };
                if (!string.IsNullOrEmpty(current.Font.TypeFace)) rec.Font = current.Font;
                tst.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
                break;
            }
            case Block:
                FixtureTemplate.EnsureAnchorBlock(db, tr, name);
                break;
            case TableStyle:
            {
                // As A2's WritersModelCheck builds its stand-in style (proven headless 2026-11-05):
                // the dictionary key names the style; Name throws eOwnerNotSet before it is owned.
                var dict = (DBDictionary)tr.GetObject(db.TableStyleDictionaryId, OpenMode.ForWrite);
                var ts = new Autodesk.AutoCAD.DatabaseServices.TableStyle();
                dict.SetAt(name, ts);
                tr.AddNewlyCreatedDBObject(ts, true);
                break;
            }
            case SurfaceStyle:
            {
                // 6.5: the owned volume surface's style displays nothing. Every display component
                // off in plan and in model.
                var id = civ.Styles.SurfaceStyles.Add(name);
                var style = (Autodesk.Civil.DatabaseServices.Styles.SurfaceStyle)tr.GetObject(id, OpenMode.ForWrite);
                foreach (var component in Enum.GetValues<SurfaceDisplayStyleType>())
                {
                    style.GetDisplayStylePlan(component).Visible = false;
                    style.GetDisplayStyleModel(component).Visible = false;
                }
                break;
            }
            default:
                throw new FixtureException(FixtureCodes.BuildFailed, $"unknown name kind '{kind}'");
        }
    }

    private static void CheckNoDisplay(Transaction tr, CivilDocument civ, string name, FixtureChecks checks)
    {
        var style = (Autodesk.Civil.DatabaseServices.Styles.SurfaceStyle)tr.GetObject(civ.Styles.SurfaceStyles[name], OpenMode.ForRead);
        var visible = new List<string>();
        int components = 0;
        foreach (var component in Enum.GetValues<SurfaceDisplayStyleType>())
        {
            components++;
            if (style.GetDisplayStylePlan(component).Visible) visible.Add($"plan {component}");
            if (style.GetDisplayStyleModel(component).Visible) visible.Add($"model {component}");
        }
        if (visible.Count == 0) checks.Pass($"surface style '{name}': {components} components off in plan and model");
        else checks.Fail($"surface style '{name}' still shows {string.Join(", ", visible)}");
    }
}

// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4 (SVSELFTEST driver).
//
// SVSELFTEST [Fakes|Installed|BindFakes|BindInstalled] (HANDOFF.md 6.12, section 8): builds
// FX's fixtures in the drawing, runs the acceptance checks against the chosen bindings, and
// writes selftest-result.json beside the drawing. Fakes runs them on the in-memory fakes;
// Installed runs them on EngineBindings.Current (the real A1-A3 factories installed at load,
// Integration/EngineStartup) with the world edits in SelfTestWorlds.Installed. Every failed
// check prints SV-E199; a check that cannot run headless is listed with its reason and counts
// as "not run", never as a pass.

using System.Diagnostics;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using SheetVol.Civil3D.Commands;
using SheetVol.Civil3D.Session;

[assembly: CommandClass(typeof(SheetVol.Civil3D.SelfTest.SelfTestCommand))]

namespace SheetVol.Civil3D.SelfTest;

/// <summary>The SVSELFTEST command.</summary>
public static class SelfTestCommand
{
    /// <summary>Result file schema version (2: checks carry a status, PASS, FAIL or NOT RUN).</summary>
    public const int ResultSchemaVersion = 2;

    private static EngineBindings? _installedBeforeFakes;

    /// <summary>Builds fixtures, runs acceptance checks, writes selftest-result.json (6.12).</summary>
    [CommandMethod("SVSELFTEST", CommandFlags.Modal)]
    public static void SelfTest() => Boundary.Run("SVSELFTEST", c =>
    {
        string mode = c.Keyword("SVSELFTEST bindings", ["Fakes", "Installed", "BindFakes", "BindInstalled"], "Fakes");
        if (mode == "BindFakes")
        {
            // Test only (tools/scripts/selftest-commands.scr): the SV commands run on the fixture
            // world and write only the self-test dictionaries, never the product's. The installed
            // bindings are kept and come back with BindInstalled (or the next session).
            if (EngineBindings.Current.Adapters is not FakeAdapterFactory)
                _installedBeforeFakes = EngineBindings.Current;
            EngineBindings.Current = new EngineBindings(new FakeAdapterFactory(FakeWorld.Fixtures()),
                new FakeWriterFactory(new TemplateInfo(FixtureSpec.Template.ViewportWidth, FixtureSpec.Template.ViewportHeight, true)));
            c.Say("SVSELFTEST: FAKE bindings installed for this session (test only): SV commands read the in-memory fixture world and write only the self-test dictionaries."
                  + (_installedBeforeFakes is { IsWired: true } ? " SVSELFTEST BindInstalled restores the installed A1-A3 bindings." : ""));
            return;
        }
        if (mode == "BindInstalled")
        {
            if (_installedBeforeFakes is null) { c.Say("SVSELFTEST BindInstalled: the installed bindings are already in place."); return; }
            EngineBindings.Current = _installedBeforeFakes;
            _installedBeforeFakes = null;
            c.Say($"SVSELFTEST: installed bindings restored ({(EngineBindings.Current.IsWired ? "A1-A3 wired" : "unwired")}).");
            return;
        }

        bool fakes = mode == "Fakes";
        var sw = Stopwatch.StartNew();
        var checks = new List<SelfTestCheck>();
        void Add(SelfTestCheck k)
        {
            checks.Add(k);
            c.Say($"{(k.Status == SelfTestCheck.Failed ? PipelineCodes.SelfTestFailed + " SVSELFTEST" : "SVSELFTEST")} {k.Id} {k.Status}: {k.Message} | measured {k.Measured} | expected {k.Expected}");
        }

        var baseSettings = c.Engine.Settings;
        string dir = RegenEngine.DrawingPath(c.Doc) is { } dwg ? Path.GetDirectoryName(dwg)! : Path.GetTempPath();
        string templatePath = Path.Combine(dir, baseSettings.SelfTest.TemplateDrawingName);
        var settings = TestSettings(baseSettings, templatePath);

        // 1. FX fixtures, in-process, so the installed adapters find the section 8 world in the
        //    drawing, plus the layers and table styles a LAN template would carry (D14: the writers
        //    never create them; a missing one fails the run with SV-E501).
        var built = new List<string>();
        try
        {
            var db = c.Doc.Database;
            var a = Fixtures.CreateA(db); built.Add($"{a.BaseName}/{a.ComparisonName}");
            var h = Fixtures.CreateH(db); built.Add($"{h.BaseName}/{h.ComparisonName}");
            var s = Fixtures.CreateAlignmentS(db); built.Add($"alignment {s.Name} {s.StartStation:0.###}..{s.EndStation:0.###}");
            var t = Fixtures.CreateTemplate(db); built.Add($"layout {t.LayoutName} {t.ViewportWidth:0.###} x {t.ViewportHeight:0.###}");
            if (!fakes)
            {
                var b = Fixtures.CreateB(db); built.Add($"{b.BaseName}/{b.ComparisonName}");
                var d = Fixtures.CreateD(db); built.Add($"{d.BaseName}/{d.ComparisonName}");
                foreach (var al in new[] { Fixtures.CreateAlignmentC(db), Fixtures.CreateAlignmentK(db), Fixtures.CreateAlignmentSP(db) })
                    built.Add($"alignment {al.Name} {al.StartStation:0.###}..{al.EndStation:0.###}");
                if (File.Exists(templatePath)) File.Delete(templatePath);
                Fixtures.WriteTemplateDrawing(templatePath);
                built.Add($"template drawing {templatePath}");
                built.Add("template stand-ins: " + string.Join(", ", TemplateStandIns(db, settings)));
            }
            Add(SelfTestCheck.Of("FX", true, string.Join("; ", built), "fixtures built", "FX fixtures built in this drawing"));
        }
        catch (System.Exception ex)
        {
            Add(SelfTestCheck.Of("FX", false, $"{ex.GetType().Name}: {ex.Message} (built: {string.Join("; ", built)})", "fixtures built",
                fakes ? "fixture build failed; the fake checks below do not depend on it" : "fixture build failed; the installed checks need it"));
        }

        // 2. Bindings.
        IAdapterFactory adapters;
        IWriterFactory writers;
        ISelfTestWorld? world;
        if (fakes)
        {
            var w = FakeWorld.Fixtures();
            world = w;
            adapters = new FakeAdapterFactory(w);
            writers = new FakeWriterFactory(new TemplateInfo(FixtureSpec.Template.ViewportWidth, FixtureSpec.Template.ViewportHeight, true));
        }
        else
        {
            var current = _installedBeforeFakes ?? EngineBindings.Current;
            if (!current.IsWired || current.Adapters is FakeAdapterFactory)
            {
                Add(SelfTestCheck.Of("BINDINGS", false, current.IsWired ? "fakes" : "unwired", "installed A1-A3 factories", "SVSELFTEST Installed needs the real bindings EngineStartup installs at load"));
                Write(c, dir, fakes, sw, checks, settings);
                return;
            }
            adapters = current.Adapters;
            writers = current.Writers;
            world = SelfTestWorlds.Installed;
        }

        // 3. The checks.
        var runner = new SelfTestRunner(c.Doc, _ => { }, settings, adapters, writers, world, fakes, dir);
        runner.Run();
        foreach (var k in runner.Checks) Add(k);

        // 4. The result file.
        Write(c, dir, fakes, sw, checks, settings);
    });

    /// <summary>
    /// Settings for the test: layouts on from FX's template drawing, fixture tolerances on both
    /// cross-checks (6.15: 1e-9 relative on fixtures; 6.5 likewise, A1 measured 4.4e-15), the
    /// HCFCD template from the repo when the stage has none.
    /// </summary>
    private static SheetVolSettings TestSettings(SheetVolSettings s, string templatePath)
    {
        string hcfcd = s.Report.Hcfcd.TemplatePath;
        string pluginTemplate = Core.Output.HcfcdTemplate.ResolvePath(hcfcd, SettingsProvider.PluginDirectory);
        if (!File.Exists(pluginTemplate) && Environment.GetEnvironmentVariable(s.SelfTest.RepoEnvironmentVariable) is { Length: > 0 } repo)
            hcfcd = Path.GetFullPath(Path.Combine(repo, s.SelfTest.RepoHcfcdTemplate));
        var st = s.SelfTest;
        return s with
        {
            Regen = s.Regen with { GenerateLayouts = true },
            Adapters = s.Adapters with { RebuildSources = true },
            Paper = s.Paper with
            {
                TemplatePath = templatePath, TemplateLayout = FixtureSpec.Names.TemplateLayout,
                PlanViewportLayer = FixtureSpec.Names.ViewportLayer, RenumberLayouts = true,
            },
            Integrator = s.Integrator with
            {
                CrossCheckRelative = st.FixtureCrossCheckRelative, CrossCheckAbsoluteCubicFeet = st.FixtureCrossCheckAbsoluteCubicFeet,
                AreaCheckRelative = st.FixtureAreaCheckRelative, AreaCheckAbsoluteSquareFeet = st.FixtureAreaCheckAbsoluteSquareFeet,
            },
            Report = s.Report with { Hcfcd = s.Report.Hcfcd with { TemplatePath = hcfcd } },
        };
    }

    /// <summary>The layers and table styles the configured names ask for, created when missing (what the LAN template would provide).</summary>
    private static List<string> TemplateStandIns(Database db, SheetVolSettings s)
    {
        var made = new List<string>();
        using var tr = db.TransactionManager.StartTransaction();
        var mw = s.ModelWriters;
        var r = s.Report;
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        foreach (string name in new[] { mw.BoundaryLayer, mw.ImpactOutlineLayer, mw.MatchMarks.Layer, r.SegmentTable.Style.Layer, r.MasterTable.Style.Layer, r.Hcfcd.Style.Layer }
                     .Concat(s.Adapters.OwnedSurfaceLayer is { } ol ? [ol] : []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (layers.Has(name)) continue;
            if (!layers.IsWriteEnabled) layers.UpgradeOpen();
            var rec = new LayerTableRecord { Name = name };
            layers.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            made.Add("layer " + name);
        }
        var styles = (DBDictionary)tr.GetObject(db.TableStyleDictionaryId, OpenMode.ForRead);
        foreach (string name in new[] { r.SegmentTable.Style.TableStyle, r.MasterTable.Style.TableStyle, r.Hcfcd.Style.TableStyle }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (styles.Contains(name)) continue;
            if (!styles.IsWriteEnabled) styles.UpgradeOpen();
            var ts = new TableStyle();   // named by its dictionary key (A2: Name throws until owned)
            styles.SetAt(name, ts);
            tr.AddNewlyCreatedDBObject(ts, true);
            made.Add("table style " + name);
        }
        tr.Commit();
        return made.Count == 0 ? ["none needed"] : made;
    }

    private static void Write(Cmd c, string dir, bool fakes, Stopwatch sw, List<SelfTestCheck> checks, SheetVolSettings settings)
    {
        int passed = checks.Count(k => k.Status == SelfTestCheck.Passed);
        int failed = checks.Count(k => k.Status == SelfTestCheck.Failed);
        int notRun = checks.Count(k => k.Status == SelfTestCheck.NotRun);
        var doc = new
        {
            schemaVersion = ResultSchemaVersion,
            command = "SVSELFTEST",
            bindings = fakes ? "Fakes" : "Installed",
            drawing = RegenEngine.DrawingPath(c.Doc),
            generatedAtUtc = DateTimeOffset.UtcNow,
            elapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
            passed,
            failed,
            notRun,
            checks = checks.Select(k => new { id = k.Id, status = k.Status, pass = k.Pass, measured = k.Measured, expected = k.Expected, message = k.Message }),
        };
        string path = Path.Combine(dir, settings.SelfTest.ResultFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
        c.Say($"SVSELFTEST: {passed} passed, {failed} failed, {notRun} not run headless in {sw.Elapsed.TotalSeconds:0.0} s ({(fakes ? "fake" : "installed")} bindings). Result: {path}");
        if (failed > 0) c.Say($"{PipelineCodes.SelfTestFailed} SVSELFTEST: {failed} check(s) failed: {string.Join(", ", checks.Where(k => k.Status == SelfTestCheck.Failed).Select(k => k.Id))}");
        if (notRun > 0) c.Say($"SVSELFTEST: not run headless (reasons in the result file): {string.Join(", ", checks.Where(k => k.Status == SelfTestCheck.NotRun).Select(k => k.Id))}");
    }
}

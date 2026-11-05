// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4 (SVSELFTEST driver).
//
// SVSELFTEST [Fakes|Installed] (HANDOFF.md 6.12, section 8): builds FX's fixtures in the
// drawing, runs the pipeline-level acceptance checks against the chosen bindings, and
// writes selftest-result.json beside the drawing. Fakes is the default until A1-A3 merge;
// Installed runs the same checks on EngineBindings.Current (the real factories) with the
// world edits in SelfTestWorlds.Installed. Every failed check prints SV-E199.

using System.Diagnostics;
using System.Text.Json;
using Autodesk.AutoCAD.Runtime;
using SheetVol.Civil3D.Commands;
using SheetVol.Civil3D.Session;

[assembly: CommandClass(typeof(SheetVol.Civil3D.SelfTest.SelfTestCommand))]

namespace SheetVol.Civil3D.SelfTest;

/// <summary>The SVSELFTEST command.</summary>
public static class SelfTestCommand
{
    /// <summary>Result file schema version.</summary>
    public const int ResultSchemaVersion = 1;

    /// <summary>Builds fixtures, runs acceptance checks, writes selftest-result.json (6.12).</summary>
    [CommandMethod("SVSELFTEST", CommandFlags.Modal)]
    public static void SelfTest() => Boundary.Run("SVSELFTEST", c =>
    {
        string mode = c.Keyword("SVSELFTEST bindings", ["Fakes", "Installed", "BindFakes"], "Fakes");
        if (mode == "BindFakes")
        {
            // Test only (tools/scripts/selftest-commands.scr): the SV commands run on the fixture
            // world and write only the self-test dictionaries. Refused over real bindings.
            if (EngineBindings.Current.IsWired && EngineBindings.Current.Adapters is not FakeAdapterFactory)
            {
                c.Say($"{PipelineCodes.SelfTestFailed} SVSELFTEST BindFakes: refused, the installed A1-A3 bindings stay in place.");
                return;
            }
            EngineBindings.Current = new EngineBindings(new FakeAdapterFactory(FakeWorld.Fixtures()),
                new FakeWriterFactory(new TemplateInfo(FixtureSpec.Template.ViewportWidth, FixtureSpec.Template.ViewportHeight, true)));
            c.Say("SVSELFTEST: FAKE bindings installed for this session (test only): SV commands read the in-memory fixture world and write only the self-test dictionaries.");
            return;
        }
        bool fakes = mode == "Fakes";
        var sw = Stopwatch.StartNew();
        var checks = new List<SelfTestCheck>();
        void Add(SelfTestCheck k)
        {
            checks.Add(k);
            c.Say($"{(k.Pass ? "SVSELFTEST" : PipelineCodes.SelfTestFailed + " SVSELFTEST")} {k.Id} {(k.Pass ? "PASS" : "FAIL")}: {k.Message} | measured {k.Measured} | expected {k.Expected}");
        }

        var baseSettings = c.Engine.Settings;
        string dir = RegenEngine.DrawingPath(c.Doc) is { } dwg ? Path.GetDirectoryName(dwg)! : Path.GetTempPath();
        string templatePath = Path.Combine(dir, baseSettings.SelfTest.TemplateDrawingName);

        // 1. FX fixtures, in-process, so the installed adapters find the section 8 world in the drawing.
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
                if (File.Exists(templatePath)) File.Delete(templatePath);
                Fixtures.WriteTemplateDrawing(templatePath);
                built.Add($"template drawing {templatePath}");
            }
            Add(new SelfTestCheck("FX", true, string.Join("; ", built), "A, H, S, TEMPLATE built", "FX fixtures built in this drawing"));
        }
        catch (System.Exception ex)
        {
            Add(new SelfTestCheck("FX", false, $"{ex.GetType().Name}: {ex.Message} (built: {string.Join("; ", built)})", "A, H, S, TEMPLATE built",
                fakes ? "fixture build failed; the fake checks below do not depend on it" : "fixture build failed; the installed checks need it"));
        }

        // 2. Settings for the test: layouts on, fixture tolerance on the cross-check, HCFCD template from the repo when not staged.
        var settings = TestSettings(baseSettings, templatePath);

        // 3. Bindings.
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
            var current = EngineBindings.Current;
            if (!current.IsWired)
            {
                Add(new SelfTestCheck("BINDINGS", false, "unwired", "installed A1-A3 factories", "SVSELFTEST Installed needs EngineBindings.Current set by the integration"));
                Write(c, dir, settings, fakes, sw, checks);
                return;
            }
            adapters = current.Adapters;
            writers = current.Writers;
            world = SelfTestWorlds.Installed;
        }

        // 4. The checks.
        var runner = new SelfTestRunner(c.Doc, _ => { }, settings, adapters, writers, world);
        runner.Run(fakes);
        foreach (var k in runner.Checks) Add(k);

        // 5. The result file.
        Write(c, dir, settings, fakes, sw, checks);
    });

    private static SheetVolSettings TestSettings(SheetVolSettings s, string templatePath)
    {
        string hcfcd = s.Report.Hcfcd.TemplatePath;
        string pluginTemplate = Core.Output.HcfcdTemplate.ResolvePath(hcfcd, SettingsProvider.PluginDirectory);
        if (!File.Exists(pluginTemplate) && Environment.GetEnvironmentVariable(s.SelfTest.RepoEnvironmentVariable) is { Length: > 0 } repo)
            hcfcd = Path.GetFullPath(Path.Combine(repo, s.SelfTest.RepoHcfcdTemplate));
        return s with
        {
            Regen = s.Regen with { GenerateLayouts = true, RebuildSources = true, RenumberLayouts = true },
            Template = s.Template with { Path = templatePath, LayoutName = FixtureSpec.Names.TemplateLayout, PlanViewportLayer = FixtureSpec.Names.ViewportLayer },
            Integrator = s.Integrator with { CrossCheckRelative = s.SelfTest.FixtureCrossCheckRelative },
            Report = s.Report with { Hcfcd = s.Report.Hcfcd with { TemplatePath = hcfcd } },
        };
    }

    private static void Write(Cmd c, string dir, SheetVolSettings settings, bool fakes, Stopwatch sw, List<SelfTestCheck> checks)
    {
        int passed = checks.Count(k => k.Pass), failed = checks.Count - passed;
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
            checks = checks.Select(k => new { id = k.Id, pass = k.Pass, measured = k.Measured, expected = k.Expected, message = k.Message }),
        };
        string path = Path.Combine(dir, settings.SelfTest.ResultFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
        c.Say($"SVSELFTEST: {passed} passed, {failed} failed in {sw.Elapsed.TotalSeconds:0.0} s ({(fakes ? "fake" : "installed")} bindings). Result: {path}");
        if (failed > 0) c.Say($"{PipelineCodes.SelfTestFailed} SVSELFTEST: {failed} check(s) failed: {string.Join(", ", checks.Where(k => !k.Pass).Select(k => k.Id))}");
    }
}

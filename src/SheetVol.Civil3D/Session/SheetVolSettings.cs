// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4.
//
// The one settings file (HANDOFF.md A7, 6.13 and Dan's settings-file rule): every value
// C1, C2, C3, C5, the writers and the commands take that the analysis itself does not
// govern. Read from sheetvol.settings.json in the plugin directory; missing file means
// the built-in defaults below, said out loud once per session. An unreadable or invalid
// file stops every SheetVol command: there is no silent fallback (6.13).
//
// Lengths a user thinks of in project units are stored in FEET and scaled by the
// drawing's unit (E216): a metric drawing gets the same physical tolerance. Values that
// are placeholders until LAN names arrive (A7) are listed by PlaceholdersInUse();
// values that are judgement calls are marked ASSUMED in their doc comment.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SheetVol.Core;
using SheetVol.Core.Geometry;
using SheetVol.Core.Output;
using SheetVol.Core.Planning;
using SheetVol.Core.Tracking;
using SheetVol.Core.Views;
using SheetVol.Core.Volumes;

namespace SheetVol.Civil3D.Session;

/// <summary>A paper or model point in settings.</summary>
public sealed record PointSetting(double X, double Y);

/// <summary>Segment planning and boundary tolerances for C1 (6.3, 6.4). Lengths in feet, scaled to drawing units.</summary>
public sealed record PlanningSettings
{
    /// <summary>Largest chord-to-curve distance on offset arcs and spirals, feet (6.4 step 1).</summary>
    public double MidOrdinateFeet { get; init; } = GeometryOptions.DefaultMidOrdinateFeet;

    /// <summary>Stations closer than this are one station, feet (E206).</summary>
    public double StationToleranceFeet { get; init; } = GeometryOptions.DefaultStationToleranceFeet;

    /// <summary>Largest tangent-to-tangent deflection joined by a mitre, degrees (6.4 step 5).</summary>
    public double MitreLimitDeg { get; init; } = GeometryOptions.DefaultMitreLimitDeg;

    /// <summary>A remainder of A..B@L shorter than this is merged into the previous segment, feet (E302). ASSUMED 0 (D11).</summary>
    public double MinSegmentLengthFeet { get; init; } = 0;

    /// <summary>Composite format for derived segment labels; {0} is the sheet position.</summary>
    public string SegmentLabelFormat { get; init; } = PlanningOptions.DefaultSegmentLabelFormat;

    /// <summary>Most breaks one specification may produce.</summary>
    public int MaxBreaks { get; init; } = PlanningOptions.DefaultMaxBreaks;

    /// <summary>C1 options for a drawing with <paramref name="drawingUnitsPerFoot"/> drawing units per foot.</summary>
    public PlanningOptions ToOptions(double drawingUnitsPerFoot)
    {
        double mid = MidOrdinateFeet * drawingUnitsPerFoot;
        return new PlanningOptions
        {
            Geometry = new GeometryOptions
            {
                MidOrdinate = mid,
                MitreLimitDeg = MitreLimitDeg,
                StationTolerance = StationToleranceFeet * drawingUnitsPerFoot,
                OverlapAreaTolerance = mid * mid,
            },
            MinSegmentLength = MinSegmentLengthFeet * drawingUnitsPerFoot,
            SegmentLabelFormat = SegmentLabelFormat,
            MaxBreaks = MaxBreaks,
        };
    }
}

/// <summary>The C5 integrator and its cross-checks (6.5, 6.15).</summary>
public sealed record IntegratorConfig
{
    /// <summary>Run the integrator cross-check on every regen for the Standard preset (6.15 "Checks on every regen"). HcfcdSegment always runs it.</summary>
    public bool CrossCheck { get; init; } = true;

    /// <summary>Cut shallower than this is not impacted, feet (E1007). Default 0 counts everything.</summary>
    public double MinCutDepthFeet { get; init; } = 0;

    /// <summary>Gaps between impacted station intervals shorter than this are bridged, feet (E1007).</summary>
    public double GapToleranceFeet { get; init; } = 0;

    /// <summary>Absolute part of the integrator-versus-bounded-volume tolerance, cubic feet (6.15: max(0.5 ft3, 1e-4 x total)).</summary>
    public double CrossCheckAbsoluteCubicFeet { get; init; } = 0.5;

    /// <summary>Relative part of the same tolerance (6.15 project data: 1e-4).</summary>
    public double CrossCheckRelative { get; init; } = 1e-4;

    /// <summary>Relative tolerance between the datum-method 2D area and the integrator's area. ASSUMED: 6.5 requires agreement but gives no tolerance (C2 note).</summary>
    public double AreaCheckRelative { get; init; } = 1e-4;

    /// <summary>Absolute part of the area tolerance, square feet. ASSUMED.</summary>
    public double AreaCheckAbsoluteSquareFeet { get; init; } = 1.0;
}

/// <summary>Template-driven paper space (6.7). Path, layout and layer are PLACEHOLDERS until Dan's LAN template arrives (section 13).</summary>
public sealed record TemplateSettings
{
    /// <summary>DWT holding the sheet layout to clone. PLACEHOLDER: empty until the LAN template arrives. Required when regen.generateLayouts is on (E601).</summary>
    public string Path { get; init; } = "";

    /// <summary>Layout in the DWT that new layouts are cloned from. PLACEHOLDER.</summary>
    public string LayoutName { get; init; } = "SV-SHEET";

    /// <summary>Layer of the plan viewport in the template; if none is on it, the largest floating viewport (6.7). PLACEHOLDER.</summary>
    public string PlanViewportLayer { get; init; } = "SV-VPORT";

    /// <summary>Table top-left on the layout when the template has no anchor block (6.7). Null: the anchor block is required.</summary>
    public PointSetting? TablePaperPoint { get; init; }

    /// <summary>Title-block attribute fill (6.7): tag to value pattern with {RUN} {N} {LABEL} {FROM} {TO} {ALIGN}. A tag missing from the block is skipped with a note (E615).</summary>
    public IReadOnlyDictionary<string, string> TitleAttributes { get; init; } = new Dictionary<string, string>
    {
        ["STA_FROM"] = "{FROM}",
        ["STA_TO"] = "{TO}",
        ["SHEET_NO"] = "{N}",
    };
}

/// <summary>Model-space outputs (6.8, A14). Layer names are PLACEHOLDERS (A7).</summary>
public sealed record ModelOutputSettings
{
    /// <summary>Layer of the boundary polylines. PLACEHOLDER. A missing layer fails the run (E501 rule applied to every owned layer).</summary>
    public string BoundaryLayer { get; init; } = "SV-BOUNDARY";

    /// <summary>Draw match-line marks at shared breaks (6.8, optional).</summary>
    public bool MatchMarks { get; init; } = true;

    /// <summary>Layer of the match-line marks. PLACEHOLDER.</summary>
    public string MatchMarkLayer { get; init; } = "SV-MATCH";

    /// <summary>Match-line text: {0} station as the alignment formats it, {1} the neighbouring sheet label (6.8).</summary>
    public string MatchMarkTextTemplate { get; init; } = "MATCH LINE STA {0} — SEE SHEET {1}";

    /// <summary>Match-line text height in paper units; model-space writers multiply by the run's model units per paper unit. PLACEHOLDER.</summary>
    public double MatchMarkTextHeight { get; init; } = 0.1;

    /// <summary>Distance beyond the right offset at the segment start where a model-space segment table is placed, feet (A14).</summary>
    public double ModelTableGapFeet { get; init; } = 10;

    /// <summary>Layout that receives the master table; null puts it in model space beside the alignment start (6.8).</summary>
    public string? MasterTableLayout { get; init; }

    /// <summary>Text shown in a table for the stations of a manual segment, which has none (6.3).</summary>
    public string ManualStationText { get; init; } = "MANUAL";
}

/// <summary>Regen behaviour switches (6.10, E307, E404).</summary>
public sealed record RegenSettings
{
    /// <summary>Rebuild an out-of-date native source surface before computing; off fails with the surface name (E404).</summary>
    public bool RebuildSources { get; init; } = true;

    /// <summary>Renumber later layouts after a break is inserted or removed, with a two-pass rename (E307).</summary>
    public bool RenumberLayouts { get; init; } = true;

    /// <summary>Generate one layout per station segment from the template (6.7). Off until template.path names the LAN template: with it off, tables go in model space at each segment (A14).</summary>
    public bool GenerateLayouts { get; init; }
}

/// <summary>Defaults for a run created by SVDEFINE. Lengths in feet.</summary>
public sealed record RunDefaults
{
    /// <summary>SVAUTO default (A4).</summary>
    public AutoMode AutoMode { get; init; } = AutoMode.Views;

    /// <summary>Break list of a new run (6.3).</summary>
    public string BreakSpec { get; init; } = "START..END@500";

    /// <summary>Left offset of a new run, feet; negative is left.</summary>
    public double OffsetLeftFeet { get; init; } = -100;

    /// <summary>Right offset of a new run, feet.</summary>
    public double OffsetRightFeet { get; init; } = 100;

    /// <summary>View orientation (A3).</summary>
    public ViewOrientation Orientation { get; init; } = ViewOrientation.AlongAlignment;

    /// <summary>Scale mode (6.6).</summary>
    public ScaleMode ScaleMode { get; init; } = ScaleMode.FitStandard;

    /// <summary>Fixed scale, model units per paper unit (6.6).</summary>
    public double ModelUnitsPerPaperUnit { get; init; } = 50;

    /// <summary>FitStandard candidates, model units per paper unit (T16).</summary>
    public IReadOnlyList<double> StandardScales { get; init; } = [20, 30, 40, 50, 60, 100];

    /// <summary>Viewport clip (6.6).</summary>
    public ClipMode Clip { get; init; } = ClipMode.Boundary;

    /// <summary>Sheet overlap along the alignment at each end, feet (6.6).</summary>
    public double ViewOverlapFeet { get; init; }
}

/// <summary>Export file naming (6.8, E809). Files go beside the drawing.</summary>
public sealed record ExportFileSettings
{
    /// <summary>File name without extension: {DWG} drawing name without extension, {RUN} run name. Re-evaluated on every export, so a renamed drawing names its exports (E809).</summary>
    public string FileNamePattern { get; init; } = "{DWG}-{RUN}";

    /// <summary>CSV extension.</summary>
    public string CsvExtension { get; init; } = ".csv";

    /// <summary>JSON extension.</summary>
    public string JsonExtension { get; init; } = ".json";

    /// <summary>Write a UTF-8 byte order mark on the CSV so spreadsheet programs read m³ and m² correctly.</summary>
    public bool CsvUtf8Bom { get; init; } = true;
}

/// <summary>SVSELFTEST names. Test-only; the fakes never touch the product dictionary.</summary>
public sealed record SelfTestSettings
{
    /// <summary>Result file written beside the drawing.</summary>
    public string ResultFileName { get; init; } = "selftest-result.json";

    /// <summary>Named Objects Dictionary entry where the fake writers keep one Xrecord per owned object.</summary>
    public string FakeObjectDictionary { get; init; } = "SHEETVOL-SELFTEST-OBJECTS";

    /// <summary>Named Objects Dictionary entry where the fake persistence store keeps one Xrecord per run.</summary>
    public string FakeStoreDictionary { get; init; } = "SHEETVOL-SELFTEST-RUNS";

    /// <summary>Relative tolerance of the integrator cross-check on fixtures (6.15: 1e-9 on fixtures).</summary>
    public double FixtureCrossCheckRelative { get; init; } = 1e-9;

    /// <summary>Acceptance tolerance on volumes, cubic yards (section 8: ±0.01 CY).</summary>
    public double VolumeToleranceCy { get; init; } = 0.01;

    /// <summary>Template drawing FX writes beside the test drawing for the installed paper writers (6.7, T18).</summary>
    public string TemplateDrawingName { get; init; } = "selftest-template.dwg";

    /// <summary>Environment variable holding the repo root (set by tools/test-headless.ps1).</summary>
    public string RepoEnvironmentVariable { get; init; } = "SHEETVOL_REPO";

    /// <summary>The HCFCD table template in the repo, relative to the repo root, used when the plugin directory has no Templates folder (a staged dev build).</summary>
    public string RepoHcfcdTemplate { get; init; } = "src/SheetVol.Core/Output/Templates/hcfcd-segment-table.json";
}

/// <summary>
/// Every setting SheetVol reads. Loaded from <c>sheetvol.settings.json</c> (camelCase keys,
/// comments and trailing commas allowed, unknown keys rejected). Missing keys keep these defaults.
/// </summary>
public sealed record SheetVolSettings
{
    /// <summary>Settings file name in the plugin directory.</summary>
    public const string FileName = "sheetvol.settings.json";

    /// <summary>Settings schema this build reads.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Settings schema version of the file.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>C1 planning and boundary tolerances.</summary>
    public PlanningSettings Planning { get; init; } = new();

    /// <summary>C2: units, tolerances, formats, table styles, layers, text heights, anchor name, persistence, HCFCD template path.</summary>
    public ReportConfig Report { get; init; } = new();

    /// <summary>C3 view math tunables.</summary>
    public ViewMathConfig ViewMath { get; init; } = new();

    /// <summary>C3 layout naming (E603, E307).</summary>
    public LayoutNameConfig LayoutNames { get; init; } = new();

    /// <summary>C3 auto-update policy settings (used by L2's wiring).</summary>
    public TrackingConfig Tracking { get; init; } = new();

    /// <summary>C5 integrator settings and cross-check tolerances.</summary>
    public IntegratorConfig Integrator { get; init; } = new();

    /// <summary>Sheet template (6.7).</summary>
    public TemplateSettings Template { get; init; } = new();

    /// <summary>Model-space outputs.</summary>
    public ModelOutputSettings ModelOutput { get; init; } = new();

    /// <summary>Regen switches.</summary>
    public RegenSettings Regen { get; init; } = new();

    /// <summary>Defaults for new runs.</summary>
    public RunDefaults RunDefaults { get; init; } = new();

    /// <summary>Export file naming.</summary>
    public ExportFileSettings ExportFiles { get; init; } = new();

    /// <summary>Self-test names and tolerances.</summary>
    public SelfTestSettings SelfTest { get; init; } = new();

    /// <summary>The built-in defaults.</summary>
    public static SheetVolSettings Default { get; } = new();

    /// <summary>Every problem with the values; empty when usable. C2's own checks come back as SV-E534, the rest as SV-E820.</summary>
    public IReadOnlyList<Problem> Validate()
    {
        var p = new List<Problem>(Report.Validate());
        void Bad(string path, string why) => p.Add(new Problem(PipelineCodes.SettingsInvalid, Severity.Error, $"Settings {path}: {why}",
            Fix: $"Correct {path} in {FileName}, or delete the key to use the built-in default."));
        void Positive(string path, double v) { if (!double.IsFinite(v) || v <= 0) Bad(path, "must be a number above zero."); }
        void NonNegative(string path, double v) { if (!double.IsFinite(v) || v < 0) Bad(path, "must be a number of zero or more."); }
        void Text(string path, string? v) { if (string.IsNullOrWhiteSpace(v)) Bad(path, "must not be empty."); }

        if (SchemaVersion != CurrentSchemaVersion)
            Bad("schemaVersion", $"is {SchemaVersion}; this build reads {CurrentSchemaVersion}.");

        // C1 validates its own options in the Planner constructor; build one at the foot scale.
        try { _ = new Planner(Planning.ToOptions(1.0)); }
        catch (ArgumentException ex) { Bad("planning", ex.Message); }

        try { var namer = new LayoutNamer(LayoutNames); _ = namer.FormatName(new LayoutNameParts("RUN", 1, "1", "0+00", "5+00", "ALIGN")); }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { Bad("layoutNames", ex.Message); }

        var vm = ViewMath;
        Positive("viewMath.fitTolerance", vm.FitTolerance);
        Positive("viewMath.coincidentChordTolerance", vm.CoincidentChordTolerance);
        Positive("viewMath.tangentStep", vm.TangentStep);
        NonNegative("viewMath.straightBulgeTolerance", vm.StraightBulgeTolerance);
        NonNegative("viewMath.duplicateVertexTolerance", vm.DuplicateVertexTolerance);
        Positive("viewMath.lengthSearchTolerance", vm.LengthSearchTolerance);
        if (vm.MaxLengthSearchIterations < 1) Bad("viewMath.maxLengthSearchIterations", "must be at least 1.");
        try { _ = 1.0.ToString(vm.MessageNumberFormat, CultureInfo.InvariantCulture); }
        catch (FormatException) { Bad("viewMath.messageNumberFormat", "is not a .NET number format."); }

        if (Tracking.UndoCommandNames is null || Tracking.ToolCommandNames is null) Bad("tracking", "command name lists must not be null.");
        try { _ = string.Format(CultureInfo.InvariantCulture, Tracking.ReportOnlyMessageFormat, "run", "reason"); }
        catch (FormatException) { Bad("tracking.reportOnlyMessageFormat", "is not a valid format with {0} run and {1} reason."); }

        var ig = Integrator;
        NonNegative("integrator.minCutDepthFeet", ig.MinCutDepthFeet);
        NonNegative("integrator.gapToleranceFeet", ig.GapToleranceFeet);
        NonNegative("integrator.crossCheckAbsoluteCubicFeet", ig.CrossCheckAbsoluteCubicFeet);
        NonNegative("integrator.crossCheckRelative", ig.CrossCheckRelative);
        NonNegative("integrator.areaCheckRelative", ig.AreaCheckRelative);
        NonNegative("integrator.areaCheckAbsoluteSquareFeet", ig.AreaCheckAbsoluteSquareFeet);

        Text("template.layoutName", Template.LayoutName);
        Text("template.planViewportLayer", Template.PlanViewportLayer);
        if (Regen.GenerateLayouts) Text("template.path (regen.generateLayouts is on)", Template.Path);
        if (Template.TablePaperPoint is { } tp && (!double.IsFinite(tp.X) || !double.IsFinite(tp.Y))) Bad("template.tablePaperPoint", "must be two finite numbers.");
        foreach (var (tag, pattern) in Template.TitleAttributes ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(tag)) Bad("template.titleAttributes", "a tag must not be empty.");
            try { _ = LayoutNamer.FormatPattern(pattern ?? "", new LayoutNameParts("RUN", 1, "1", "0+00", "5+00", "ALIGN"), LayoutNames.SheetNumberFormat); }
            catch (FormatException ex) { Bad($"template.titleAttributes.{tag}", ex.Message); }
        }

        var mo = ModelOutput;
        Text("modelOutput.boundaryLayer", mo.BoundaryLayer);
        Text("modelOutput.matchMarkLayer", mo.MatchMarkLayer);
        Positive("modelOutput.matchMarkTextHeight", mo.MatchMarkTextHeight);
        NonNegative("modelOutput.modelTableGapFeet", mo.ModelTableGapFeet);
        Text("modelOutput.manualStationText", mo.ManualStationText);
        if (mo.MasterTableLayout is not null) Text("modelOutput.masterTableLayout", mo.MasterTableLayout);
        try { _ = string.Format(CultureInfo.InvariantCulture, mo.MatchMarkTextTemplate, "1+00", "2"); }
        catch (FormatException) { Bad("modelOutput.matchMarkTextTemplate", "is not a valid format with {0} station and {1} sheet."); }

        var rd = RunDefaults;
        Text("runDefaults.breakSpec", rd.BreakSpec);
        if (!(double.IsFinite(rd.OffsetLeftFeet) && double.IsFinite(rd.OffsetRightFeet) && rd.OffsetLeftFeet < rd.OffsetRightFeet))
            Bad("runDefaults.offsetLeftFeet/offsetRightFeet", "must be numbers with left less than right.");
        Positive("runDefaults.modelUnitsPerPaperUnit", rd.ModelUnitsPerPaperUnit);
        if (rd.StandardScales is null || rd.StandardScales.Count == 0 || rd.StandardScales.Any(s => !double.IsFinite(s) || s <= 0))
            Bad("runDefaults.standardScales", "must list one or more numbers above zero.");
        NonNegative("runDefaults.viewOverlapFeet", rd.ViewOverlapFeet);

        var ef = ExportFiles;
        Text("exportFiles.fileNamePattern", ef.FileNamePattern);
        if (ef.FileNamePattern is not null && (!ef.FileNamePattern.Contains("{RUN}", StringComparison.Ordinal)))
            Bad("exportFiles.fileNamePattern", "must contain {RUN} so two runs never write the same file.");
        foreach (var (path, ext) in new[] { ("exportFiles.csvExtension", ef.CsvExtension), ("exportFiles.jsonExtension", ef.JsonExtension) })
            if (string.IsNullOrWhiteSpace(ext) || !ext.StartsWith('.') || ext.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                Bad(path, "must be a file extension starting with a dot.");

        var st = SelfTest;
        Text("selfTest.resultFileName", st.ResultFileName);
        Text("selfTest.fakeObjectDictionary", st.FakeObjectDictionary);
        Text("selfTest.fakeStoreDictionary", st.FakeStoreDictionary);
        NonNegative("selfTest.fixtureCrossCheckRelative", st.FixtureCrossCheckRelative);
        Positive("selfTest.volumeToleranceCy", st.VolumeToleranceCy);
        return p;
    }

    /// <summary>Setting paths whose value is still a placeholder default (A7): C2's under <c>report.</c>, then A4's own.</summary>
    public IReadOnlyList<string> PlaceholdersInUse()
    {
        var list = Report.PlaceholdersInUse().Select(s => "report." + s).ToList();
        var d = Default;
        if (Template.Path == d.Template.Path) list.Add("template.path");
        if (Template.LayoutName == d.Template.LayoutName) list.Add("template.layoutName");
        if (Template.PlanViewportLayer == d.Template.PlanViewportLayer) list.Add("template.planViewportLayer");
        if (ModelOutput.BoundaryLayer == d.ModelOutput.BoundaryLayer) list.Add("modelOutput.boundaryLayer");
        if (ModelOutput.MatchMarkLayer == d.ModelOutput.MatchMarkLayer) list.Add("modelOutput.matchMarkLayer");
        if (ModelOutput.MatchMarkTextHeight == d.ModelOutput.MatchMarkTextHeight) list.Add("modelOutput.matchMarkTextHeight");
        return list;
    }

    /// <summary>Reads settings from JSON and validates them. Unknown keys and nulls in non-nullable members are errors.</summary>
    public static Outcome<SheetVolSettings> FromJson(string json, string source)
    {
        SheetVolSettings? s;
        try
        {
            s = JsonSerializer.Deserialize<SheetVolSettings>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Outcome<SheetVolSettings>.Fail(new Problem(PipelineCodes.SettingsInvalid, Severity.Error,
                $"Settings file {source} is not valid: {ex.Message}", Fix: $"Fix the JSON, or delete {FileName} to use the built-in defaults."));
        }
        if (s is null)
            return Outcome<SheetVolSettings>.Fail(new Problem(PipelineCodes.SettingsInvalid, Severity.Error, $"Settings file {source} is empty."));
        var problems = s.Validate();
        return problems.Count > 0 ? Outcome<SheetVolSettings>.Fail(problems) : Outcome<SheetVolSettings>.Ok(s);
    }

    /// <summary>These settings as indented JSON, the same shape <see cref="FromJson"/> reads.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Drawing units per foot for a drawing's units: 1 for feet, 1/0.3048 for metres (E216).</summary>
    public double DrawingUnitsPerFoot(DrawingUnits units)
    {
        double basePerDu = units.BaseUnitsPerDrawingUnit;
        if (!double.IsFinite(basePerDu) || basePerDu <= 0)
            throw new ArgumentOutOfRangeException(nameof(units), basePerDu, "Drawing unit scale must be a positive number.");
        return units.System == UnitSystem.Imperial
            ? 1.0 / basePerDu
            : Report.Volumes.Constants.MetresPerFoot / basePerDu;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Where the effective settings came from.</summary>
/// <param name="Settings">The settings; null when the file is invalid (every command refuses to run).</param>
/// <param name="Source">The file path, or the words for built-in defaults.</param>
/// <param name="FromFile">True when read from a file.</param>
/// <param name="Problems">Load problems; an Error means <paramref name="Settings"/> is null.</param>
public sealed record SettingsLoad(SheetVolSettings? Settings, string Source, bool FromFile, IReadOnlyList<Problem> Problems);

/// <summary>Loads the settings file once per session and remembers whether placeholders were reported.</summary>
public static class SettingsProvider
{
    private static readonly object Gate = new();
    private static SettingsLoad? _current;
    private static bool _placeholdersReported;

    /// <summary>The plugin directory: where the engine DLL, the settings file and Templates\ live.</summary>
    public static string PluginDirectory =>
        System.IO.Path.GetDirectoryName(typeof(SettingsProvider).Assembly.Location) ?? AppContext.BaseDirectory;

    /// <summary>Full path of the settings file.</summary>
    public static string SettingsPath => System.IO.Path.Combine(PluginDirectory, SheetVolSettings.FileName);

    /// <summary>The effective settings, loaded on first use.</summary>
    public static SettingsLoad Current
    {
        get
        {
            lock (Gate) return _current ??= Load(SettingsPath);
        }
    }

    /// <summary>Re-reads the file (SVCONFIG Reload) and re-arms the placeholder notice.</summary>
    public static SettingsLoad Reload()
    {
        lock (Gate)
        {
            _placeholdersReported = false;
            return _current = Load(SettingsPath);
        }
    }

    /// <summary>Reads one settings file. A missing file is the built-in defaults (Info); an unreadable or invalid one is an Error.</summary>
    public static SettingsLoad Load(string path)
    {
        if (!File.Exists(path))
            return new SettingsLoad(SheetVolSettings.Default, "built-in defaults", false,
            [
                new Problem(PipelineCodes.SettingsPlaceholder, Severity.Info,
                    $"No settings file at {path}; the built-in defaults are in use.",
                    Fix: "Run SVCONFIG Write to create one, then edit it."),
            ]);
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsLoad(null, path, true,
                [new Problem(PipelineCodes.SettingsInvalid, Severity.Error, $"Settings file {path} could not be read: {ex.Message}")]);
        }
        var o = SheetVolSettings.FromJson(json, path);
        return o.Succeeded
            ? new SettingsLoad(o.Value, path, true, o.Problems)
            : new SettingsLoad(null, path, true, o.Problems);
    }

    /// <summary>
    /// The placeholder notice, returned once per session (A7): one Info line listing every
    /// setting that still holds its placeholder default. Empty on every later call.
    /// </summary>
    public static IReadOnlyList<Problem> TakePlaceholderNotice(SheetVolSettings settings)
    {
        lock (Gate)
        {
            if (_placeholdersReported) return [];
            _placeholdersReported = true;
        }
        var list = settings.PlaceholdersInUse();
        return list.Count == 0
            ? []
            : [new Problem(PipelineCodes.SettingsPlaceholder, Severity.Info,
                $"{list.Count} setting(s) still hold placeholder defaults until LAN names arrive (A7): {string.Join(", ", list)}.",
                Fix: $"Set them in {SheetVolSettings.FileName}; SVCONFIG shows the effective values.")];
    }
}

using System.Runtime.CompilerServices;
using SheetVol.Core.Output;
using SheetVol.Core.Tests.Volumes;
using SheetVol.Core.Volumes;

namespace SheetVol.Core.Tests.Output;

public class HcfcdTemplateTests
{
    private static readonly ReportConfig Cfg = ReportConfig.Default;
    private static readonly QuantityFormatter F = OutputTestKit.Formatter();
    private static readonly Guid Seg = Guid.NewGuid();
    private static readonly AnalysisDef Sediment = new(Guid.NewGuid(), "SEDIMENT", "EG_H", "FG_H");

    /// <summary>The folder the shipped template's relative config path resolves against: src/SheetVol.Core/Output.</summary>
    private static string OutputFolder([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "src", "SheetVol.Core", "Output"));

    private static HcfcdTemplate Shipped() => HcfcdTemplate.Load(Cfg.Hcfcd.TemplatePath, OutputFolder()).Value;

    private static List<List<string>> Cells(TableModel t) => t.Rows.Select(r => r.Cells.ToList()).ToList();

    private static IReadOnlyList<IReadOnlyList<string>> Ro(List<List<string>> cells) => cells.Select(r => (IReadOnlyList<string>)r).ToList();

    /// <summary>T25 per segment: length 500 ft, area 16,000 ft², cut 962.963 CY (26,000 ft³).</summary>
    private static HcfcdMaterial T25(string name = "SEDIMENT", double length = 500, double cutFt3 = 26_000, double factor = 1)
        => new(Sediment.Id, name, new ImpactResult(Seg, name, length, 16_000, cutFt3, [(0, 500)], [], false), factor);

    private static List<List<string>> Created(params AnalysisDef[] materials)
        => Cells(Shipped().CreateTable(Seg, materials, AreaUnit.Acres, F.Units, Cfg).Value);

    private static void Apply(List<List<string>> cells, IEnumerable<CellWrite> writes)
    {
        foreach (var w in writes) cells[w.Row][w.Column] = w.Text;
    }

    [Fact]
    public void Shipped_template_loads_from_the_config_path_and_is_marked_placeholder()
    {
        var o = HcfcdTemplate.Load(Cfg.Hcfcd.TemplatePath, OutputFolder());

        Assert.True(o.Succeeded, string.Join("; ", o.Problems.Select(p => p.Message)));
        Assert.Equal("hcfcd-segment-table", o.Value.Definition.Name);
        Assert.Equal(OutputCodes.TemplatePlaceholder, o.Problems.Single().Code);
        Assert.Equal(Severity.Info, o.Problems.Single().Severity);
    }

    [Fact]
    public void A13_first_creation_puts_the_bound_rows_at_6_to_9_with_one_material_row_per_analysis()
    {
        var t = Shipped().CreateTable(Seg, [Sediment], AreaUnit.Acres, F.Units, Cfg).Value;

        Assert.Equal(TableKind.Hcfcd, t.Kind);
        Assert.Equal(9, t.Rows.Count);
        Assert.StartsWith("TOTAL IMPACTED LENGTH", t.Cell(5, 0));     // row 6
        Assert.StartsWith("TOTAL IMPACTED AREA", t.Cell(6, 0));       // row 7
        Assert.Equal(["TOTAL CUT MATERIAL", "VOLUME (CY)", "AVERAGE (CY/LF)"], t.Rows[7].Cells);   // row 8, tokens resolved
        Assert.Equal(["SEDIMENT", "", ""], t.Rows[8].Cells);          // row 9
        Assert.Equal(RowKind.Material, t.Rows[8].Kind);
        Assert.Equal(Sediment.Id, t.Rows[8].AnalysisId);
        Assert.Contains(new CellRange(0, 0, 0, 2), t.Merges);
        Assert.Equal("SV-Volumes", t.Style.TableStyle);
    }

    [Fact]
    public void E1018_core_two_materials_add_two_rows_and_merges_below_shift()
    {
        var gravel = new AnalysisDef(Guid.NewGuid(), "GRAVEL", "EG_H", "FG_G");
        var t = Shipped().CreateTable(Seg, [Sediment, gravel], AreaUnit.Acres, F.Units, Cfg).Value;
        Assert.Equal(["SEDIMENT", "GRAVEL"], t.RowsOf(RowKind.Material).Select(r => r.Cells[0]));

        var dup = Shipped().CreateTable(Seg, [Sediment, Sediment with { Id = Guid.NewGuid() }], AreaUnit.Acres, F.Units, Cfg);
        Assert.Equal(OutputCodes.TemplateInvalid, dup.Problems.Single().Code);
    }

    [Fact]
    public void T25_values_bind_by_label_with_the_HCFCD_formats()
    {
        var cells = Created(Sediment);
        var b = Shipped().Bind(Ro(cells), Seg, [T25()], AreaUnit.Acres, F, Cfg);
        Apply(cells, b.Changes);

        Assert.Empty(b.Problems);
        Assert.Equal(["TOTAL IMPACTED LENGTH (BELOW OHWM)", "500", "LF"], cells[5]);
        Assert.Equal(["TOTAL IMPACTED AREA (BELOW OHWM)", "0.37", "AC"], cells[6]);
        Assert.Equal(["SEDIMENT", "963", "1.93"], cells[8]);
    }

    [Fact]
    public void Binding_finds_rows_by_label_not_by_position_and_leaves_manual_cells_alone()
    {
        var cells = Created(Sediment);
        cells[1][1] = "13,800,123.45 N, 3,100,456.78 E";                 // typed center coordinates
        cells.RemoveAt(3);                                               // user deleted the WETLANDS row
        cells.Insert(0, ["USER NOTE", "keep me", ""]);                   // user added a row on top
        var before = cells.Select(r => r.ToList()).ToList();

        var b = Shipped().Bind(Ro(cells), Seg, [T25()], AreaUnit.Acres, F, Cfg);
        Apply(cells, b.Changes);

        Assert.Empty(b.Problems);
        Assert.Equal("500", cells.Single(r => r[0].StartsWith("TOTAL IMPACTED LENGTH"))[1]);
        Assert.Equal("963", cells.Single(r => r[0] == "SEDIMENT")[1]);
        Assert.Equal("13,800,123.45 N, 3,100,456.78 E", cells.Single(r => r[0].StartsWith("CENTER"))[1]);
        Assert.Equal(["USER NOTE", "keep me", ""], cells[0]);
        Assert.All(b.Bound, w => Assert.True(w.Column > 0));             // never a label cell
        int unboundChanged = cells.Select((r, i) => (r, i)).Count(x => !b.Bound.Any(w => w.Row == x.i) && !x.r.SequenceEqual(before[x.i]));
        Assert.Equal(0, unboundChanged);
    }

    [Fact]
    public void E1015_core_a_missing_label_skips_that_value_and_the_others_still_update()
    {
        var cells = Created(Sediment);
        cells[5][0] = "LENGTH (RENAMED BY USER)";

        var b = Shipped().Bind(Ro(cells), Seg, [T25()], AreaUnit.Acres, F, Cfg);
        Apply(cells, b.Changes);

        var p = Assert.Single(b.Problems);
        Assert.Equal(OutputCodes.BoundLabelMissing, p.Code);
        Assert.Equal(Severity.Warning, p.Severity);
        Assert.Equal(Seg, p.SegmentId);
        Assert.Equal("", cells[5][1]);
        Assert.Equal("0.37", cells[6][1]);
        Assert.Equal("963", cells[8][1]);
    }

    [Fact]
    public void An_ambiguous_label_is_skipped_rather_than_guessed()
    {
        var cells = Created(Sediment);
        cells.Insert(6, ["TOTAL IMPACTED LENGTH (BELOW OHWM) COPY", "", ""]);

        var b = Shipped().Bind(Ro(cells), Seg, [T25()], AreaUnit.Acres, F, Cfg);

        Assert.Contains(b.Problems, x => x.Code == OutputCodes.BoundLabelAmbiguous);
        Assert.DoesNotContain(b.Bound, w => cells[w.Row][0].StartsWith("TOTAL IMPACTED LENGTH"));
        Assert.Contains(b.Bound, w => cells[w.Row][0] == "SEDIMENT");
    }

    [Fact]
    public void E1018_core_a_material_without_a_row_is_reported()
    {
        var cells = Created(Sediment);
        var gravel = new HcfcdMaterial(Guid.NewGuid(), "GRAVEL", new ImpactResult(Seg, "GRAVEL", 500, 16_000, 2700, [], [], false), 1);

        var b = Shipped().Bind(Ro(cells), Seg, [T25(), gravel], AreaUnit.Acres, F, Cfg);

        var p = Assert.Single(b.Problems);
        Assert.Equal(OutputCodes.BoundLabelMissing, p.Code);
        Assert.Contains("GRAVEL", p.Message);
        Assert.Contains("E1018", p.Message);
    }

    [Fact]
    public void T27_average_is_NA_when_the_length_is_zero()
    {
        var cells = Created(Sediment);
        var none = new HcfcdMaterial(Sediment.Id, "SEDIMENT", new ImpactResult(Seg, "SEDIMENT", 0, 0, 0, [], [], false), 1);

        var b = Shipped().Bind(Ro(cells), Seg, [none], AreaUnit.Acres, F, Cfg);
        Apply(cells, b.Changes);

        Assert.Equal("0", cells[5][1]);
        Assert.Equal("0.00", cells[6][1]);
        Assert.Equal(["SEDIMENT", "0", "N/A"], cells[8]);
    }

    [Fact]
    public void E1019_core_average_is_computed_from_unrounded_volume_and_length()
    {
        var cells = Created(Sediment);
        // 10.4 CY over 3.4 ft = 3.0588; from rounded inputs it would be 10 / 3 = 3.33.
        var m = new HcfcdMaterial(Sediment.Id, "SEDIMENT", new ImpactResult(Seg, "SEDIMENT", 3.4, 100, 10.4 * 27, [], [], false), 1);

        var b = Shipped().Bind(Ro(cells), Seg, [m], AreaUnit.Acres, F, Cfg);
        Apply(cells, b.Changes);

        Assert.Equal(["SEDIMENT", "10", "3.06"], cells[8]);
        Assert.Equal("3", cells[5][1]);
    }

    [Fact]
    public void D7_impact_volume_is_unfactored_by_default_and_takes_the_cut_factor_only_when_configured()
    {
        // D7: rows 6-9 and the average report the in-place (bank) cut; the factor is opt-in.
        Assert.False(Cfg.Hcfcd.ImpactVolumesUseCutFactor);
        var off = Created(Sediment);
        Apply(off, Shipped().Bind(Ro(off), Seg, [T25(factor: 1.25)], AreaUnit.Acres, F, Cfg).Changes);
        Assert.Equal(["SEDIMENT", "963", "1.93"], off[8]);       // 962.963 CY, 962.963 / 500

        var cfgOn = Cfg with { Hcfcd = new HcfcdConfig { ImpactVolumesUseCutFactor = true } };
        var on = Created(Sediment);
        Apply(on, Shipped().Bind(Ro(on), Seg, [T25(factor: 1.25)], AreaUnit.Acres, OutputTestKit.Formatter(cfgOn), cfgOn).Changes);
        Assert.Equal(["SEDIMENT", "1,204", "2.41"], on[8]);      // 962.963 × 1.25, ÷ 500
    }

    [Fact]
    public void E811_core_an_unchanged_regen_has_no_changes()
    {
        var cells = Created(Sediment);
        var first = Shipped().Bind(Ro(cells), Seg, [T25()], AreaUnit.Acres, F, Cfg);
        Apply(cells, first.Changes);

        var second = Shipped().Bind(Ro(cells), Seg, [T25()], AreaUnit.Acres, F, Cfg);

        Assert.NotEmpty(second.Bound);
        Assert.Empty(second.Changes);
    }

    [Fact]
    public void E418_area_unit_cell_follows_the_run_area_unit()
    {
        var cells = Created(Sediment);
        Apply(cells, Shipped().Bind(Ro(cells), Seg, [T25()], AreaUnit.SquareFeet, F, Cfg).Changes);
        Assert.Equal(["TOTAL IMPACTED AREA (BELOW OHWM)", "16,000", "SF"], cells[6]);
    }

    [Fact]
    public void Impact_values_for_another_segment_are_refused()
    {
        var cells = Created(Sediment);
        var wrong = new HcfcdMaterial(Sediment.Id, "SEDIMENT", new ImpactResult(Guid.NewGuid(), "SEDIMENT", 1, 1, 1, [], [], false), 1);
        var b = Shipped().Bind(Ro(cells), Seg, [wrong], AreaUnit.Acres, F, Cfg);
        Assert.Equal(VolumeCodes.InvalidSourceValue, b.Problems.Single().Code);
        Assert.Empty(b.Bound);
    }

    [Fact]
    public void A_missing_template_file_is_reported()
    {
        var o = HcfcdTemplate.Load("Templates/does-not-exist.json", OutputFolder());
        Assert.Equal(OutputCodes.TemplateNotFound, o.Problems.Single().Code);
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"name":"x","columns":2,"labelColumn":0,"rows":[["A","{bogus}"]],"bindings":[],"materials":{"headerLabelPrefix":"A","cutVolumeColumn":1,"averageColumn":1}}""", "unknown token")]
    [InlineData("""{"schemaVersion":1,"name":"x","columns":2,"labelColumn":0,"rows":[["A","B","C"]],"bindings":[],"materials":{"headerLabelPrefix":"A","cutVolumeColumn":1,"averageColumn":1}}""", "row 0 has 3 cells")]
    [InlineData("""{"schemaVersion":2,"name":"x","columns":1,"labelColumn":0,"rows":[["A"]]}""", "schemaVersion is 2")]
    [InlineData("""{"schemaVersion":1,"name":"x","columns":3,"labelColumn":0,"rows":[["H","",""],["T","",""]],"merges":[{"row":0,"firstColumn":0,"lastColumn":2},{"row":0,"firstColumn":1,"lastColumn":1}],"bindings":[],"materials":{"headerLabelPrefix":"T","cutVolumeColumn":1,"averageColumn":2}}""", "merges overlap")]
    [InlineData("""{"schemaVersion":1,"name":"x","columns":3,"labelColumn":0,"rows":[["T","",""]],"bindings":[{"quantity":"ImpactedLength","labelPrefix":"NOPE","valueColumn":1}],"materials":{"headerLabelPrefix":"T","cutVolumeColumn":1,"averageColumn":2}}""", "matches 0 template rows")]
    [InlineData("""{"schemaVersion":1,"name":"x","columns":3,"labelColumn":0,"rows":[["T","",""]],"unexpected":true,"materials":{"headerLabelPrefix":"T","cutVolumeColumn":1,"averageColumn":2}}""", "JSON is invalid")]
    public void An_invalid_template_is_refused(string json, string expected)
    {
        var o = HcfcdTemplate.Parse(json);
        Assert.False(o.Succeeded);
        Assert.All(o.Problems, p => Assert.Equal(OutputCodes.TemplateInvalid, p.Code));
        Assert.Contains(o.Problems, p => p.Message.Contains(expected));
    }
}

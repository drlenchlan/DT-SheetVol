using System.Text.Json.Nodes;
using SheetVol.Core.Output;
using SheetVol.Core.Volumes;

namespace SheetVol.Core.Tests.Output;

public class RunPersistenceTests
{
    private static RunDefinition SampleRun()
    {
        var seg1 = Guid.NewGuid();
        var seg2 = Guid.NewGuid();
        return new RunDefinition(
            "Run 1 — Main St", "ALIGN-S", SegmentDriver.Stations,
            "START..20+00@500, 28+00, skip 28+00..31+50, 31+50..END@400", null,
            -100, 100,
            [new AnalysisDef(Guid.NewGuid(), "EG vs FG", "EG", "FG", 1.25, 0.9), new AnalysisDef(Guid.NewGuid(), "SEDIMENT m³", "EG_H", "FG_H")],
            [new ManualSegmentDef(Guid.NewGuid(), "M1", "2A7F", "Sheet 9")],
            [new SegmentDef(seg1, "1", 0, 500, -100, 100), new SegmentDef(seg2, "2", 500, 1300, -100, 100, null, "C-102")],
            new Dictionary<Guid, SegmentOverrides> { [seg2] = new SegmentOverrides("C-102", 40, LockView: true) },
            new ViewOptions(ViewOrientation.North, ScaleMode.FitStandard, 50, [20, 30, 40, 50, 60, 100], ClipMode.None, 25),
            ReportPreset.HcfcdSegment, AreaUnit.Acres,
            new OhwmSpec(100.0, new Dictionary<Guid, double> { [seg1] = 97.0 }),
            AutoMode.All);
    }

    [Fact]
    public void Round_trip_preserves_the_run()
    {
        var run = SampleRun();
        string json = RunPersistence.Serialize(run);

        var loaded = RunPersistence.Deserialize(json);

        Assert.True(loaded.CanModify, string.Join("; ", loaded.Problems.Select(p => p.Message)));
        var back = loaded.Run!;
        Assert.Equal(json, RunPersistence.Serialize(back));
        Assert.Equal(run.Name, back.Name);
        Assert.Equal(run.Analyses[0], back.Analyses[0]);
        Assert.Equal(run.KnownSegments[1], back.KnownSegments[1]);
        Assert.Equal(run.Overrides[run.KnownSegments[1].Id], back.Overrides[run.KnownSegments[1].Id]);
        Assert.Equal(97.0, back.Ohwm!.BySegment[run.KnownSegments[0].Id]);
        Assert.Equal([20.0, 30, 40, 50, 60, 100], back.Views.StandardScales!);
        Assert.Equal(AreaUnit.Acres, back.AreaUnit);
        Assert.Equal(ReportPreset.HcfcdSegment, back.Preset);
        Assert.Equal(1, loaded.StoredSchemaVersion);
    }

    [Fact]
    public void Serialized_json_is_ascii_and_uses_enum_names()
    {
        string json = RunPersistence.Serialize(SampleRun());
        Assert.All(json, c => Assert.True(c < 128, $"non-ASCII character U+{(int)c:X4}"));
        Assert.Contains("\"areaUnit\":\"Acres\"", json);
        Assert.Contains("\"schemaVersion\":1", json);
    }

    [Fact]
    public void Chunks_are_at_most_250_characters_and_rejoin_exactly()
    {
        string json = RunPersistence.Serialize(SampleRun());
        var chunks = RunPersistence.ToChunks(SampleRun() with { Name = "Run 1 — Main St" }, new PersistenceConfig());

        Assert.True(json.Length > 250);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.InRange(c.Length, 1, 250));
        var back = RunPersistence.FromChunks(chunks);
        Assert.True(back.CanModify);
        Assert.Equal("Run 1 — Main St", back.Run!.Name);
    }

    [Fact]
    public void Chunking_never_splits_a_surrogate_pair()
    {
        string text = new string('a', 249) + "\U0001F600" + "tail";   // the pair would straddle 249/250
        var chunks = RunPersistence.Chunk(text, 250);

        Assert.Equal(249, chunks[0].Length);
        Assert.True(char.IsHighSurrogate(chunks[1][0]));
        Assert.Equal(text, string.Concat(chunks));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(251)]
    public void Chunk_length_outside_2_to_250_is_refused(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RunPersistence.Chunk("abc", length));
    }

    [Fact]
    public void E105_a_newer_stored_schema_is_refused_and_both_versions_are_reported()
    {
        var node = JsonNode.Parse(RunPersistence.Serialize(SampleRun()))!;
        node["schemaVersion"] = 3;
        node["someFutureField"] = "x";

        var loaded = RunPersistence.FromChunks(RunPersistence.Chunk(node.ToJsonString()));

        Assert.False(loaded.CanModify);
        Assert.Null(loaded.Run);
        Assert.Equal(3, loaded.StoredSchemaVersion);
        Assert.Equal(RunPersistence.PluginSchemaVersion, loaded.PluginSchemaVersion);
        var p = loaded.Problems.Single();
        Assert.Equal(OutputCodes.StoredSchemaNewer, p.Code);
        Assert.Equal(Severity.Error, p.Severity);
        Assert.Contains("schema version 3", p.Message);
        Assert.Contains("version 1", p.Message);
    }

    [Fact]
    public void Serialize_stamps_the_plugin_schema_version()
    {
        string json = RunPersistence.Serialize(SampleRun() with { SchemaVersion = 0 });
        Assert.Contains("\"schemaVersion\":1", json);
    }

    [Theory]
    [InlineData("""{"name":"x"}""", "no integer 'schemaVersion'")]
    [InlineData("""{"schemaVersion":"1"}""", "no integer 'schemaVersion'")]
    [InlineData("""{"schemaVersion":0}""", "versions start at 1")]
    [InlineData("""[1,2]""", "not a JSON object")]
    [InlineData("""{"schemaVersion":1,""", "malformed")]
    public void Malformed_stored_runs_are_refused(string json, string expected)
    {
        var loaded = RunPersistence.Deserialize(json);
        Assert.False(loaded.CanModify);
        Assert.Null(loaded.Run);
        Assert.Equal(OutputCodes.PersistedJsonInvalid, loaded.Problems.Single().Code);
        Assert.Contains(expected, loaded.Problems.Single().Message);
    }

    [Fact]
    public void A_missing_required_member_is_an_error_not_a_silent_default()
    {
        var node = JsonNode.Parse(RunPersistence.Serialize(SampleRun()))!.AsObject();
        node.Remove("analyses");

        var loaded = RunPersistence.Deserialize(node.ToJsonString());

        Assert.False(loaded.CanModify);
        Assert.Equal(OutputCodes.PersistedJsonInvalid, loaded.Problems.Single().Code);
    }

    [Fact]
    public void A_null_in_a_non_nullable_member_is_an_error()
    {
        var node = JsonNode.Parse(RunPersistence.Serialize(SampleRun()))!.AsObject();
        node["alignmentName"] = null;
        Assert.False(RunPersistence.Deserialize(node.ToJsonString()).CanModify);
    }

    [Fact]
    public void Join_refuses_no_chunks_a_missing_chunk_or_an_oversized_chunk()
    {
        Assert.Equal(OutputCodes.PersistedJsonInvalid, RunPersistence.Join([]).Problems.Single().Code);
        Assert.Equal(OutputCodes.PersistedJsonInvalid, RunPersistence.Join(["{", null, "}"]).Problems.Single().Code);
        Assert.Equal(OutputCodes.PersistedJsonInvalid, RunPersistence.Join([new string('x', 251)]).Problems.Single().Code);
        Assert.Equal("ab", RunPersistence.Join(["a", "b"]).Value);
    }
}

public class ReportConfigTests
{
    [Fact]
    public void Defaults_validate_clean()
    {
        Assert.Empty(ReportConfig.Default.Validate());
    }

    [Fact]
    public void A7_placeholders_are_listed_and_detected_while_in_use()
    {
        Assert.Contains("segmentTable.style.tableStyle", ReportConfig.PlaceholderSettings);
        Assert.Contains("placement.tableAnchorBlock", ReportConfig.PlaceholderSettings);
        Assert.Equal(ReportConfig.PlaceholderSettings, ReportConfig.Default.PlaceholdersInUse());

        var named = ReportConfig.Default with { Placement = new PlacementConfig { TableAnchorBlock = "LAN-TBL-ANCHOR" } };
        Assert.DoesNotContain("placement.tableAnchorBlock", named.PlaceholdersInUse());
        Assert.Contains("volumes.tolerances.volumeZeroTolerance", ReportConfig.AssumedSettings);
    }

    [Fact]
    public void Json_round_trips_and_a_partial_file_keeps_the_other_defaults()
    {
        var back = ReportConfig.FromJson(ReportConfig.Default.ToJson());
        Assert.True(back.Succeeded, string.Join("; ", back.Problems.Select(p => p.Message)));
        Assert.Equal(ReportConfig.Default.ToJson(), back.Value.ToJson());

        var partial = ReportConfig.FromJson("""
            {
              // LAN names arrive
              "segmentTable": { "style": { "tableStyle": "LAN-EARTHWORK", "textHeight": 0.08 } },
              "formats": { "acresDecimals": 3 },
            }
            """);
        Assert.True(partial.Succeeded, string.Join("; ", partial.Problems.Select(p => p.Message)));
        Assert.Equal("LAN-EARTHWORK", partial.Value.SegmentTable.Style.TableStyle);
        Assert.Equal(0.08, partial.Value.SegmentTable.Style.TextHeight);
        Assert.Equal("SV-TABLE", partial.Value.SegmentTable.Style.Layer);
        Assert.Equal(3, partial.Value.AreaDecimals(AreaUnit.Acres));
        Assert.Equal(27.0, partial.Value.Volumes.Constants.CubicFeetPerCubicYard);
    }

    [Theory]
    [InlineData("""{ "segmentTable": { "styel": {} } }""")]
    [InlineData("""{ "formats": { "tableCultureName": null } }""")]
    [InlineData("""not json""")]
    public void Unknown_keys_nulls_and_bad_json_are_rejected_not_ignored(string json)
    {
        var o = ReportConfig.FromJson(json);
        Assert.False(o.Succeeded);
        Assert.Equal(OutputCodes.ConfigInvalid, o.Problems.Single().Code);
    }

    [Fact]
    public void Out_of_range_values_are_each_reported()
    {
        var bad = ReportConfig.Default with
        {
            Volumes = new VolumeConfig { Tolerances = new VolumeTolerances { VolumeZeroTolerance = -1 } },
            Formats = new FormatConfig { AcresDecimals = 11, TableCultureName = "xx-NOPE-zz", NetCellTemplate = "{0} {3}" },
            Persistence = new PersistenceConfig { ChunkLength = 300 },
            MasterTable = new MasterTableConfig { Style = new TableStyleSettings { Layer = " ", TextHeight = 0 } },
        };

        var problems = bad.Validate();

        Assert.All(problems, p => Assert.Equal(OutputCodes.ConfigInvalid, p.Code));
        foreach (var path in new[]
                 {
                     "volumes.tolerances.volumeZeroTolerance", "formats.acresDecimals", "formats.tableCultureName",
                     "formats.netCellTemplate", "persistence.chunkLength", "masterTable.style.layer", "masterTable.style.textHeight",
                 })
            Assert.Contains(problems, p => p.Message.StartsWith("Config " + path));
    }

    [Fact]
    public void Builders_refuse_an_invalid_config()
    {
        var r = Volumes_T2();
        var bad = ReportConfig.Default with { Formats = new FormatConfig { VolumeDecimals = -1 } };
        var o = MasterTableBuilder.Build(r, AreaUnit.SquareFeet, Tests.Volumes.Scenario.Station, bad);
        Assert.Equal(OutputCodes.ConfigInvalid, o.Problems.Single().Code);
    }

    private static ReconcileResult Volumes_T2() => Tests.Volumes.Scenario.T2().Reconcile();
}

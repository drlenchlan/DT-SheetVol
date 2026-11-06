using System.Globalization;
using SheetVol.Core.Tests.Fakes;
using SheetVol.Core.Tracking;

namespace SheetVol.Core.Tests.Tracking;

/// <summary>Run fingerprints per 6.11, and T14's fingerprint behavior.</summary>
public class FingerprintTests
{
    private static readonly Guid AnalysisId = Guid.Parse("0a0a0a0a-0000-0000-0000-000000000001");
    private static readonly Guid ManualId = Guid.Parse("0b0b0b0b-0000-0000-0000-000000000001");
    private static readonly Guid SegA = Guid.Parse("0c0c0c0c-0000-0000-0000-000000000001");
    private static readonly Guid SegB = Guid.Parse("0c0c0c0c-0000-0000-0000-000000000002");

    private static RunDefinition BaseRun() => new(
        "Run1", "S", SegmentDriver.Stations, "START..END@500", null, -100, 100,
        [new AnalysisDef(AnalysisId, "EG vs FG", "EG", "FG_A")],
        [new ManualSegmentDef(ManualId, "M1", "2F")],
        [],
        new Dictionary<Guid, SegmentOverrides>(),
        new ViewOptions());

    private static FakeAlignment AlignmentS(double length = 2000) => FakeAlignments.Straight(new Pt(0, 0), 0, length);

    private static FrameGeom Frame(double start = 0, double end = 500, double rotation = 0)
        => new("F1", "VF - 1", start, end, new Pt(250, 0), rotation, 600, 250, null, null);

    private static IReadOnlyList<PolyVertex> ManualPoly(double x = 0) =>
        [new(new Pt(x, 0), 0), new(new Pt(x + 100, 0), 0), new(new Pt(x + 100, 50), 0), new(new Pt(x, 50), 0)];

    private static FingerprintInput Input(
        RunDefinition? run = null, IAlignmentGeometry? alignment = null, FrameGeom? frame = null,
        AnalysisSurfaceState? surface = null, IReadOnlyList<PolyVertex>? manual = null)
        => new(
            run ?? BaseRun(),
            alignment ?? AlignmentS(),
            [frame ?? Frame()],
            [surface ?? new AnalysisSurfaceState(AnalysisId, false, new RawVolume(200, 200))],
            new Dictionary<Guid, IReadOnlyList<PolyVertex>> { [ManualId] = manual ?? ManualPoly() });

    [Fact]
    public void Same_inputs_give_the_same_fingerprint()
    {
        RunFingerprint a = Fingerprints.Compute(Input());
        RunFingerprint b = Fingerprints.Compute(Input());     // fresh objects with equal values
        Assert.Equal(a, b);
        Assert.False(Fingerprints.Compare(a, b).IsStale);
        Assert.Matches("^[0-9a-f]{64}$", a.Value);
    }

    public static TheoryData<string, string> Changes() => new()
    {
        { "alignment lengthened", Fingerprints.AlignmentComponent },
        { "alignment moved 0.001", Fingerprints.AlignmentComponent },
        { "arc radius", Fingerprints.AlignmentComponent },
        { "break spec", Fingerprints.DefinitionComponent },
        { "offset left", Fingerprints.DefinitionComponent },
        { "offset right", Fingerprints.DefinitionComponent },
        { "cut factor", Fingerprints.DefinitionComponent },
        { "comparison surface", Fingerprints.DefinitionComponent },
        { "view scale", Fingerprints.DefinitionComponent },
        { "segment override", Fingerprints.DefinitionComponent },
        { "segment offset override", Fingerprints.DefinitionComponent },
        { "draw options", Fingerprints.DefinitionComponent },
        { "volume surface set", Fingerprints.DefinitionComponent },       // rev 3: presence
        { "volume surface renamed", Fingerprints.DefinitionComponent },   // rev 3: name
        { "frame station", Fingerprints.FramesComponent },
        { "frame rotation", Fingerprints.FramesComponent },
        { "surface out of date", Fingerprints.SurfacesComponent },
        { "surface totals", Fingerprints.SurfacesComponent },
        { "manual polyline", Fingerprints.ManualSegmentsComponent },
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public void Any_input_change_gives_a_different_fingerprint_and_names_the_component(string change, string component)
    {
        RunDefinition run = BaseRun();
        FingerprintInput changed = change switch
        {
            "alignment lengthened" => Input(alignment: AlignmentS(2500)),
            "alignment moved 0.001" => Input(alignment: FakeAlignments.Straight(new Pt(0.001, 0), 0, 2000)),
            "arc radius" => Input(alignment: new AlignmentBuilder(new Pt(0, 0), 0).Line(1000).ArcByLength(3000.0001, 1000, clockwise: false).Build("S")),
            "break spec" => Input(run with { BreakSpec = "START, 5+00, 13+00, 18+00, END" }),
            "offset left" => Input(run with { OffsetLeft = -99 }),
            "offset right" => Input(run with { OffsetRight = 101 }),
            "cut factor" => Input(run with { Analyses = [run.Analyses[0] with { CutFactor = 1.25 }] }),
            "comparison surface" => Input(run with { Analyses = [run.Analyses[0] with { ComparisonSurface = "FG_B" }] }),
            "view scale" => Input(run with { Views = run.Views with { ModelUnitsPerPaperUnit = 40 } }),
            "segment override" => Input(run with { Overrides = new Dictionary<Guid, SegmentOverrides> { [SegA] = new(LockView: true) } }),
            "segment offset override" => Input(run with { Overrides = new Dictionary<Guid, SegmentOverrides> { [SegA] = new(OffsetRight: 250) } }),
            "draw options" => Input(run with { Draw = new DrawOptions(BoundaryPolylines: false) }),
            "volume surface set" => Input(run with { Analyses = [run.Analyses[0] with { VolumeSurface = "VOL1" }] }),
            "volume surface renamed" => Input(run with { Analyses = [run.Analyses[0] with { VolumeSurface = "VOL2" }] }),
            "frame station" => Input(frame: Frame(end: 500.01)),
            "frame rotation" => Input(frame: Frame(rotation: 0.0001)),
            "surface out of date" => Input(surface: new AnalysisSurfaceState(AnalysisId, true, new RawVolume(200, 200))),
            "surface totals" => Input(surface: new AnalysisSurfaceState(AnalysisId, false, new RawVolume(200, 200.5))),
            "manual polyline" => Input(manual: ManualPoly(0.01)),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        FingerprintInput baseline = change switch
        {
            "arc radius" => Input(alignment: new AlignmentBuilder(new Pt(0, 0), 0).Line(1000).ArcByLength(3000, 1000, clockwise: false).Build("S")),
            "volume surface renamed" => Input(run with { Analyses = [run.Analyses[0] with { VolumeSurface = "VOL1" }] }),
            _ => Input(),
        };

        RunFingerprint before = Fingerprints.Compute(baseline);
        RunFingerprint after = Fingerprints.Compute(changed);
        FingerprintComparison cmp = Fingerprints.Compare(before, after);

        Assert.NotEqual(before.Value, after.Value);
        Assert.True(cmp.IsStale);
        Assert.Equal([component], cmp.ChangedComponents);
    }

    [Fact]
    public void Auto_mode_and_known_segments_do_not_change_the_fingerprint()
    {
        RunDefinition run = BaseRun();
        RunFingerprint a = Fingerprints.Compute(Input(run));
        RunFingerprint b = Fingerprints.Compute(Input(run with
        {
            AutoMode = AutoMode.All,
            KnownSegments = [new SegmentDef(SegA, "1", 0, 500, -100, 100)],
        }));
        Assert.Equal(a.Value, b.Value);
    }

    [Fact]
    public void Override_insertion_order_does_not_matter()
    {
        var first = new Dictionary<Guid, SegmentOverrides> { [SegA] = new(TargetLayout: "L1"), [SegB] = new(ModelUnitsPerPaperUnit: 40) };
        var second = new Dictionary<Guid, SegmentOverrides> { [SegB] = new(ModelUnitsPerPaperUnit: 40), [SegA] = new(TargetLayout: "L1") };
        Assert.Equal(
            Fingerprints.Compute(Input(BaseRun() with { Overrides = first })).Value,
            Fingerprints.Compute(Input(BaseRun() with { Overrides = second })).Value);
    }

    [Fact]
    public void T14_fingerprint_differs_after_lengthening_and_is_equal_after_regen()
    {
        // Regen on Fixture A's alignment stores the fingerprint of the sources it used.
        RunFingerprint stored = Fingerprints.Compute(Input(alignment: AlignmentS(2000)));

        // The alignment is lengthened to 2500: the run is stale before regen.
        RunFingerprint current = Fingerprints.Compute(Input(alignment: AlignmentS(2500)));
        FingerprintComparison before = Fingerprints.Compare(stored, current);
        Assert.True(before.IsStale);
        Assert.Equal([Fingerprints.AlignmentComponent], before.ChangedComponents);

        // Regen stores the new fingerprint; reading the same sources again compares equal.
        stored = current;
        RunFingerprint reread = Fingerprints.Compute(Input(alignment: AlignmentS(2500)));
        Assert.False(Fingerprints.Compare(stored, reread).IsStale);
        Assert.Equal(stored, reread);
    }

    [Fact]
    public void No_stored_fingerprint_is_stale_with_every_component()
    {
        FingerprintComparison cmp = Fingerprints.Compare(null, Fingerprints.Compute(Input()));
        Assert.True(cmp.IsStale);
        Assert.Equal(5, cmp.ChangedComponents.Count);
    }

    [Fact]
    public void A_different_format_version_is_stale()
    {
        RunFingerprint current = Fingerprints.Compute(Input());
        FingerprintComparison cmp = Fingerprints.Compare(current with { Format = "SVFP0" }, current);
        Assert.True(cmp.IsStale);
        Assert.Equal([Fingerprints.FormatComponent], cmp.ChangedComponents);
    }

    [Fact]
    public void Fingerprint_is_culture_independent()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            string invariant = Fingerprints.Compute(Input()).Value;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            string german = Fingerprints.Compute(Input()).Value;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            string arabic = Fingerprints.Compute(Input()).Value;
            Assert.Equal(invariant, german);
            Assert.Equal(invariant, arabic);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Canonical_text_is_tagged_length_prefixed_and_invariant()
    {
        string text = new CanonicalWriter()
            .Section("x").Add("a;b").Add(1.5).Add(-0.0).Add(0.1 + 0.2).Add(true).Add(3).Add((string?)null)
            .Add(Guid.Parse("0A0A0A0A-0000-0000-0000-000000000001")).AddEnum(AutoMode.Views)
            .Text;
        Assert.Equal("#1:x;s3:a;b;d1.5;d0;d0.30000000000000004;b1;i3;n;g0a0a0a0a-0000-0000-0000-000000000001;e5:Views;", text);

        // SHA-256 of "abc" (FIPS 180-2 test vector).
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", CanonicalWriter.Hash("abc"));
    }

    // ---- Contract rev 2: draw options and per-segment offset overrides --------------------

    private static RunDefinition WithOverride(SegmentOverrides o) =>
        BaseRun() with { Overrides = new Dictionary<Guid, SegmentOverrides> { [SegA] = o } };

    [Fact]
    public void Draw_null_and_all_on_give_the_same_fingerprint()
    {
        Assert.Equal(
            Fingerprints.Compute(Input(BaseRun() with { Draw = null })).Value,
            Fingerprints.Compute(Input(BaseRun() with { Draw = new DrawOptions(true, true, true) })).Value);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Changing_only_Draw_changes_the_definition_only(bool boundaryPolylines, bool tableAtEachSegment, bool summaryTable)
    {
        RunFingerprint before = Fingerprints.Compute(Input());
        RunFingerprint after = Fingerprints.Compute(Input(BaseRun() with { Draw = new DrawOptions(boundaryPolylines, tableAtEachSegment, summaryTable) }));
        Assert.NotEqual(before.Value, after.Value);
        Assert.Equal([Fingerprints.DefinitionComponent], Fingerprints.Compare(before, after).ChangedComponents);
    }

    [Theory]
    [InlineData("target layout")]
    [InlineData("scale")]
    [InlineData("lock view")]
    [InlineData("offset left")]
    [InlineData("offset right")]
    [InlineData("offset left cleared")]
    [InlineData("offset right zero")]
    public void Changing_only_one_override_field_changes_the_definition_only(string field)
    {
        var baseline = new SegmentOverrides("L1", 40, false, -80, null);
        SegmentOverrides changed = field switch
        {
            "target layout" => baseline with { TargetLayout = "L2" },
            "scale" => baseline with { ModelUnitsPerPaperUnit = 30 },
            "lock view" => baseline with { LockView = true },
            "offset left" => baseline with { OffsetLeft = -81 },
            "offset right" => baseline with { OffsetRight = 120 },
            "offset left cleared" => baseline with { OffsetLeft = null },
            "offset right zero" => baseline with { OffsetRight = 0 },   // 0 is a value, not "not set"
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        RunFingerprint before = Fingerprints.Compute(Input(WithOverride(baseline)));
        RunFingerprint after = Fingerprints.Compute(Input(WithOverride(changed)));
        Assert.NotEqual(before.Value, after.Value);
        Assert.Equal([Fingerprints.DefinitionComponent], Fingerprints.Compare(before, after).ChangedComponents);
    }

    [Fact]
    public void Same_draw_options_and_offset_overrides_give_the_same_fingerprint_in_any_order()
    {
        RunDefinition Run(bool reversed)
        {
            var a = new KeyValuePair<Guid, SegmentOverrides>(SegA, new SegmentOverrides("L1", OffsetLeft: -60));
            var b = new KeyValuePair<Guid, SegmentOverrides>(SegB, new SegmentOverrides(OffsetRight: 120));
            return BaseRun() with
            {
                Draw = new DrawOptions(SummaryTable: false),
                Overrides = new Dictionary<Guid, SegmentOverrides>(reversed ? new[] { b, a } : new[] { a, b }),
            };
        }
        RunFingerprint first = Fingerprints.Compute(Input(Run(reversed: false)));
        Assert.Equal(first, Fingerprints.Compute(Input(Run(reversed: false))));
        Assert.Equal(first, Fingerprints.Compute(Input(Run(reversed: true))));
        Assert.False(Fingerprints.Compare(first, Fingerprints.Compute(Input(Run(reversed: true)))).IsStale);
    }

    [Fact]
    public void Fingerprint_is_stable_across_runs()
    {
        // Pinned values. If the canonical text changes on purpose, bump Fingerprints.FormatVersion
        // and re-pin; an accidental change fails here. Re-pinned at SVFP2 (contract rev 2): the
        // definition signature gained Draw and the per-segment offset overrides. Re-pinned at SVFP3
        // (contract rev 3): the ohwm section is gone and each analysis adds its VolumeSurface presence
        // and name; the baseline's default SchemaVersion is now 3.
        Assert.Equal(Fingerprints.FormatVersion, Fingerprints.Compute(Input()).Format);
        Assert.Equal(PinnedBaselineValue, Fingerprints.Compute(Input()).Value);

        // RunDefinition.SchemaVersion is hashed: the schema 1 input has its own value.
        Assert.Equal(PinnedSchema1Value, Fingerprints.Compute(Input(BaseRun() with { SchemaVersion = 1 })).Value);

        // The rev 2 fields themselves: one draw option off and an override with every field set.
        RunDefinition rev2 = WithOverride(new SegmentOverrides("L1", 40, true, -80, 120)) with { Draw = new DrawOptions(TableAtEachSegment: false) };
        Assert.Equal(PinnedRev2FieldsValue, Fingerprints.Compute(Input(rev2)).Value);
    }

    private const string PinnedBaselineValue = "5eab423dff27c3fb50fe64873ee942e006d998cf908152b87b09b0f18042e73d";
    private const string PinnedSchema1Value = "01061d44d365b2019e386cdee63b2a2d4553e181263957765b79543e234510a1";
    private const string PinnedRev2FieldsValue = "d7a084e0ad48562b7685ff3c3cd2c97d4958ca1d4b1442f522534c35ef0f7163";
}

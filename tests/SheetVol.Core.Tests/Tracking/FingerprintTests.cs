using System.Globalization;
using SheetVol.Core.Tests.Views;
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

    private static C3Alignment AlignmentS(double length = 2000) => C3Alignment.Straight(new Pt(0, 0), 0, length);

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
            "alignment moved 0.001" => Input(alignment: C3Alignment.Straight(new Pt(0.001, 0), 0, 2000)),
            "arc radius" => Input(alignment: C3Alignment.Build("S", new Pt(0, 0), 0, 0, new C3Piece(1000), new C3Piece(1000, 3000.0001))),
            "break spec" => Input(run with { BreakSpec = "START, 5+00, 13+00, 18+00, END" }),
            "offset left" => Input(run with { OffsetLeft = -99 }),
            "offset right" => Input(run with { OffsetRight = 101 }),
            "cut factor" => Input(run with { Analyses = [run.Analyses[0] with { CutFactor = 1.25 }] }),
            "comparison surface" => Input(run with { Analyses = [run.Analyses[0] with { ComparisonSurface = "FG_B" }] }),
            "view scale" => Input(run with { Views = run.Views with { ModelUnitsPerPaperUnit = 40 } }),
            "segment override" => Input(run with { Overrides = new Dictionary<Guid, SegmentOverrides> { [SegA] = new(LockView: true) } }),
            "frame station" => Input(frame: Frame(end: 500.01)),
            "frame rotation" => Input(frame: Frame(rotation: 0.0001)),
            "surface out of date" => Input(surface: new AnalysisSurfaceState(AnalysisId, true, new RawVolume(200, 200))),
            "surface totals" => Input(surface: new AnalysisSurfaceState(AnalysisId, false, new RawVolume(200, 200.5))),
            "manual polyline" => Input(manual: ManualPoly(0.01)),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        FingerprintInput baseline = change == "arc radius"
            ? Input(alignment: C3Alignment.Build("S", new Pt(0, 0), 0, 0, new C3Piece(1000), new C3Piece(1000, 3000)))
            : Input();

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

    [Fact]
    public void Fingerprint_is_stable_across_runs()
    {
        // Pinned value for the baseline input. If the canonical text changes on purpose,
        // bump Fingerprints.FormatVersion and re-pin; an accidental change fails here.
        Assert.Equal(PinnedBaselineValue, Fingerprints.Compute(Input()).Value);
    }

    private const string PinnedBaselineValue = "386de603b99a304a690de3c21f91d6e040deaafa123625409612436c67d211f8";
}

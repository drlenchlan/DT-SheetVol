// T25 to T28, T30 and the E10xx rows marked U, on generated triangles (no Civil 3D).
//
// Fixture H geometry used in every derivation (section 8):
//   EG_H(y): 96 for |y| <= 10; 96 + (|y| - 10)/3 for 10..40; 106 beyond.
//   FG_H(y), x <= 1300: 94 for |y| <= 10; 94 + 2(|y| - 10)/3 for 10..16; EG_H beyond. EG_H for x >= 1310.
//   Depth d = EG - FG: 2 on the bed, 2 - (|y| - 10)/3 on 10..16 (0 at 16). Full section 52 ft2, 32 ft wide.
// Units: ft, ft2, ft3. 1 CY = 27 ft3. HCFCD display: length whole ft, average to 2 decimals (A12).
//
// Rev 3 (NOW.md X5, rule 6): OHWM is a surface, the analysis's base; there is no OHWM elevation and
// no base < OHWM clip. Impact cut is d = base - comparison > MinCutDepth. T25 to T30 run with Fixture
// H's flat OHWM surface at the stated elevation z as the base and FG_H as the comparison, so every
// expected value is OHWM minus design, d = z - FG_H. With FG_H (x <= 1300) 94 on the bed (|y| <= 10),
// 94 + 2(|y| - 10)/3 on 10..16 (98 at 16), then EG_H's 3:1 slope 96 + (|y| - 10)/3 (100 at 22):
//   z = 100 -> cut |y| < 22, width 44; section: bed 20 * 6 = 120, each side 10..16 (6 + 2)/2 * 6 = 24
//              and 16..22 2 * 6/2 = 6, so 120 + 2 * 30 = 180 ft2. Past the transition (x >= 1310,
//              FG_H = EG_H) still |y| < 22: bed 20 * 4 = 80 + 2 * (4 * 12/2) = 128 ft2.
//   z = 97  -> cut |y| < 14.5, width 29; section 20 * 3 + 2 * (3 * 4.5/2) = 73.5 ft2.
//   z = 95  -> cut |y| < 11.5, width 23; section 20 * 1 + 2 * (1 * 1.5/2) = 21.5 ft2. Past the
//              transition FG_H >= 96 > 95: no cut (the E1001 case).
//   z = 94  -> equal to the FG_H bed and below FG_H everywhere else: no cut.
// Domain note (NOW.md rule 6): a flat OHWM counts the water column above the existing bed as cut.
// Robustness and E10xx rows that are not HCFCD rows keep EG_H vs FG_H.

using System.Diagnostics;
using SheetVol.Core.Tests.Fakes;
using SheetVol.Core.Volumes.Integrator;
using SheetVol.Core.Volumes.Integrator.Fixtures;
using Xunit.Abstractions;

namespace SheetVol.Core.Tests.Integrator;

public class ImpactTests(ITestOutputHelper output)
{
    private const double Rel = 1e-9;
    private static readonly FakeAlignment S = FakeAlignments.S();

    private static SegmentImpactResult RunH(double s0, double s1, double offL = -100, double offR = 100,
                                            IntegratorSettings? settings = null)
    {
        var seg = C5.Seg(s0, s1, offL, offR);
        return OverlayIntegrator.IntegrateSegment(FixtureSurfaces.EgH(), FixtureSurfaces.FgH(), S, seg,
            FakeBands.Band(S, s0, s1, offL, offR), "SEDIMENT", settings);
    }

    /// <summary>Fixture H's OHWM surface (rev 3): flat at <paramref name="z"/> over H's extent (x 0..2000, y -100..100), two triangles.</summary>
    internal static TriangleListSource FlatBase(double z) => FixtureSurfaces.Flat("OHWM", z, 0, -100, 2000, 100);

    /// <summary>An HCFCD analysis on Fixture H: base the OHWM surface at <paramref name="z"/>, comparison FG_H.</summary>
    private static SegmentImpactResult RunFlat(double s0, double s1, double z, double offL = -100, double offR = 100)
        => OverlayIntegrator.IntegrateSegment(FlatBase(z), FixtureSurfaces.FgH(), S, C5.Seg(s0, s1, offL, offR),
            FakeBands.Band(S, s0, s1, offL, offR), "SEDIMENT");

    private static ImpactResult Ok(SegmentImpactResult r)
    {
        Assert.True(r.Succeeded, string.Join("; ", r.Problems.Select(p => p.Code + " " + p.Message)));
        return r.Impact!;
    }

    private static double Cy(double ft3) => ft3 / C5.CubicFeetPerCubicYard;

    // ---------------------------------------------------------------------------------------
    // T25: OHWM_100 (flat 100) vs FG_H. d = 100 - FG_H > 0 for |y| < 22 over the whole segment:
    // area 44 * 500 = 22,000 ft2 (0.505 ac -> 0.51), volume 180 * 500 = 90,000 ft3 = 3,333.333 CY,
    // length 500, average 3,333.333 / 500 = 6.666667 -> 6.67. Pre-rev-3 (EG_H clipped at 100):
    // 16,000 / 26,000. Band cut equals impact cut (MinCutDepth 0); the flat base covers the band.
    // Outline: one ring, the rectangle [s0, s1] x [-22, 22] (4 vertices plus the closing point).
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 500)]
    [InlineData(500, 1000)]
    public void T25_OHWM_100(double s0, double s1)
    {
        var r = RunFlat(s0, s1, 100.0);
        var i = Ok(r);
        C5.Rel(500, i.ImpactedLength, Rel, what: "length");
        C5.Rel(22000, i.ImpactedArea, Rel, what: "area");
        C5.Rel(90000, i.CutVolume, Rel, what: "volume");
        Assert.Equal(3333.333, Cy(i.CutVolume), 3);
        Assert.Equal(0.51, Math.Round(i.ImpactedArea / 43560.0, 2));
        Assert.Equal(6.67, Math.Round(Cy(r.AveragePerLength!.Value), 2));
        var iv = Assert.Single(i.Intervals);
        C5.Rel(s0, iv.From, Rel); C5.Rel(s1, iv.To, Rel);
        Assert.False(i.TouchesSearchLimit);
        C5.Rel(r.Band!.Cut, i.CutVolume, Rel, what: "band cut = impact cut");
        C5.Rel(500 * 200, r.BaseArea, Rel, what: "base area");
        Assert.True(r.HasBase);
        var ring = Assert.Single(i.Outlines);
        Assert.Equal(5, ring.Count);
        C5.Rel(22000, OutlineTests.RingArea(ring), Rel, what: "ring area");
        Assert.Empty(r.Problems);
    }

    // ---------------------------------------------------------------------------------------
    // T26: OHWM_97 vs FG_H. d = 97 - FG_H > 0 for |y| < 14.5 (FG_H = 94 + 2(|y| - 10)/3 = 97 there):
    // area 29 * 500 = 14,500 ft2 (0.333 ac -> 0.33); section 73.5 ft2 -> 36,750 ft3 = 1,361.111 CY;
    // average 2.722222 -> 2.72. Pre-rev-3 (EG_H clipped at 97): 13,000 / 24,500. E1003 is retired:
    // there is no cut above the base, so band cut = impact cut.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 500)]
    [InlineData(500, 1000)]
    public void T26_OHWM_97(double s0, double s1)
    {
        var r = RunFlat(s0, s1, 97.0);
        var i = Ok(r);
        C5.Rel(500, i.ImpactedLength, Rel, what: "length");
        C5.Rel(14500, i.ImpactedArea, Rel, what: "area");
        C5.Rel(36750, i.CutVolume, Rel, what: "volume");
        Assert.Equal(1361.111, Cy(i.CutVolume), 3);
        Assert.Equal(0.33, Math.Round(i.ImpactedArea / 43560.0, 2));
        Assert.Equal(2.72, Math.Round(Cy(r.AveragePerLength!.Value), 2));
        C5.Rel(r.Band!.Cut, i.CutVolume, Rel, what: "band cut = impact cut");
        C5.Rel(14500, OutlineTests.RingArea(Assert.Single(i.Outlines)), Rel, what: "ring area");
    }

    // ---------------------------------------------------------------------------------------
    // T27: OHWM_95 vs FG_H. Pre-rev-3 OHWM 95 sat below the EG_H bed (96): zeros. As OHWM minus
    // design it sits 1 ft above the FG_H sediment bed (94): d = 95 - FG_H > 0 for |y| < 11.5
    // (94 + 2(|y| - 10)/3 = 95). Area 23 * 500 = 11,500 ft2 (0.264 ac -> 0.26); section 21.5 ft2 ->
    // 10,750 ft3 = 398.148 CY; average 0.796296 -> 0.80.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 500)]
    [InlineData(500, 1000)]
    public void T27_OHWM_95(double s0, double s1)
    {
        var r = RunFlat(s0, s1, 95.0);
        var i = Ok(r);
        C5.Rel(500, i.ImpactedLength, Rel, what: "length");
        C5.Rel(11500, i.ImpactedArea, Rel, what: "area");
        C5.Rel(10750, i.CutVolume, Rel, what: "volume");
        Assert.Equal(398.148, Cy(i.CutVolume), 3);
        Assert.Equal(0.26, Math.Round(i.ImpactedArea / 43560.0, 2));
        Assert.Equal(0.80, Math.Round(Cy(r.AveragePerLength!.Value), 2));
        C5.Rel(r.Band!.Cut, i.CutVolume, Rel, what: "band cut = impact cut");
    }

    // ---------------------------------------------------------------------------------------
    // E1001 ("no cut in the segment", rev 3): OHWM_95 on [1500, 2000], past the transition, where
    // FG_H = EG_H >= 96 > 95: d <= -1 everywhere. Zeros, N/A, no interval, no outline, one SV-E1001
    // note; the base still covers the band (100,000 ft2), and band cut is 0. The flat 94 base (equal
    // to the FG_H bed, d exactly 0 there) is the same case on [0, 500].
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1500, 2000, 95.0)]
    [InlineData(0, 500, 94.0)]
    public void E1001_no_cut_in_the_segment(double s0, double s1, double z)
    {
        var r = RunFlat(s0, s1, z);
        var i = Ok(r);
        Assert.Equal((0.0, 0.0, 0.0), (i.ImpactedLength, i.ImpactedArea, i.CutVolume));
        Assert.Null(r.AveragePerLength);
        Assert.Empty(i.Intervals);
        Assert.Empty(i.Outlines);
        Assert.True(r.HasBase);
        C5.Rel(500 * 200, r.BaseArea, Rel, what: "base area");
        var p = Assert.Single(r.Problems);
        Assert.Equal((IntegratorCodes.NoCut, Severity.Info), (p.Code, p.Severity));
        Assert.Equal(0.0, r.Band!.Cut);
    }

    // ---------------------------------------------------------------------------------------
    // T28: OHWM_100 vs FG_H, segment [1000, 1500]. Pre-rev-3 (EG_H clipped at 100) the cut ended in
    // the 1300-1310 transition (310 ft, 15,880 ft3). As OHWM minus design the cut continues past it:
    // FG_H = EG_H < 100 for |y| < 22 there, so the impacted length is the whole 500.
    // Area: width 44 everywhere (the transition's 10..16 cells have corner depths 6, 4, 2, 2, all
    // positive), 44 * 500 = 22,000 ft2.
    // Volume: 300 * 180 (x 1000..1300) = 54,000; transition 1300..1310 (depth corners at x = 1300 /
    // 1310: bed 6 / 4, |y| = 10 6 / 4, |y| = 16 2 / 2, |y| = 40 -6 / -6): bed cells planar, mean 5 *
    // 200 = 1,000; 16..40 cells planar, positive part 2 * 6/2 * 10 * 2 = 120; 10..16 cells per side,
    // diagonal (1300, 10)-(1310, 16) (FixtureSurfaces.FgH): (6, 4, 2) * 30 / 3 = 120 + (6, 2, 2) * 30 / 3
    // = 100 -> 220, both sides 440; transition 1,560 (the other diagonal gives 200 per side, 1,520);
    // 190 * 128 (x 1310..1500) = 24,320. Total 79,880 ft3 = 2,958.519 CY.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void T28_transition_segment()
    {
        var r = RunFlat(1000, 1500, 100.0);
        var i = Ok(r);
        C5.Rel(500, i.ImpactedLength, Rel, what: "length");
        C5.Rel(79880, i.CutVolume, Rel, what: "volume");
        Assert.Equal(2958.519, Cy(i.CutVolume), 3);
        C5.Rel(22000, i.ImpactedArea, Rel, what: "area");
        var iv = Assert.Single(i.Intervals);
        C5.Rel(1000, iv.From, Rel); C5.Rel(1500, iv.To, Rel);
        C5.Rel(22000, OutlineTests.RingArea(Assert.Single(i.Outlines)), Rel, what: "ring area");
    }

    // ---------------------------------------------------------------------------------------
    // T30 / E1002: OHWM_100 vs FG_H, offsets (-12, +12). The cut (|y| < 22) reaches the lateral edges:
    // d at |y| = 12 is 100 - (94 + 4/3) = 4.667. Flagged, values still shown. Area 24 * 500 = 12,000;
    // volume = 500 * (bed 120 + 2 * integral_10^12 (6 - 2(y - 10)/3) dy) = 500 * (120 + 2 * (12 - 4/3))
    // = 70,666.667 ft3. Control: offsets +-100 put the edges on d = -6: no flag. The cut ends at
    // |y| = 22, so (-23, 23) does not flag (d = -1/3 at 23) and (-21.9, 23) does (d = +1/30 at 21.9).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void T30_E1002_search_limit_flag()
    {
        var r = RunFlat(0, 500, 100.0, -12, 12);
        var i = Ok(r);
        Assert.True(i.TouchesSearchLimit);
        var p = Assert.Single(r.Problems);
        Assert.Equal((IntegratorCodes.SearchLimit, Severity.Warning), (p.Code, p.Severity));
        C5.Rel(12000, i.ImpactedArea, Rel);
        C5.Rel(500 * (120 + 2 * (12 - 4.0 / 3)), i.CutVolume, Rel);

        Assert.False(Ok(RunFlat(0, 500, 100.0)).TouchesSearchLimit);
        Assert.False(Ok(RunFlat(0, 500, 100.0, -23, 23)).TouchesSearchLimit);
        Assert.True(Ok(RunFlat(0, 500, 100.0, -21.9, 23)).TouchesSearchLimit);
    }

    // ---------------------------------------------------------------------------------------
    // E1004 is retired (7.10 rev 3): the comparison above the base is the band's fill, exported as
    // the analysis's fill and never counted. Swapped surfaces (base FG_H, comparison EG_H): d <= 0
    // everywhere, band fill 26,000 ft3 per 500 ft, impact zeros, N/A, SV-E1001.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Comparison_above_base_is_band_fill_never_impact()
    {
        var seg = C5.Seg(0, 500);
        var r = OverlayIntegrator.IntegrateSegment(FixtureSurfaces.FgH(), FixtureSurfaces.EgH(), S, seg,
            FakeBands.Band(S, 0, 500, -100, 100), "SEDIMENT");
        var i = Ok(r);
        Assert.Equal((0.0, 0.0, 0.0), (i.ImpactedLength, i.ImpactedArea, i.CutVolume));
        C5.Rel(26000, r.Band!.Fill, Rel);
        Assert.True(r.HasBase);
        Assert.Null(r.AveragePerLength);
        Assert.Contains(r.Problems, p => p.Code == IntegratorCodes.NoCut && p.Severity == Severity.Info);
    }

    // ---------------------------------------------------------------------------------------
    // E1005 / E1007 gap: two cut stretches (C5Fixtures.TwoStretches), depth > 0 on (90, 310) and
    // (590, 810), gap 280. Length 220 + 220 = 440, volume 2 * 10,920 = 21,840 ft3.
    // GapTolerance 300 bridges (one interval 90..810, 720); 280 does not ("shorter than").
    // Rev 3: no OHWM argument; the old OHWM 100 sat above every cut node (base 96..98), so values hold.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void E1005_two_stretches_are_summed_and_both_listed()
    {
        var (b, c) = C5Fixtures.TwoStretches();
        var seg = C5.Seg(0, 1000);
        var bnd = FakeBands.Band(S, 0, 1000, -100, 100);
        var i = Ok(OverlayIntegrator.IntegrateSegment(b, c, S, seg, bnd, "SEDIMENT"));
        Assert.Equal(2, i.Intervals.Count);
        C5.Rel(90, i.Intervals[0].From, Rel); C5.Rel(310, i.Intervals[0].To, Rel);
        C5.Rel(590, i.Intervals[1].From, Rel); C5.Rel(810, i.Intervals[1].To, Rel);
        C5.Rel(440, i.ImpactedLength, Rel);
        C5.Rel(21840, i.CutVolume, Rel);

        var bridged = Ok(OverlayIntegrator.IntegrateSegment(b, c, S, seg, bnd, "SEDIMENT", new IntegratorSettings(GapTolerance: 300)));
        var one = Assert.Single(bridged.Intervals);
        C5.Rel(90, one.From, Rel); C5.Rel(810, one.To, Rel); C5.Rel(720, bridged.ImpactedLength, Rel);
        C5.Rel(21840, bridged.CutVolume, Rel);   // bridging changes length only

        var notBridged = Ok(OverlayIntegrator.IntegrateSegment(b, c, S, seg, bnd, "SEDIMENT", new IntegratorSettings(GapTolerance: 280)));
        Assert.Equal(2, notBridged.Intervals.Count);
    }

    // ---------------------------------------------------------------------------------------
    // E1006 is retired (7.10 rev 3); what stands is the plan-area rule for a second cut region away
    // from the channel (a pond in the overbank, C5Fixtures): its cut counts and its interval appears.
    // With no base < OHWM clip the pond counts wherever d > 0, the open x = (700, 800), y = (50, 70):
    // intervals [0, 310] and [700, 800], length 310 + 100. Pre-rev-3 (clipped at 100): [707.5, 792.5].
    // E1007 slivers: a pond cut of 0.001 ft counts by default and disappears with MinCutDepth 0.01.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void E1006_pond_away_from_the_channel_is_counted_with_its_interval()
    {
        var (b, c) = C5Fixtures.ChannelAndPond(1.0);
        var i = Ok(OverlayIntegrator.IntegrateSegment(b, c, S, C5.Seg(0, 1000), FakeBands.Band(S, 0, 1000, -100, 100), "SEDIMENT"));
        Assert.Equal(2, i.Intervals.Count);
        C5.Rel(0, i.Intervals[0].From, Rel); C5.Rel(310, i.Intervals[0].To, Rel);
        C5.Rel(700, i.Intervals[1].From, Rel); C5.Rel(800, i.Intervals[1].To, Rel);
        C5.Rel(310 + 100, i.ImpactedLength, Rel);
        Assert.True(i.CutVolume > 15880 + 800);   // channel 15,880 + pond core alone 80 * 10 * 1
    }

    [Fact]
    public void E1007_MinCutDepth_removes_slivers_and_trims_shallow_cut()
    {
        var (b, c) = C5Fixtures.ChannelAndPond(0.001);
        var seg = C5.Seg(0, 1000);
        var bnd = FakeBands.Band(S, 0, 1000, -100, 100);
        var all = Ok(OverlayIntegrator.IntegrateSegment(b, c, S, seg, bnd, "SEDIMENT"));
        Assert.Equal(2, all.Intervals.Count);                       // the defaults count everything
        var trimmed = Ok(OverlayIntegrator.IntegrateSegment(b, c, S, seg, bnd, "SEDIMENT", new IntegratorSettings(MinCutDepth: 0.01)));
        var iv = Assert.Single(trimmed.Intervals);
        // The threshold also trims the channel's ramp out: on the bed d = 2 - 0.2 (x - 300) > 0.01
        // for x < 309.95, and the 10..16 ramp triangles (depth 2, 0, 0 from x = 300) end at the same x.
        C5.Rel(0, iv.From, Rel); C5.Rel(309.95, iv.To, Rel);

        // H with MinCutDepth 0.5: d > 0.5 for |y| < 14.5 -> area 29 * 500 = 14,500;
        // volume = 500 * (40 + 2 * integral_10^14.5 (2 - (y-10)/3) dy) = 500 * (40 + 2 * 5.625) = 25,625.
        var h = Ok(RunH(0, 500, settings: new IntegratorSettings(MinCutDepth: 0.5)));
        C5.Rel(14500, h.ImpactedArea, Rel); C5.Rel(25625, h.CutVolume, Rel); C5.Rel(500, h.ImpactedLength, Rel);
    }

    // ---------------------------------------------------------------------------------------
    // E1008: curved channel. Alignment C's arc (R 300 clockwise, 60 degrees, stations 300 to
    // 300 + 100 pi), band entirely on the inside, offsets (+50, +100) = radii 250..200. Uniform cut
    // of 1.0 (base flat 100, comparison flat 99, OHWM 101). The impacted strip's own centerline is
    // 225 * pi/3 = 235.62 long; the length must be the station length 100 pi = 314.159.
    // Area = annular sector (pi/6)(250^2 - 200^2) = 3,750 pi = 11,780.97 (2e-4 vs the polygon).
    // Rev 3: no OHWM argument (the old 101 was above the whole base); values hold.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void E1008_length_is_measured_along_the_alignment()
    {
        var c = FakeAlignments.C();
        double s0 = 300, s1 = FakeAlignments.C_PT;
        var bnd = FakeBands.Band(c, s0, s1, 50, 100);
        var r = OverlayIntegrator.IntegrateSegment(FixtureSurfaces.Eg(), FixtureSurfaces.Flat("FG99", 99, -200, -300, 2200, 300),
            c, new SegmentDef(Guid.NewGuid(), "arc", s0, s1, 50, 100), bnd, "SEDIMENT");
        var i = Ok(r);
        C5.Rel(100 * Math.PI, i.ImpactedLength, Rel, what: "length along alignment");
        Assert.True(Math.Abs(i.ImpactedLength - 225 * Math.PI / 3) > 70);
        double poly = FakeBands.Area(bnd);
        C5.Rel(poly, i.ImpactedArea, Rel, what: "area vs polygon");
        C5.Rel(poly, i.CutVolume, Rel, what: "volume = area * 1");
        C5.Rel(3750 * Math.PI, i.ImpactedArea, 2e-4, what: "area vs analytic");
    }

    // ---------------------------------------------------------------------------------------
    // E1009: state-plane coordinates. Same geometry moved by (+3,000,000, +10,000,000) gives the
    // same result as at the origin within 1e-9 relative. H on S uses exactly representable
    // coordinates; the arc case (D and E1008 on C) does not, so it exercises the rounding.
    // ---------------------------------------------------------------------------------------

    private const double Ox = 3_000_000, Oy = 10_000_000;

    [Fact]
    public void E1009_state_plane_H_T25_matches_origin()
    {
        var sp = FakeAlignments.S(origin: new Pt(Ox, Oy));
        var seg = C5.Seg(0, 500);
        var far = Ok(OverlayIntegrator.IntegrateSegment(
            new C5ShiftedSource(FixtureSurfaces.EgH(), Ox, Oy), new C5ShiftedSource(FixtureSurfaces.FgH(), Ox, Oy),
            sp, seg, FakeBands.Band(sp, 0, 500, -100, 100), "SEDIMENT"));
        var near = Ok(RunH(0, 500));
        C5.Rel(near.ImpactedLength, far.ImpactedLength, Rel, what: "length");
        C5.Rel(near.ImpactedArea, far.ImpactedArea, Rel, what: "area");
        C5.Rel(near.CutVolume, far.CutVolume, Rel, what: "volume");
        C5.Rel(near.Intervals[0].To, far.Intervals[0].To, Rel, what: "interval end");
    }

    [Fact]
    public void E1009_state_plane_arc_matches_origin()
    {
        double s0 = 300, s1 = FakeAlignments.C_PT;
        var c0 = FakeAlignments.C();
        var cf = FakeAlignments.C(new Pt(Ox, Oy));

        var near = OverlayIntegrator.IntegrateBand(FixtureSurfaces.Eg(), FixtureSurfaces.FgD(), FakeBands.Band(c0, s0, s1, -100, 100)).Totals!;
        var far = OverlayIntegrator.IntegrateBand(new C5ShiftedSource(FixtureSurfaces.Eg(), Ox, Oy), new C5ShiftedSource(FixtureSurfaces.FgD(), Ox, Oy),
                                                  FakeBands.Band(cf, s0, s1, -100, 100)).Totals!;
        C5.Rel(near.Fill, far.Fill, Rel, what: "fill");
        C5.Rel(near.PlanArea, far.PlanArea, Rel, what: "plan area");

        var seg = new SegmentDef(Guid.NewGuid(), "arc", s0, s1, 50, 100);
        var i0 = Ok(OverlayIntegrator.IntegrateSegment(FixtureSurfaces.Eg(), FixtureSurfaces.Flat("FG99", 99, -200, -300, 2200, 300),
            c0, seg, FakeBands.Band(c0, s0, s1, 50, 100), "SEDIMENT"));
        var i1 = Ok(OverlayIntegrator.IntegrateSegment(new C5ShiftedSource(FixtureSurfaces.Eg(), Ox, Oy),
            new C5ShiftedSource(FixtureSurfaces.Flat("FG99", 99, -200, -300, 2200, 300), Ox, Oy),
            cf, seg, FakeBands.Band(cf, s0, s1, 50, 100), "SEDIMENT"));
        C5.Rel(i0.ImpactedLength, i1.ImpactedLength, Rel, what: "impacted length");
        C5.Rel(i0.ImpactedArea, i1.ImpactedArea, Rel, what: "impacted area");
        C5.Rel(i0.CutVolume, i1.CutVolume, Rel, what: "cut volume");
    }

    // ---------------------------------------------------------------------------------------
    // E1010 is retired (7.10 rev 3: no OHWM elevation, no per-segment override); what stands is the
    // partition. Both segments on OHWM_100 vs FG_H give T25's 90,000 ft3 each, and band totals add up
    // to the whole band.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Segments_partition_without_double_counting()
    {
        var r1 = RunFlat(0, 500, 100.0);
        var r2 = RunFlat(500, 1000, 100.0);
        C5.Rel(90000, Ok(r1).CutVolume, Rel);
        C5.Rel(90000, Ok(r2).CutVolume, Rel);
        var whole = OverlayIntegrator.IntegrateBand(FlatBase(100.0), FixtureSurfaces.FgH(), FakeBands.Band(S, 0, 1000, -100, 100)).Totals!;
        C5.Rel(whole.Cut, r1.Band!.Cut + r2.Band!.Cut, Rel);
        C5.Rel(whole.Fill, r1.Band.Fill + r2.Band.Fill, Rel);
        C5.Rel(whole.PlanArea, r1.Band.PlanArea + r2.Band.PlanArea, Rel);
    }

    // ---------------------------------------------------------------------------------------
    // E1011: comparison missing for x > 250 (FG_H refined to 50 ft cells, which is the same surface,
    // then cut off by centroid). That part contributes nothing: length 250, volume 250 * 52 = 13,000,
    // coverage 0.5 reported.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void E1011_missing_comparison_contributes_nothing_and_coverage_is_reported()
    {
        var partial = new C5FilteredSource(FixtureSurfaces.FgH(maxCellLength: 50), (x, _) => x < 250);
        var r = OverlayIntegrator.IntegrateSegment(FixtureSurfaces.EgH(), partial, S, C5.Seg(0, 500),
            FakeBands.Band(S, 0, 500, -100, 100), "SEDIMENT");
        var i = Ok(r);
        C5.Rel(250, i.ImpactedLength, Rel); C5.Rel(13000, i.CutVolume, Rel); C5.Rel(8000, i.ImpactedArea, Rel);
        C5.Rel(0.5, r.Band!.Coverage, Rel);
        Assert.Contains(r.Problems, p => p.Code == IntegratorCodes.PartialCoverage && p.Severity == Severity.Warning);
    }

    // ---------------------------------------------------------------------------------------
    // E1019: average from unrounded values. Segment [0, 10.4], OHWM 100: volume 52 * 10.4 = 540.8 ft3
    // = 20.0296 CY, length 10.4. Unrounded: 1.925926 CY/ft -> 1.93. From the rounded displays
    // (20 CY / 10 ft) it would be 2.00.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void E1019_average_uses_unrounded_volume_and_length()
    {
        var r = RunH(0, 10.4);
        var i = Ok(r);
        C5.Rel(540.8, i.CutVolume, Rel); C5.Rel(10.4, i.ImpactedLength, Rel);
        Assert.Equal(i.CutVolume / i.ImpactedLength, r.AveragePerLength);
        Assert.Equal(1.93, Math.Round(Cy(r.AveragePerLength!.Value), 2));
        Assert.Equal(2.00, Math.Round(Math.Round(Cy(i.CutVolume)) / Math.Round(i.ImpactedLength), 2));
    }

    // ---------------------------------------------------------------------------------------
    // E1020 (display is not C5's): the integrator has no unit assumption. Scale Fixture H by 0.3048
    // (feet to metres, elevations too): length scales by k, area by k^2, volume by k^3.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void E1020_integrator_is_unit_agnostic()
    {
        const double k = 0.3048;
        var sm = new AlignmentBuilder(new Pt(0, 0), 0).Line(2000 * k).Build("S_m");
        var seg = C5.Seg(0, 500 * k, -100 * k, 100 * k);
        var m = Ok(OverlayIntegrator.IntegrateSegment(new C5ScaledSource(FixtureSurfaces.EgH(), k), new C5ScaledSource(FixtureSurfaces.FgH(), k),
            sm, seg, FakeBands.Band(sm, 0, 500 * k, -100 * k, 100 * k), "SEDIMENT"));
        C5.Rel(500 * k, m.ImpactedLength, Rel); C5.Rel(16000 * k * k, m.ImpactedArea, Rel); C5.Rel(26000 * k * k * k, m.CutVolume, Rel);
    }

    // ---------------------------------------------------------------------------------------
    // Robustness: base and comparison triangulated on different grids (EG_H on 37 ft cells, FG_H
    // on 50 ft cells; the same surfaces). Where they coincide, depth is zero only up to plane
    // rounding (about 1e-13), which must not count as cut (the depth snap, D9; the OHWM-elevation
    // snap retired with the clip). baseZ NaN = EG_H base, which coincides with FG_H for |y| >= 16 and
    // x >= 1310 (EG_H - FG_H: 32 ft cut, 52 ft2 section, 16,000 / 26,000 per 500 ft; [1000, 1500] ends
    // in the transition at 1310: 9,920 / 15,880; [1500, 2000] nothing). Otherwise the OHWM surface at
    // baseZ against FG_H on 50 ft cells: 100 -> T25 (22,000 / 90,000), 97 -> T26 (14,500 / 36,750),
    // 94 -> equal to the FG_H bed, d 0 up to plane rounding: no phantom cut.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 500, double.NaN, 500, 16000, 26000)]
    [InlineData(0, 500, 100, 500, 22000, 90000)]
    [InlineData(500, 1000, 97, 500, 14500, 36750)]
    [InlineData(1000, 1500, double.NaN, 310, 9920, 15880)]
    [InlineData(1500, 2000, double.NaN, 0, 0, 0)]
    [InlineData(0, 500, 94, 0, 0, 0)]
    public void Differently_triangulated_coincident_surfaces_do_not_create_phantom_cut(
        double s0, double s1, double baseZ, double length, double area, double volume)
    {
        var eg = double.IsNaN(baseZ) ? FixtureSurfaces.EgH(maxCellLength: 37) : FlatBase(baseZ);
        var fg = FixtureSurfaces.FgH(maxCellLength: 50);
        var r = OverlayIntegrator.IntegrateSegment(eg, fg, S, C5.Seg(s0, s1), FakeBands.Band(S, s0, s1, -100, 100),
                                                   "SEDIMENT");
        var i = Ok(r);
        C5.Rel(length, i.ImpactedLength, Rel, what: "length");
        C5.Rel(area, i.ImpactedArea, Rel, what: "area");
        C5.Rel(volume, i.CutVolume, Rel, what: "volume");
        if (length == 0) Assert.Empty(i.Intervals); else Assert.Single(i.Intervals);
    }

    /// <summary>
    /// The same cases with every coordinate inexact: Fixture H and alignment S rotated by 17 degrees
    /// about the origin and moved to (1234.567, -890.123). Rotation preserves lengths, areas and
    /// volumes, so the expected values are unchanged; only rounding differs. Identical and different
    /// grids both run. baseZ as in the test above (NaN = EG_H, else the OHWM surface at that
    /// elevation, which has one triangulation, so both passes use it).
    /// </summary>
    [Theory]
    [InlineData(0, 500, double.NaN, 500, 16000, 26000)]
    [InlineData(0, 500, 100, 500, 22000, 90000)]
    [InlineData(500, 1000, 97, 500, 14500, 36750)]
    [InlineData(1000, 1500, double.NaN, 310, 9920, 15880)]
    [InlineData(1500, 2000, double.NaN, 0, 0, 0)]
    [InlineData(0, 500, 94, 0, 0, 0)]
    public void Rotated_fixture_with_inexact_coordinates_gives_the_same_impacts(
        double s0, double s1, double baseZ, double length, double area, double volume)
    {
        double a = 17 * Math.PI / 180;
        TriangleListSource Base(TriangleListSource egH) => double.IsNaN(baseZ) ? egH : FlatBase(baseZ);
        foreach (var (dx, dy) in new[] { (1234.567, -890.123), (Ox + 1234.567, Oy - 890.123) })
        foreach (var (eg, fg) in new[] { (Base(FixtureSurfaces.EgH()), FixtureSurfaces.FgH()),
                                         (Base(FixtureSurfaces.EgH(maxCellLength: 37)), FixtureSurfaces.FgH(maxCellLength: 50)) })
        {
            var al = new AlignmentBuilder(new Pt(dx, dy), a).Line(2000).Build("S_rot");
            var r = OverlayIntegrator.IntegrateSegment(new C5RotatedSource(eg, a, dx, dy), new C5RotatedSource(fg, a, dx, dy),
                al, C5.Seg(s0, s1), FakeBands.Band(al, s0, s1, -100, 100), "SEDIMENT");
            var i = Ok(r);
            C5.Rel(length, i.ImpactedLength, Rel, what: $"length ({eg.All.Count} tris)");
            C5.Rel(area, i.ImpactedArea, Rel, what: $"area ({eg.All.Count} tris)");
            C5.Rel(volume, i.CutVolume, Rel, what: $"volume ({eg.All.Count} tris)");
            Assert.Equal(length == 0 ? 0 : 1, i.Intervals.Count);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Consistency and input errors
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void IntegrateSegment_band_equals_IntegrateBand()
    {
        var bnd = FakeBands.Band(S, 1000, 1500, -100, 100);
        var a = OverlayIntegrator.IntegrateBand(FixtureSurfaces.EgH(), FixtureSurfaces.FgH(), bnd).Totals!;
        var b = RunH(1000, 1500).Band!;
        Assert.Equal(a, b);
    }

    [Fact]
    public void Invalid_settings_and_empty_surface_are_errors_with_no_numbers()
    {
        // The NaN OHWM elevation case (SV-E1010) retired with OhwmSpec (rev 3): no OHWM elevation
        // reaches the integrator, so settings and surfaces are all there is to validate.
        var bad = RunH(0, 500, settings: new IntegratorSettings(MinCutDepth: -1));
        Assert.False(bad.Succeeded);
        Assert.True(double.IsNaN(bad.BaseArea));
        Assert.Equal(IntegratorCodes.InvalidSettings, Assert.Single(bad.Problems).Code);

        var empty = OverlayIntegrator.IntegrateSegment(FixtureSurfaces.EgH(), new C5EmptySource(), S, C5.Seg(0, 500),
            FakeBands.Band(S, 0, 500, -100, 100), "SEDIMENT");
        Assert.False(empty.Succeeded);
        Assert.Equal(IntegratorCodes.EmptySurface, Assert.Single(empty.Problems).Code);
    }

    [Fact]
    public void Inconsistent_alignment_geometry_is_an_error()
    {
        var c = FakeAlignments.C();
        var flipped = new C5SegmentsOverride(c, [.. c.Segments.Select(g => g.Kind == SegKind.Arc ? g with { Clockwise = !g.Clockwise } : g)]);
        var r = OverlayIntegrator.IntegrateSegment(FixtureSurfaces.Eg(), FixtureSurfaces.FgD(), flipped, C5.Seg(0, 300),
            FakeBands.Band(c, 0, 300, -100, 100), "SEDIMENT");
        Assert.False(r.Succeeded);
        var p = Assert.Single(r.Problems);
        Assert.Equal(IntegratorCodes.StationGeometry, p.Code);
        Assert.Contains("Clockwise", p.Message);
    }

    // ---------------------------------------------------------------------------------------
    // Performance: Fixture H at full extent ([0, 2000], offsets +-100), coarse and refined to 1 ft
    // cells along x (the same surface, about 32,000 triangles each). Results must agree.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Performance_FixtureH_full_extent()
    {
        var bnd = FakeBands.Band(S, 0, 2000, -100, 100);
        var seg = C5.Seg(0, 2000);
        var sw = Stopwatch.StartNew();
        var coarse = Ok(OverlayIntegrator.IntegrateSegment(FixtureSurfaces.EgH(), FixtureSurfaces.FgH(), S, seg, bnd, "SEDIMENT"));
        double tCoarse = sw.Elapsed.TotalMilliseconds;

        var egFine = FixtureSurfaces.EgH(maxCellLength: 1.0);
        var fgFine = FixtureSurfaces.FgH(maxCellLength: 1.0);
        sw.Restart();
        var fineR = OverlayIntegrator.IntegrateSegment(egFine, fgFine, S, seg, bnd, "SEDIMENT");
        double tFine = sw.Elapsed.TotalMilliseconds;
        var fine = Ok(fineR);

        output.WriteLine($"Fixture H full extent, coarse ({FixtureSurfaces.EgH().All.Count} + {FixtureSurfaces.FgH().All.Count} triangles): {tCoarse:0.0} ms");
        output.WriteLine($"Fixture H full extent, 1 ft cells ({egFine.All.Count} + {fgFine.All.Count} triangles): {tFine:0.0} ms");

        C5.Rel(1310, coarse.ImpactedLength, Rel); C5.Rel(67880, coarse.CutVolume, Rel);
        C5.Rel(coarse.ImpactedLength, fine.ImpactedLength, Rel);
        C5.Rel(coarse.ImpactedArea, fine.ImpactedArea, Rel);
        C5.Rel(coarse.CutVolume, fine.CutVolume, Rel);
        C5.Rel(400000, fineR.Band!.PlanArea, Rel);
        Assert.True(tFine < 5000, $"1 ft H took {tFine:0} ms");
    }
}

/// <summary>An alignment whose segment list is replaced (to inject inconsistent geometry).</summary>
internal sealed class C5SegmentsOverride(IAlignmentGeometry inner, IReadOnlyList<AlignSeg> segments) : IAlignmentGeometry
{
    public string Name => inner.Name;
    public double StartStation => inner.StartStation;
    public double EndStation => inner.EndStation;
    public IReadOnlyList<AlignSeg> Segments => segments;
    public Pt PointAt(double rawStation, double offset) => inner.PointAt(rawStation, offset);
    public string FormatStation(double rawStation) => inner.FormatStation(rawStation);
    public bool TryParseStation(string text, out double rawStation, out string? error) => inner.TryParseStation(text, out rawStation, out error);
}

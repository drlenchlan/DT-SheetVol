// T25 to T28, T30 and the E10xx rows marked U, on generated triangles (no Civil 3D).
//
// Fixture H geometry used in every derivation (section 8):
//   EG_H(y): 96 for |y| <= 10; 96 + (|y| - 10)/3 for 10..40; 106 beyond.
//   FG_H(y), x <= 1300: 94 for |y| <= 10; 94 + 2(|y| - 10)/3 for 10..16; EG_H beyond. EG_H for x >= 1310.
//   Depth d = EG - FG: 2 on the bed, 2 - (|y| - 10)/3 on 10..16 (0 at 16). Full section 52 ft2, 32 ft wide.
// Units: ft, ft2, ft3. 1 CY = 27 ft3. HCFCD display: length whole ft, average to 2 decimals (A12).
//
// Rev 3 (NOW.md X5): OHWM is a surface, the analysis's base; there is no OHWM elevation and no
// base < OHWM clip. Impact cut is d = base - comparison > MinCutDepth. Tests that ran EG_H/FG_H with
// OHWM 100 keep their values: EG_H < 100 for |y| < 22, wider than the 32 ft cut, so that clip removed
// nothing. Tests whose OHWM elevation mattered use a flat base at that elevation (the flat-base
// construction NOW.md rev 3 rule 6 names) with FG_H as the comparison, d = z - FG_H:
//   flat 97 -> FG_H < 97 for |y| < 14.5: width 29; section 20 * 3 + 2 * (3 * 4.5 - 4.5^2 / 3) = 73.5 ft2.
//   flat 94 -> equal to the FG_H bed and below it everywhere else: no cut.
// Each such test carries "rev 3: re-derived by rev3-integrator"; that package moves T25 to T30 onto
// Fixture H's OHWM surface and owns the final numbers.

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

    /// <summary>Rev 3 flat-base construction: a flat base (the OHWM surface) at <paramref name="z"/> over Fixture H's extent, FG_H as the comparison.</summary>
    private static TriangleListSource FlatBase(double z) => FixtureSurfaces.Flat("OHWM", z, 0, -100, 2000, 100);

    private static SegmentImpactResult RunFlat(double s0, double s1, double z)
        => OverlayIntegrator.IntegrateSegment(FlatBase(z), FixtureSurfaces.FgH(), S, C5.Seg(s0, s1),
            FakeBands.Band(S, s0, s1, -100, 100), "SEDIMENT");

    private static ImpactResult Ok(SegmentImpactResult r)
    {
        Assert.True(r.Succeeded, string.Join("; ", r.Problems.Select(p => p.Code + " " + p.Message)));
        return r.Impact!;
    }

    private static double Cy(double ft3) => ft3 / C5.CubicFeetPerCubicYard;

    // ---------------------------------------------------------------------------------------
    // T25: OHWM 100. Below OHWM is |y| < 22, cut is |y| < 16, so the impacted strip is the full cut:
    // area 32 * 500 = 16,000 ft2, volume 52 * 500 = 26,000 ft3 = 962.963 CY, length 500,
    // average 962.963 / 500 = 1.925926 -> 1.93. Handoff: 500, 16,000 (0.37 ac), 962.963, 1.93. Agree.
    // rev 3: re-derived by rev3-integrator. EG_H base, no OHWM clip: the same full cut, same values.
    // The two QA accumulators are gone; band cut equals impact cut (MinCutDepth 0); the base covers the band.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 500)]
    [InlineData(500, 1000)]
    public void T25_OHWM_100(double s0, double s1)
    {
        var r = RunH(s0, s1);
        var i = Ok(r);
        C5.Rel(500, i.ImpactedLength, Rel, what: "length");
        C5.Rel(16000, i.ImpactedArea, Rel, what: "area");
        C5.Rel(26000, i.CutVolume, Rel, what: "volume");
        Assert.Equal(962.963, Cy(i.CutVolume), 3);
        Assert.Equal(0.37, Math.Round(i.ImpactedArea / 43560.0, 2));
        Assert.Equal(1.93, Math.Round(Cy(r.AveragePerLength!.Value), 2));
        var iv = Assert.Single(i.Intervals);
        C5.Rel(s0, iv.From, Rel); C5.Rel(s1, iv.To, Rel);
        Assert.False(i.TouchesSearchLimit);
        C5.Rel(r.Band!.Cut, i.CutVolume, Rel, what: "band cut = impact cut");
        C5.Rel(500 * 200, r.BaseArea, Rel, what: "base area");
        Assert.True(r.HasBase);
        Assert.Empty(i.Outlines);                  // rev 3: filled by rev3-integrator
        Assert.Empty(r.Problems);
    }

    // ---------------------------------------------------------------------------------------
    // T26: OHWM 97. Pre-rev-3 (EG_H clipped at 97): area 13,000 ft2, 24,500 ft3 = 907.407 CY, 1.81,
    // plus E1003 bank cut 1,500 ft3 reported separately.
    // rev 3: re-derived by rev3-integrator. Flat base 97, comparison FG_H: d = 97 - FG_H > 0 for
    // |y| < 14.5. Area 29 * 500 = 14,500 ft2 (0.33 ac); section 73.5 ft2 -> 36,750 ft3 = 1,361.111 CY;
    // average 2.722222 -> 2.72. E1003 is retired: there is no cut above the base, so band cut = impact cut.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 500)]
    [InlineData(500, 1000)]
    public void T26_OHWM_97_and_E1003_bank_cut_reported_separately(double s0, double s1)
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
    }

    // ---------------------------------------------------------------------------------------
    // T27 / E1001: OHWM 95 is below the 96 bed everywhere. Length 0, area 0, volume 0, average N/A,
    // and the segment is noted as having no ground below OHWM. Handoff agrees.
    // rev 3: re-derived by rev3-integrator. E1001 is "no cut in the segment": a flat base at 94, equal to
    // the FG_H bed (d exactly 0 there) and below FG_H everywhere else. Zeros, N/A, one SV-E1001 note; the
    // base still covers the band (100,000 ft2), and band cut is 0.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 500)]
    [InlineData(500, 1000)]
    public void T27_E1001_OHWM_95_nothing_below(double s0, double s1)
    {
        var r = RunFlat(s0, s1, 94.0);
        var i = Ok(r);
        Assert.Equal((0.0, 0.0, 0.0), (i.ImpactedLength, i.ImpactedArea, i.CutVolume));
        Assert.Null(r.AveragePerLength);
        Assert.Empty(i.Intervals);
        Assert.True(r.HasBase);
        C5.Rel(500 * 200, r.BaseArea, Rel, what: "base area");
        var p = Assert.Single(r.Problems);
        Assert.Equal((IntegratorCodes.NoCut, Severity.Info), (p.Code, p.Severity));
        Assert.Equal(0.0, r.Band!.Cut);
    }

    // ---------------------------------------------------------------------------------------
    // T28: OHWM 100, segment [1000, 1500]. Cut exists for x < 1310: impacted length 310.00.
    // Volume = 300 * 52 + transition 280 (see FixtureSurfaces.FgH) = 15,880 ft3 = 588.148 CY.
    // Handoff band: 577.78 (300 ft of full section = 15,600) to 597.04 (310 ft = 16,120). Inside.
    // Area: 300 * 32 = 9,600 + transition bed 200 + both 10..16 cells fully positive 2 * 60 = 9,920 ft2.
    // rev 3: re-derived by rev3-integrator. EG_H base, no OHWM clip: the same values (the clip removed nothing).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void T28_transition_segment()
    {
        var r = RunH(1000, 1500);
        var i = Ok(r);
        Assert.InRange(i.ImpactedLength, 310.00 - 0.01, 310.00 + 0.01);
        C5.Rel(310, i.ImpactedLength, Rel, what: "length");
        Assert.InRange(Cy(i.CutVolume), 577.78, 597.04);
        C5.Rel(15880, i.CutVolume, Rel, what: "volume");
        Assert.Equal(588.148, Cy(i.CutVolume), 3);
        C5.Rel(9920, i.ImpactedArea, Rel, what: "area");
        var iv = Assert.Single(i.Intervals);
        C5.Rel(1000, iv.From, Rel); C5.Rel(1310, iv.To, Rel);
    }

    // ---------------------------------------------------------------------------------------
    // T30 / E1002: offsets (-12, +12). Ground below OHWM 100 is |y| < 22, which reaches the lateral
    // edges at +-12 (EG 96.667 there): flagged, values still shown. Area 24 * 500 = 12,000; volume
    // = 500 * (40 + 2 * integral_10^12 (2 - (y-10)/3) dy) = 500 * (40 + 2 * 3.3333) = 23,333.33 ft3.
    // Control: offsets +-100 put the edges on 106 ground: no flag.
    // rev 3: re-derived by rev3-integrator. E1002 is "cut reaches a lateral edge": the cut is 1.333 ft deep
    // at |y| = 12, so (-12, +12) still flags with the same values. The cut ends at |y| = 16, so the
    // pre-rev-3 pair (-23, 23) / (-21.9, 23) becomes (-17, 17) no flag / (-15.9, 17) flag.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void T30_E1002_search_limit_flag()
    {
        var r = RunH(0, 500, -12, 12);
        var i = Ok(r);
        Assert.True(i.TouchesSearchLimit);
        var p = Assert.Single(r.Problems);
        Assert.Equal((IntegratorCodes.SearchLimit, Severity.Warning), (p.Code, p.Severity));
        C5.Rel(12000, i.ImpactedArea, Rel);
        C5.Rel(500 * (40 + 2 * (4 - 4.0 / 6)), i.CutVolume, Rel);

        Assert.False(Ok(RunH(0, 500)).TouchesSearchLimit);
        // Just outside the cut (|y| = 16, depth 0): no flag. One edge inside: flag.
        Assert.False(Ok(RunH(0, 500, -17, 17)).TouchesSearchLimit);
        Assert.True(Ok(RunH(0, 500, -15.9, 17)).TouchesSearchLimit);
    }

    // ---------------------------------------------------------------------------------------
    // E1004: swap the surfaces (base = FG_H, comparison = EG_H): all of it becomes fill below OHWM.
    // Base < 100 for |y| < 22; fill = 26,000 ft3 per 500 ft, none of it counted in rows 6 to 9.
    // rev 3: re-derived by rev3-integrator. E1004 is retired: the comparison above the base is the band's
    // fill (exported as the analysis's fill), never counted. d <= 0 everywhere: zeros, N/A, SV-E1001.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void E1004_fill_below_OHWM_is_reported_not_counted()
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
    // E1006: low ground below OHWM away from the channel (a pond in the overbank, C5Fixtures)
    // with cut counts under the plan-area rule and its interval appears: [0, 310] and [707.5, 792.5].
    // E1007 slivers: a pond cut of 0.001 ft counts by default and disappears with MinCutDepth 0.01.
    // rev 3: re-derived by rev3-integrator. No base < OHWM clip: the pond's cut counts wherever
    // d > 0, the open x = (700, 800) (C5Fixtures), so its interval is [700, 800] and the length 310 + 100.
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
    // E1010: per-segment OHWM. Segment 1 uses the default 100 (T25 values), segment 2 overrides 97
    // (T26 values). Segments partition by station, so band totals add up to the whole band.
    // rev 3: re-derived by rev3-integrator. E1010 is retired (no OHWM elevation, no per-segment override);
    // what stands is the partition: both segments on EG_H/FG_H give T25's 26,000 ft3 each, and band totals
    // still add up to the whole band.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void E1010_per_segment_override_without_double_counting()
    {
        var s1 = C5.Seg(0, 500);
        var s2 = C5.Seg(500, 1000);
        var r1 = OverlayIntegrator.IntegrateSegment(FixtureSurfaces.EgH(), FixtureSurfaces.FgH(), S, s1, FakeBands.Band(S, 0, 500, -100, 100), "SEDIMENT");
        var r2 = OverlayIntegrator.IntegrateSegment(FixtureSurfaces.EgH(), FixtureSurfaces.FgH(), S, s2, FakeBands.Band(S, 500, 1000, -100, 100), "SEDIMENT");
        C5.Rel(26000, Ok(r1).CutVolume, Rel);
        C5.Rel(26000, Ok(r2).CutVolume, Rel);
        var whole = OverlayIntegrator.IntegrateBand(FixtureSurfaces.EgH(), FixtureSurfaces.FgH(), FakeBands.Band(S, 0, 1000, -100, 100)).Totals!;
        C5.Rel(whole.Cut, r1.Band!.Cut + r2.Band!.Cut, Rel);
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
    // rounding (about 1e-13), which must not count as cut: the T25, T26 and T28 values hold with
    // default settings, and an OHWM equal to the bed elevation (96) puts nothing below it.
    // rev 3: re-derived by rev3-integrator. The OHWM-elevation snap is gone with the clip (D9); the depth
    // snap stays. baseZ NaN = EG_H base (37 ft cells); otherwise the flat-base construction against
    // FG_H on 50 ft cells: the T26 row is flat 97 (14,500 / 36,750), and the snap row is a flat base equal
    // to the FG_H bed (94), where d is 0 up to plane rounding: no phantom cut.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 500, double.NaN, 500, 16000, 26000)]
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
    /// grids both run. rev 3: re-derived by rev3-integrator; baseZ as in the test above (NaN = EG_H,
    /// else a flat base at that elevation, which has one triangulation, so both passes use it).
    /// </summary>
    [Theory]
    [InlineData(0, 500, double.NaN, 500, 16000, 26000)]
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
    public void Invalid_settings_and_OHWM_are_errors_with_no_numbers()
    {
        var bad = RunH(0, 500, settings: new IntegratorSettings(MinCutDepth: -1));
        Assert.False(bad.Succeeded);
        Assert.True(double.IsNaN(bad.BaseArea));
        Assert.Equal(IntegratorCodes.InvalidSettings, Assert.Single(bad.Problems).Code);

        // rev 3: re-derived by rev3-integrator. The NaN OHWM elevation case (SV-E1010) is retired with
        // OhwmSpec: no OHWM elevation reaches the integrator, so there is nothing to validate.

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

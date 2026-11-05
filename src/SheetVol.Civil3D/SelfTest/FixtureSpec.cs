// FX work package (HANDOFF.md section 10, Wave 1). Owner: FX.
//
// The fixture world of HANDOFF.md section 8, as plain numbers: names, coordinates,
// alignment design values, template dimensions, and the analytic expected values
// of the acceptance tests, each with its derivation. No Autodesk types here, so
// the arithmetic can be read and checked on its own. Units are drawing units; the
// section 8 values are feet, square feet and cubic feet (1 CY = 27 ft3).
//
// These literals ARE the specification of the test world (section 8 names EG,
// FG_A, S, ...; section 6.7 names SV-VPORT and SV_TABLE_ANCHOR). They are kept in
// this one class so no other fixture file carries a name, layer or style literal.

namespace SheetVol.Civil3D.SelfTest;

public static class FixtureSpec
{
    public const double CubicFeetPerCubicYard = 27.0;

    /// <summary>Object, layer, layout and block names used by the fixtures.</summary>
    public static class Names
    {
        public const string EG = "EG";          // section 8: flat EG shared by A, B and D
        public const string FgA = "FG_A";
        public const string FgB = "FG_B";
        public const string FgD = "FG_D";
        public const string EgH = "EG_H";       // Fixture H has its own existing ground
        public const string FgH = "FG_H";

        public const string AlignmentS = "S";
        public const string AlignmentC = "C";
        public const string AlignmentK = "K";
        public const string AlignmentSP = "SP";

        /// <summary>Prefix of the fixtures' own verification volume surfaces. Distinct from
        /// the tool's owned "SV-&lt;base&gt;-&lt;comparison&gt;" surfaces (section 6.5), so a
        /// fixture drawing never pre-empts a name the engine owns.</summary>
        public const string VolumePrefix = "FX-VOL-";

        public const string TemplateLayout = "SV-SHEET";
        public const string ViewportLayer = "SV-VPORT";          // section 6.7
        public const string TableAnchorBlock = "SV_TABLE_ANCHOR"; // section 6.7
    }

    /// <summary>Styles the fixtures ask for first. When the drawing lacks the preferred
    /// name the first style in the collection is used and the build result says which.</summary>
    public static class Styles
    {
        public const string AlignmentStyle = "Standard";
        public const string AlignmentLabelSet = "_No Labels";
    }

    /// <summary>Breakline parameters. 2-point breaklines are used throughout, so
    /// mid-ordinate never applies (it must still be &gt; 0 or the API throws);
    /// maximum distance 0 and weeding 0 switch supplementing and weeding off
    /// (Autodesk reference: "Set a parameter to 0 to ignore it").</summary>
    public static class Breaklines
    {
        public const double MidOrdinate = 0.1;
        public const double MaximumDistance = 0.0;
        public const double WeedingDistance = 0.0;
        public const double WeedingAngle = 0.0;
    }

    /// <summary>Absolute tolerance for elevation probes and station checks on built objects.</summary>
    public const double GeometryTolerance = 1e-6;

    // ------------------------------------------------------------------ EG

    public const double EgZ = 100.0;

    /// <summary>Section 8: EG corners, counter-clockwise from the south-west.</summary>
    public static readonly (double X, double Y)[] EgCorners =
    {
        (-200.0, -300.0), (2200.0, -300.0), (2200.0, 300.0), (-200.0, 300.0),
    };

    public const double EgWidth = 2400.0;   // 2200 - (-200)
    public const double EgDepth = 600.0;    // 300 - (-300)

    /// <summary>4 vertices, convex hull of 4: T = 2V - h - 2 = 2.</summary>
    public const int EgPoints = 4, EgTriangles = 2;

    // ------------------------------------------------------------ Fixture A

    /// <summary>Square base edge of every pyramid; base at z = 100 centred on y = 0.</summary>
    public const double PyramidBase = 90.0;
    public const double PyramidHalfBase = PyramidBase / 2.0;

    public sealed record Pyramid(string Name, double CenterX, double ApexZ)
    {
        /// <summary>Apex height above EG; negative is a pit (cut), positive a mound (fill).</summary>
        public double Height => ApexZ - EgZ;

        /// <summary>Pyramid volume = base area x |height| / 3 = 8100 |h| / 3 = 2700 |h| ft3.</summary>
        public double VolumeFt3 => PyramidBase * PyramidBase * Math.Abs(Height) / 3.0;

        public bool IsCut => Height < 0;
    }

    /// <summary>Section 8 table. |h| = 1 gives 2700 ft3 = 100 CY; |h| = 0.5 gives 1350 ft3 = 50 CY.</summary>
    public static readonly Pyramid[] Pyramids =
    {
        new("P1", 125.0, 99.0),    // 100 CY cut,  base x 80..170
        new("P2", 375.0, 100.5),   //  50 CY fill, base x 330..420
        new("P3", 1125.0, 99.0),   // 100 CY cut,  base x 1080..1170
        new("P4", 1375.0, 100.5),  //  50 CY fill, base x 1330..1420
        new("P5", 1750.0, 101.0),  // 100 CY fill, base x 1705..1795
    };

    /// <summary>EG's 4 corners plus 5 per pyramid (4 base corners and the apex) = 29.
    /// Every vertex but the 4 EG corners is interior: T = 2(29) - 4 - 2 = 52
    /// (4 faces per pyramid = 20, plus 32 flat triangles outside).</summary>
    public const int FgAPoints = 29, FgATriangles = 52;

    /// <summary>Whole-surface totals, EG vs FG_A: cut = P1 + P3 = 2 x 2700 = 5400 ft3 (200 CY);
    /// fill = P2 + P4 + P5 = 1350 + 1350 + 2700 = 5400 ft3 (200 CY). Cut equals fill, so
    /// these totals cannot tell which API property is cut; Fixture D (fill only) can.</summary>
    public const double ACutFt3 = 5400.0, AFillFt3 = 5400.0;

    // ------------------------------------------------------------ Fixture B

    public const double RidgeFromX = -100.0, RidgeToX = 2100.0;
    public const double RidgeHalfWidth = 30.0, RidgeCrestZ = 101.0;

    /// <summary>Triangle of base 60 and height 1: 30 ft2 in every section with x in [-100, 2100].</summary>
    public const double RidgeSectionFt2 = 0.5 * 2.0 * RidgeHalfWidth * (RidgeCrestZ - EgZ);

    /// <summary>EG's 4 corners plus 3 breaklines of 2 vertices = 10; hull 4: T = 2(10) - 4 - 2 = 14.</summary>
    public const int FgBPoints = 10, FgBTriangles = 14;

    /// <summary>Whole-surface fill is at least the prism 30 x 2200 = 66000 ft3. The tapers
    /// from the ridge ends (x = -100 and 2100) to the EG corners add a little more, and
    /// how much depends on the triangulation, so there is no exact whole-surface value.
    /// Tests use x in [0, 2000] where the section is exact.</summary>
    public const double BFillPrismFt3 = RidgeSectionFt2 * (RidgeToX - RidgeFromX);

    /// <summary>Whole-surface fill when each ridge end fans to the two near EG corners, which is
    /// the triangulation the 2026.2.3 host produced (MEASURED 2026-11-05: 88000.00000000015).
    /// Per end, the triangles holding the raised ridge end (one vertex 1 ft up, so mean height
    /// 1/3): (-100,0)(-100,30)(-200,300) area 1500 -> 500; (-100,0)(-200,300)(-200,-300) area
    /// 30,000 -> 10,000; the mirror of the first -> 500. 66,000 + 2 x 11,000 = 88,000 ft3.</summary>
    public const double BFillFanFt3 = BFillPrismFt3 + 2.0 * (500.0 + 10_000.0 + 500.0);

    // ------------------------------------------------------------ Fixture D

    public const double SlabRaise = 1.00;

    /// <summary>EG raised 1: both surfaces cover the same 2400 x 600 rectangle, so fill =
    /// 2400 x 600 x 1 = 1,440,000 ft3 and cut = 0, for any triangulation.</summary>
    public const double DFillFt3 = EgWidth * EgDepth * SlabRaise;
    public const int FgDPoints = 4, FgDTriangles = 2;

    // ------------------------------------------------------------ Fixture H

    public const double ChannelFromX = 0.0, ChannelToX = 2000.0;
    public const double ChannelHalfBed = 10.0, ChannelHalfTop = 40.0, ChannelHalfWidth = 100.0;
    public const double ChannelBedZ = 96.0, ChannelTopZ = 106.0;   // 3:1: (106 - 96) x 3 = 30 = 40 - 10
    public const double SedimentBedZ = 94.0, SedimentHalfWidth = 16.0, SedimentMeetZ = 98.0;
    public const double TransitionFromX = 1300.0, TransitionToX = 1310.0;

    /// <summary>EG_H elevation at offset y: bed 96 for |y| &lt;= 10, 3:1 up to 106 at |y| = 40, flat beyond.</summary>
    public static double EgHZ(double y)
    {
        double a = Math.Abs(y);
        if (a <= ChannelHalfBed) return ChannelBedZ;
        if (a <= ChannelHalfTop) return ChannelBedZ + (a - ChannelHalfBed) / 3.0;
        return ChannelTopZ;
    }

    /// <summary>EG_H longitudinal breaklines (y), each from x = 0 to 2000. y = 0 is added for symmetry.</summary>
    public static readonly double[] EgHLines = { -100.0, -40.0, -10.0, 0.0, 10.0, 40.0, 100.0 };

    /// <summary>7 lines x 2 vertices = 14, all on the x = 0 or x = 2000 hull edges (h = 14):
    /// T = 2(14) - 14 - 2 = 12, two per strip.</summary>
    public const int EgHPoints = 14, EgHTriangles = 12;

    /// <summary>FG_H longitudinal breaklines. Each line is (y, vertices as (x, z)), and every
    /// line has vertices at x = 0, 1300, 1310 and 2000, so each strip between two lines is
    /// three quads. |y| &lt;= 10: sediment bed 94 to x = 1300, EG bed 96 from x = 1310 (the
    /// 10 ft transition is whatever the TIN makes of it). |y| = 16: 98, on EG_H's slope.
    /// |y| &gt;= 40: EG_H.
    /// MEASURED 2026-11-05 (2026.2.3 Core Console): with only 2 vertices on the |y| = 40 and
    /// 100 lines, Civil 3D marked 2 of 36 triangles hidden: the two slivers between y = 16 and
    /// 40 with angles of about 177 and 178 degrees, on the +y side only (their -y mirrors
    /// stayed visible), with every build option off. The collinear vertices at 1300 and 1310
    /// remove those slivers; the surface is the same.</summary>
    public static readonly (double Y, (double X, double Z)[] Vertices)[] FgHLines = BuildFgHLines();

    private static (double Y, (double X, double Z)[] Vertices)[] BuildFgHLines()
    {
        var lines = new List<(double, (double, double)[])>();
        foreach (var y in new[] { -100.0, -40.0, -16.0, -10.0, 0.0, 10.0, 16.0, 40.0, 100.0 })
        {
            double a = Math.Abs(y);
            if (a <= ChannelHalfBed)
                lines.Add((y, new[] { (ChannelFromX, SedimentBedZ), (TransitionFromX, SedimentBedZ), (TransitionToX, ChannelBedZ), (ChannelToX, ChannelBedZ) }));
            else
            {
                double z = a == SedimentHalfWidth ? SedimentMeetZ : EgHZ(y);
                lines.Add((y, new[] { (ChannelFromX, z), (TransitionFromX, z), (TransitionToX, z), (ChannelToX, z) }));
            }
        }
        return lines.ToArray();
    }

    /// <summary>Vertices: 9 lines x 4 = 36. Hull vertices: 9 on x = 0, 9 on x = 2000, and the
    /// 1300 and 1310 vertices on y = -100 and +100 = 22. T = 2(36) - 22 - 2 = 48
    /// (8 strips x 3 quads x 2).</summary>
    public const int FgHPoints = 36, FgHTriangles = 48;

    /// <summary>Full cut section, |y| &lt;= 16: bed 2 ft deep over 20 ft = 40 ft2, plus on each
    /// side a triangle 6 ft wide tapering from 2 ft to 0 (EG - FG = 2 - (|y| - 10)/3) = 6 ft2,
    /// so 40 + 12 = 52 ft2, 32 ft wide.</summary>
    public const double HCutSectionFt2 = 52.0, HCutWidth = 32.0;

    /// <summary>Whole-surface cut: 52 x 1300 = 67,600 ft3 to x = 1300, plus the transition, which
    /// lies between 0 and 52 x 10 = 520 ft3 depending on triangulation: [67,600, 68,120].
    /// FG_H is at or below EG_H at every vertex and EG_H is planar under each FG_H triangle,
    /// so fill is 0.</summary>
    public const double HCutMinFt3 = HCutSectionFt2 * (TransitionFromX - ChannelFromX);
    public const double HCutMaxFt3 = HCutSectionFt2 * (TransitionToX - ChannelFromX);

    // ------------------------------------------------------------ Alignments

    /// <summary>S: straight (0,0) to (2000,0), station 0 at x = 0, so left is +y.</summary>
    public static class S
    {
        public const double StartX = 0.0, EndX = 2000.0;
        public const double Length = EndX - StartX;
    }

    /// <summary>C: tangent 300 east from (0,0), arc R = 300 clockwise (turning right, toward -y)
    /// with delta 60 degrees, tangent 300. Built PI-style: two tangents meeting at the PI and a
    /// free curve between them, so tangency is exact.</summary>
    public static class C
    {
        public const double TangentIn = 300.0, Radius = 300.0, DeltaDeg = 60.0, TangentOut = 300.0;
        public static readonly double Delta = DeltaDeg * Math.PI / 180.0;
        /// <summary>T = R tan(delta/2) = 300 tan 30 = 173.2051.</summary>
        public static readonly double CurveTangent = Radius * Math.Tan(Delta / 2.0);
        /// <summary>PI at x = 300 + 173.2051 = 473.2051 on the x-axis.</summary>
        public static readonly double PiX = TangentIn + CurveTangent;
        /// <summary>Out-tangent bearing: east turned clockwise by 60 degrees = -60 degrees.</summary>
        public static readonly double OutDirection = -Delta;
        /// <summary>Arc length R x delta = 300 x pi/3 = 314.159265.</summary>
        public static readonly double ArcLength = Radius * Delta;
        public static readonly double ArcStartStation = TangentIn;                     // 300
        public static readonly double ArcEndStation = TangentIn + ArcLength;           // 614.159265
        public static readonly double Length = TangentIn + ArcLength + TangentOut;     // 914.159265
        /// <summary>Centre (300, -300). Arc end = centre + R (sin 60, cos 60) = (559.8076, -150).</summary>
        public static readonly (double X, double Y) Center = (TangentIn, -Radius);
        public static readonly (double X, double Y) ArcEnd = (TangentIn + Radius * Math.Sin(Delta), -Radius + Radius * Math.Cos(Delta));
        /// <summary>End = arc end + 300 (cos -60, sin -60) = (709.8076, -409.8076).</summary>
        public static readonly (double X, double Y) End = (ArcEnd.X + TangentOut * Math.Cos(OutDirection), ArcEnd.Y + TangentOut * Math.Sin(OutDirection));
    }

    /// <summary>K: two tangents meeting at a 30 degree angle point, no curve. Read as a 30 degree
    /// DEFLECTION (direction changes by 30 degrees), turning right (clockwise, toward -y); C
    /// already turns right with an arc and SP turns left. Tangent 1 (0,0)-(500,0), tangent 2 300
    /// long at bearing -30 degrees, ending at (759.8076, -150). With offsets of +-100 the band
    /// stays inside EG (|y| &lt;= 300) so T10's "API volume equals polygon area" holds.</summary>
    public static class K
    {
        public const double Tangent1 = 500.0, Tangent2 = 300.0, DeflectionDeg = -30.0;
        public static readonly double Deflection = DeflectionDeg * Math.PI / 180.0;
        public static readonly (double X, double Y) AnglePoint = (Tangent1, 0.0);
        public static readonly (double X, double Y) End = (Tangent1 + Tangent2 * Math.Cos(Deflection), Tangent2 * Math.Sin(Deflection));
        public static readonly double AnglePointStation = Tangent1;   // 500
        public static readonly double Length = Tangent1 + Tangent2;   // 800
    }

    /// <summary>SP: tangent, clothoid spiral in, circular arc, clothoid spiral out, tangent.
    /// Chosen values (section 8 leaves them open): PI at (500,0); out-tangent at bearing +30
    /// degrees (turning left, counter-clockwise, toward +y) running 300 from the PI to
    /// (759.8076, 150); R = 500; both spirals 100 long (clothoid). Kept small enough that a
    /// +-100 band stays inside EG.
    ///
    /// Clothoid geometry (standard series, AASHTO / Hickerson):
    ///   theta_s = Ls / 2R = 0.1 rad
    ///   Xs = Ls (1 - t^2/10 + t^4/216 - t^6/9360)            = 99.900046
    ///   Ys = Ls (t/3 - t^3/42 + t^5/1320 - t^7/75600)         = 3.330955
    ///   p  = Ys - R (1 - cos theta_s)                          = 0.833038   (shift)
    ///   k  = Xs - R sin theta_s                                = 49.983338
    ///   Ts = (R + p) tan(delta/2) + k                          = 184.181    (TS to PI)
    ///   Lc = R (delta - 2 theta_s)                             = 161.799
    ///   TS = 500 - Ts = 315.819; SC = TS + 100; CS = SC + Lc; ST = CS + 100 = 677.618
    ///   End station = ST + (300 - Ts) = 793.437.</summary>
    public static class SP
    {
        public const double PiDistance = 500.0, TangentOut = 300.0, DeltaDeg = 30.0;
        public const double Radius = 500.0, SpiralIn = 100.0, SpiralOut = 100.0;
        public static readonly double Delta = DeltaDeg * Math.PI / 180.0;
        public static readonly (double X, double Y) Pi = (PiDistance, 0.0);
        public static readonly (double X, double Y) End = (PiDistance + TangentOut * Math.Cos(Delta), TangentOut * Math.Sin(Delta));

        public static readonly double ThetaS = SpiralIn / (2.0 * Radius);
        public static readonly double Xs = SpiralIn * (1 - Math.Pow(ThetaS, 2) / 10 + Math.Pow(ThetaS, 4) / 216 - Math.Pow(ThetaS, 6) / 9360);
        public static readonly double Ys = SpiralIn * (ThetaS / 3 - Math.Pow(ThetaS, 3) / 42 + Math.Pow(ThetaS, 5) / 1320 - Math.Pow(ThetaS, 7) / 75600);
        public static readonly double P = Ys - Radius * (1 - Math.Cos(ThetaS));
        public static readonly double Kshift = Xs - Radius * Math.Sin(ThetaS);
        public static readonly double Ts = (Radius + P) * Math.Tan(Delta / 2.0) + Kshift;
        public static readonly double ArcLength = Radius * (Delta - 2.0 * ThetaS);
        public static readonly double TsStation = PiDistance - Ts;
        public static readonly double ScStation = TsStation + SpiralIn;
        public static readonly double CsStation = ScStation + ArcLength;
        public static readonly double StStation = CsStation + SpiralOut;
        public static readonly double Length = StStation + (TangentOut - Ts);
        /// <summary>Series truncation at theta = 0.1 is below 1e-12 ft; the check allows for
        /// the host's own spiral evaluation and is reported, not hidden.</summary>
        public const double StationTolerance = 1e-3;
    }

    // ------------------------------------------------------------ Test template

    /// <summary>Code-built stand-in for the LAN sheet template (section 6.7, T15, T18): one
    /// layout, a floating viewport on SV-VPORT 12 x 5 paper units, and an SV_TABLE_ANCHOR
    /// block reference (insertion point = table top-left). Paper size is left at the host
    /// default; nothing here depends on it.</summary>
    public static class Template
    {
        public const double ViewportWidth = 12.0, ViewportHeight = 5.0;
        public static readonly (double X, double Y) ViewportCenter = (7.0, 4.5);  // spans x 1..13, y 2..7
        /// <summary>T15: 50 model units per paper unit (1" = 50').</summary>
        public const double ModelUnitsPerPaperUnit = 50.0;
        public static readonly (double X, double Y) TableAnchor = (13.5, 7.0);    // right of the viewport, level with its top
        /// <summary>Arm length of the cross that marks the anchor inside its block definition.</summary>
        public const double AnchorMarkSize = 0.25;
    }

    // ------------------------------------------------------------ Expected test values

    /// <summary>Section 8 pass values, in cubic feet (divide by 27 for CY) or square feet,
    /// with the derivation of each. Offsets follow the contracts: negative = left, and left
    /// of S is +y.</summary>
    public static class Expected
    {
        public static double Cy(double ft3) => ft3 / CubicFeetPerCubicYard;

        // T1: A, START..END@500, offsets (-100, +100). Every pyramid base (|y| <= 45) is inside
        // the band, so each segment holds the whole pyramids whose base x-range it contains.
        //   [0,500]     P1 (80..170) cut 2700, P2 (330..420) fill 1350  -> C 100, F 50 CY
        //   [500,1000]  none                                             -> 0, 0
        //   [1000,1500] P3 cut 2700, P4 fill 1350                        -> C 100, F 50
        //   [1500,2000] P5 (1705..1795) fill 2700                        -> C 0, F 100
        public static readonly (double From, double To, double CutFt3, double FillFt3)[] T1 =
        {
            (0, 500, 2700, 1350), (500, 1000, 0, 0), (1000, 1500, 2700, 1350), (1500, 2000, 0, 2700),
        };

        // T2: A, breaks 0, 500, 1300, 1800, 2000. P4's base starts at 1330, so [500,1300] holds
        // only P3, and [1300,1800] holds P4 and P5.
        //   C/F CY: 100/50, 100/0, 0/150, 0/0
        public static readonly (double From, double To, double CutFt3, double FillFt3)[] T2 =
        {
            (0, 500, 2700, 1350), (500, 1300, 2700, 0), (1300, 1800, 0, 4050), (1800, 2000, 0, 0),
        };

        // T3: A, breaks 0, 125, 500. The break at x = 125 passes through P1's apex; the pyramid
        // is symmetric about it, so each side gets half: [0,125] C 50 CY; [125,500] C 50 + P2 F 50.
        public static readonly (double From, double To, double CutFt3, double FillFt3)[] T3 =
        {
            (0, 125, 1350, 0), (125, 500, 1350, 1350),
        };

        // T4: A, [0,500], offsets (-100, 0): the band is y in [0, 100] (left of S is +y). The lateral
        // edge y = 0 runs through both apexes, halving P1 and P2: C 50, F 25 CY.
        public const double T4CutFt3 = 1350, T4FillFt3 = 675;

        // T5: B, START..END@500, offsets (-100, +100): 30 ft2 x 500 = 15,000 ft3 = 555.556 CY each.
        public const double T5FillFt3PerSegment = RidgeSectionFt2 * 500.0;

        // T6: B, [0,500], offsets (-30, +15): band y in [-15, 30]. Section: y 0..30 is a triangle
        // 30 x 1 / 2 = 15; y -15..0 is a trapezoid (1 + 0.5)/2 x 15 = 11.25. 26.25 x 500 =
        // 13,125 ft3 = 486.111 CY.
        public const double T6FillFt3 = (15.0 + 11.25) * 500.0;

        // T7: D on C, [300, 614.159] = the arc, offsets (-100, +100). C turns right, so the right
        // (+) side is the inside: radii 200 and 400. Annular sector (delta/2)(Ro^2 - Ri^2) =
        // (pi/6)(160,000 - 40,000) = 20,000 pi = 62,831.853 ft2; slab 1 ft deep: 62,831.853 ft3
        // = 2327.106 CY.
        public static readonly double T7FillFt3 = (C.Delta / 2.0) * (400.0 * 400.0 - 200.0 * 200.0) * SlabRaise;

        // T8: same segment, offsets (-50, +150): radii 350 outside, 150 inside.
        // (pi/6)(122,500 - 22,500) = 52,359.878 ft3 = 1939.255 CY.
        public static readonly double T8FillFt3 = (C.Delta / 2.0) * (350.0 * 350.0 - 150.0 * 150.0) * SlabRaise;

        // T9: offsets (-50, +300): inner radius 300 - 300 = 0, E201, no number.

        // T23: A, T2's breaks, offsets (-100, +100): plan area 200 x length =
        // 100,000 / 160,000 / 100,000 / 40,000 SF, total 400,000; in acres (/43,560)
        // 2.2957 / 3.6731 / 2.2957 / 0.9183, export total 9.1827.
        public static readonly double[] T23AreaFt2 = { 100_000, 160_000, 100_000, 40_000 };

        // T24: D on C, [300, 614.159], offsets (-100, +100): same annular sector as T7 = 62,831.85 SF.
        public static readonly double T24AreaFt2 = (C.Delta / 2.0) * (400.0 * 400.0 - 200.0 * 200.0);

        // T25: H, OHWM 100, breaks 0, 500, 1000, offsets (-100, +100). Cut below OHWM over the full
        // 32 ft width: area 32 x 500 = 16,000 ft2 (0.37 ac), volume 52 x 500 = 26,000 ft3 =
        // 962.963 CY, average 962.963 / 500 = 1.93 CY/ft.
        public const double T25AreaFt2 = HCutWidth * 500.0, T25VolumeFt3 = HCutSectionFt2 * 500.0;

        // T26: OHWM 97. EG_H reaches 97 at |y| = 10 + 3 = 13, so the impacted width is 26:
        // area 13,000 ft2 (0.30 ac). Section: bed 2 x 20 = 40, plus each side y 10..13 with depth
        // 2 - (|y| - 10)/3: 3 x 2 - 9/6 = 4.5, so 49 ft2; 49 x 500 = 24,500 ft3 = 907.407 CY,
        // average 1.81 CY/ft.
        public const double T26AreaFt2 = 26.0 * 500.0, T26VolumeFt3 = 49.0 * 500.0;

        // T27: OHWM 95, below the EG_H bed (96): length 0, area 0, volume 0, average N/A.

        // T28: H, OHWM 100, segment [1000, 1500]: impacted from 1000 through the end of the
        // transition at 1310, so length 310. Volume: 52 x 300 = 15,600 ft3 (577.78 CY) up to
        // 1300, plus the transition between 0 and 52 x 10 = 520 ft3: at most 16,120 ft3
        // (597.04 CY).
        public const double T28Length = TransitionToX - 1000.0;
        public const double T28VolumeMinFt3 = HCutSectionFt2 * (TransitionFromX - 1000.0);
        public const double T28VolumeMaxFt3 = HCutSectionFt2 * (TransitionToX - 1000.0);
    }
}

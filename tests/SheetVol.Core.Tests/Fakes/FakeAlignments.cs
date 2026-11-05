namespace SheetVol.Core.Tests.Fakes;

/// <summary>
/// The fixture alignments of HANDOFF.md section 8, built in code. Drawing units are feet.
/// The FX work package builds the same shapes in Civil 3D; where section 8 leaves a number
/// open it is fixed here and stated on the method.
/// <para>This is the one set of alignment fakes for every Core test suite (planning,
/// geometry, views, tracking, integrator). A suite that needs a shape section 8 reads
/// differently gets its own named method here that says how it differs, never a private
/// alignment class.</para>
/// </summary>
public static class FakeAlignments
{
    /// <summary>
    /// Raw station of the PT on alignment C: 300 + 100 pi, computed as the builder does
    /// (300 + R * delta). Use this, not 300 + 100 * Math.PI, which is one unit in the last
    /// place larger.
    /// </summary>
    public const double C_PT = 300 + 300 * (Math.PI / 3);

    /// <summary>Alignment S: straight from (0,0) to (2000,0), station 0 at x = 0; left is +y.</summary>
    /// <param name="startStation">Raw station at the start point; 0 in section 8, other values for E207.</param>
    /// <param name="origin">Start point; (0,0) in section 8, state-plane values for E1009-type tests.</param>
    public static FakeAlignment S(double startStation = 0, Pt origin = default) =>
        new AlignmentBuilder(origin, 0, startStation).Line(2000).Build("S");

    /// <summary>
    /// Alignment C: tangent 300 east from (0,0), arc R = 300 clockwise through 60 degrees
    /// (length 100 pi = 314.159, center (300, -300)), tangent 300. Stations 0 to 914.159.
    /// </summary>
    /// <param name="origin">Start point; (0,0) in section 8. The whole alignment moves with it.</param>
    public static FakeAlignment C(Pt origin = default) =>
        new AlignmentBuilder(origin, 0).Line(300).Arc(300, Math.PI / 3, clockwise: true).Line(300).Build("C");

    /// <summary>
    /// A straight alignment for tests that need a bearing or a start point other than S's.
    /// </summary>
    /// <param name="start">Start point.</param>
    /// <param name="bearing">Direction of travel, radians counter-clockwise from +X.</param>
    /// <param name="length">Length, drawing units.</param>
    /// <param name="startStation">Raw station at <paramref name="start"/>.</param>
    /// <param name="name">Alignment name.</param>
    public static FakeAlignment Straight(Pt start, double bearing, double length, double startStation = 0, string name = "S") =>
        new AlignmentBuilder(start, bearing, startStation).Line(length).Build(name);

    /// <summary>
    /// Alignment K: tangent 1000 east from (0,0), then a 30 degree deflection to the left
    /// with no curve (angle point at station 1000), then tangent 1000. "30 degree angle
    /// point" in section 8 is read as the deflection.
    /// </summary>
    public static FakeAlignment K() =>
        new AlignmentBuilder(new Pt(0, 0), 0).Line(1000).Angle(Math.PI / 6).Line(1000).Build("K");

    /// <summary>
    /// Alignment SP, turning right: tangent 200 east from (0,0), spiral 150 (tangent to
    /// R 500), arc R 500 length 200, spiral 150 (R 500 to tangent), tangent 200. Stations 0 to
    /// 900; total deflection 0.7 radians.
    /// </summary>
    public static FakeAlignment SP() =>
        new AlignmentBuilder(new Pt(0, 0), 0)
            .Line(200)
            .Spiral(150, double.PositiveInfinity, 500, clockwise: true)
            .Arc(500, 200.0 / 500, clockwise: true)
            .Spiral(150, 500, double.PositiveInfinity, clockwise: true)
            .Line(200)
            .Build("SP");

    /// <summary>
    /// The SP shape the C5 integrator tests were written against. It differs from
    /// <see cref="SP"/> in the curve radius only: R 400 here, R 500 there (tangent 200, spiral
    /// 150 from tangent to R 400 turning right, arc R 400 length 200, spiral 150 back to
    /// tangent, tangent 200; stations 0 to 900, total deflection 0.875 radians). Kept so those
    /// tests keep their intent; FX's Civil 3D SP is a third reading (see NOW.md, From C1).
    /// </summary>
    public static FakeAlignment SP_C5() =>
        new AlignmentBuilder(new Pt(0, 0), 0)
            .Line(200)
            .Spiral(150, double.PositiveInfinity, 400, clockwise: true)
            .ArcByLength(400, 200, clockwise: true)
            .Spiral(150, 400, double.PositiveInfinity, clockwise: true)
            .Line(200)
            .Build("SP");
}

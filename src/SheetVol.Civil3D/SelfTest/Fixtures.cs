// FX work package (HANDOFF.md section 10, Wave 1). Owner: FX.
//
// Public entry points for the code-built fixtures of HANDOFF.md section 8. Other
// workers (S1, S2, A3, A4's SVSELFTEST) call these; expected values live in
// FixtureSpec. Every method runs in its own transaction and commits on success;
// when called inside an outer transaction it nests and commits into it. Any
// failure throws FixtureException (SV-E19x) and nothing from that call is kept.
//
// The caller holds the document lock when it is not in a modal command.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;

namespace SheetVol.Civil3D.SelfTest;

/// <summary>A base/comparison surface pair. <c>BaseReused</c> is true when the fixture EG
/// was already in the drawing (A, B and D share it). <c>Checks</c> lists every
/// count and elevation probe that passed.</summary>
public sealed record SurfaceFixture(
    string Fixture,
    ObjectId BaseId, string BaseName,
    ObjectId ComparisonId, string ComparisonName,
    bool BaseReused,
    IReadOnlyList<string> Checks);

/// <summary>A built alignment; stations are raw, as read back from Civil 3D.</summary>
public sealed record AlignmentFixture(
    string Name, ObjectId Id,
    double StartStation, double EndStation,
    IReadOnlyList<string> Checks);

/// <summary>The test template layout and what sits on it. <c>ViewportId</c> is the plan
/// viewport (on SV-VPORT); the layout also holds its overall paper-space viewport.</summary>
public sealed record TemplateFixture(
    string LayoutName, ObjectId LayoutId,
    ObjectId ViewportId, ObjectId AnchorReferenceId, ObjectId AnchorDefinitionId, ObjectId ViewportLayerId,
    double ViewportWidth, double ViewportHeight,
    IReadOnlyList<string> Notes);

/// <summary>A fixture verification volume surface and its raw totals from
/// <c>GetVolumeProperties()</c>, unadjusted, in cubic drawing units, with no sign or unit
/// mapping applied (that mapping is probe P1/P2 and lives in the adapters).</summary>
public sealed record VolumeFixture(
    string Name, ObjectId Id,
    string BaseName, string ComparisonName,
    double UnadjustedCut, double UnadjustedFill, double UnadjustedNet,
    int Triangles);

/// <summary>One drawing name the settings expect (SVFIXTURE T). <c>Status</c> is "created",
/// "present" (left untouched) or "not set" (the setting is null, which switches its lookup
/// off; <c>Name</c> is then empty). <c>Keys</c> are every settings key naming it.</summary>
public sealed record SettingsNameResult(string Kind, string Name, string Status, IReadOnlyList<string> Keys);

/// <summary>The result of SVFIXTURE T: where the settings came from and one result per name.</summary>
public sealed record SettingsNamesFixture(string SettingsSource, IReadOnlyList<SettingsNameResult> Names);

public static class Fixtures
{
    /// <summary>Names SVFIXTURE accepts, in the order ALL builds them.</summary>
    public static readonly string[] BuildNames = { "A", "B", "D", "H", "S", "C", "K", "SP", "TEMPLATE" };

    /// <summary>EG (created, or reused when the fixture EG is present) and FG_A: five pyramids.</summary>
    public static SurfaceFixture CreateA(Database db) => InCivil(db, FixtureSurfaces.BuildA);

    /// <summary>EG and FG_B: a ridge 60 wide and 1 high from x = -100 to 2100.</summary>
    public static SurfaceFixture CreateB(Database db) => InCivil(db, FixtureSurfaces.BuildB);

    /// <summary>EG and FG_D: EG raised 1.00.</summary>
    public static SurfaceFixture CreateD(Database db) => InCivil(db, FixtureSurfaces.BuildD);

    /// <summary>EG_H (channel) and FG_H (sediment to x = 1300, transition to 1310), plus (rev 3) the flat
    /// OHWM surfaces OHWM_100, OHWM_97 and OHWM_95 over H's extent plus 20 ft on every side. The returned pair is EG_H/FG_H; the
    /// OHWM surfaces' count and elevation checks are in <see cref="SurfaceFixture.Checks"/>.</summary>
    public static SurfaceFixture CreateH(Database db) => InCivil(db, FixtureSurfaces.BuildH);

    /// <summary>
    /// Rev 3: Fixture H's flat OHWM surfaces as C5 triangle lists (two triangles each, the same flat
    /// rectangle the TIN holds), for the in-memory fake world. Adds or replaces them in
    /// <paramref name="surfaces"/> by name.
    /// </summary>
    public static void AddOhwmSurfaces(IDictionary<string, SheetVol.Core.Volumes.Integrator.Fixtures.TriangleListSource> surfaces)
    {
        var (x0, y0) = FixtureSpec.OhwmCorners[0];
        var (x1, y1) = FixtureSpec.OhwmCorners[2];
        foreach (var (name, z) in FixtureSpec.OhwmSurfaces)
            surfaces[name] = SheetVol.Core.Volumes.Integrator.Fixtures.FixtureSurfaces.Flat(name, z, x0, y0, x1, y1);
    }

    /// <summary>Alignment S: straight (0,0)-(2000,0).</summary>
    public static AlignmentFixture CreateAlignmentS(Database db) => InCivil(db, FixtureAlignments.BuildS);

    /// <summary>Alignment C: tangent 300, arc R 300 clockwise 60 degrees, tangent 300.</summary>
    public static AlignmentFixture CreateAlignmentC(Database db) => InCivil(db, FixtureAlignments.BuildC);

    /// <summary>Alignment K: tangents 500 and 300 meeting at a 30 degree angle point.</summary>
    public static AlignmentFixture CreateAlignmentK(Database db) => InCivil(db, FixtureAlignments.BuildK);

    /// <summary>Alignment SP: tangent, spiral 100, arc R 500, spiral 100, tangent; delta 30 degrees.</summary>
    public static AlignmentFixture CreateAlignmentSP(Database db) => InCivil(db, FixtureAlignments.BuildSP);

    /// <summary>Adds the test template layout (viewport on SV-VPORT, SV_TABLE_ANCHOR) to <paramref name="db"/>.</summary>
    public static TemplateFixture CreateTemplate(Database db)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var result = Wrap(() => FixtureTemplate.Build(db, tr));
        tr.Commit();
        return result;
    }

    /// <summary>Writes a new drawing at <paramref name="path"/> holding only the template layout,
    /// then rereads and checks it; a failed reread deletes the file. Refuses to overwrite an
    /// existing file. Returns check notes. CAVEAT (measured): the plan viewport's scale drifts
    /// from 1:50 on the file's first activation after reopening; where the scale matters (T18),
    /// build with <see cref="CreateTemplate"/> into an open drawing and save that instead.</summary>
    public static IReadOnlyList<string> WriteTemplateDrawing(string path) => Wrap(() => FixtureTemplate.WriteDrawing(path));

    /// <summary>Creates a TinVolumeSurface from two existing surfaces and reads its totals.
    /// Default name: FX-VOL-&lt;base&gt;-&lt;comparison&gt;.</summary>
    public static VolumeFixture CreateVolume(Database db, string baseName, string comparisonName, string? volumeName = null) =>
        InCivil(db, (d, tr, civ) => FixtureSurfaces.BuildVolume(d, tr, civ, baseName, comparisonName,
            volumeName ?? FixtureSurfaces.VolumeName(baseName, comparisonName)));

    /// <summary>For every fixture pair (EG/FG_A, EG/FG_B, EG/FG_D, EG_H/FG_H) whose surfaces are in
    /// the drawing and whose FX-VOL surface is not, creates it. Returns the ones created.</summary>
    public static IReadOnlyList<VolumeFixture> CreateFixtureVolumes(Database db) =>
        InCivil(db, (d, tr, civ) =>
        {
            var made = new List<VolumeFixture>();
            foreach (var (b, c) in FixtureSurfaces.Pairs)
            {
                var name = FixtureSurfaces.VolumeName(b, c);
                if (FixtureSurfaces.FindSurface(tr, civ, b).IsNull || FixtureSurfaces.FindSurface(tr, civ, c).IsNull) continue;
                if (!FixtureSurfaces.FindSurface(tr, civ, name).IsNull) continue;
                made.Add(FixtureSurfaces.BuildVolume(d, tr, civ, b, c, name));
            }
            return made;
        });

    /// <summary>Creates every layer, surface style, table style, text style and block that
    /// <paramref name="settings"/> names and the drawing lacks, so a regen can complete on a
    /// fixture drawing; existing ones are reported present and left alone. Rerunnable.</summary>
    public static SettingsNamesFixture CreateSettingsNames(Database db, Session.SheetVolSettings settings, string settingsSource) =>
        InCivil(db, (d, tr, civ) => FixtureSettingsNames.Build(d, tr, civ, settings, settingsSource));

    /// <summary>The analytic whole-surface totals for a fixture pair in words, or null for other pairs.</summary>
    public static string? ExpectedWholeSurface(string baseName, string comparisonName) =>
        FixtureSurfaces.ExpectedWholeSurface(baseName, comparisonName);

    // ------------------------------------------------------------ plumbing

    private static T InCivil<T>(Database db, Func<Database, Transaction, CivilDocument, T> build)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var result = Wrap(() =>
        {
            var civ = CivilDocument.GetCivilDocument(db)
                ?? throw new FixtureException(FixtureCodes.BuildFailed, "CivilDocument.GetCivilDocument returned null; is this a Civil 3D session?");
            return build(db, tr, civ);
        });
        tr.Commit();
        return result;
    }

    /// <summary>Turns any non-fixture exception into SV-E190 with the original type and message.</summary>
    private static T Wrap<T>(Func<T> f)
    {
        try { return f(); }
        catch (FixtureException) { throw; }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            throw new FixtureException(FixtureCodes.BuildFailed, $"AutoCAD {ex.ErrorStatus}: {ex.Message}", ex);
        }
        catch (System.Exception ex)
        {
            throw new FixtureException(FixtureCodes.BuildFailed, $"{ex.GetType().FullName}: {ex.Message}", ex);
        }
    }
}

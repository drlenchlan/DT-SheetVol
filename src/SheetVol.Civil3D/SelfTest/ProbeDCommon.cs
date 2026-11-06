// S4 probe D (NOW.md X6, Dan 2026-11-06): can a TIN volume surface the user already made be
// an analysis source in place of a Base + Comparison pair? Evidence code, not product code:
// every command prints what it observed so docs/api-findings.md section D can quote it.
// Reuses S1's harness (ProbeACommands.Run/Say/Bounded/Band/Surf) so the output reads the
// same; names and numbers specific to probe D live here, in one place.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using SheetVol.Civil3D.Adapters;
using SheetVol.Core;
using SheetVol.Core.Volumes;
using static SheetVol.Civil3D.SelfTest.ProbeACommands;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using N = SheetVol.Civil3D.SelfTest.FixtureSpec.Names;

[assembly: CommandClass(typeof(SheetVol.Civil3D.SelfTest.ProbeDCommands))]

namespace SheetVol.Civil3D.SelfTest;

/// <summary>Every name and number probe D uses. Probe-only values.</summary>
internal static class ProbeDSettings
{
    /// <summary>The user's volume surface: what <c>SVFIXTURE VOLUMES</c> makes for EG / FG_A.</summary>
    public static string UserVol => FixtureSurfaces.VolumeName(N.EG, N.FgA);

    /// <summary>Item 6: a second user volume surface on the same pair, given an outer boundary.</summary>
    public static string UserVolOuter => UserVol + "-OUTER";

    /// <summary>Item 6: outer boundary, x -150..800 by y -250..250. Holds P1 and P2 (segment 1)
    /// and none of P3, P4, P5 (segments 2 and 3).</summary>
    public static readonly (double X0, double Y0, double X1, double Y1) Outer = (-150, -250, 800, 250);
    public const double BoundaryMidOrdinate = 0.1;

    /// <summary>Item 3: the factors a user would type in Surface Properties.</summary>
    public const double CutFactor = 1.25, FillFactor = 0.9;

    /// <summary>Item 4: P3's apex (segment 2, [500, 1300]) lowered from 99.0 to 98.0, so the
    /// segment's cut doubles from 2700 to 5400 ft3 (pyramid volume is linear in height).</summary>
    public static readonly (double X, double Y) P3Apex = (FixtureSpec.Pyramids[2].CenterX, 0.0);
    public static double P3ApexZ2 => FixtureSpec.Pyramids[2].ApexZ - 1.0;
    public const double Seg2CutAfterEditFt3 = 5400.0;

    /// <summary>Item 4: the Xrecord written in the same command as the Rebuild, so one UNDO is
    /// shown to have acted on that command whatever happens to the surface.</summary>
    public const string UndoMarkerKey = "SV-PROBED-UNDO-MARKER";

    /// <summary>Item 2: the owned surface A1 makes for EG / FG_A (C2's default naming).</summary>
    public static string OwnedFallbackName => $"{new VolumeConfig().OwnedSurfacePrefix}{N.EG}-{N.FgA}";
}

/// <summary>Command class for probe D; partial across the ProbeD*.cs files.</summary>
public static partial class ProbeDCommands
{
    private static string? _ownedName;

    private static string OwnedName => _ownedName ?? ProbeDSettings.OwnedFallbackName;

    /// <summary>T2's breaks on alignment S as segments (FixtureSpec.Expected.T2).</summary>
    private static (double From, double To, double CutFt3, double FillFt3)[] Segs => FixtureSpec.Expected.T2;

    /// <summary>GetBoundedVolumes over every T2 segment, one printed line each; null where the call threw.</summary>
    private static SurfaceVolumeInfo?[] Segments(CivSurface s, Alignment al, string tag)
    {
        var res = new SurfaceVolumeInfo?[Segs.Length];
        for (int i = 0; i < Segs.Length; i++)
        {
            var (from, to, _, _) = Segs[i];
            res[i] = Bounded(s, Band(al, new[] { from, to }, ProbeASettings.OffsetLeft, ProbeASettings.OffsetRight), $"T2 seg {i + 1} [{F(from)},{F(to)}] {tag}");
        }
        return res;
    }

    /// <summary>GetBoundedVolumes over one T2 segment (1-based).</summary>
    private static SurfaceVolumeInfo? Segment(CivSurface s, Alignment al, int n, string tag)
    {
        var (from, to, _, _) = Segs[n - 1];
        return Bounded(s, Band(al, new[] { from, to }, ProbeASettings.OffsetLeft, ProbeASettings.OffsetRight), $"T2 seg {n} [{F(from)},{F(to)}] {tag}");
    }

    /// <summary>One line with the surface's state and whole-surface volume properties.</summary>
    private static void State(string tag, TinVolumeSurface v)
    {
        string props;
        try
        {
            var p = v.GetVolumeProperties();
            props = $"UnadjustedCut={R(p.UnadjustedCutVolume)} UnadjustedFill={R(p.UnadjustedFillVolume)} AdjustedCut={R(p.AdjustedCutVolume)} AdjustedFill={R(p.AdjustedFillVolume)} props.CutFactor={R(p.CutFactor)} props.FillFactor={R(p.FillFactor)}";
        }
        catch (System.Exception ex) { props = $"GetVolumeProperties threw {Describe(ex)}"; }
        Say($"[{tag}] {v.Name}: IsOutOfDate={S(() => v.IsOutOfDate)} CutFactor={S(() => v.CutFactor)} FillFactor={S(() => v.FillFactor)} z({F(ProbeDSettings.P3Apex.X)},0)={S(() => v.FindElevationAtXY(ProbeDSettings.P3Apex.X, ProbeDSettings.P3Apex.Y))} {props}");
    }

    /// <summary>A1's path: ensure (create or find, write factors, rebuild) the owned surface for EG / FG_A.</summary>
    private static TinVolumeSurface? EnsureOwned(Transaction tr, Database db, string tag)
    {
        var def = new AnalysisDef(Guid.NewGuid(), "EG vs FG_A", N.EG, N.FgA);
        var o = AnalysisResolver.EnsureAnalyses(tr, db, new[] { def }, null, new AdapterConfig(), new VolumeConfig());
        foreach (var p in o.Problems) Say($"[{tag}] EnsureAnalyses problem {p.Code} {p.Severity}: {p.Message}");
        if (!o.Succeeded) { Say($"[{tag}] EnsureAnalyses failed"); return null; }
        var owned = o.Value[0].Owned;
        _ownedName = owned.Name;
        Say($"[{tag}] EnsureAnalyses: owned '{owned.Name}' handle {owned.Handle} created={owned.Created} triangles={owned.Triangles} factors {R(owned.CutFactor)}/{R(owned.FillFactor)} base {owned.Sources.Base.Name} comparison {owned.Sources.Comparison.Name}");
        return (TinVolumeSurface)tr.GetObject(owned.Id, OpenMode.ForRead);
    }

    /// <summary>A surface's name from its id, or why it could not be opened.</summary>
    private static string NameOf(Transaction tr, ObjectId id)
    {
        if (id.IsNull) return "ObjectId.Null";
        if (id.IsErased) return $"{id} (IsErased=True)";
        try { return tr.GetObject(id, OpenMode.ForRead) is CivSurface s ? $"'{s.Name}' {id} ({s.GetType().Name})" : $"{id} (not a surface)"; }
        catch (System.Exception ex) { return $"{id} open threw {Describe(ex)}"; }
    }

    private static string Diff(double? a, double? b) => a is { } x && b is { } y ? E(x - y) : "n/a";
}

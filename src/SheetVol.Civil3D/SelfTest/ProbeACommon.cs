// S1 probes (HANDOFF.md 5.3: P1-P10, P18, P19, P22, P23). Evidence code, not product
// code: every command prints what it observed so docs/api-findings.md section A can quote
// it. Shared names, numbers and helpers for the ProbeA*.cs files live here, in one place.
// Fixtures come from FX's API (Fixtures.CreateA/B/D, CreateAlignmentS/C/K/SP,
// CreateVolume); analytic expectations come from FixtureSpec.

using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(SheetVol.Civil3D.SelfTest.ProbeACommands))]

namespace SheetVol.Civil3D.SelfTest;

/// <summary>Every name and number the S1 probes use. Probe-only values; the product's
/// equivalents come from config (HANDOFF.md 6.5, 6.13).</summary>
internal static class ProbeASettings
{
    // Owned-style volume surfaces (6.5 names the tool's own as SV-<base>-<comparison>).
    public static string VolA => $"SV-{FixtureSpec.Names.EG}-{FixtureSpec.Names.FgA}";
    public static string VolB => $"SV-{FixtureSpec.Names.EG}-{FixtureSpec.Names.FgB}";
    public static string VolD => $"SV-{FixtureSpec.Names.EG}-{FixtureSpec.Names.FgD}";

    // P1/P2/P3/P5: rectangles around one pyramid each (bases are 90 x 90 centred on y = 0).
    public static readonly (double X0, double Y0, double X1, double Y1) AroundP1 = (60, -60, 190, 60);
    public static readonly (double X0, double Y0, double X1, double Y1) AroundP2 = (310, -60, 440, 60);

    // P4: partly outside (EG ends at y = 300), wholly outside, and a hide boundary over P1.
    public static readonly (double X0, double Y0, double X1, double Y1) PartlyOutside = (60, -60, 190, 400);
    public static readonly (double X0, double Y0, double X1, double Y1) WhollyOutside = (3000, 0, 3100, 100);
    public static readonly (double X0, double Y0, double X1, double Y1) Hole = (105, -20, 145, 20);
    public static readonly (double X0, double Y0, double X1, double Y1) InsideHole = (110, -15, 140, 15);
    public const double BoundaryMidOrdinate = 0.1;

    // T1, T2, P22: band offsets (contracts: negative = left).
    public const double OffsetLeft = -100.0, OffsetRight = 100.0;

    // P22: tessellation tolerance (6.4 default MidOrdinate) and the two datums.
    public const double MidOrdinate = 0.01;
    public static readonly double[] Datums = { 0.0, 1.0, -5.0, 10.0 };

    // T1, T2: pass tolerance in CY (section 8).
    public const double TolCy = 0.01;

    // P7: stations sampled per alignment and the agreement required.
    public const int P7Stations = 20;
    public const double P7Tolerance = 1e-6;
    public const string CompoundAlignment = "PA-CC";

    // P8: one station equation on S.
    public const double EqRawBack = 1000.0, EqAhead = 800.0;
    public const double EqRawBack2 = 1500.0, EqAhead2 = 2000.0;

    // P18: the vertex edit on FG_B.
    public static readonly Point3d P18Vertex = new(1000.0, 15.0, 102.0);
    public const double P18VertexZ2 = 103.0;

    // P19: a point outside every fixture surface.
    public static readonly Point2d Outside = new(5000.0, 5000.0);

    // P23: outer boundary and mask on FG_B, grid TINs for timing.
    public static readonly (double X0, double Y0, double X1, double Y1) OuterB = (-50, -200, 2050, 200);
    public static readonly (double X0, double Y0, double X1, double Y1) MaskB = (900, -50, 1100, 50);
    public const string MaskMaterial = "Global";   // AutoCAD's default material; Masks.Add requires a Material id
    public const string GridPrefix = "PA-GRID-";
    public const double GridSpacing = 2.0;
    public const double GridZ = 100.0;

    // P6, P10: side databases and files.
    public static string OutDir => Path.Combine(Path.GetTempPath(), "sheetvol");
    public static string SourcePath => Path.Combine(OutDir, "probeA-src.dwg");
    public static string HostPath => Path.Combine(OutDir, "probeA-host.dwg");
    public static string BlankDrawing =>
        Path.Combine(Environment.GetEnvironmentVariable("SHEETVOL_REPO") ?? ".", "tools", "fixtures", "blank-c3d.dwg");
    public const string DataShortcutAssembly = "AeccDataShortcutMgd.dll";
    public const string DataShortcutType = "Autodesk.Civil.DataShortcuts.DataShortcuts";
}

/// <summary>Command class for all S1 probes; partial across the ProbeA*.cs files.</summary>
public static partial class ProbeACommands
{
    internal const string FailCode = "SV-E194";   // probe-step failure at the command boundary

    private static Editor? _ed;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static void Say(string s)
    {
        if (_ed is not null) _ed.WriteMessage("\n" + s);
        else Console.WriteLine(s);
    }

    /// <summary>Command boundary: one line with a stable code on any escape; the
    /// transactions inside are <c>using</c> blocks, so an escape aborts them.</summary>
    internal static void Run(string probe, Action<Document, Database> body)
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        _ed = doc?.Editor;
        if (doc is null) { Console.WriteLine($"{FailCode} {probe}: no active document"); return; }
        Say($"===== {probe} begin =====");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            body(doc, doc.Database);
            Say($"===== {probe} end ({sw.Elapsed.TotalSeconds:0.00} s) =====");
        }
        catch (System.Exception ex)
        {
            Say($"{FailCode} {probe} failed: {Describe(ex)}");
            Say($"===== {probe} end (FAILED) =====");
        }
    }

    /// <summary>Runs one probe step; an exception is an observation, printed, not a failure.</summary>
    internal static bool Try(string label, Action step)
    {
        try { step(); return true; }
        catch (System.Exception ex) { Say($"{label} threw {Describe(ex)}"); return false; }
    }

    internal static string Describe(System.Exception ex) => ex switch
    {
        Autodesk.AutoCAD.Runtime.Exception a => $"{a.GetType().FullName} ErrorStatus={a.ErrorStatus} Message='{a.Message}'",
        System.Reflection.TargetInvocationException t when t.InnerException is not null => Describe(t.InnerException),
        _ => $"{ex.GetType().FullName} Message='{ex.Message}'",
    };

    // ---------- formatting ----------

    /// <summary>A property read that may throw, as text: the value, or "threw &lt;type&gt;".</summary>
    internal static string S(Func<object?> read)
    {
        try
        {
            return read() switch { null => "null", double d => R(d), Point2d p => P(p), var o => o.ToString() ?? "null" };
        }
        catch (System.Exception ex) { return $"threw {ex.GetType().Name}('{ex.Message}')"; }
    }

    internal static string R(double d) => d.ToString("R", Inv);
    internal static string F(double d) => d.ToString("0.######", Inv);
    internal static string F3(double d) => d.ToString("0.000", Inv);
    internal static string E(double d) => d.ToString("0.00E+00", Inv);
    internal static string P(Point2d p) => $"({F(p.X)}, {F(p.Y)})";
    internal static string P(Point3d p) => $"({F(p.X)}, {F(p.Y)}, {F(p.Z)})";
    internal static double Cy(double ft3) => ft3 / FixtureSpec.CubicFeetPerCubicYard;

    // ---------- polygons ----------

    /// <summary>A closed polygon (first point repeated last) from plan points at z = 0.</summary>
    internal static Point3dCollection Closed(IEnumerable<(double X, double Y)> pts)
    {
        var list = pts.ToList();
        var c = new Point3dCollection();
        foreach (var (x, y) in list) c.Add(new Point3d(x, y, 0.0));
        c.Add(new Point3d(list[0].X, list[0].Y, 0.0));
        return c;
    }

    /// <summary>Axis-aligned rectangle, counter-clockwise from the south-west corner unless
    /// <paramref name="ccw"/> is false.</summary>
    internal static Point3dCollection Rect((double X0, double Y0, double X1, double Y1) r, bool ccw = true)
    {
        var pts = new[] { (r.X0, r.Y0), (r.X1, r.Y0), (r.X1, r.Y1), (r.X0, r.Y1) };
        if (!ccw) Array.Reverse(pts);
        return Closed(pts);
    }

    /// <summary>Signed shoelace area of a closed polygon (positive = counter-clockwise).</summary>
    internal static double SignedArea(Point3dCollection poly)
    {
        double a = 0;
        for (int i = 0; i + 1 < poly.Count; i++) a += poly[i].X * poly[i + 1].Y - poly[i + 1].X * poly[i].Y;
        return a / 2.0;
    }

    /// <summary>Clips a closed polygon to the half-plane a·x + b·y + c ≥ 0 (Sutherland–Hodgman).</summary>
    internal static List<(double X, double Y)> ClipHalfPlane(List<(double X, double Y)> poly, double a, double b, double c)
    {
        var res = new List<(double, double)>();
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % n];
            double fp = a * p.X + b * p.Y + c, fq = a * q.X + b * q.Y + c;
            if (fp >= 0) res.Add(p);
            if ((fp >= 0) != (fq >= 0))
            {
                double t = fp / (fp - fq);
                res.Add((p.X + t * (q.X - p.X), p.Y + t * (q.Y - p.Y)));
            }
        }
        return res;
    }

    internal static double Area(List<(double X, double Y)> poly)
    {
        double a = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return Math.Abs(a) / 2.0;
    }

    // ---------- bounded volumes ----------

    /// <summary>GetBoundedVolumes with the call and its result (or exception) on one line.</summary>
    internal static SurfaceVolumeInfo? Bounded(CivSurface s, Point3dCollection poly, string label, double? datum = null)
    {
        string call = datum is null ? $"{s.Name}.GetBoundedVolumes({label})" : $"{s.Name}.GetBoundedVolumes({label}, datum {F(datum.Value)})";
        try
        {
            var v = datum is null ? s.GetBoundedVolumes(poly) : s.GetBoundedVolumes(poly, datum.Value);
            Say($"{call}: Cut={R(v.Cut)} Fill={R(v.Fill)} Net={R(v.Net)}");
            return v;
        }
        catch (System.Exception ex)
        {
            Say($"{call} threw {Describe(ex)}");
            return null;
        }
    }

    // ---------- lookups (caller supplies the transaction) ----------

    internal static CivilDocument Civil(Database db) =>
        CivilDocument.GetCivilDocument(db) ?? throw new InvalidOperationException("CivilDocument.GetCivilDocument returned null");

    internal static T Surf<T>(Transaction tr, Database db, string name, OpenMode mode = OpenMode.ForRead) where T : CivSurface
    {
        foreach (ObjectId id in Civil(db).GetSurfaceIds())
            if (tr.GetObject(id, OpenMode.ForRead) is CivSurface s && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                if (mode == OpenMode.ForWrite) s.UpgradeOpen();
                return s as T ?? throw new InvalidOperationException($"surface '{name}' is a {s.GetType().Name}, not a {typeof(T).Name}");
            }
        throw new InvalidOperationException($"surface '{name}' not found; run SVPROBEA_SETUP first");
    }

    internal static Alignment Align(Transaction tr, Database db, string name, OpenMode mode = OpenMode.ForRead)
    {
        foreach (ObjectId id in Civil(db).GetAlignmentIds())
            if (tr.GetObject(id, OpenMode.ForRead) is Alignment a && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                if (mode == OpenMode.ForWrite) a.UpgradeOpen();
                return a;
            }
        throw new InvalidOperationException($"alignment '{name}' not found; run SVPROBEA_SETUP first");
    }

    internal static (double X, double Y) At(Alignment al, double station, double offset)
    {
        double e = 0, n = 0;
        al.PointLocation(station, offset, ref e, ref n);
        return (e, n);
    }

    /// <summary>Band polygon on an alignment: the left offset line forward, the right one back.
    /// Stations are used as given (the caller tessellates).</summary>
    internal static Point3dCollection Band(Alignment al, IReadOnlyList<double> stations, double left, double right)
    {
        var pts = new List<(double, double)>();
        foreach (var s in stations) pts.Add(At(al, s, left));
        for (int i = stations.Count - 1; i >= 0; i--) pts.Add(At(al, stations[i], right));
        return Closed(pts);
    }

    internal static string TriCounts(TinSurface s)
    {
        int all = 0, visible = 0, hidden = 0, prop = s.GetTinProperties().NumberOfTriangles;
        using (var t = s.GetTriangles(true)) { all = t.Count; foreach (TinSurfaceTriangle tri in t) if (!tri.IsVisible) hidden++; }
        using (var t = s.GetTriangles(false)) visible = t.Count;
        int coll = s.Triangles.Count;
        return $"{s.Name}: GetTriangles(true).Count={all} GetTriangles(false).Count={visible} hidden(IsVisible=false)={hidden} Triangles.Count={coll} GetTinProperties().NumberOfTriangles={prop} Vertices.Count={s.Vertices.Count}";
    }
}

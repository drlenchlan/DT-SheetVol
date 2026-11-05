// S2 probes (HANDOFF.md 5.3: P11, P12, P13, P17, P20, P24). Evidence code, not product
// code: every command prints what it observed so docs/api-findings.md section B can quote
// it. Shared names, paths and helpers for the ProbeB*.cs files live here, in one place.

using System.Globalization;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(SheetVol.Civil3D.SelfTest.ProbeBCommands))]

namespace SheetVol.Civil3D.SelfTest;

/// <summary>Every name and number the S2 probes use. Probe-only values; the product's
/// equivalents come from config (HANDOFF.md 6.7, 6.13).</summary>
internal static class ProbeBSettings
{
    public const string VportLayer = "SV-VPORT";             // 6.7 plan viewport layer
    public const string AnchorBlock = "SV_TABLE_ANCHOR";     // 6.7 table anchor block
    public const string TemplateLayout = "SV-TEMPLATE";      // layout name inside the code-built DWT
    public const string LayoutPrefix = "SV-";                // the snapshot covers layouts with this prefix
    public const string PlotDevice = "DWG To PDF.pc3";
    public const string MediaHint = "11.00_x_17.00";         // ANSI B landscape in DWG To PDF
    public const string CtbSheet = "monochrome.ctb";
    public const string StbSheet = "monochrome.stb";

    public static string OutDir => Path.Combine(Path.GetTempPath(), "sheetvol");
    public static string TemplatePath => Path.Combine(OutDir, "probeB-template.dwt");
    public static string OutPath => Path.Combine(OutDir, "probeB-out.dwg");
    public static string ReopenScriptPath => Path.Combine(OutDir, "probeB-reopen.scr");

    // P12: the known model point and its neighbour 100 units east.
    public static readonly Point3d ModelPoint = new(5000.0, 3000.0, 0.0);
    public static readonly Point3d ModelPointEast = new(5100.0, 3000.0, 0.0);
    public const double Twist = Math.PI / 6.0;               // 30 degrees
    public const double Scale = 1.0 / 50.0;                  // 1" = 50'
    public static readonly Point3d VpPaperCenter = new(8.5, 5.5, 0.0);
    public const double VpWidth = 12.0;
    public const double VpHeight = 7.0;

    // P17: a ratio no stock list carries, so the probe must add it.
    public const double MissingScaleRatio = 37.0;
    public const string MissingScaleName = "1\" = 37'";
}

/// <summary>Command class for all S2 probes; partial across the ProbeB*.cs files.</summary>
public static partial class ProbeBCommands
{
    internal const string FailCode = "SV-E190";   // probe-step failure at the command boundary

    private static Editor? _ed;

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
        try
        {
            body(doc, doc.Database);
            Say($"===== {probe} end =====");
        }
        catch (System.Exception ex)
        {
            Say($"{FailCode} {probe} failed: {ex.GetType().Name}: {ex.Message}");
            Say($"===== {probe} end (FAILED) =====");
        }
    }

    /// <summary>Runs one probe step; an exception is an observation, printed, not a failure.</summary>
    internal static bool Try(string label, Action step)
    {
        try { step(); return true; }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) { Say($"{label} threw {ex.ErrorStatus} ({ex.Message})"); return false; }
        catch (System.Exception ex) { Say($"{label} threw {ex.GetType().Name}: {ex.Message}"); return false; }
    }

    // ---------- formatting ----------

    internal static string F(double d) => d.ToString("0.##########", CultureInfo.InvariantCulture);
    internal static string F6(double d) => d.ToString("0.000000", CultureInfo.InvariantCulture);
    internal static string P(Point3d p) => $"({F(p.X)}, {F(p.Y)}, {F(p.Z)})";
    internal static string P(Point2d p) => $"({F(p.X)}, {F(p.Y)})";
    internal static string P6(Point2d p) => $"({F6(p.X)}, {F6(p.Y)})";
    internal static string Deg(double rad) => F(rad * 180.0 / Math.PI) + " deg";

    // ---------- database helpers (caller supplies the transaction) ----------

    internal static ObjectId EnsureLayer(Transaction tr, Database db, string name)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(name)) return lt[name];
        lt.UpgradeOpen();
        var rec = new LayerTableRecord { Name = name };
        var id = lt.Add(rec);
        tr.AddNewlyCreatedDBObject(rec, true);
        return id;
    }

    internal static ObjectId EnsureAnchorBlock(Transaction tr, Database db)
    {
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        if (bt.Has(ProbeBSettings.AnchorBlock)) return bt[ProbeBSettings.AnchorBlock];
        bt.UpgradeOpen();
        var def = new BlockTableRecord { Name = ProbeBSettings.AnchorBlock, Origin = Point3d.Origin };
        var id = bt.Add(def);
        tr.AddNewlyCreatedDBObject(def, true);
        var c = new Circle(Point3d.Origin, Vector3d.ZAxis, 0.05);
        c.SetDatabaseDefaults(db);
        def.AppendEntity(c);
        tr.AddNewlyCreatedDBObject(c, true);
        return id;
    }

    /// <summary>LayoutManager.CreateLayout without making the layout current, optionally
    /// followed by Layout.Initialize(), which gives the layout its overall (paper-space)
    /// viewport before any plan viewport is added.</summary>
    internal static ObjectId CreateLayoutHeadless(Database db, string name, bool initialize, string tag)
    {
        var id = LayoutManager.Current.CreateLayout(name);
        if (!initialize) { Say($"{tag} CreateLayout('{name}') without Initialize"); return id; }
        using var tr = db.TransactionManager.StartTransaction();
        var lay = (Layout)tr.GetObject(id, OpenMode.ForWrite);
        InitializeAndReport(tr, lay, tag);
        tr.Commit();
        return id;
    }

    internal static void InitializeAndReport(Transaction tr, Layout lay, string tag)
    {
        var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
        int before = 0;
        foreach (ObjectId v in btr) if (tr.GetObject(v, OpenMode.ForRead) is Viewport) before++;
        Try($"{tag} Layout.Initialize()", () => lay.Initialize());
        int after = 0;
        foreach (ObjectId v in btr)
            if (tr.GetObject(v, OpenMode.ForRead) is Viewport ov)
            {
                after++;
                Say($"{tag} after Initialize: Viewport {ov.Handle} {VpSummary(ov)}");
            }
        Say($"{tag} Initialize('{lay.LayoutName}'): viewports {before} -> {after}; GetViewports().Count={lay.GetViewports().Count}; CTAB={AcApp.GetSystemVariable("CTAB")}");
    }

    internal static int LayoutDictCount(Transaction tr, Database db)
        => ((DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead)).Count;

    internal static List<string> LayoutNames(Transaction tr, Database db)
    {
        var names = new List<string>();
        foreach (DBDictionaryEntry e in (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead))
            names.Add(e.Key);
        return names;
    }

    internal static List<string> PaperSpaceBlockNames(Transaction tr, Database db)
    {
        var names = new List<string>();
        foreach (ObjectId id in (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))
        {
            var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (btr.IsLayout && !string.Equals(btr.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase))
                names.Add(btr.Name);
        }
        return names;
    }

    internal static BlockTableRecord LayoutBlock(Transaction tr, ObjectId layoutId, OpenMode mode)
    {
        var lay = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
        return (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, mode);
    }

    internal static string PlotSummary(PlotSettings ps)
        => $"device='{ps.PlotConfigurationName}' media='{ps.CanonicalMediaName}' styleSheet='{ps.CurrentStyleSheet}' " +
           $"rotation={ps.PlotRotation} units={ps.PlotPaperUnits} type={ps.PlotType} std={ps.UseStandardScale}/{ps.StdScaleType} " +
           $"paper={P(ps.PlotPaperSize)}";

    internal static string VpSummary(Viewport vp)
    {
        string scale = "";
        try { scale = vp.AnnotationScale?.Name ?? "(null)"; } catch (System.Exception ex) { scale = "threw " + ex.GetType().Name; }
        return $"On={vp.On} Number={vp.Number} Locked={vp.Locked} layer={vp.Layer} center={P(vp.CenterPoint)} w={F(vp.Width)} h={F(vp.Height)} " +
               $"target={P(vp.ViewTarget)} viewCenter={P(vp.ViewCenter)} twist={Deg(vp.TwistAngle)} customScale={F(vp.CustomScale)} " +
               $"viewHeight={F(vp.ViewHeight)} dir={vp.ViewDirection} clipOn={vp.NonRectClipOn} clipId={(vp.NonRectClipEntityId.IsNull ? "null" : vp.NonRectClipEntityId.Handle.ToString())} " +
               $"annoScale={scale}";
    }

    // ---------- model-to-paper predictions (P12) ----------

    /// <summary>Hypothesis H1 (community claim, 5.2): ViewCenter is in DCS. The DCS is the
    /// WCS translated to the target and rotated by +twist (plan view), so
    /// paper = vpCenter + scale * (R(+twist)(w - target) - viewCenter).</summary>
    internal static Point2d PredictDcs(Point3d w, Point3d target, Point2d viewCenter, double twist, double scale, Point3d paperCenter, double sign = 1.0)
    {
        double dx = w.X - target.X, dy = w.Y - target.Y;
        double c = Math.Cos(sign * twist), s = Math.Sin(sign * twist);
        double ex = c * dx - s * dy, ey = s * dx + c * dy;
        return new Point2d(paperCenter.X + scale * (ex - viewCenter.X), paperCenter.Y + scale * (ey - viewCenter.Y));
    }

    /// <summary>Hypothesis H2: ViewCenter is an unrotated WCS offset from the target, so the
    /// viewport centre shows model point target + viewCenter.</summary>
    internal static Point2d PredictWcsCenter(Point3d w, Point3d target, Point2d viewCenter, double twist, double scale, Point3d paperCenter)
    {
        double dx = w.X - target.X - viewCenter.X, dy = w.Y - target.Y - viewCenter.Y;
        double c = Math.Cos(twist), s = Math.Sin(twist);
        return new Point2d(paperCenter.X + scale * (c * dx - s * dy), paperCenter.Y + scale * (s * dx + c * dy));
    }

    // ---------- acedTrans, AutoCAD's own coordinate transform (the P12 oracle) ----------

    [DllImport("accore.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "acedTrans")]
    private static extern int acedTrans(double[] point, IntPtr fromRb, IntPtr toRb, int disp, double[] result);

    private const int RTNORM = 5100;

    /// <summary>acedTrans between coordinate system codes (0 WCS, 1 UCS, 2 DCS, 3 PSDCS).</summary>
    internal static Point3d? Trans(Point3d p, short from, short to)
    {
        using var rbFrom = new ResultBuffer(new TypedValue((int)LispDataType.Int16, from));
        using var rbTo = new ResultBuffer(new TypedValue((int)LispDataType.Int16, to));
        var res = new double[3];
        int rc = acedTrans(new[] { p.X, p.Y, p.Z }, rbFrom.UnmanagedObject, rbTo.UnmanagedObject, 0, res);
        if (rc != RTNORM) { Say($"acedTrans {from}->{to} returned {rc}"); return null; }
        return new Point3d(res[0], res[1], res[2]);
    }
}

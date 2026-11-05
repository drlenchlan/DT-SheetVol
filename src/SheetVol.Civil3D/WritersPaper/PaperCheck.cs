// A3 work package. Owner: A3.
//
// SVWRITERSPAPER <step>: the headless check of the paper writers, driven by
// tools/scripts/writersPaper.scr (first session) and writersPaperReopen.scr (second
// session on the saved drawing). Steps:
//   PREP       after SVFIXTURE TEMPLATE in the open drawing: title block, key-map viewport
//              and four extra template layouts (largest, no viewport, no anchor, two plans)
//   BUILD      E601, E501, T18 generation, P12 numeric check, twist sign, E614 before
//              activation, E602, E811, E604, E605, E608, E609, E615, E503, E502, E504, E505,
//              E307, E612, E613; then saves the drawing for T18 and reads it back (VERIFY)
//   REOPEN     second session: the saved T18 viewports before any activation
//   ACTIVE     after (setvar "CTAB" ...): E614, the plan viewport was not made the overall one
//   TRANS      after _.MSPACE: acedTrans oracle against C3's math and the written viewport
//   E611       while a generated layout is current and in model space: regen it, CTAB kept
//   E611CHECK  after _.PSPACE: the regenerated view survived leaving the viewport
//   S2REPLICA  numbering probe: builds layouts R1-R8 (see PaperCheckSession.cs)
//   VPSTATE    prints every viewport of the current layout with CVPORT
// Every evidence line starts with "A3 "; a failed expectation prints "FAIL", a probe-step
// failure prints SV-E197 with the step name.

using System.Globalization;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using SheetVol.Core;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(SheetVol.Civil3D.WritersPaper.PaperCheckCommands))]

namespace SheetVol.Civil3D.WritersPaper;

/// <summary>The SVWRITERSPAPER command. Check-only; see the file header.</summary>
public static partial class PaperCheckCommands
{
    private static Editor? _ed;
    private static int _fails;

    [CommandMethod("SVWRITERSPAPER", CommandFlags.Modal)]
    public static void Check()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null) { Console.WriteLine($"{PaperCodes.CheckFailed} SVWRITERSPAPER: no active document"); return; }
        _ed = doc.Editor;
        var pr = _ed.GetString(new PromptStringOptions("\nStep [PREP/BUILD/REOPEN/ACTIVE/TRANS/E611/E611CHECK/S2REPLICA/VPSTATE]: ") { AllowSpaces = false });
        string step = pr.Status == PromptStatus.OK ? pr.StringResult.Trim().ToUpperInvariant() : "";
        _fails = 0;
        Say($"===== SVWRITERSPAPER {step} begin =====");
        try
        {
            switch (step)
            {
                case "PREP": Prep(doc.Database); break;
                case "BUILD": Build(doc.Database); break;
                case "REOPEN": Reopen(doc.Database); break;
                case "ACTIVE": Active(doc.Database); break;
                case "TRANS": Trans(doc.Database); break;
                case "E611": E611(doc.Database); break;
                case "E611CHECK": E611Check(doc.Database); break;
                case "S2REPLICA": S2Replica(doc.Database); break;
                case "VPSTATE": PrintViewportState(doc.Database); break;
                default: Say($"{PaperCodes.CheckFailed} SVWRITERSPAPER: unknown step '{step}'"); return;
            }
            Say($"===== SVWRITERSPAPER {step} end: {(_fails == 0 ? "no FAIL lines" : $"{_fails} FAIL line(s)")} =====");
        }
        catch (PaperWriterException ex)
        {
            Say($"{ex.Code} SVWRITERSPAPER {step}: {ex.Message}");
            Say($"{PaperCodes.CheckFailed} SVWRITERSPAPER {step} stopped");
        }
        catch (System.Exception ex)
        {
            Say($"{PaperCodes.CheckFailed} SVWRITERSPAPER {step} failed: {ex.GetType().Name}: {ex.Message}");
            Say(ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("WritersPaper", StringComparison.Ordinal))?.Trim() ?? "");
        }
    }

    // ------------------------------------------------------------------ output helpers

    internal static void Say(string s)
    {
        if (_ed is not null) _ed.WriteMessage("\n" + s);
        else Console.WriteLine(s);
    }

    /// <summary>Prints one expectation as PASS or FAIL.</summary>
    internal static bool Expect(string label, bool ok, string detail)
    {
        if (!ok) _fails++;
        Say($"A3 {label}: {(ok ? "PASS" : "FAIL")} {detail}");
        return ok;
    }

    internal static string F(double d) => d.ToString("0.##########", CultureInfo.InvariantCulture);
    internal static string F6(double d) => d.ToString("0.000000", CultureInfo.InvariantCulture);
    internal static string E(double d) => d.ToString("0.0E+0", CultureInfo.InvariantCulture);
    internal static string P(Pt p) => $"({F(p.X)}, {F(p.Y)})";
    internal static string P(Point3d p) => $"({F(p.X)}, {F(p.Y)})";
    internal static string P(Point2d p) => $"({F(p.X)}, {F(p.Y)})";
    internal static string Deg(double rad) => F6(rad * 180.0 / Math.PI) + " deg";
    internal static double Dist(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    internal static string VpLine(Transaction tr, Viewport vp)
    {
        int clipVerts = !vp.NonRectClipEntityId.IsNull && !vp.NonRectClipEntityId.IsErased && tr.GetObject(vp.NonRectClipEntityId, OpenMode.ForRead) is Polyline pl ? pl.NumberOfVertices : 0;
        string anno = PaperViewMath.SafeAnnotationScale(vp)?.Name ?? "(none)";
        return $"vp {vp.Handle} layer={vp.Layer} center={P(vp.CenterPoint)} {F(vp.Width)}x{F(vp.Height)} target={P(vp.ViewTarget)} viewCenter={P(vp.ViewCenter)} " +
               $"twist={Deg(vp.TwistAngle)} customScale={F(vp.CustomScale)} anno='{anno}' clipOn={vp.NonRectClipOn} clipVertices={clipVerts} locked={vp.Locked} on={vp.On} number={vp.Number}";
    }

    internal static void PrintResult(string tag, PaperApplyResult r, DbCounts counts)
    {
        Say($"A3 {tag}: succeeded={r.Succeeded} writes={r.Writes} dbEvents appended={counts.Appended} modified={counts.Modified} erased={counts.Erased}");
        foreach (var s in r.Sheets)
            Say($"A3 {tag}   sheet '{s.LayoutName}' layout={s.LayoutHandle}/{s.Layout} viewport={s.ViewportHandle}/{s.Viewport} clip={s.ClipHandle ?? "-"}/{s.Clip} table={s.TableHandle ?? "-"}/{s.Table} attributes=[{string.Join(",", s.TitleBlockHandles)}]/{s.Attributes}");
        if (r.RenamePlan is { } plan)
        {
            foreach (var x in plan.FirstPass) Say($"A3 {tag}   rename pass 1: '{x.From}' -> '{x.To}'");
            foreach (var x in plan.SecondPass) Say($"A3 {tag}   rename pass 2: '{x.From}' -> '{x.To}'");
            foreach (var x in plan.Creates) Say($"A3 {tag}   create: '{x.Name}'");
        }
        foreach (var o in r.Orphans) Say($"A3 {tag}   orphan: '{o.Name}' ({o.Handle}) {o.Reason}");
        foreach (var p in r.Problems) Say($"A3 {tag}   {p.Code} {p.Severity}: {p.Message}");
    }

    // ------------------------------------------------------------------ database event counter (E811 evidence)

    internal readonly record struct DbCounts(int Appended, int Modified, int Erased)
    {
        public bool None => Appended == 0 && Modified == 0 && Erased == 0;
    }

    internal sealed class DbEvents : IDisposable
    {
        private readonly Database _db;
        private int _appended, _modified, _erased;

        public DbEvents(Database db)
        {
            _db = db;
            _db.ObjectAppended += OnAppended;
            _db.ObjectModified += OnModified;
            _db.ObjectErased += OnErased;
        }

        public DbCounts Counts => new(_appended, _modified, _erased);
        private void OnAppended(object? s, ObjectEventArgs e) => _appended++;
        private void OnModified(object? s, ObjectEventArgs e) => _modified++;
        private void OnErased(object? s, ObjectErasedEventArgs e) => _erased++;

        public void Dispose()
        {
            _db.ObjectAppended -= OnAppended;
            _db.ObjectModified -= OnModified;
            _db.ObjectErased -= OnErased;
        }
    }

    /// <summary>Validate + Apply in one committed transaction (aborted when refused), counting database events.</summary>
    internal static PaperApplyResult Run(Database db, PaperWriter writer, PaperTemplate template, PaperRunRequest request, out DbCounts counts, out double ms)
    {
        using var ev = new DbEvents(db);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        PaperApplyResult result;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            result = writer.Apply(tr, db, template, request);
            if (result.Succeeded) tr.Commit(); else tr.Abort();
        }
        sw.Stop();
        ms = sw.Elapsed.TotalMilliseconds;
        counts = ev.Counts;
        return result;
    }

    // ------------------------------------------------------------------ PREP

    /// <summary>Adds what the check needs to FX's template in the open drawing, before the script saves it as the template.</summary>
    private static void Prep(Database db)
    {
        var lm = LayoutManager.Current;
        if (!lm.LayoutExists(CheckPlans.TemplateLayout)) throw new InvalidOperationException($"run SVFIXTURE TEMPLATE first: no layout '{CheckPlans.TemplateLayout}'");
        using var tr = db.TransactionManager.StartTransaction();
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
        foreach (var name in new[] { CheckPlans.FrozenLayer, "SV-VPORT" })
            if (!layers.Has(name)) { var r = new LayerTableRecord { Name = name }; layers.Add(r); tr.AddNewlyCreatedDBObject(r, true); }
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var anchorDef = bt["SV_TABLE_ANCHOR"];
        var titleDef = TitleBlockDefinition(tr, db, bt);

        // SV-SHEET: a title block with STA_FROM and SHEET_NO (no STA_TO: E615), and a key-map viewport (E602).
        var sheet = LayoutBlock(tr, lm.GetLayoutId(CheckPlans.TemplateLayout));
        var title = InsertWithAttributes(tr, db, sheet, titleDef, new Point3d(1.0, 0.5, 0));
        var key = AddViewport(tr, db, sheet, "0", new Point3d(15.0, 3.0, 0), 2.0, 2.0, new Point3d(999, 999, 0), 1.0 / 500.0);
        Say($"A3 PREP {CheckPlans.TemplateLayout}: title block {title.Handle} with STA_FROM and SHEET_NO; key-map viewport {key.Handle} 2x2 target (999, 999) 1:500 on layer 0");

        // Extra template layouts for E601 and E602.
        var largest = NewLayout(tr, lm, CheckPlans.LayoutLargest);
        var big = AddViewport(tr, db, largest, "0", new Point3d(7.0, 4.0, 0), 10.0, 4.0, new Point3d(100, 100, 0), 1.0 / 50.0);
        var small = AddViewport(tr, db, largest, "0", new Point3d(15.0, 3.0, 0), 3.0, 2.0, new Point3d(999, 999, 0), 1.0 / 500.0);
        Insert(tr, db, largest, anchorDef, new Point3d(13.5, 7.0, 0));
        Say($"A3 PREP {CheckPlans.LayoutLargest}: viewports {big.Handle} 10x4 and {small.Handle} 3x2, both on layer 0; anchor");

        var noVp = NewLayout(tr, lm, CheckPlans.LayoutNoViewport);
        Insert(tr, db, noVp, anchorDef, new Point3d(13.5, 7.0, 0));
        Say($"A3 PREP {CheckPlans.LayoutNoViewport}: anchor only, no floating viewport");

        var noAnchor = NewLayout(tr, lm, CheckPlans.LayoutNoAnchor);
        var na = AddViewport(tr, db, noAnchor, "SV-VPORT", new Point3d(7.0, 4.5, 0), 12.0, 5.0, Point3d.Origin, 1.0 / 50.0);
        Say($"A3 PREP {CheckPlans.LayoutNoAnchor}: viewport {na.Handle} on SV-VPORT, no anchor");

        var two = NewLayout(tr, lm, CheckPlans.LayoutTwoPlan);
        AddViewport(tr, db, two, "SV-VPORT", new Point3d(5.0, 4.5, 0), 6.0, 5.0, Point3d.Origin, 1.0 / 50.0);
        AddViewport(tr, db, two, "SV-VPORT", new Point3d(12.0, 4.5, 0), 6.0, 5.0, Point3d.Origin, 1.0 / 50.0);
        Insert(tr, db, two, anchorDef, new Point3d(13.5, 7.0, 0));
        Say($"A3 PREP {CheckPlans.LayoutTwoPlan}: two viewports on SV-VPORT; anchor");

        var clipped = NewLayout(tr, lm, CheckPlans.LayoutClipped);
        var cvp = AddViewport(tr, db, clipped, "SV-VPORT", new Point3d(7.0, 4.5, 0), 12.0, 5.0, Point3d.Origin, 1.0 / 50.0);
        var hex = ClipPolyline(tr, db, clipped, [(2, 2.5, 0), (12, 2.5, 0), (12.5, 4.5, 0), (12, 6.5, 0), (2, 6.5, 0), (1.5, 4.5, 0)]);
        cvp.NonRectClipEntityId = hex.ObjectId;
        cvp.NonRectClipOn = true;
        Insert(tr, db, clipped, anchorDef, new Point3d(13.5, 7.0, 0));
        Say($"A3 PREP {CheckPlans.LayoutClipped}: plan viewport {cvp.Handle} on SV-VPORT clipped by a 6-vertex polyline {hex.Handle}; anchor");
        tr.Commit();

        // A second key map made the way an author makes one: while SV-SHEET is current, then turned on.
        string previous = lm.CurrentLayout;
        lm.CurrentLayout = CheckPlans.TemplateLayout;
        try
        {
            using var tr2 = db.TransactionManager.StartTransaction();
            var sheet2 = LayoutBlock(tr2, lm.GetLayoutId(CheckPlans.TemplateLayout));
            var key2 = AddViewport(tr2, db, sheet2, "0", new Point3d(15.0, 6.0, 0), 2.0, 1.5, new Point3d(777, 777, 0), 1.0 / 200.0);
            key2.On = true;
            var five = ClipPolyline(tr2, db, sheet2, [(14.2, 5.4, 0), (15.8, 5.4, 0), (15.8, 6.4, 0.3), (15.0, 6.7, 0), (14.2, 6.4, 0)]);
            key2.NonRectClipEntityId = five.ObjectId;
            key2.NonRectClipOn = true;
            Say($"A3 PREP {CheckPlans.TemplateLayout}: second key-map viewport {key2.Handle} 2x1.5 target (777, 777) 1:200, created while the layout was current, On={key2.On}, clipped by a 5-vertex polyline {five.Handle}");
            tr2.Commit();
        }
        finally { lm.CurrentLayout = previous; }
        Say($"A3 PREP CTAB={AcApp.GetSystemVariable("CTAB")}");
    }

    private static BlockTableRecord LayoutBlock(Transaction tr, ObjectId layoutId)
        => (BlockTableRecord)tr.GetObject(((Layout)tr.GetObject(layoutId, OpenMode.ForRead)).BlockTableRecordId, OpenMode.ForWrite);

    private static BlockTableRecord NewLayout(Transaction tr, LayoutManager lm, string name)
    {
        var id = lm.CreateLayout(name);
        var lay = (Layout)tr.GetObject(id, OpenMode.ForWrite);
        lay.Initialize();
        return (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForWrite);
    }

    private static Viewport AddViewport(Transaction tr, Database db, BlockTableRecord owner, string layer, Point3d center, double w, double h, Point3d target, double scale)
    {
        var vp = new Viewport();
        vp.SetDatabaseDefaults(db);
        vp.Layer = layer;
        vp.CenterPoint = center;
        vp.Width = w;
        vp.Height = h;
        owner.AppendEntity(vp);
        tr.AddNewlyCreatedDBObject(vp, true);
        vp.ViewDirection = Vector3d.ZAxis;
        vp.ViewTarget = target;
        vp.ViewCenter = Point2d.Origin;
        vp.TwistAngle = 0;
        vp.CustomScale = scale;
        return vp;
    }

    private static Polyline ClipPolyline(Transaction tr, Database db, BlockTableRecord owner, (double X, double Y, double Bulge)[] vertices)
    {
        var pl = new Polyline();
        pl.SetDatabaseDefaults(db);
        for (int i = 0; i < vertices.Length; i++) pl.AddVertexAt(i, new Point2d(vertices[i].X, vertices[i].Y), vertices[i].Bulge, 0, 0);
        pl.Closed = true;
        owner.AppendEntity(pl);
        tr.AddNewlyCreatedDBObject(pl, true);
        return pl;
    }

    private static BlockReference Insert(Transaction tr, Database db, BlockTableRecord owner, ObjectId def, Point3d at)
    {
        var br = new BlockReference(at, def);
        br.SetDatabaseDefaults(db);
        owner.AppendEntity(br);
        tr.AddNewlyCreatedDBObject(br, true);
        return br;
    }

    private static ObjectId TitleBlockDefinition(Transaction tr, Database db, BlockTable bt)
    {
        if (bt.Has(CheckPlans.TitleBlock)) return bt[CheckPlans.TitleBlock];
        bt.UpgradeOpen();
        var def = new BlockTableRecord { Name = CheckPlans.TitleBlock, Origin = Point3d.Origin };
        var id = bt.Add(def);
        tr.AddNewlyCreatedDBObject(def, true);
        var frame = new Line(new Point3d(0, 0, 0), new Point3d(6, 0, 0));
        frame.SetDatabaseDefaults(db);
        def.AppendEntity(frame);
        tr.AddNewlyCreatedDBObject(frame, true);
        foreach (var (tagName, x) in new[] { ("STA_FROM", 0.2), ("SHEET_NO", 4.0) })
        {
            var ad = new AttributeDefinition(new Point3d(x, 0.2, 0), "", tagName, tagName, db.Textstyle) { Height = 0.15 };
            ad.SetDatabaseDefaults(db);
            def.AppendEntity(ad);
            tr.AddNewlyCreatedDBObject(ad, true);
        }
        return id;
    }

    private static BlockReference InsertWithAttributes(Transaction tr, Database db, BlockTableRecord owner, ObjectId defId, Point3d at)
    {
        var br = Insert(tr, db, owner, defId, at);
        var def = (BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead);
        foreach (ObjectId id in def)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not AttributeDefinition ad || ad.Constant) continue;
            var ar = new AttributeReference();
            ar.SetAttributeFromBlock(ad, br.BlockTransform);
            ar.TextString = "?";
            br.AttributeCollection.AppendAttribute(ar);
            tr.AddNewlyCreatedDBObject(ar, true);
        }
        return br;
    }

    // ------------------------------------------------------------------ acedTrans (AutoCAD's own transform; the P12 oracle S2 used)

    [DllImport("accore.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "acedTrans")]
    private static extern int acedTrans(double[] point, IntPtr fromRb, IntPtr toRb, int disp, double[] result);

    internal static Point3d? Trans(Point3d p, short from, short to)
    {
        using var rbFrom = new ResultBuffer(new TypedValue((int)LispDataType.Int16, from));
        using var rbTo = new ResultBuffer(new TypedValue((int)LispDataType.Int16, to));
        var res = new double[3];
        int rc = acedTrans([p.X, p.Y, p.Z], rbFrom.UnmanagedObject, rbTo.UnmanagedObject, 0, res);
        return rc == 5100 ? new Point3d(res[0], res[1], res[2]) : null;   // 5100 = RTNORM
    }
}

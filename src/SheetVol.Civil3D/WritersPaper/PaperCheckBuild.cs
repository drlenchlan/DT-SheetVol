// A3 work package. Owner: A3.
//
// SVWRITERSPAPER BUILD: E601, E501, T18 generation with the P12 numeric check, twist sign,
// E614 before activation and E602 on the key map, E811, then the E-row checks
// (PaperCheckRows.cs), then save and read back for T18.

using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Tracking;
using SheetVol.Core.Views;

namespace SheetVol.Civil3D.WritersPaper;

public static partial class PaperCheckCommands
{
    private static void Build(Database db)
    {
        Directory.CreateDirectory(CheckPlans.OutDir);
        if (!File.Exists(CheckPlans.TemplatePath)) throw new InvalidOperationException($"no template at {CheckPlans.TemplatePath}; the script saves it after PREP");

        // ---------- E601: each missing piece fails before any write, by name.
        E601Case("template file missing", CheckPlans.Config() with { TemplatePath = Path.Combine(CheckPlans.OutDir, "a3-no-such-template.dwt") }, db);
        E601Case("template layout missing", CheckPlans.Config("NO-SUCH-LAYOUT"), db);
        E601Case("plan viewport missing", CheckPlans.Config(CheckPlans.LayoutNoViewport), db);
        E601Case("table anchor missing, no TablePaperPoint", CheckPlans.Config(CheckPlans.LayoutNoAnchor), db);
        E601Case("two viewports on SV-VPORT", CheckPlans.Config(CheckPlans.LayoutTwoPlan), db);
        E601Case("templatePath not set", CheckPlans.Config() with { TemplatePath = "" }, db);
        using (var fallback = CheckPlans.Writer(CheckPlans.Config(CheckPlans.LayoutNoAnchor) with { TablePaperPoint = new Pt(16, 7) }).LoadTemplate().Value)
            Expect("E601 anchor falls back to TablePaperPoint", !fallback.AnchorFromBlock && Dist(fallback.TableAnchor, new Pt(16, 7)) == 0,
                $"anchor {P(fallback.TableAnchor)} fromBlock={fallback.AnchorFromBlock}");

        // ---------- the real template
        var writer = CheckPlans.Writer();
        var loaded = writer.LoadTemplate();
        if (!loaded.Succeeded) throw new InvalidOperationException("template refused: " + string.Join(" | ", loaded.Problems.Select(p => $"{p.Code} {p.Message}")));
        using var template = loaded.Value;
        Say($"A3 TEMPLATE '{template.Path}' layout '{template.LayoutName}': plan viewport {F(template.PlanViewportWidth)}x{F(template.PlanViewportHeight)} at {P(template.PlanViewportCenter)} on '{template.PlanViewportLayer}' (byLayer={template.PlanChosenByLayer}); anchor {P(template.TableAnchor)} fromBlock={template.AnchorFromBlock}; other viewports [{string.Join(", ", template.OtherViewportHandles)}]");
        foreach (var n in template.Notes) Say($"A3 TEMPLATE {n.Code} {n.Severity}: {n.Message}");

        var t18 = CheckPlans.T18(template.PlanViewportWidth, template.PlanViewportHeight);
        var req18 = new PaperRunRequest(CheckPlans.RunT18, t18.Select(s => s.Request()).ToList());
        foreach (var s in t18)
            Say($"A3 PLAN {s.Run}-{s.Number}: alignment {s.Alignment.Name} {s.Parts.FromStation}..{s.Parts.ToStation} centre {P(s.View.ModelCenter)} twist {Deg(s.View.TwistRadians)} 1:{F(s.View.ModelUnitsPerPaperUnit)} clip {s.View.ClipInPaper?.Count ?? 0} vertices; table {s.Table!.Rows.Count}x{s.Table.ColumnCount}");

        // ---------- E501: table style and layer from config missing; nothing written.
        int layoutsBefore = LayoutCount(db);
        IReadOnlyList<Problem> validation;
        DbCounts vc;
        using (var ev = new DbEvents(db))
        {
            using (var tr = db.TransactionManager.StartTransaction()) { validation = writer.Validate(tr, db, template, req18); tr.Commit(); }
            vc = ev.Counts;
        }
        var e501 = validation.Where(p => p.Code == PaperCodes.TableStyleMissing).ToList();
        Expect("E501 validate", e501.Count == 2 && vc.None, $"{string.Join(" | ", e501.Select(p => $"{p.Code} {p.Message}"))} dbEvents={vc.Appended}/{vc.Modified}/{vc.Erased}");
        var refused = Run(db, writer, template, req18, out var rc, out _);
        Expect("E501 apply refused", !refused.Succeeded && rc.None && LayoutCount(db) == layoutsBefore,
            $"succeeded={refused.Succeeded} dbEvents={rc.Appended}/{rc.Modified}/{rc.Erased} layouts {layoutsBefore}->{LayoutCount(db)}");
        SetupTableStyleAndLayer(db, ReportConfig.Default.SegmentTable.Style);

        // ---------- T18: generate
        var r18 = Run(db, writer, template, req18, out var c18, out var ms18);
        PrintResult("T18 generate", r18, c18);
        Expect("T18 generate", r18.Succeeded && r18.Sheets.Count == 2 && r18.Sheets.All(s => s.Layout == PaperChange.Created && s.Viewport == PaperChange.Created && s.Table == PaperChange.Created),
            $"{r18.Sheets.Count} sheets in {F6(ms18)} ms");
        CheckSheets(db, template, t18, r18);

        // ---------- E811: the same request again writes nothing.
        var r18b = Run(db, writer, template, req18, out var c18b, out var ms18b);
        PrintResult("E811 regen unchanged", r18b, c18b);
        bool sameHandles = r18b.Sheets.Zip(r18.Sheets).All(z => z.First.LayoutHandle == z.Second.LayoutHandle && z.First.ViewportHandle == z.Second.ViewportHandle
            && z.First.ClipHandle == z.Second.ClipHandle && z.First.TableHandle == z.Second.TableHandle);
        bool allUnchanged = r18b.Sheets.All(s => s.Layout == PaperChange.Unchanged && s.Viewport == PaperChange.Unchanged && s.Clip == PaperChange.Unchanged
            && s.Table == PaperChange.Unchanged && s.Attributes == PaperChange.Unchanged);
        Expect("E811", r18b.Succeeded && c18b.None && r18b.Writes == 0 && sameHandles && allUnchanged,
            $"writes={r18b.Writes} dbEvents={c18b.Appended}/{c18b.Modified}/{c18b.Erased} sameHandles={sameHandles} allUnchanged={allUnchanged} in {F6(ms18b)} ms");

        // ---------- the E-rows (PaperCheckRows.cs)
        E602Largest(db);
        E6Rows(db, writer, template);
        E608ClipRows(db, writer, template);
        E605Rows(db, writer, template);
        E307Rows(db, writer, template);
        E613Rows(db, writer, template);

        // ---------- T18: save, read back
        SaveAndVerify(db, t18, template);
    }

    private static int LayoutCount(Database db)
    {
        using var tr = db.TransactionManager.StartTransaction();
        int n = ((DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead)).Count;
        tr.Commit();
        return n;
    }

    internal static ObjectId Id(Database db, string? handle)
        => handle is null ? ObjectId.Null : db.GetObjectId(false, new Handle(long.Parse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture)), 0);

    private static void E601Case(string label, PaperConfig config, Database db)
    {
        int before = LayoutCount(db);
        Core.Volumes.Outcome<PaperTemplate> o;
        DbCounts counts;
        using (var ev = new DbEvents(db))
        {
            o = new PaperWriter(config, ReportConfig.Default.Placement, CheckPlans.Namer()).LoadTemplate();
            if (o.Succeeded) o.Value.Dispose();
            counts = ev.Counts;
        }
        var p = o.Problems.FirstOrDefault(x => x.Severity == Severity.Error);
        Expect($"E601 {label}", !o.Succeeded && p?.Code == PaperCodes.TemplateMissing && counts.None && LayoutCount(db) == before,
            $"{p?.Code} \"{p?.Message}\" dbEvents={counts.Appended}/{counts.Modified}/{counts.Erased} layouts {before}->{LayoutCount(db)}");
    }

    /// <summary>Check setup, not product behaviour: the drawing gets the configured table style and layer only after E501 has been shown.</summary>
    private static void SetupTableStyleAndLayer(Database db, TableStyleSettings style)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var styles = (DBDictionary)tr.GetObject(db.TableStyleDictionaryId, OpenMode.ForRead);
        if (!styles.Contains(style.TableStyle))
        {
            var ts = new TableStyle();      // the dictionary key is its name; setting Name before it has an owner throws eOwnerNotSet
            styles.UpgradeOpen();
            styles.SetAt(style.TableStyle, ts);
            tr.AddNewlyCreatedDBObject(ts, true);
        }
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
        if (!layers.Has(style.Layer)) { var l = new LayerTableRecord { Name = style.Layer }; layers.Add(l); tr.AddNewlyCreatedDBObject(l, true); }
        tr.Commit();
        Say($"A3 SETUP table style '{style.TableStyle}' and layer '{style.Layer}' created for the rest of the check");
    }

    /// <summary>Reads every T18 sheet back from the live drawing: view, P12 mapping, twist sign, clip, E614 before activation, key map (E602), table, attributes (E615).</summary>
    private static void CheckSheets(Database db, PaperTemplate template, IReadOnlyList<CheckSheet> sheets, PaperApplyResult result)
    {
        using var tr = db.TransactionManager.StartTransaction();
        foreach (var (sheet, res) in sheets.Zip(result.Sheets))
        {
            string name = res.LayoutName;
            var spec = sheet.View;
            double k = spec.ModelUnitsPerPaperUnit;
            var vp = (Viewport)tr.GetObject(Id(db, res.ViewportHandle), OpenMode.ForRead);
            Say($"A3 T18 {name} readback: {VpLine(tr, vp)}");

            double targetErr = Dist(new Pt(vp.ViewTarget.X, vp.ViewTarget.Y), spec.ModelCenter);
            double twistErr = PaperViewMath.AngleDifference(vp.TwistAngle, spec.TwistRadians);
            double scaleErr = Math.Abs(vp.CustomScale * k - 1);
            Expect($"T18 {name} view", targetErr <= 1e-9 && twistErr <= 1e-12 && scaleErr <= 1e-12,
                $"target |d|={E(targetErr)} twist |d|={E(twistErr)} scale rel={E(scaleErr)}");
            Say($"A3 TWIST {name}: C3 twist {Deg(spec.TwistRadians)} written as TwistAngle {Deg(PaperViewMath.Mod2Pi(spec.TwistRadians))}, read back {Deg(vp.TwistAngle)}; difference mod 360 = {E(twistErr)} rad");

            // P12: a known model point lands where C3's math says.
            var a = template.PlanViewportCenter;
            var pts = new (string, Pt)[]
            {
                ("centre", spec.ModelCenter),
                ("start", sheet.Alignment.PointAt(sheet.Segment.StartStation, 0)),
                ("end", sheet.Alignment.PointAt(sheet.Segment.EndStation, 0)),
                ("start-left", sheet.Alignment.PointAt(sheet.Segment.StartStation, sheet.Segment.OffsetLeft)),
                ("end-right", sheet.Alignment.PointAt(sheet.Segment.EndStation, sheet.Segment.OffsetRight)),
            };
            double maxErr = 0;
            var parts = new List<string>();
            foreach (var (label, w) in pts)
            {
                var live = PaperViewMath.ModelToPaper(vp, w);
                var c3 = ViewMath.ToPaper(w, spec.ModelCenter, spec.TwistRadians, k);
                var expected = new Pt(a.X + c3.X, a.Y + c3.Y);
                double err = Dist(live, expected);
                maxErr = Math.Max(maxErr, err);
                parts.Add($"{label} {P(w)} -> {P(live)} (C3 {P(expected)})");
            }
            Expect($"P12 check {name}", maxErr <= 1e-6, $"max |paper - C3| = {E(maxErr)} over {pts.Length} points: {string.Join("; ", parts)}");
            var start = PaperViewMath.ModelToPaper(vp, pts[1].Item2);
            double halfLength = (sheet.Segment.EndStation - sheet.Segment.StartStation) / k / 2;
            Expect($"T17-style start at left edge {name}", Math.Abs(start.X - (a.X - halfLength)) <= 1e-9 && Math.Abs(start.Y - a.Y) <= 1e-9,
                $"segment start on paper {P(start)}, viewport anchor {P(a)}, expected x = {F(a.X - halfLength)}");

            // Clip: vertex count and positions; the viewport refitted to the clip's box.
            if (!vp.NonRectClipEntityId.IsNull && tr.GetObject(vp.NonRectClipEntityId, OpenMode.ForRead) is Polyline pl)
            {
                var want = spec.ClipInPaper!;
                double clipErr = 0, minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                for (int i = 0; i < Math.Min(pl.NumberOfVertices, want.Count); i++)
                {
                    var q = pl.GetPoint2dAt(i);
                    clipErr = Math.Max(clipErr, Dist(new Pt(q.X, q.Y), new Pt(a.X + want[i].Point.X, a.Y + want[i].Point.Y)));
                    minX = Math.Min(minX, q.X); minY = Math.Min(minY, q.Y); maxX = Math.Max(maxX, q.X); maxY = Math.Max(maxY, q.Y);
                }
                double fitErr = Math.Max(Math.Max(Math.Abs(vp.Width - (maxX - minX)), Math.Abs(vp.Height - (maxY - minY))),
                    Dist(new Pt(vp.CenterPoint.X, vp.CenterPoint.Y), new Pt((minX + maxX) / 2, (minY + maxY) / 2)));
                Expect($"T18 {name} clip", pl.NumberOfVertices == want.Count && clipErr <= 1e-9 && vp.NonRectClipOn,
                    $"{pl.NumberOfVertices} vertices (plan {want.Count}), max |vertex - (A + ClipInPaper)| = {E(clipErr)}, viewport vs clip box |d| = {E(fitErr)}, ViewCenter {P(vp.ViewCenter)}");
            }
            else Expect($"T18 {name} clip", false, "no clip polyline on the plan viewport");

            // E614 before activation: the plan viewport is not the overall viewport and not first in the block.
            var layout = (Layout)tr.GetObject(Id(db, res.LayoutHandle), OpenMode.ForRead);
            var block = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            var vps = block.Cast<ObjectId>().Where(id => tr.GetObject(id, OpenMode.ForRead) is Viewport).ToList();
            var listed = layout.GetViewports();
            var overall = listed is { Count: > 0 } ? listed[0] : ObjectId.Null;
            string overallLine = overall.IsNull ? "none" : VpLine(tr, (Viewport)tr.GetObject(overall, OpenMode.ForRead));
            Expect($"E614 {name} before activation", !overall.IsNull && overall != vp.ObjectId && vps.Count > 0 && vps[0] != vp.ObjectId,
                $"viewports in block order [{string.Join(", ", vps.Select(v => v.Handle.ToString()))}], GetViewports()[0]={(overall.IsNull ? "none" : overall.Handle.ToString())}, plan={vp.Handle}; overall (paper-space view fitted to the cloned extents): {overallLine}");

            // E602: every other viewport cloned from the template keeps the template's view.
            CheckOthers($"E602 {name}", tr, vps.Where(v => v != overall && v != vp.ObjectId).ToList(), TemplateOthers(template));

            // Table text and position.
            var table = (Table)tr.GetObject(Id(db, res.TableHandle), OpenMode.ForRead);
            var (match, detail) = TableMatches(table, sheet.Table!);
            Expect($"T18 {name} table", match && Dist(new Pt(table.Position.X, table.Position.Y), template.TableAnchor) <= 1e-9,
                $"{detail}; position {P(table.Position)} anchor {P(template.TableAnchor)} style '{((TableStyle)tr.GetObject(table.TableStyle, OpenMode.ForRead)).Name}' layer '{table.Layer}'");

            // Title attributes (E615 for the missing STA_TO).
            var attrs = Attributes(tr, block);
            bool attrsOk = attrs.TryGetValue("STA_FROM", out var from) && from == sheet.Parts.FromStation
                        && attrs.TryGetValue("SHEET_NO", out var no) && no == sheet.Number.ToString(CultureInfo.InvariantCulture);
            var e615 = res.Problems.FirstOrDefault(p => p.Code == PaperCodes.TitleTagMissing);
            Expect($"E615 {name}", attrsOk && e615 is not null && e615.Severity == Severity.Info && result.Succeeded,
                $"attributes [{string.Join(", ", attrs.Select(kv => $"{kv.Key}='{kv.Value}'"))}]; {e615?.Code} {e615?.Severity}: {e615?.Message}");
        }
        tr.Commit();
    }

    /// <summary>A template viewport that is neither the overall nor the plan viewport: where it sits and what it shows.</summary>
    internal sealed record OtherViewport(string Handle, Pt Center, double Width, double Height, Pt Target, double Scale, double Twist, bool On);

    /// <summary>The template's other floating viewports, read from the template's side database.</summary>
    internal static IReadOnlyList<OtherViewport> TemplateOthers(PaperTemplate template)
    {
        using var tr = template.Source.TransactionManager.StartTransaction();
        var list = new List<OtherViewport>();
        foreach (var h in template.OtherViewportHandles)
        {
            var v = (Viewport)tr.GetObject(Id(template.Source, h), OpenMode.ForRead);
            list.Add(new OtherViewport(h, new Pt(v.CenterPoint.X, v.CenterPoint.Y), v.Width, v.Height, new Pt(v.ViewTarget.X, v.ViewTarget.Y), v.CustomScale, v.TwistAngle, v.On));
        }
        tr.Commit();
        return list;
    }

    /// <summary>
    /// Each generated other viewport matches its template counterpart (found by paper position) in
    /// size and view, and is not number 1. After activation (<paramref name="activated"/>) it is also
    /// on and numbered exactly when the template's was on.
    /// </summary>
    internal static void CheckOthers(string label, Transaction tr, IReadOnlyList<ObjectId> generated, IReadOnlyList<OtherViewport> template, bool activated = false)
    {
        Expect($"{label} other viewport count", generated.Count == template.Count, $"{generated.Count} generated, {template.Count} in the template");
        foreach (var id in generated)
        {
            var v = (Viewport)tr.GetObject(id, OpenMode.ForRead);
            var t = template.FirstOrDefault(o => Dist(o.Center, new Pt(v.CenterPoint.X, v.CenterPoint.Y)) <= 1e-9);
            bool ok = t is not null && Math.Abs(v.Width - t.Width) <= 1e-9 && Math.Abs(v.Height - t.Height) <= 1e-9
                      && Dist(new Pt(v.ViewTarget.X, v.ViewTarget.Y), t.Target) <= 1e-9 && Math.Abs(v.CustomScale - t.Scale) <= 1e-12 * t.Scale
                      && PaperViewMath.AngleDifference(v.TwistAngle, t.Twist) <= 1e-12 && v.Number != 1;
            if (activated && t is not null) ok &= t.On ? v.On && v.Number >= 2 : !v.On;
            Expect($"{label} other viewport untouched", ok, $"{VpLine(tr, v)}; template {t?.Handle ?? "none"} target {(t is null ? "-" : P(t.Target))} customScale {(t is null ? "-" : F(t.Scale))} on={t?.On}");
        }
    }

    /// <summary>Compares every non-covered cell's plain text with the model.</summary>
    internal static (bool Match, string Detail) TableMatches(Table t, TableModel model)
    {
        if (t.Rows.Count != model.Rows.Count || t.Columns.Count != model.ColumnCount)
            return (false, $"size {t.Rows.Count}x{t.Columns.Count}, plan {model.Rows.Count}x{model.ColumnCount}");
        var covered = PaperTableWriter.Covered(model);
        int cells = 0, bad = 0;
        string first = "";
        for (int r = 0; r < model.Rows.Count; r++)
            for (int c = 0; c < model.ColumnCount; c++)
            {
                if (covered.Contains((r, c))) continue;
                cells++;
                string live = t.Cells[r, c].GetTextString(FormatOption.IgnoreMtextFormat) ?? "";
                if (live != model.Cell(r, c)) { bad++; if (first.Length == 0) first = $" first mismatch ({r},{c}) '{live}' vs '{model.Cell(r, c)}'"; }
            }
        return (bad == 0, $"{t.Rows.Count}x{t.Columns.Count}, {cells} cells compared, {bad} differ{first}; rows: {string.Join(" | ", Enumerable.Range(0, t.Rows.Count).Select(r => t.Cells[r, 0].GetTextString(FormatOption.IgnoreMtextFormat) + (model.ColumnCount > 1 && !covered.Contains((r, 1)) ? " = " + t.Cells[r, 1].GetTextString(FormatOption.IgnoreMtextFormat) : "")))}");
    }

    internal static Dictionary<string, string> Attributes(Transaction tr, BlockTableRecord block)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectId id in block)
            if (tr.GetObject(id, OpenMode.ForRead) is BlockReference br and not Table)
                foreach (ObjectId a in br.AttributeCollection)
                    if (tr.GetObject(a, OpenMode.ForRead) is AttributeReference ar) d[ar.Tag] = ar.TextString;
        return d;
    }

    /// <summary>T18: save the drawing, read it back with ReadDwgFile, compare with the plan.</summary>
    private static void SaveAndVerify(Database db, IReadOnlyList<CheckSheet> t18, PaperTemplate template)
    {
        if (File.Exists(CheckPlans.SavedPath)) File.Delete(CheckPlans.SavedPath);
        int liveLayouts = LayoutCount(db);
        db.SaveAs(CheckPlans.SavedPath, DwgVersion.Current);
        Say($"A3 SAVE {CheckPlans.SavedPath} ({new FileInfo(CheckPlans.SavedPath).Length} bytes, {liveLayouts} layouts incl. Model)");

        using var rdb = new Database(false, true);
        rdb.ReadDwgFile(CheckPlans.SavedPath, FileOpenMode.OpenForReadAndAllShare, true, "");
        rdb.CloseInput(true);
        using var tr = rdb.TransactionManager.StartTransaction();
        var key = CheckPlans.Config().OwnerTagKey;
        var owned = PaperWriter.FindOwned(tr, rdb, key, CheckPlans.RunT18);
        int fileLayouts = ((DBDictionary)tr.GetObject(rdb.LayoutDictionaryId, OpenMode.ForRead)).Count;
        var layouts = owned.Where(o => o.Role == OwnedRole.Layout).ToList();
        Expect("T18 reopen layout count", layouts.Count == t18.Count && fileLayouts == liveLayouts,
            $"run layouts {layouts.Count} (plan {t18.Count}): [{string.Join(", ", layouts.Select(l => $"{l.LayoutName} {l.Handle}"))}]; drawing layouts file {fileLayouts}, live {liveLayouts}");

        foreach (var sheet in t18)
        {
            var vpObj = owned.Single(o => o.Role == OwnedRole.Viewport && o.SegmentId == sheet.Segment.Id);
            var vp = (Viewport)tr.GetObject(Id(rdb, vpObj.Handle), OpenMode.ForRead);
            var spec = sheet.View;
            double targetErr = Dist(new Pt(vp.ViewTarget.X, vp.ViewTarget.Y), spec.ModelCenter);
            double twistErr = PaperViewMath.AngleDifference(vp.TwistAngle, spec.TwistRadians);
            double scaleErr = Math.Abs(vp.CustomScale * spec.ModelUnitsPerPaperUnit - 1);
            int clipVertices = !vp.NonRectClipEntityId.IsNull && tr.GetObject(vp.NonRectClipEntityId, OpenMode.ForRead) is Polyline pl ? pl.NumberOfVertices : -1;
            int planVertices = spec.ClipInPaper!.Count;
            var tableObj = owned.Single(o => o.Role == OwnedRole.PaperTable && o.SegmentId == sheet.Segment.Id);
            var (match, detail) = TableMatches((Table)tr.GetObject(Id(rdb, tableObj.Handle), OpenMode.ForRead), sheet.Table!);
            var mapped = PaperViewMath.ModelToPaper(vp, spec.ModelCenter);
            Say($"A3 T18 reopen {vpObj.LayoutName}: {VpLine(tr, vp)}");
            Expect($"T18 reopen {vpObj.LayoutName}", targetErr <= 1e-9 && twistErr <= 1e-12 && scaleErr <= 1e-12 && clipVertices == planVertices && match
                    && Dist(mapped, template.PlanViewportCenter) <= 1e-9,
                $"target {P(vp.ViewTarget)} (plan {P(spec.ModelCenter)}) twist {Deg(vp.TwistAngle)} (plan {Deg(spec.TwistRadians)}) scale 1:{F(1 / vp.CustomScale)} (plan 1:{F(spec.ModelUnitsPerPaperUnit)}) " +
                $"clip vertices {clipVertices} (plan {planVertices}) model centre lands at {P(mapped)}; table {detail}");
        }
        tr.Commit();
    }
}

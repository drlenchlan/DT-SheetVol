// S3 probe P14: view frame geometry (HANDOFF.md 5.1, 5.2, 5.3, 7.7). Core Console safe.
//
// SVPROBEFRAMES   on the active drawing: every ViewFrameGroup, its frames (stations,
//                 alignment, name, Sheet, SheetSet, reference flags) and match lines; the
//                 5.2 hypothesis that Explode yields a block whose first line is the frame's
//                 top edge left to right, with every exploded primitive printed; the frame's
//                 center, rotation, width and height solved from that; every layout's
//                 viewports with their XData and extension dictionaries (how Create Sheets
//                 marks a "Plan" viewport), in this drawing and in a sheet drawing the
//                 frames' Sheet property names. Read only: nothing is written to any drawing.
//
// Needs a drawing made by the Create View Frames wizard (Dan, HANDOFF.md section 13). On a
// drawing without view frames it reports zero groups and still inspects the layouts.
// Output: command line and %TEMP%\sheetvol\probeFrames.log.

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using static SheetVol.Civil3D.SelfTest.ProbeCSettings;
using CivEntity = Autodesk.Civil.DatabaseServices.Entity;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using DbEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace SheetVol.Civil3D.SelfTest;

public static partial class ProbeCCommands
{
    private sealed record Seg(Point3d A, Point3d B)
    {
        public Vector3d V => B - A;
        public double Length => A.DistanceTo(B);
        public Point3d Mid => new((A.X + B.X) / 2, (A.Y + B.Y) / 2, (A.Z + B.Z) / 2);
        public double Angle => Math.Atan2(B.Y - A.Y, B.X - A.X);
    }

    /// <summary>A frame rectangle: center, rotation (radians CCW from +X, along increasing
    /// station), width along the alignment, height across it.</summary>
    private sealed record FrameRect(Point3d Center, double Rotation, double Width, double Height);

    private sealed record FrameInfo(string Name, string Handle, string Sheet, FrameRect? Rect);

    [CommandMethod("SVPROBEFRAMES", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
    public static void Frames() => Run("P14 SVPROBEFRAMES", new ProbeCLog(FramesLogName), (doc, db) =>
    {
        foreach (var f in ProbeCLog.HostFacts()) Say(f);
        Say($"drawing file: {S(() => db.Filename)}");
        var frames = new List<FrameInfo>();
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var civ = CivilDocument.GetCivilDocument(db);
            var groupIds = civ.GetViewFrameGroupIds();
            Say($"CivilDocument.GetViewFrameGroupIds(): {groupIds.Count}");
            if (groupIds.Count == 0)
                Say("no ViewFrameGroup in this drawing: P14 needs Dan's view-frame fixture pair (HANDOFF.md section 13). Layouts are still inspected below.");
            foreach (ObjectId gid in groupIds)
                Try($"group {gid.Handle}", () => ProbeGroup(tr, gid, frames));
            InspectLayouts(tr, db, "this drawing", frames);
            RegApps(tr, db);
            tr.Commit();
        }
        InspectSheetFiles(db, frames);
    });

    /// <summary>Checks SVPROBEFRAMES' own rectangle solver before a real view frame exists: an
    /// in-memory rectangle polyline with known center, rotation and size is exploded and solved
    /// exactly as a frame would be. Not P14 evidence; it only proves the probe's arithmetic.
    /// Needs FX alignments S and K (SVFIXTURE S, SVFIXTURE K).</summary>
    [CommandMethod("SVPROBEC_SOLVECHECK", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
    public static void SolveCheck() => Run("P14 SVPROBEC_SOLVECHECK", null, (doc, db) =>
    {
        using var tr = db.TransactionManager.StartTransaction();
        var s = FindAlignment(tr, db, FixtureSpec.Names.AlignmentS);
        var k = FindAlignment(tr, db, FixtureSpec.Names.AlignmentK);
        const double w = 800.0, h = 300.0;
        double kSta = (FixtureSpec.K.AnglePointStation + FixtureSpec.K.Length) / 2;
        var cases = new (string Label, Alignment Al, double Station, int FirstCorner, bool HypothesisHolds)[]
        {
            ("S, top edge first, left to right", s, 1000.0, 0, true),
            ("S, bottom edge first, right to left", s, 1000.0, 2, false),
            ("K second leg, top edge first, left to right", k, kSta, 0, true),
        };
        int failed = 0;
        foreach (var (label, al, sta, first, holds) in cases)
        {
            double e = 0, n = 0;
            al.PointLocation(sta, 0.0, ref e, ref n);
            double rot = Tangent(al, sta) ?? throw new InvalidOperationException($"no tangent on {al.Name} at {sta}");
            // Clockwise from top-left: TL, TR, BR, BL. "Top" is the left of the direction of travel.
            var local = new[] { (-w / 2, h / 2), (w / 2, h / 2), (w / 2, -h / 2), (-w / 2, -h / 2) };
            using var pl = new Polyline();
            for (int i = 0; i < 4; i++)
            {
                var (x, y) = local[(first + i) % 4];
                pl.AddVertexAt(i, new Point2d(e + x * Math.Cos(rot) - y * Math.Sin(rot), n + x * Math.Sin(rot) + y * Math.Cos(rot)), 0, 0, 0);
            }
            pl.Closed = true;
            Say($"case '{label}': expected center ({F(e)}, {F(n)}) rotation {F(Deg(rot))} deg width {F(w)} height {F(h)}; hypothesis should {(holds ? "HOLD" : "NOT HOLD")}");
            var segs = new List<Seg>();
            ExplodeTree(pl, 1, segs);
            var r = SolveFrame(segs, al, sta - w / 2, sta + w / 2, out var verdict);
            bool ok = r is not null && verdict == holds
                      && Math.Abs(r.Center.X - e) <= CornerTolerance && Math.Abs(r.Center.Y - n) <= CornerTolerance
                      && Math.Abs(Wrap(r.Rotation - rot)) <= 1e-9 && Math.Abs(r.Width - w) <= CornerTolerance && Math.Abs(r.Height - h) <= CornerTolerance;
            if (!ok) failed++;
            Say($"case '{label}': hypothesis verdict {(verdict is null ? "none" : verdict.Value ? "HOLDS" : "DOES NOT HOLD")}, independent solution {(r is null ? "none" : "solved")}: {(ok ? "PASS" : "FAIL")}");
        }
        Say(failed == 0 ? "SOLVECHECK: all cases PASS" : $"{FailCode} SOLVECHECK: {failed} case(s) FAILED");
        tr.Commit();
    });

    // ------------------------------------------------------------------ groups and frames

    private static void ProbeGroup(Transaction tr, ObjectId gid, List<FrameInfo> frames)
    {
        var g = (ViewFrameGroup)tr.GetObject(gid, OpenMode.ForRead);
        Say($"ViewFrameGroup '{S(() => g.Name)}' h={g.Handle} class={g.GetRXClass().Name} {RefFlagsC(g)}");
        RefInfo(g, "  group");
        Say($"  group extents: {Extents(g)}");
        var frameIds = g.GetViewFrameIds();
        var lineIds = g.GetMatchLineIds();
        Say($"  GetViewFrameIds: {frameIds.Count}  GetMatchLineIds: {lineIds.Count}");

        var alignments = new HashSet<ObjectId>();
        var stations = new List<(string Name, double Start, double End)>();
        int i = 0;
        foreach (ObjectId fid in frameIds)
        {
            var f = (ViewFrame)tr.GetObject(fid, OpenMode.ForRead);
            var al = f.AlignmentId.IsNull ? null : tr.GetObject(f.AlignmentId, OpenMode.ForRead) as Alignment;
            alignments.Add(f.AlignmentId);
            Say($"  frame [{i}] '{S(() => f.Name)}' h={f.Handle} class={f.GetRXClass().Name}");
            Say($"    StartStation={S(() => f.StartStation)} ({Displayed(al, f.StartStation)})  EndStation={S(() => f.EndStation)} ({Displayed(al, f.EndStation)})");
            Say($"    AlignmentId={f.AlignmentId.Handle} alignment='{(al is null ? "(not an Alignment)" : al.Name)}'  GroupId={f.GroupId.Handle}");
            Say($"    Sheet={S(() => f.Sheet)}");
            Say($"    SheetSet={S(() => f.SheetSet)}");
            Say($"    {RefFlagsC(f)}");
            RefInfo(f, "    frame");
            Say($"    GeometricExtents: {Extents(f)}");
            var segs = new List<Seg>();
            ExplodeTree(f, 2, segs);
            Say($"    {segs.Count} line segment(s) collected from the explode tree");
            var rect = al is null ? null : SolveFrame(segs, al, f.StartStation, f.EndStation);
            frames.Add(new FrameInfo(S(() => f.Name), f.Handle.ToString(), S(() => f.Sheet).Trim('\''), rect));
            stations.Add((S(() => f.Name), f.StartStation, f.EndStation));
            i++;
        }

        var lines = new List<(string Name, double Station)>();
        int j = 0;
        foreach (ObjectId mid in lineIds)
        {
            var m = (MatchLine)tr.GetObject(mid, OpenMode.ForRead);
            var al = m.AlignmentId.IsNull ? null : tr.GetObject(m.AlignmentId, OpenMode.ForRead) as Alignment;
            Say($"  match line [{j}] '{S(() => m.Name)}' h={m.Handle} class={m.GetRXClass().Name} Station={S(() => m.Station)} ({Displayed(al, m.Station)}) Number={S(() => m.Number)} AlignmentId={m.AlignmentId.Handle}");
            Say($"    {RefFlagsC(m)}");
            Say($"    GeometricExtents: {Extents(m)}");
            var segs = new List<Seg>();
            ExplodeTree(m, 2, segs);
            if (al is not null)
                foreach (var s in segs)
                    Say($"    segment {P(s.A)} -> {P(s.B)}: station/offset {StaOff(al, s.A)} -> {StaOff(al, s.B)}, length {F(s.Length)}");
            lines.Add((S(() => m.Name), m.Station));
            j++;
        }

        Say($"  alignments referenced by the group's frames: {alignments.Count}{(alignments.Count > 1 ? " (E711: mixed group)" : "")}");
        if (stations.Count > 0)
        {
            var al = alignments.Count == 1 && !alignments.First().IsNull ? tr.GetObject(alignments.First(), OpenMode.ForRead) as Alignment : null;
            if (al is not null)
                Say($"  E712: alignment '{al.Name}' stations {R(al.StartingStation)}..{R(al.EndingStation)}; first frame starts {R(stations[0].Start)} (diff {R(stations[0].Start - al.StartingStation)}); last frame ends {R(stations[^1].End)} (diff {R(stations[^1].End - al.EndingStation)})");
            var ordered = lines.OrderBy(l => l.Station).ToList();
            for (int k = 0; k + 1 < stations.Count; k++)
            {
                string ml = k < ordered.Count ? $"match line '{ordered[k].Name}' at {R(ordered[k].Station)}" : "no match line";
                Say($"  join {k}: frame '{stations[k].Name}' ends {R(stations[k].End)}, frame '{stations[k + 1].Name}' starts {R(stations[k + 1].Start)} (gap {R(stations[k + 1].Start - stations[k].End)}), {ml}");
            }
        }
    }

    /// <summary>Chains the exploded segments into the frame rectangle and solves center,
    /// rotation, width and height twice: once by the 5.2 hypothesis (first edge = top edge,
    /// left to right) and once independently from the alignment direction at mid-frame.</summary>
    private static FrameRect? SolveFrame(List<Seg> segs, Alignment al, double start, double end) =>
        SolveFrame(segs, al, start, end, out _);

    private static FrameRect? SolveFrame(List<Seg> segs, Alignment al, double start, double end, out bool? hypothesisHolds)
    {
        hypothesisHolds = null;
        var edges = FindRectangle(segs, out int at, out bool reoriented);
        if (edges is null)
        {
            Say("    SOLVE: no four consecutive segments close into a rectangle (E707 path: geometry not readable by Explode)");
            return null;
        }
        var corners = edges.Select(e => e.A).ToArray();
        var center = new Point3d(corners.Average(c => c.X), corners.Average(c => c.Y), corners.Average(c => c.Z));
        Say($"    SOLVE: rectangle = segments [{at}..{at + 3}] of the collected list{(reoriented ? " (some segments reversed to chain)" : " (chained as exploded)")}; corners {string.Join(" ", corners.Select(P))}");

        // Hypothesis (5.2): first line = top edge, left to right.
        var first = edges[0];
        double hRot = first.Angle, hW = first.Length, hH = edges[1].Length;
        var (fSta, fOff, fTan) = StationFrame(al, first.Mid);
        double dir = fTan is null ? double.NaN : Deg(Wrap(hRot - fTan.Value));
        bool alongStation = fTan is not null && Math.Abs(dir) <= HypothesisAngleDeg;
        bool onLeft = fOff is < 0;
        hypothesisHolds = alongStation && onLeft;
        Say($"    hypothesis (first edge is the top edge, left to right): first edge {P(first.A)} -> {P(first.B)} angle {F(Deg(hRot))} deg, length {F(hW)}; " +
            $"its midpoint is at station {S(() => fSta)} offset {S(() => fOff)} (negative = left); alignment tangent there {(fTan is null ? "n/a" : F(Deg(fTan.Value)) + " deg")}; direction difference {F(dir)} deg");
        Say($"    hypothesis {(alongStation && onLeft ? "HOLDS" : "DOES NOT HOLD")}: runs with increasing station={alongStation}, lies left of the alignment (top of the sheet)={onLeft}");
        Say($"    hypothesis solution: center {P(center)} rotation {F(Deg(hRot))} deg width {F(hW)} height {F(hH)}");

        // Independent: the edge most parallel to the alignment at mid-frame, on its left side,
        // oriented with increasing station.
        double mid = (start + end) / 2;
        double? midTan = Tangent(al, mid);
        if (midTan is null)
        {
            Say($"    independent solution: no tangent at mid-frame station {R(mid)}");
            return new FrameRect(center, hRot, hW, hH);
        }
        int best = Enumerable.Range(0, 4).OrderByDescending(k => Math.Abs(Math.Cos(edges[k].Angle - midTan.Value))).First();
        int opposite = (best + 2) % 4;
        var (_, offBest, _) = StationFrame(al, edges[best].Mid);
        var (_, offOpp, _) = StationFrame(al, edges[opposite].Mid);
        int top = offBest is not null && offOpp is not null && offOpp < offBest ? opposite : best;
        var topEdge = edges[top];
        double rot = topEdge.Angle;
        if (Math.Cos(rot - midTan.Value) < 0) rot = Wrap(rot + Math.PI);
        double w = topEdge.Length, h = edges[(top + 1) % 4].Length;
        Say($"    independent solution (mid-frame station {R(mid)}, tangent {F(Deg(midTan.Value))} deg): top edge = rectangle edge {top}; center {P(center)} rotation {F(Deg(rot))} deg width {F(w)} height {F(h)}");
        Say($"    hypothesis vs independent: rotation diff {F(Deg(Wrap(hRot - rot)))} deg, width diff {E(hW - w)}, height diff {E(hH - h)}");

        var staOffs = corners.Select(c => StaOff(al, c)).ToList();
        Say($"    corners as station/offset: {string.Join("  ", staOffs)} (frame StartStation {R(start)}, EndStation {R(end)})");
        return new FrameRect(center, rot, w, h);
    }

    /// <summary>First run of four consecutive segments that chain end to start and close with
    /// square corners; a second pass lets a segment run either way.</summary>
    private static Seg[]? FindRectangle(List<Seg> segs, out int at, out bool reoriented)
    {
        foreach (bool allowReverse in new[] { false, true })
        {
            for (int i = 0; i + 4 <= segs.Count; i++)
            {
                var e = new Seg[4];
                e[0] = segs[i];
                bool ok = true;
                for (int k = 1; k < 4 && ok; k++)
                {
                    var s = segs[i + k];
                    if (s.A.DistanceTo(e[k - 1].B) <= CornerTolerance) e[k] = s;
                    else if (allowReverse && s.B.DistanceTo(e[k - 1].B) <= CornerTolerance) e[k] = new Seg(s.B, s.A);
                    else if (allowReverse && k == 1 && s.A.DistanceTo(e[0].A) <= CornerTolerance) { e[0] = new Seg(e[0].B, e[0].A); e[1] = s; }
                    else ok = false;
                }
                if (!ok || e[3].B.DistanceTo(e[0].A) > CornerTolerance) continue;
                bool square = Enumerable.Range(0, 4).All(k =>
                {
                    var u = e[k].V; var v = e[(k + 1) % 4].V;
                    return u.Length > 0 && v.Length > 0 && Math.Abs(u.GetNormal().DotProduct(v.GetNormal())) <= SquareTolerance;
                });
                if (!square) continue;
                at = i;
                reoriented = allowReverse;
                return e;
            }
        }
        at = -1;
        reoriented = false;
        return null;
    }

    // ------------------------------------------------------------------ explode

    private static void ExplodeTree(DbEntity ent, int depth, List<Seg> segs)
    {
        string ind = new(' ', 2 * depth);
        if (depth > 2 * ExplodeMaxDepth) { Say($"{ind}depth limit reached at {ent.GetType().Name}"); return; }
        var coll = new DBObjectCollection();
        try { ent.Explode(coll); }
        catch (System.Exception ex) { Say($"{ind}{ent.GetType().Name}.Explode threw {Describe(ex)}"); return; }
        Say($"{ind}{ent.GetType().Name}.Explode -> {coll.Count} object(s)");
        int i = 0;
        foreach (DBObject o in coll)
        {
            try
            {
                string head = $"{ind}  [{i}] ";
                switch (o)
                {
                    case Line l:
                        segs.Add(new Seg(l.StartPoint, l.EndPoint));
                        Say($"{head}Line {P(l.StartPoint)} -> {P(l.EndPoint)} length={F(l.Length)} angle={F(Deg(l.Angle))} deg layer={l.Layer}");
                        break;
                    case Polyline pl:
                        Say($"{head}Polyline vertices={pl.NumberOfVertices} closed={pl.Closed} layer={pl.Layer}: " +
                            string.Join(" ", Enumerable.Range(0, pl.NumberOfVertices).Select(k => P(pl.GetPoint3dAt(k)) + (pl.GetBulgeAt(k) != 0 ? $" bulge {F(pl.GetBulgeAt(k))}" : ""))));
                        ExplodeTree(pl, depth + 2, segs);
                        break;
                    case BlockReference br:
                        Say($"{head}BlockReference name={S(() => br.Name)} position={P(br.Position)} rotation={F(Deg(br.Rotation))} deg scale=({F(br.ScaleFactors.X)}, {F(br.ScaleFactors.Y)}, {F(br.ScaleFactors.Z)}) layer={br.Layer}");
                        ExplodeTree(br, depth + 2, segs);
                        break;
                    case MText mt:
                        Say($"{head}MText '{mt.Text}' at {P(mt.Location)} rotation={F(Deg(mt.Rotation))} deg layer={mt.Layer}");
                        break;
                    case DBText t:
                        Say($"{head}DBText '{t.TextString}' at {P(t.Position)} rotation={F(Deg(t.Rotation))} deg layer={t.Layer}");
                        break;
                    case Curve c:
                        Say($"{head}{c.GetType().Name} {S(() => c.StartPoint)} -> {S(() => c.EndPoint)} layer={c.Layer}");
                        if (c is Polyline2d or Polyline3d) ExplodeTree(c, depth + 2, segs);
                        break;
                    case DbEntity en:
                        Say($"{head}{en.GetType().Name} class={en.GetRXClass().Name} extents {Extents(en)} layer={en.Layer}");
                        break;
                    default:
                        Say($"{head}{o.GetType().Name} class={o.GetRXClass().Name}");
                        break;
                }
            }
            finally { o.Dispose(); }
            i++;
        }
    }

    // ------------------------------------------------------------------ layouts and viewports

    private static void InspectLayouts(Transaction tr, Database db, string where, List<FrameInfo> frames)
    {
        var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        Say($"[{where}] layouts: {dict.Count} including Model");
        // Each frame belongs to the layout with the longest name its Sheet string contains,
        // so "Sheet 1" does not also claim the frame whose Sheet names "Sheet 10".
        var names = new List<string>();
        foreach (DBDictionaryEntry entry in dict) names.Add(entry.Key);
        string? LayoutOf(FrameInfo f) => names.Where(n => n.Length > 0 && f.Sheet.Contains(n, StringComparison.OrdinalIgnoreCase)).MaxBy(n => n.Length);
        foreach (DBDictionaryEntry entry in dict)
        {
            var lay = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
            if (lay.ModelType) continue;
            Say($"[{where}] layout '{lay.LayoutName}' tab {lay.TabOrder} h={lay.Handle} plot style {S(() => lay.CurrentStyleSheet)}");
            Annotations(tr, lay, "  layout");
            var paper = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var viewports = new List<Viewport>();
            foreach (ObjectId id in paper)
            {
                var obj = tr.GetObject(id, OpenMode.ForRead);
                string k = obj.GetRXClass().Name;
                counts[k] = counts.TryGetValue(k, out var c) ? c + 1 : 1;
                if (obj is Viewport v) viewports.Add(v);
            }
            Say($"  paper space contents: {(counts.Count == 0 ? "empty" : string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}")))}");
            for (int n = 0; n < viewports.Count; n++)
            {
                var v = viewports[n];
                var mc = ModelCenter(v);
                Say($"  viewport [{n}]{(n == 0 ? " (first in the block: AutoCAD's overall viewport slot)" : "")} h={v.Handle} Number={S(() => v.Number)} layer={v.Layer} " +
                    $"size {F(v.Width)} x {F(v.Height)} at {P(v.CenterPoint)} CustomScale={S(() => v.CustomScale)} StandardScale={S(() => v.StandardScale)} " +
                    $"TwistAngle={F(Deg(v.TwistAngle))} deg ViewTarget={P(v.ViewTarget)} ViewCenter={P(v.ViewCenter)} model center {P(mc)} " +
                    $"NonRectClipOn={v.NonRectClipOn} clip={v.NonRectClipEntityId.Handle} Locked={S(() => v.Locked)} On={S(() => v.On)}");
                Annotations(tr, v, "    viewport");
            }

            // Frame-to-viewport check: the frame whose Sheet names this layout, and the floating
            // viewport whose model center is nearest the solved frame center.
            var match = frames.FirstOrDefault(f => f.Rect is not null && string.Equals(LayoutOf(f), entry.Key, StringComparison.OrdinalIgnoreCase));
            if (match?.Rect is { } r && viewports.Count > 1)
            {
                var vp = viewports.Skip(1).OrderBy(v => ModelCenter(v).DistanceTo(r.Center)).First();
                Say($"  frame '{match.Name}' (Sheet names this layout): nearest floating viewport h={vp.Handle}; model center distance {F(ModelCenter(vp).DistanceTo(r.Center))}; " +
                    $"TwistAngle + frame rotation = {F(Deg(Wrap(vp.TwistAngle + r.Rotation)))} deg (0 when Twist = -rotation, 6.6); " +
                    $"CustomScale {R(vp.CustomScale)} vs viewport width / frame width {R(vp.Width / r.Width)} and height / height {R(vp.Height / r.Height)} (E708)");
            }
        }
    }

    /// <summary>Model point shown at the viewport's paper center: ViewTarget + R(−twist)·ViewCenter
    /// (inverse of S2's P12 mapping).</summary>
    private static Point3d ModelCenter(Viewport v)
    {
        double c = Math.Cos(-v.TwistAngle), s = Math.Sin(-v.TwistAngle);
        var vc = v.ViewCenter;
        return new Point3d(v.ViewTarget.X + c * vc.X - s * vc.Y, v.ViewTarget.Y + s * vc.X + c * vc.Y, v.ViewTarget.Z);
    }

    /// <summary>XData (every registered application) and the extension dictionary, one level
    /// of nesting, Xrecord contents printed.</summary>
    private static void Annotations(Transaction tr, DBObject o, string label)
    {
        using (var rb = o.XData)
            Say(rb is null ? $"{label} XData: none" : $"{label} XData: {Values(rb)}");
        if (o.ExtensionDictionary.IsNull) { Say($"{label} extension dictionary: none"); return; }
        var ext = (DBDictionary)tr.GetObject(o.ExtensionDictionary, OpenMode.ForRead);
        Say($"{label} extension dictionary {o.ExtensionDictionary.Handle}: {ext.Count} entr{(ext.Count == 1 ? "y" : "ies")}");
        foreach (DBDictionaryEntry e in ext) DictEntry(tr, e, label + "   ", 1);
    }

    private static void DictEntry(Transaction tr, DBDictionaryEntry e, string ind, int depth)
    {
        var obj = tr.GetObject(e.Value, OpenMode.ForRead);
        switch (obj)
        {
            case Xrecord x:
                using (var data = x.Data) Say($"{ind}'{e.Key}': Xrecord {Values(data)}");
                break;
            case DBDictionary d when depth < 3:
                Say($"{ind}'{e.Key}': dictionary with {d.Count} entr{(d.Count == 1 ? "y" : "ies")}");
                foreach (DBDictionaryEntry c in d) DictEntry(tr, c, ind + "  ", depth + 1);
                break;
            default:
                Say($"{ind}'{e.Key}': {obj.GetRXClass().Name} ({obj.GetType().Name}) h={obj.Handle}");
                break;
        }
    }

    private static string Values(ResultBuffer? rb)
    {
        if (rb is null) return "(null)";
        var all = rb.AsArray();
        const int cap = 80;
        var shown = all.Take(cap).Select(tv => $"({tv.TypeCode} {tv.Value})");
        return string.Join(" ", shown) + (all.Length > cap ? $" ... {all.Length - cap} more" : "");
    }

    private static void RegApps(Transaction tr, Database db)
    {
        var t = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        var names = new List<string>();
        foreach (ObjectId id in t) names.Add(((RegAppTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);
        Say($"registered applications ({names.Count}): {string.Join(", ", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}");
    }

    /// <summary>A Sheet string that names another .dwg: opened read-only as a side database and
    /// its layouts inspected the same way.</summary>
    private static void InspectSheetFiles(Database db, List<FrameInfo> frames)
    {
        string here = S(() => db.Filename).Trim('\'');
        string? folder = string.IsNullOrEmpty(here) ? null : Path.GetDirectoryName(here);
        foreach (var sheet in frames.Select(f => f.Sheet).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
            Try($"Sheet '{sheet}'", () => InspectSheetFile(sheet, here, folder, frames));
    }

    private static void InspectSheetFile(string sheet, string here, string? folder, List<FrameInfo> frames)
    {
        int idx = sheet.IndexOf(".dwg", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) { Say($"Sheet '{sheet}' names no .dwg file; layouts matched by name in this drawing only"); return; }
        string file = sheet[..(idx + 4)].Trim().Trim('"');
        string rest = sheet[(idx + 4)..];
        string path = Path.IsPathRooted(file) || folder is null ? file : Path.Combine(folder, file);
        Say($"Sheet '{sheet}': file part '{file}' resolves to '{path}', remainder '{rest}'");
        if (!string.IsNullOrEmpty(here) && string.Equals(Path.GetFullPath(path), Path.GetFullPath(here), StringComparison.OrdinalIgnoreCase))
        {
            Say("  that is this drawing; inspected above");
            return;
        }
        if (!File.Exists(path)) { Say("  file not found; not inspected"); return; }
        using var side = new Database(false, true);
        side.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
        side.CloseInput(true);
        using var tr = side.TransactionManager.StartTransaction();
        InspectLayouts(tr, side, Path.GetFileName(path), frames);
        RegApps(tr, side);
        tr.Commit();
    }

    // ------------------------------------------------------------------ alignment helpers

    private static string Displayed(Alignment? al, double raw) =>
        al is null ? "no alignment" : "displayed " + S(() => al.GetStationStringWithEquations(raw));

    private static string StaOff(Alignment al, Point3d p)
    {
        try
        {
            double sta = 0, off = 0;
            al.StationOffset(p.X, p.Y, ref sta, ref off);
            return $"{F3(sta)}/{F3(off)}";
        }
        catch (System.Exception ex) { return $"({ex.GetType().Name})"; }
    }

    private static (double? Station, double? Offset, double? Tangent) StationFrame(Alignment al, Point3d p)
    {
        try
        {
            double sta = 0, off = 0;
            al.StationOffset(p.X, p.Y, ref sta, ref off);
            return (sta, off, Tangent(al, sta));
        }
        catch (System.Exception) { return (null, null, null); }
    }

    /// <summary>Direction of the alignment at a station (radians CCW from +X), by a short chord
    /// through PointLocation; null when the station is off the alignment.</summary>
    private static double? Tangent(Alignment al, double station)
    {
        try
        {
            const double h = 0.01;
            double a = Math.Max(al.StartingStation, station - h), b = Math.Min(al.EndingStation, station + h);
            double e1 = 0, n1 = 0, e2 = 0, n2 = 0;
            al.PointLocation(a, 0.0, ref e1, ref n1);
            al.PointLocation(b, 0.0, ref e2, ref n2);
            return Math.Atan2(n2 - n1, e2 - e1);
        }
        catch (System.Exception) { return null; }
    }

    private static string Extents(DbEntity e)
    {
        try { var x = e.GeometricExtents; return $"{P(x.MinPoint)}..{P(x.MaxPoint)}"; }
        catch (System.Exception ex) { return $"threw {ex.GetType().Name}"; }
    }

    private static string RefFlagsC(CivEntity e) =>
        $"IsReferenceObject={S(() => e.IsReferenceObject)} IsReferenceStale={S(() => e.IsReferenceStale)} IsReferenceValid={S(() => e.IsReferenceValid)} " +
        $"IsReadOnlyReferenceObject={S(() => e.IsReadOnlyReferenceObject)} IsReferencedSourceExisting={S(() => e.IsReferencedSourceExisting)}";

    private static void RefInfo(CivEntity e, string label)
    {
        bool isRef;
        try { isRef = e.IsReferenceObject; } catch (System.Exception ex) { Say($"{label} IsReferenceObject threw {Describe(ex)}"); return; }
        if (!isRef) return;
        Try($"{label} GetReferenceInfo", () =>
        {
            var k = e.GetReferenceInfo();
            Say($"{label} GetReferenceInfo: Name={k?.Name} Type={k?.Type} SourceDrawing={k?.SourceDrawing} IsSourceDrawingExistent={k?.IsSourceDrawingExistent}");
        });
    }
}

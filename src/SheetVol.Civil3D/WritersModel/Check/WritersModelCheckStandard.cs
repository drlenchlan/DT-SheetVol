// A2 headless check, phase A: the Standard preset on Fixture A, T1's segments.
// E501, create, E811/T19, T12, E502, E503, E504, E505, E802, T13, E308, T20.

using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Civil3D.SelfTest;
using SheetVol.Core;
using SheetVol.Core.Geometry;
using SheetVol.Core.Output;
using SheetVol.Core.Tracking;
using SheetVol.Core.Volumes;

namespace SheetVol.Civil3D.WritersModel.Check;

public static partial class WritersModelCheck
{
    private sealed class StdState
    {
        public string Name = "WM-STANDARD";
        public List<SegmentDef> Segments = [];
        public List<AnalysisDef> Analyses = [];
        public List<(double X, double Cut, double Fill)> Pyramids = [];
        public TablePlacement? MasterPlacement;
    }

    private static readonly StdState Std = new();
    private static readonly List<SourceRef> StdSources = [];

    private static SegmentDef Seg(int run, int n, string label, double from, double to) => new(G(run, n), label, from, to, -100, 100);

    /// <summary>Builds the whole Standard plan the way A4 will: C1 boundaries, C2 reconcile and table models, A2 helpers.</summary>
    private static (ModelWritePlan Plan, RunDefinition Run, IReadOnlyList<Boundary> Boundaries) StandardPlan(
        StdState s, ReportConfig? report = null, Func<SegmentDef, TableModel, TableModel>? tamper = null)
    {
        var rep = report ?? Report;
        var built = BoundaryBuilder.Build(AlignmentS, s.Segments, -100, 100, Geom);
        if (built.Problems.Any(p => p.Severity == Severity.Error) || built.Boundaries.Count != s.Segments.Count)
            throw new InvalidOperationException("check defect: boundaries did not build: " + string.Join("; ", built.Problems.Select(p => p.Message)));
        var source = new PyramidVolumeSource(s.Pyramids.ToList());
        var rec = Reconciler.Reconcile(new ReconcileRequest(s.Segments, built.Boundaries, built.Band.Select(b => b.Tessellated).ToList(),
            s.Analyses.Select(a => new AnalysisInput(a, source)).ToList(), Units), rep.Volumes);
        if (!rec.Succeeded) throw new InvalidOperationException("check defect: reconcile failed: " + string.Join("; ", rec.Problems.Select(p => p.Message)));

        var tables = new List<ModelTableSpec>();
        foreach (var seg in s.Segments)
        {
            var model = SegmentTableBuilder.Build(rec, seg.Id, AreaUnit.SquareFeet, AlignmentS.FormatStation, rep).Value;
            if (tamper is not null) model = tamper(seg, model);
            var boundary = built.Boundaries.Single(b => b.SegmentId == seg.Id);
            tables.Add(ModelTableSpec.ForSegment(model, ModelTablePlacement.AtSegment(boundary, Scale, Cfg)));
        }
        var master = MasterTableBuilder.Build(rec, AreaUnit.SquareFeet, AlignmentS.FormatStation, rep).Value;
        tables.Add(ModelTableSpec.ForMaster(master, s.MasterPlacement ?? ModelTablePlacement.MasterInModel(built.Boundaries, Scale, Cfg)));
        var marks = MatchMarkPlanner.Plan(s.Segments, AlignmentS, _ => Scale, Cfg, Geom.StationTolerance, rep.TableCulture());
        var plan = new ModelWritePlan(built.Boundaries, [], marks, tables, []);
        return (plan, Def(s.Name, s.Segments, s.Analyses), built.Boundaries);
    }

    private static ApplyOutcome RegenStd(bool commit = true) { var (plan, run, _) = StandardPlan(Std); return Regen(run, plan, StdSources, commit); }

    private static OwnedKey BKey(Guid seg) => new(seg, OwnedRole.Boundary);
    private static OwnedKey TKey(Guid seg) => new(seg, OwnedRole.ModelTable, OwnedQualifiers.StandardTable);
    private static readonly OwnedKey MasterKey = new(Guid.Empty, OwnedRole.MasterTable);

    private static void StandardPhase()
    {
        Say("----- phase A: Standard preset, Fixture A, T1 segments START..END@500, offsets (-100, +100), scale 1\"=50' -----");
        Std.Segments = [Seg(1, 1, "1", 0, 500), Seg(1, 2, "2", 500, 1000), Seg(1, 3, "3", 1000, 1500), Seg(1, 4, "4", 1500, 2000)];
        Std.Analyses = [new AnalysisDef(G(1, 101), "EG vs FG_A", FixtureSpec.Names.EG, FixtureSpec.Names.FgA)];
        Std.Pyramids = FixtureSpec.Pyramids.Select(p => (p.CenterX, p.IsCut ? p.VolumeFt3 : 0, p.IsCut ? 0 : p.VolumeFt3)).ToList();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            StdSources.Add(new SourceRef(SourceKinds.Alignment, FixtureSpec.Names.AlignmentS, WritersModel.Handles.Of(_alignmentId)));
            StdSources.Add(new SourceRef(SourceKinds.Surface, FixtureSpec.Names.EG, WritersModel.Handles.Of(_egId)));
            StdSources.Add(new SourceRef(SourceKinds.Surface, FixtureSpec.Names.FgA, WritersModel.Handles.Of(_fgaId)));
            tr.Commit();
        }
        Guid s1 = Std.Segments[0].Id, s2 = Std.Segments[1].Id, s3 = Std.Segments[2].Id, s4 = Std.Segments[3].Id;

        // ---- E501: missing table style, missing layer: fail naming it, write nothing.
        {
            var badReport = Report with { SegmentTable = Report.SegmentTable with { Style = Report.SegmentTable.Style with { TableStyle = Report.SegmentTable.Style.TableStyle + CheckSettings.MissingSuffix } } };
            var (plan, run, _) = StandardPlan(Std, badReport);
            var o = Regen(run, plan, StdSources, report: badReport);
            Check("E501", !o.Result.Succeeded && o.Has(ModelWriterCodes.MissingStyleOrLayer) && o.Events.Appended.Count == 0 && o.Events.Modified.Count == 0,
                $"missing table style: succeeded={o.Result.Succeeded}, {o.Events.Summary}; {Problems(o, ModelWriterCodes.MissingStyleOrLayer)}");
            var badCfg = Cfg with { BoundaryLayer = Cfg.BoundaryLayer + CheckSettings.MissingSuffix };
            var (plan2, run2, _) = StandardPlan(Std);
            var o2 = Regen(run2, plan2, StdSources, cfg: badCfg);
            Check("E501", !o2.Result.Succeeded && o2.Has(ModelWriterCodes.MissingStyleOrLayer) && o2.Events.Appended.Count == 0 && o2.Events.Modified.Count == 0,
                $"missing layer: succeeded={o2.Result.Succeeded}, {o2.Events.Summary}; {Problems(o2, ModelWriterCodes.MissingStyleOrLayer)}");
        }

        // ---- Create.
        var c1 = RegenStd();
        Check("create", c1.Result.Succeeded && c1.Count(ChangeKind.Create, OwnedRole.Boundary) == 4 && c1.Count(ChangeKind.Create, OwnedRole.MatchMark) == 12
                        && c1.Count(ChangeKind.Create, OwnedRole.ModelTable) == 4 && c1.Count(ChangeKind.Create, OwnedRole.MasterTable) == 1 && c1.Store?.Kind == StoreWriteKind.Created,
            $"{c1.Kinds}; store {c1.Store?.Kind} key {c1.Store?.Key}; {c1.Events.Summary}");
        Say("create handles: boundaries " + string.Join(" ", Std.Segments.Select(s => c1.Handles[BKey(s.Id)]))
            + "; tables " + string.Join(" ", Std.Segments.Select(s => c1.Handles[TKey(s.Id)])) + "; master " + c1.Handles[MasterKey]);
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var (_, _, boundaries) = StandardPlan(Std);
            int exact = 0;
            foreach (var b in boundaries)
            {
                var pl = Open<Polyline>(tr, c1.Handles[BKey(b.SegmentId)]);
                var want = PolylineWriter<Boundary>.Normalize(b.Drawn);
                bool same = pl.Closed && pl.NumberOfVertices == want.Count && pl.Layer == Cfg.BoundaryLayer
                            && Enumerable.Range(0, want.Count).All(i => pl.GetPoint2dAt(i).X == want[i].Point.X && pl.GetPoint2dAt(i).Y == want[i].Point.Y && pl.GetBulgeAt(i) == want[i].Bulge);
                if (same) exact++;
            }
            Check("boundary", exact == 4, $"{exact}/4 boundary polylines closed on layer {Cfg.BoundaryLayer} with vertices and bulges identical to Boundary.Drawn");
            var t1 = Open<Table>(tr, c1.Handles[TKey(s1)]);
            for (int r = 0; r < t1.Rows.Count; r++) Say($"segment 1 table r{r}: {RowText(t1, r)}");
            Check("tables", RowText(t1, FindRow(t1, Report.SegmentTable.CutLabel)) == "CUT | 100 CY" && RowText(t1, FindRow(t1, Report.SegmentTable.FillLabel)) == "FILL | 50 CY",
                $"segment 1 table: layer {t1.Layer}, {t1.Rows.Count}x{t1.Columns.Count}, text height {t1.Cells[1, 0].TextHeight}, cut/fill as T1 0-500 (100/50 CY)");
            var m = Open<Table>(tr, c1.Handles[MasterKey]);
            for (int r = 0; r < m.Rows.Count; r++) Say($"master table r{r}: {RowText(m, r)}");
            var mark = Open<MText>(tr, c1.Handles[new OwnedKey(s1, OwnedRole.MatchMark, OwnedQualifiers.EndText)]);
            var line = Open<Line>(tr, c1.Handles[new OwnedKey(s1, OwnedRole.MatchMark, OwnedQualifiers.EndLine)]);
            Check("marks", mark.Text.StartsWith("MATCH LINE STA 5+00", StringComparison.Ordinal) && mark.Text.EndsWith("SEE SHEET 2", StringComparison.Ordinal),
                $"segment 1 end mark: text '{mark.Text}' at ({F(mark.Location.X)}, {F(mark.Location.Y)}) height {F(mark.TextHeight)}; line ({F(line.StartPoint.X)}, {F(line.StartPoint.Y)})-({F(line.EndPoint.X)}, {F(line.EndPoint.Y)})");
            var tag = OwnerTag.Read(tr, Open<Polyline>(tr, c1.Handles[BKey(s1)]), Cfg.OwnerTagKey);
            Check("tag", tag is not null && tag.RunName == Std.Name && tag.Key == BKey(s1), $"boundary 1 extension dictionary '{Cfg.OwnerTagKey}': run '{tag?.RunName}', key {tag?.Key}");
            tr.Commit();
        }

        // ---- E811 / T19: regen with nothing changed.
        var c2 = RegenStd();
        bool sameHandles = c1.Handles.OrderBy(k => k.Key.ToString()).SequenceEqual(c2.Handles.OrderBy(k => k.Key.ToString()));
        Check("E811", c2.Result.Writes.Count() == 0 && c2.Count(ChangeKind.Unchanged) == c2.Result.Changes.Count && c2.Store?.Kind == StoreWriteKind.TimestampOnly,
            $"second regen: {c2.Kinds}; writes {c2.Result.Writes.Count()}; store {c2.Store?.Kind}");
        Check("E811", c2.Events.Appended.Count == 0 && c2.Events.Erased.Count == 0 && c2.Events.Modified.Count == 1 && c2.Events.Modified[0].StartsWith("Xrecord:", StringComparison.Ordinal),
            $"database events during the second regen: {c2.Events.Summary} [{string.Join(", ", c2.Events.Modified)}] (the store record's timestamp only)");
        Check("T19", sameHandles, $"all {c2.Handles.Count} owned handles identical after the second regen");

        // ---- T12: P1 apex to 98.0 (cut doubles): same table handles, 0-500 cut 200 CY.
        Std.Pyramids[0] = (Std.Pyramids[0].X, 2 * Std.Pyramids[0].Cut, 0);
        var c3 = RegenStd();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var t1 = Open<Table>(tr, c3.Handles[TKey(s1)]);
            Check("T12", c3.Handles[TKey(s1)] == c1.Handles[TKey(s1)] && c3.Handles[MasterKey] == c1.Handles[MasterKey]
                         && RowText(t1, FindRow(t1, Report.SegmentTable.CutLabel)) == "CUT | 200 CY" && c3.Events.Appended.Count == 0,
                $"{c3.Kinds}; segment 1 table {c3.Handles[TKey(s1)]} (was {c1.Handles[TKey(s1)]}) now '{RowText(t1, FindRow(t1, Report.SegmentTable.CutLabel))}'; {c3.Events.Summary}");
            tr.Commit();
        }

        // ---- E502: the user moves and restyles table 2; a value change updates text only.
        string restyle = Report.SegmentTable.Style.TableStyle + CheckSettings.RestyleSuffix;
        Autodesk.AutoCAD.Geometry.Point3d movedTo = default;
        ObjectId restyleId = ObjectId.Null;
        UserEdit("table 2 moved by (37, 23) and restyled to " + restyle, tr =>
        {
            var t = (Table)tr.GetObject(WritersModel.Handles.Resolve(_db, c3.Handles[TKey(s2)]), OpenMode.ForWrite);
            movedTo = t.Position + new Autodesk.AutoCAD.Geometry.Vector3d(37, 23, 0);
            t.Position = movedTo;
            restyleId = ((DBDictionary)tr.GetObject(_db.TableStyleDictionaryId, OpenMode.ForRead)).GetAt(restyle);
            t.TableStyle = restyleId;
        });
        Std.Pyramids.Add((750, 270, 0));   // 10 CY of cut in segment 2
        var c4 = RegenStd();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var t2 = Open<Table>(tr, c4.Handles[TKey(s2)]);
            Check("E502", t2.Position.IsEqualTo(movedTo) && t2.TableStyle == restyleId && RowText(t2, FindRow(t2, Report.SegmentTable.CutLabel)) == "CUT | 10 CY"
                          && c4.Handles[TKey(s2)] == c1.Handles[TKey(s2)],
                $"table 2 {t2.Handle}: position {t2.Position} (user's {movedTo}), style kept={t2.TableStyle == restyleId}, cut now '{RowText(t2, FindRow(t2, Report.SegmentTable.CutLabel))}'; {c4.Kinds}");
            tr.Commit();
        }

        // ---- E503: the user erases table 3; regen recreates it at the anchor.
        string t3old = c4.Handles[TKey(s3)];
        UserEdit($"table 3 ({t3old}) erased", tr => tr.GetObject(WritersModel.Handles.Resolve(_db, t3old), OpenMode.ForWrite).Erase());
        var c5 = RegenStd();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var (_, _, boundaries) = StandardPlan(Std);
            var anchor = ModelTablePlacement.AtSegment(boundaries.Single(b => b.SegmentId == s3), Scale, Cfg).Anchor!.Value;
            var t3 = Open<Table>(tr, c5.Handles[TKey(s3)]);
            var stored = RunStore.Read(tr, _db, Std.Name, Report).Record!.Objects.Single(o => o.Key == TKey(s3)).Handle;
            Check("E503", c5.Handles[TKey(s3)] != t3old && c5.Has(ModelWriterCodes.TableRecreated) && Math.Abs(t3.Position.X - anchor.X) < 1e-9
                          && Math.Abs(t3.Position.Y - t3.Height - anchor.Y) < 1e-9 && stored == t3.Handle.ToString(),
                $"table 3 recreated as {t3.Handle} (was {t3old}); bottom-left ({F(t3.Position.X)}, {F(t3.Position.Y - t3.Height)}) = anchor ({F(anchor.X)}, {F(anchor.Y)}); store now holds {stored}; {Problems(c5, ModelWriterCodes.TableRecreated)}");
            tr.Commit();
        }

        // ---- E504: table layer locked, then frozen.
        string tableLayer = Report.SegmentTable.Style.Layer;
        UserEdit($"layer {tableLayer} locked", tr => SetLayer(tr, tableLayer, locked: true, frozen: null));
        Std.Pyramids[4] = (Std.Pyramids[4].X, 0, 4050);   // segment 4 fill 150 CY
        var c6 = RegenStd();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var t4 = Open<Table>(tr, c6.Handles[TKey(s4)]);
            var layer = Layer(tr, tableLayer);
            Check("E504", c6.Result.Succeeded && RowText(t4, FindRow(t4, Report.SegmentTable.FillLabel)) == "FILL | 150 CY" && layer.IsLocked && c6.Has(ModelWriterCodes.LayerLockedOrFrozen),
                $"locked layer: table 4 fill '{RowText(t4, FindRow(t4, Report.SegmentTable.FillLabel))}', layer locked after regen={layer.IsLocked}; {Problems(c6, ModelWriterCodes.LayerLockedOrFrozen)}");
            tr.Commit();
        }
        UserEdit($"layer {tableLayer} unlocked and frozen", tr => SetLayer(tr, tableLayer, locked: false, frozen: true));
        Std.Pyramids[4] = (Std.Pyramids[4].X, 0, 5400);   // segment 4 fill 200 CY
        var c7 = RegenStd();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var t4 = Open<Table>(tr, c7.Handles[TKey(s4)]);
            var layer = Layer(tr, tableLayer);
            Check("E504", c7.Result.Succeeded && RowText(t4, FindRow(t4, Report.SegmentTable.FillLabel)) == "FILL | 200 CY" && layer.IsFrozen && !layer.IsLocked,
                $"frozen layer: table 4 fill '{RowText(t4, FindRow(t4, Report.SegmentTable.FillLabel))}', layer still frozen={layer.IsFrozen}; {Problems(c7, ModelWriterCodes.LayerLockedOrFrozen)}");
            tr.Commit();
        }
        UserEdit($"layer {tableLayer} thawed", tr => SetLayer(tr, tableLayer, locked: false, frozen: false));

        // ---- E505: an analysis added, then removed: tables grow and shrink by one block in place.
        int segRows0, masterRows0;
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            segRows0 = Open<Table>(tr, c7.Handles[TKey(s1)]).Rows.Count;
            masterRows0 = Open<Table>(tr, c7.Handles[MasterKey]).Rows.Count;
            tr.Commit();
        }
        Std.Analyses.Add(new AnalysisDef(G(1, 102), "EG vs FG_A (2)", FixtureSpec.Names.EG, FixtureSpec.Names.FgA));
        var c8 = RegenStd();
        var c8b = RegenStd();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var t1 = Open<Table>(tr, c8.Handles[TKey(s1)]);
            var m = Open<Table>(tr, c8.Handles[MasterKey]);
            for (int r = 0; r < t1.Rows.Count; r++) Say($"segment 1 table after analysis added r{r}: {RowText(t1, r)}");
            Check("E505", c8.Handles[TKey(s1)] == c1.Handles[TKey(s1)] && c8.Handles[MasterKey] == c1.Handles[MasterKey] && t1.Rows.Count == segRows0 + 4
                          && RowText(t1, segRows0) == "EG vs FG_A (2) | " && c8.Has(ModelWriterCodes.TableResized),
                $"analysis added: segment tables {segRows0} -> {t1.Rows.Count} rows, master {masterRows0} -> {m.Rows.Count} rows, handles kept; {c8.Kinds}");
            Check("E505", c8b.Result.Writes.Count() == 0, $"regen right after the grow: {c8b.Kinds} (the grown table matches its model exactly)");
            tr.Commit();
        }
        Std.Analyses.RemoveAt(1);
        var c9 = RegenStd();
        var c9b = RegenStd();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var t1 = Open<Table>(tr, c9.Handles[TKey(s1)]);
            var m = Open<Table>(tr, c9.Handles[MasterKey]);
            Check("E505", t1.Rows.Count == segRows0 && m.Rows.Count == masterRows0 && c9.Handles[TKey(s1)] == c1.Handles[TKey(s1)] && c9b.Result.Writes.Count() == 0,
                $"analysis removed: segment tables back to {t1.Rows.Count} rows, master {m.Rows.Count}, handles kept; following regen {c9b.Kinds}");
            tr.Commit();
        }

        // ---- Generated tables the user edited structurally come back to their model and then stay put (E811).
        string masterH = c9.Handles[MasterKey], seg2H = c9.Handles[TKey(s2)];
        UserEdit("master table: footer row unmerged and text typed in its second cell; segment 2 table: a row inserted and typed in", tr =>
        {
            var m = (Table)tr.GetObject(WritersModel.Handles.Resolve(_db, masterH), OpenMode.ForWrite);
            int footer = m.Rows.Count - 1;
            m.UnmergeCells(m.Cells[footer, 0].GetMergeRange());
            m.Cells[footer, 1].TextString = "USER NOTE";
            var t2 = (Table)tr.GetObject(WritersModel.Handles.Resolve(_db, seg2H), OpenMode.ForWrite);
            t2.InsertRows(2, t2.Rows[1].Height, 1);
            t2.Cells[2, 0].TextString = "USER ROW";
            t2.Cells[2, 1].TextString = "99 CY";
        });
        var r1 = RegenStd();
        var r2 = RegenStd();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var m = Open<Table>(tr, masterH);
            var t2 = Open<Table>(tr, seg2H);
            Check("tables", r1.Result.Changes.Single(c => c.Key == MasterKey).Kind == ChangeKind.Update && r2.Result.Writes.Count() == 0
                            && m.Cells[m.Rows.Count - 1, 0].IsMerged == true && TableRenderer.Plain(m, m.Rows.Count - 1, 1) == "" && t2.Rows.Count == segRows0 && FindRow(t2, "USER ROW") < 0,
                $"after the user edits: {r1.Kinds}; master footer merged again with its hidden cell cleared, segment 2 table back to {t2.Rows.Count} rows; next regen {r2.Kinds}");
            tr.Commit();
        }

        // ---- MTEXT-special characters in table text show literally and do not cause rewrites.
        string literal = "EG {v2} \\ FG_A";
        Std.Analyses[0] = Std.Analyses[0] with { Name = literal };
        var x1 = RegenStd();
        var x2 = RegenStd();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var t1 = Open<Table>(tr, x1.Handles[TKey(s1)]);
            Check("tables", TableRenderer.Raw(t1, 1, 0) == TableRenderer.Escape(literal) && TableRenderer.Shows(t1, 1, 0, literal) && x2.Result.Writes.Count() == 0,
                $"analysis named '{literal}': segment 1 header cell raw MTEXT '{TableRenderer.Raw(t1, 1, 0)}' (plain read gives '{TableRenderer.Plain(t1, 1, 0)}': escaped braces are dropped by IgnoreMtextFormat); next regen {x2.Kinds}");
            tr.Commit();
        }
        Std.Analyses[0] = Std.Analyses[0] with { Name = "EG vs FG_A" };
        RegenStd();

        // ---- E802: the user erases boundary 2 and the end text of segment 1.
        string b2old = c9.Handles[BKey(s2)];
        var textKey = new OwnedKey(s1, OwnedRole.MatchMark, OwnedQualifiers.EndText);
        string textOld = c9.Handles[textKey];
        UserEdit($"boundary 2 ({b2old}) and segment 1 end text ({textOld}) erased", tr =>
        {
            tr.GetObject(WritersModel.Handles.Resolve(_db, b2old), OpenMode.ForWrite).Erase();
            tr.GetObject(WritersModel.Handles.Resolve(_db, textOld), OpenMode.ForWrite).Erase();
        });
        var c10 = RegenStd();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var rec = RunStore.Read(tr, _db, Std.Name, Report).Record!;
            string b2stored = rec.Objects.Single(o => o.Key == BKey(s2)).Handle, textStored = rec.Objects.Single(o => o.Key == textKey).Handle;
            Check("E802", c10.Count(ChangeKind.Create) == 2 && b2stored != b2old && textStored != textOld && Alive(b2stored) && Alive(textStored) && c10.Has(ModelWriterCodes.ObjectRecreated),
                $"{c10.Kinds}; boundary 2 {b2old} -> {b2stored}, end text {textOld} -> {textStored}; mapping repaired in the store; {Problems(c10, ModelWriterCodes.ObjectRecreated)}");
            tr.Commit();
        }

        // ---- T13: insert a break at 250.
        var h10 = c10.Handles;
        Guid s1b = G(1, 5);
        Std.Segments = [Seg(1, 1, "1", 0, 250), new SegmentDef(s1b, "2", 250, 500, -100, 100), Std.Segments[1] with { Label = "3" }, Std.Segments[2] with { Label = "4" }, Std.Segments[3] with { Label = "5" }];
        var c11 = RegenStd();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var t1 = Open<Table>(tr, c11.Handles[TKey(s1)]);
            var tNew = Open<Table>(tr, c11.Handles[TKey(s1b)]);
            Check("T13", c11.Handles[BKey(s1)] == h10[BKey(s1)] && c11.Handles[TKey(s1)] == h10[TKey(s1)] && !h10.ContainsKey(TKey(s1b))
                         && c11.Result.Changes.Single(c => c.Key == BKey(s1)).Kind == ChangeKind.Update && c11.Result.Changes.Single(c => c.Key == TKey(s1b)).Kind == ChangeKind.Create
                         && TableRenderer.Plain(t1, 0, 0) == "EARTHWORK STA 0+00 TO STA 2+50" && TableRenderer.Plain(tNew, 0, 0) == "EARTHWORK STA 2+50 TO STA 5+00",
                $"{c11.Kinds}; left segment keeps boundary {c11.Handles[BKey(s1)]} and table {c11.Handles[TKey(s1)]} ('{TableRenderer.Plain(t1, 0, 0)}', '{RowText(t1, FindRow(t1, Report.SegmentTable.CutLabel))}'); new segment table {c11.Handles[TKey(s1b)]} ('{TableRenderer.Plain(tNew, 0, 0)}', '{RowText(tNew, FindRow(tNew, Report.SegmentTable.FillLabel))}')");
            tr.Commit();
        }

        // ---- E308: remove the break again: the right part's boundary, marks and table are erased.
        var h11 = c11.Handles;
        var goneKeys = h11.Keys.Where(k => k.SegmentId == s1b).ToList();
        Std.Segments = [Seg(1, 1, "1", 0, 500), Std.Segments[2] with { Label = "2" }, Std.Segments[3] with { Label = "3" }, Std.Segments[4] with { Label = "4" }];
        var c12 = RegenStd();
        Check("E308", goneKeys.Count == 6 && goneKeys.All(k => c12.Result.Changes.Any(c => c.Key == k && c.Kind == ChangeKind.Erase)) && goneKeys.All(k => !Alive(h11[k]))
                      && c12.Handles[BKey(s1)] == h10[BKey(s1)] && c12.Handles[TKey(s1)] == h10[TKey(s1)],
            $"{c12.Kinds}; erased {string.Join(", ", goneKeys.Select(k => $"{k.Role}{(k.Qualifier.Length > 0 ? "/" + k.Qualifier : "")}={h11[k]}"))}; left segment keeps {c12.Handles[BKey(s1)]} and {c12.Handles[TKey(s1)]}");

        // ---- T20: failure atomicity.
        var before = Snapshot();
        Std.Pyramids = Std.Pyramids.Select(p => (p.X, p.Cut * 3, p.Fill * 3)).ToList();
        {
            var (plan, run, _) = StandardPlan(Std, tamper: (seg, model) => seg.Id != s3 ? model
                : model with { Rows = model.Rows.Select((r, i) => i == 1 ? r with { Cells = [.. r.Cells, "extra"] } : r).ToList() });
            var bad = Regen(run, plan, StdSources);
            var after = Snapshot();
            Check("T20", !bad.Result.Succeeded && bad.Has(ModelWriterCodes.InvalidInput) && bad.Events.Appended.Count + bad.Events.Modified.Count + bad.Events.Erased.Count == 0 && after.SequenceEqual(before),
                $"forced error in segment 3's table model: succeeded={bad.Result.Succeeded}; {bad.Events.Summary}; {before.Count} owned objects and store records identical after; {Problems(bad, ModelWriterCodes.InvalidInput)}");
        }
        {
            var rolled = RegenStd(commit: false);
            var after = Snapshot();
            Check("T20", rolled.Result.Succeeded && rolled.Result.Writes.Count() > 0 && after.SequenceEqual(before),
                $"valid apply with {rolled.Result.Writes.Count()} writes, then the caller aborts its transaction: {before.Count} owned objects and store records identical after");
        }
        Std.Pyramids = Std.Pyramids.Select(p => (p.X, p.Cut / 3, p.Fill / 3)).ToList();
        var settle = RegenStd();
        Check("T19", settle.Result.Writes.Count() == 0, $"after the aborted attempts the drawing still matches the plan: {settle.Kinds}");
    }

    private static void SetLayer(Transaction tr, string name, bool locked, bool? frozen)
    {
        var lt = (LayerTable)tr.GetObject(_db.LayerTableId, OpenMode.ForRead);
        var layer = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
        layer.IsLocked = locked;
        if (frozen is bool f) layer.IsFrozen = f;
    }

    private static LayerTableRecord Layer(Transaction tr, string name)
    {
        var lt = (LayerTable)tr.GetObject(_db.LayerTableId, OpenMode.ForRead);
        return (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForRead);
    }
}

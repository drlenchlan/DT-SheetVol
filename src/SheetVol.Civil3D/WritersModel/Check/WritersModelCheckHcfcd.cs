// A2 headless check, phase B: bulges preserved, and the HcfcdSegment preset with hand-built
// impact results (T25 then T26 values): T31, E1015, E1016, E1017, E1018, no band outline (D13).

using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Civil3D.SelfTest;
using SheetVol.Core;
using SheetVol.Core.Geometry;
using SheetVol.Core.Output;
using SheetVol.Core.Tracking;

namespace SheetVol.Civil3D.WritersModel.Check;

public static partial class WritersModelCheck
{
    // ------------------------------------------------------------------ bulges

    private static void BulgePhase()
    {
        Say("----- bulges: a hand-built boundary with arcs (an annulus sector, R 300 and 500, 60 degrees) -----");
        var seg = new SegmentDef(G(3, 1), "B1", 0, 1, -100, 100);
        double t = Math.Tan(Math.PI / 3 / 4);                 // bulge of a 60-degree arc
        Pt P(double r, double deg) => new(3000 + r * Math.Cos(deg * Math.PI / 180), r * Math.Sin(deg * Math.PI / 180));
        var drawn = new List<PolyVertex> { new(P(300, 0), t), new(P(300, 60), 0), new(P(500, 60), -t), new(P(500, 0), 0) };
        var boundary = new Boundary(seg.Id, drawn, Polygon.TessellateBulges(drawn, closed: true, Geom.MidOrdinate).ToList());
        var run = Def("WM-BULGE", [seg], []);
        var plan = new ModelWritePlan([boundary], [], [], [], []);
        var o1 = Regen(run, plan);
        var o2 = Regen(run, plan);
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var pl = Open<Polyline>(tr, o1.Handles[BKey(seg.Id)]);
            string got = string.Join(" ", Enumerable.Range(0, pl.NumberOfVertices).Select(i => pl.GetBulgeAt(i).ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
            bool exact = pl.NumberOfVertices == 4 && Enumerable.Range(0, 4).All(i => pl.GetBulgeAt(i) == drawn[i].Bulge);
            Check("bulge", exact && o2.Result.Writes.Count() == 0,
                $"polyline {pl.Handle} bulges [{got}] equal Drawn exactly; arc length {F(pl.Length)} (analytic {F(Math.PI / 3 * 800 + 400)}); second regen {o2.Kinds}");
            tr.Commit();
        }
        var o3 = Regen(run, ModelWritePlan.Empty);
        Check("E308", o3.Count(ChangeKind.Erase) == 1 && !Alive(o1.Handles[BKey(seg.Id)]), $"empty plan erases what the run owns: {o3.Kinds}");
    }

    // ------------------------------------------------------------------ HCFCD

    private sealed class HState
    {
        public string Name = "WM-HCFCD";
        public List<SegmentDef> Segments = [];
        public List<AnalysisDef> Analyses = [];
        public Dictionary<(Guid Seg, Guid Analysis), ImpactResult> Impacts = [];
        public Dictionary<Guid, string> LayoutFor = [];
    }

    private static readonly HState Hc = new();

    // Rev 3: these impacts carry intervals but no rings, so no outline is drawn here (D13: the band outline is
    // gone, not kept as a fallback); the ring cases are the outline phase (WritersModelCheckOutline.cs). The
    // hand-built values below are the pre-rev-3 T25/T26 numbers. rev 3: expected values re-derived by rev3-integrator.
    private static ImpactResult Impact(Guid seg, string material, double length, double area, double volumeFt3, params (double, double)[] intervals)
        => new(seg, material, length, area, volumeFt3, intervals, [], false);

    private static (ModelWritePlan Plan, RunDefinition Run, List<Problem> Notes) HcfcdPlan(HState h)
    {
        var built = BoundaryBuilder.Build(AlignmentS, h.Segments, -100, 100, Geom);
        if (built.Boundaries.Count != h.Segments.Count) throw new InvalidOperationException("check defect: HCFCD boundaries did not build.");
        var notes = new List<Problem>();
        var outlines = new List<ImpactOutlineSpec>();
        var tables = new List<HcfcdTableSpec>();
        foreach (var seg in h.Segments)
        {
            var impacts = h.Analyses.Where(a => h.Impacts.ContainsKey((seg.Id, a.Id))).Select(a => (a.Id, h.Impacts[(seg.Id, a.Id)])).ToList();
            outlines.AddRange(ImpactOutlines.Build(seg.Id, impacts));
            var materials = h.Analyses.Where(a => h.Impacts.ContainsKey((seg.Id, a.Id)))
                .Select(a => new HcfcdMaterial(a.Id, a.Name, h.Impacts[(seg.Id, a.Id)], a.CutFactor)).ToList();
            var boundary = built.Boundaries.Single(b => b.SegmentId == seg.Id);
            var placement = h.LayoutFor.TryGetValue(seg.Id, out string? layout) ? TablePlacement.InLayout(layout) : ModelTablePlacement.AtSegment(boundary, Scale, Cfg);
            tables.Add(new HcfcdTableSpec(seg.Id, _template, h.Analyses, materials, AreaUnit.Acres, placement));
        }
        var marks = MatchMarkPlanner.Plan(h.Segments, AlignmentS, _ => Scale, Cfg, Geom.StationTolerance, Report.TableCulture());
        var run = Def(h.Name, h.Segments, h.Analyses, ReportPreset.HcfcdSegment, AreaUnit.Acres);   // rev 3: no OHWM elevation; the analyses' base is the OHWM surface
        return (new ModelWritePlan(built.Boundaries, outlines, marks, [], tables), run, notes);
    }

    private static ApplyOutcome RegenHc() { var (plan, run, _) = HcfcdPlan(Hc); return Regen(run, plan); }

    private static OwnedKey HKey(Guid seg) => new(seg, OwnedRole.ModelTable, OwnedQualifiers.HcfcdTable);

    private static void HcfcdPhase()
    {
        Say("----- phase B: HcfcdSegment preset, segments 0-500 and 500-1000, two materials, hand-built impacts (T25 values, then T26) -----");
        Guid h1 = G(2, 1), h2 = G(2, 2);
        Hc.Segments = [new SegmentDef(h1, "1", 0, 500, -100, 100), new SegmentDef(h2, "2", 500, 1000, -100, 100)];
        AnalysisDef sediment = new(G(2, 101), "SEDIMENT", FixtureSpec.Names.EgH, FixtureSpec.Names.FgH), rock = new(G(2, 102), "ROCK", FixtureSpec.Names.EgH, FixtureSpec.Names.FgH);
        Hc.Analyses = [sediment, rock];
        foreach (var s in Hc.Segments)
        {
            // T25 (OHWM 100): length 500, area 16,000 ft2 = 0.37 ac, volume 26,000 ft3 = 962.963 CY, average 1.93.
            // rev 3: expected values re-derived by rev3-integrator (hand-built values; the writers do not compute them).
            Hc.Impacts[(s.Id, sediment.Id)] = Impact(s.Id, sediment.Name, 500, FixtureSpec.Expected.T25AreaFt2, FixtureSpec.Expected.T25VolumeFt3, (s.StartStation, s.EndStation));
            Hc.Impacts[(s.Id, rock.Id)] = Impact(s.Id, rock.Name, 500, FixtureSpec.Expected.T25AreaFt2, 13500, (s.StartStation, s.EndStation));
        }

        // ---- Create: E1018 one material row per analysis.
        var c1 = RegenHc();
        int templateRows = _template.Definition.Rows.Count;
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var t1 = Open<Table>(tr, c1.Handles[HKey(h1)]);
            for (int r = 0; r < t1.Rows.Count; r++) Say($"HCFCD table 1 r{r}: {RowText(t1, r)}");
            string len = RowText(t1, FindRow(t1, "TOTAL IMPACTED LENGTH")), area = RowText(t1, FindRow(t1, "TOTAL IMPACTED AREA")), sed = RowText(t1, FindRow(t1, "SEDIMENT"));
            Check("E1018", c1.Result.Succeeded && t1.Rows.Count == templateRows + 2 && FindRow(t1, "SEDIMENT") == FindRow(t1, "TOTAL CUT MATERIAL") + 1 && FindRow(t1, "ROCK") == FindRow(t1, "TOTAL CUT MATERIAL") + 2,
                $"created {c1.Kinds}; table 1 has {t1.Rows.Count} rows = {templateRows} template rows + 1 row each for SEDIMENT and ROCK under TOTAL CUT MATERIAL");
            Check("T25", len.EndsWith("| 500 | LF", StringComparison.Ordinal) && area.EndsWith("| 0.37 | AC", StringComparison.Ordinal) && sed == "SEDIMENT | 963 | 1.93",
                $"bound rows: '{len}' ; '{area}' ; '{sed}'");
            int drawn = c1.Result.Changes.Count(c => c.Key.Role == OwnedRole.ImpactOutline);
            Check("D13-noband", drawn == 0 && Hc.Impacts.Values.All(i => i.Intervals.Count > 0 && i.Outlines.Count == 0),
                $"impacts with intervals (whole segment) and empty Outlines: {drawn} impact outline objects planned or drawn (no full-width band fallback)");
            tr.Commit();
        }
        var c2 = RegenHc();
        Check("E811", c2.Result.Writes.Count() == 0 && c2.Events.Appended.Count == 0 && c2.Events.Modified.Count == 1,
            $"HCFCD regen with nothing changed: {c2.Kinds}; {c2.Events.Summary} [{string.Join(", ", c2.Events.Modified)}]");

        // ---- T31 / E1016: type into the centre-coordinates cell, delete WETLANDS, change to T26 values, regen.
        const string typed = "N 13,812,345.67  E 3,101,234.56 (TYPED)";
        UserEdit("table 1: typed into CENTER COORDINATES value cell and deleted the WETLANDS row", tr =>
        {
            var t = (Table)tr.GetObject(WritersModel.Handles.Resolve(_db, c1.Handles[HKey(h1)]), OpenMode.ForWrite);
            int cr = FindRow(t, "CENTER COORDINATES");
            t.Cells[cr, 1].TextString = typed;
            t.DeleteRows(FindRow(t, "WETLANDS"), 1);
        });
        foreach (var s in Hc.Segments)
        {
            // T26 (OHWM 97): area 13,000 ft2 = 0.30 ac, volume 24,500 ft3 = 907.407 CY, average 1.81. Segment 1 now cuts in two stretches.
            // rev 3: expected values re-derived by rev3-integrator (hand-built values; the writers do not compute them).
            var iv = s.Id == h1 ? new[] { (0.0, 300.0), (320.0, 500.0) } : new[] { (s.StartStation, s.EndStation) };
            Hc.Impacts[(s.Id, sediment.Id)] = Impact(s.Id, sediment.Name, s.Id == h1 ? 480 : 500, FixtureSpec.Expected.T26AreaFt2, FixtureSpec.Expected.T26VolumeFt3, iv);
            Hc.Impacts[(s.Id, rock.Id)] = Impact(s.Id, rock.Name, s.Id == h1 ? 480 : 500, FixtureSpec.Expected.T26AreaFt2, 12000, iv);
        }
        var c3 = RegenHc();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var t1 = Open<Table>(tr, c3.Handles[HKey(h1)]);
            var t2 = Open<Table>(tr, c3.Handles[HKey(h2)]);
            for (int r = 0; r < t1.Rows.Count; r++) Say($"HCFCD table 1 after T31 r{r}: {RowText(t1, r)}");
            string len2 = RowText(t2, FindRow(t2, "TOTAL IMPACTED LENGTH")), area2 = RowText(t2, FindRow(t2, "TOTAL IMPACTED AREA")), sed2 = RowText(t2, FindRow(t2, "SEDIMENT"));
            bool typedKept = TableRenderer.Plain(t1, FindRow(t1, "CENTER COORDINATES"), 1) == typed;
            bool deletionKept = FindRow(t1, "WETLANDS") < 0 && t1.Rows.Count == templateRows + 2 - 1;
            bool handles = c3.Handles[HKey(h1)] == c1.Handles[HKey(h1)] && c3.Handles[HKey(h2)] == c1.Handles[HKey(h2)];
            Check("T31", typedKept && deletionKept && handles && sed2 == "SEDIMENT | 907 | 1.81" && area2.EndsWith("| 0.30 | AC", StringComparison.Ordinal) && len2.EndsWith("| 500 | LF", StringComparison.Ordinal),
                $"typed text kept={typedKept}; WETLANDS deletion kept={deletionKept} ({t1.Rows.Count} rows); table handles unchanged={handles}; table 2 rows 6, 7, 9 (T26): '{len2}' ; '{area2}' ; '{sed2}'");
            Check("E1016", typedKept && deletionKept && RowText(t1, FindRow(t1, "SEDIMENT")) == "SEDIMENT | 907 | 1.89"
                           && RowText(t1, FindRow(t1, "TOTAL IMPACTED LENGTH")).EndsWith("| 480 | LF", StringComparison.Ordinal),
                $"table 1 after rebind: typed cell and removed row preserved; its SEDIMENT row (shifted up one) reads '{RowText(t1, FindRow(t1, "SEDIMENT"))}'");
            int drawn3 = c3.Result.Changes.Count(c => c.Key.Role == OwnedRole.ImpactOutline);
            Check("D13-noband", drawn3 == 0,
                $"segment 1 now cuts in two stretches (intervals 0-300, 320-500) with empty Outlines: {drawn3} impact outline objects (pre-rev-3 drew a band per stretch)");
            tr.Commit();
        }

        // ---- E1015: the user renames a bound label; that value is skipped with a message, the rest update.
        UserEdit("table 2: label 'TOTAL IMPACTED AREA (BELOW OHWM)' renamed to 'AREA (USER EDIT)'", tr =>
        {
            var t = (Table)tr.GetObject(WritersModel.Handles.Resolve(_db, c1.Handles[HKey(h2)]), OpenMode.ForWrite);
            t.Cells[FindRow(t, "TOTAL IMPACTED AREA"), 0].TextString = "AREA (USER EDIT)";
        });
        Hc.Impacts[(h2, sediment.Id)] = Hc.Impacts[(h2, sediment.Id)] with { ImpactedLength = 450, ImpactedArea = 26136, CutVolume = 27000 };
        Hc.Impacts[(h2, rock.Id)] = Hc.Impacts[(h2, rock.Id)] with { ImpactedLength = 450, ImpactedArea = 26136 };
        var c4 = RegenHc();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var t2 = Open<Table>(tr, c4.Handles[HKey(h2)]);
            string len = RowText(t2, FindRow(t2, "TOTAL IMPACTED LENGTH")), area = RowText(t2, FindRow(t2, "AREA (USER EDIT)")), sed = RowText(t2, FindRow(t2, "SEDIMENT"));
            var p = c4.Result.Problems.Where(x => x.Code == OutputCodes.BoundLabelMissing && x.SegmentId == h2).ToList();
            Check("E1015", p.Count == 1 && len.EndsWith("| 450 | LF", StringComparison.Ordinal) && sed == "SEDIMENT | 1,000 | 2.22" && area.EndsWith("| 0.30 | AC", StringComparison.Ordinal),
                $"'{len}' and '{sed}' updated; renamed row left as '{area}'; reported: {string.Join(" | ", p.Select(x => $"{x.Code} {x.Severity}: {x.Message}"))}");
            tr.Commit();
        }

        // ---- E1018 (second half): a later analysis gets no row; it is reported, the rest update.
        int rows1;
        using (var tr = _db.TransactionManager.StartTransaction()) { rows1 = Open<Table>(tr, c4.Handles[HKey(h1)]).Rows.Count; tr.Commit(); }
        var gravel = new AnalysisDef(G(2, 103), "GRAVEL", FixtureSpec.Names.EgH, FixtureSpec.Names.FgH);
        Hc.Analyses.Add(gravel);
        foreach (var s in Hc.Segments) Hc.Impacts[(s.Id, gravel.Id)] = Hc.Impacts[(s.Id, sediment.Id)] with { Material = gravel.Name, CutVolume = 2700 };
        var c5 = RegenHc();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var t1 = Open<Table>(tr, c5.Handles[HKey(h1)]);
            var p = c5.Result.Problems.Where(x => x.Code == OutputCodes.BoundLabelMissing && x.Message.Contains("GRAVEL", StringComparison.Ordinal)).ToList();
            Check("E1018", t1.Rows.Count == rows1 && FindRow(t1, "GRAVEL") < 0 && p.Count == 2 && c5.Handles[HKey(h1)] == c1.Handles[HKey(h1)],
                $"GRAVEL added after creation: table 1 still {t1.Rows.Count} rows, no GRAVEL row; reported per segment: {p.FirstOrDefault()?.Code} {p.FirstOrDefault()?.Message}");
            tr.Commit();
        }

        // ---- A new segment whose impact value is invalid fails in the plan, before anything is written.
        Guid h3 = G(2, 3);
        Hc.Segments.Add(new SegmentDef(h3, "3", 1000, 1500, -100, 100));
        foreach (var a in Hc.Analyses) Hc.Impacts[(h3, a.Id)] = Impact(h3, a.Name, 310, 9920, double.NaN, (1000, 1310));
        var bad = RegenHc();
        Check("T20", !bad.Result.Succeeded && bad.Has("SV-E432") && bad.Events.Appended.Count + bad.Events.Modified.Count + bad.Events.Erased.Count == 0,
            $"segment 3's HCFCD table would be created with a NaN cut volume: succeeded={bad.Result.Succeeded}, {bad.Events.Summary}; {Problems(bad, "SV-E432").Split(" | ")[0]}");

        // ---- E1017: a segment's table belongs on a layout that does not exist: reported, not created elsewhere.
        foreach (var a in Hc.Analyses) Hc.Impacts[(h3, a.Id)] = Impact(h3, a.Name, 310, 9920, 16120, (1000, 1310));
        string missingLayout = FixtureSpec.Names.TemplateLayout + CheckSettings.MissingSuffix;
        Hc.LayoutFor[h3] = missingLayout;
        var c6 = RegenHc();
        Check("E1017", c6.Result.Succeeded && !c6.Handles.ContainsKey(HKey(h3)) && c6.Has(ModelWriterCodes.TableLayoutGone) && c6.Handles.ContainsKey(BKey(h3)),
            $"segment 3 targets layout '{missingLayout}': no table created anywhere, boundary still drawn ({c6.Handles[BKey(h3)]}); {Problems(c6, ModelWriterCodes.TableLayoutGone)}");
        var c7 = RegenHc();
        Check("E811", c7.Result.Writes.Count() == 0, $"HCFCD run settles: {c7.Kinds}; warnings repeat each regen: {c7.Result.Problems.Count(p => p.Severity == Severity.Warning)}");
    }
}

// A2 headless check, outline phase (rev 3, D13): the impact outline writer draws each ring of
// ImpactResult.Outlines as a closed polyline, one owned object per (segment, analysis, ring).
// The rings are hand-made here (one rectangle, two rings, an outer ring with a hole); the
// integrator that fills Outlines is rev3-integrator's. Proves create, unchanged regen, update
// (ring changed), erase-and-recreate (E802), a ring dropped, an empty list erasing what the
// segment owned (E308), and an unclosed ring refused before any write (SV-E541).

using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Civil3D.Ownership;
using SheetVol.Civil3D.SelfTest;
using SheetVol.Core;
using SheetVol.Core.Tracking;

namespace SheetVol.Civil3D.WritersModel.Check;

public static partial class WritersModelCheck
{
    private sealed class OState
    {
        public string Name = "WM-OUTLINE";
        public List<SegmentDef> Segments = [];
        public List<AnalysisDef> Analyses = [];
        public Dictionary<(Guid Seg, Guid Analysis), ImpactResult> Impacts = [];
    }

    private static readonly OState Ol = new();

    /// <summary>Closed axis-aligned ring (first == last), counter-clockwise unless <paramref name="clockwise"/>.</summary>
    private static IReadOnlyList<Pt> RectRing(double x0, double y0, double x1, double y1, bool clockwise = false)
    {
        var ccw = new List<Pt> { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1), new(x0, y0) };
        if (clockwise) ccw.Reverse();
        return ccw;
    }

    private static ImpactResult RingImpact(Guid seg, string material, params IReadOnlyList<Pt>[] rings)
        => new(seg, material, 0, 0, 0, [], rings, false);   // the writer reads only the rings

    private static (ModelWritePlan Plan, RunDefinition Run) OutlinePlan(OState o)
    {
        var specs = new List<ImpactOutlineSpec>();
        foreach (var seg in o.Segments)
            specs.AddRange(ImpactOutlines.Build(seg.Id, o.Analyses.Where(a => o.Impacts.ContainsKey((seg.Id, a.Id))).Select(a => (a.Id, o.Impacts[(seg.Id, a.Id)]))));
        var run = Def(o.Name, o.Segments, o.Analyses, ReportPreset.HcfcdSegment, AreaUnit.Acres);
        return (new ModelWritePlan([], specs, [], [], []), run);
    }

    private static ApplyOutcome RegenOl() { var (plan, run) = OutlinePlan(Ol); return Regen(run, plan); }

    private static OwnedKey OKey(Guid seg, Guid analysis, int ring) => new(seg, OwnedRole.ImpactOutline, OwnedQualifiers.Outline(analysis, ring));

    /// <summary>True when the live polyline is the ring as given: closed, the ring's points without the closing repeat, bulges 0, on the configured layer, tagged with the key.</summary>
    private static bool IsRing(Transaction tr, string handle, OwnedKey key, IReadOnlyList<Pt> ring, out string text)
    {
        var pl = Open<Polyline>(tr, handle);
        var pts = Enumerable.Range(0, pl.NumberOfVertices).Select(i => pl.GetPoint2dAt(i)).ToList();
        var tag = OwnerTag.Read(tr, pl, Cfg.OwnerTagKey);
        text = $"{handle} {pl.NumberOfVertices} vertices [{string.Join(" ", pts.Select(p => $"({F(p.X)},{F(p.Y)})"))}] closed={pl.Closed} layer={pl.Layer} area={F(pl.Area)} tag={tag?.Qualifier}";
        return pl.Closed && pl.Layer == Cfg.ImpactOutlineLayer && tag is not null && tag.Key == key
               && pl.NumberOfVertices == ring.Count - 1
               && Enumerable.Range(0, pl.NumberOfVertices).All(i => pts[i].X == ring[i].X && pts[i].Y == ring[i].Y && pl.GetBulgeAt(i) == 0);
    }

    private static string Q(OwnedKey k) => k.Qualifier;

    private static void OutlinePhase()
    {
        Say("----- outline phase (rev 3, D13): rings of ImpactResult.Outlines drawn as-is, key (segment, analysis, ring) -----");
        Guid q1 = G(4, 1), q2 = G(4, 2);
        Ol.Segments = [new SegmentDef(q1, "1", 0, 500, -100, 100), new SegmentDef(q2, "2", 500, 1000, -100, 100)];
        AnalysisDef sed = new(G(4, 101), "SEDIMENT", FixtureSpec.Names.EgH, FixtureSpec.Names.FgH), rock = new(G(4, 102), "ROCK", FixtureSpec.Names.EgH, FixtureSpec.Names.FgH);
        Ol.Analyses = [sed, rock];

        var rect = RectRing(100, -40, 300, 40);                                       // one rectangle ring
        var two0 = RectRing(50, -60, 150, -10);                                       // two rings
        var two1 = RectRing(250, 10, 450, 60);
        var outer = RectRing(550, -80, 950, 80);                                      // outer ring plus a hole (clockwise)
        var hole = RectRing(650, -20, 850, 20, clockwise: true);
        Ol.Impacts[(q1, sed.Id)] = RingImpact(q1, sed.Name, rect);
        Ol.Impacts[(q1, rock.Id)] = RingImpact(q1, rock.Name, two0, two1);
        Ol.Impacts[(q2, sed.Id)] = RingImpact(q2, sed.Name, outer, hole);
        Ol.Impacts[(q2, rock.Id)] = RingImpact(q2, rock.Name);                       // nothing impacted: no rings

        OwnedKey kRect = OKey(q1, sed.Id, 0), kTwo0 = OKey(q1, rock.Id, 0), kTwo1 = OKey(q1, rock.Id, 1), kOuter = OKey(q2, sed.Id, 0), kHole = OKey(q2, sed.Id, 1);
        var all = new[] { kRect, kTwo0, kTwo1, kOuter, kHole };
        Say($"outline key format: (segment, {OwnedRole.ImpactOutline}, \"{{analysis GUID D}}/{{ring}}\"), e.g. {Q(kRect)}");

        // ---- Create.
        var c1 = RegenOl();
        int created = c1.Count(ChangeKind.Create, OwnedRole.ImpactOutline);
        Say($"create: {c1.Kinds}; outlines created {created}; segment 2 ROCK has an empty Outlines list");
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            bool r1 = IsRing(tr, c1.Handles[kRect], kRect, rect, out string t1);
            Check("D13-rect", c1.Result.Succeeded && created == 5 && r1, $"one rectangle ring -> one closed polyline: {Q(kRect)} {t1}");
            bool a = IsRing(tr, c1.Handles[kTwo0], kTwo0, two0, out string ta), b = IsRing(tr, c1.Handles[kTwo1], kTwo1, two1, out string tb);
            Check("D13-tworings", a && b && c1.Handles[kTwo0] != c1.Handles[kTwo1], $"two rings -> two polylines: {Q(kTwo0)} {ta} ; {Q(kTwo1)} {tb}");
            bool o = IsRing(tr, c1.Handles[kOuter], kOuter, outer, out string to), h = IsRing(tr, c1.Handles[kHole], kHole, hole, out string th);
            double net = Open<Polyline>(tr, c1.Handles[kOuter]).Area - Open<Polyline>(tr, c1.Handles[kHole]).Area;
            Check("D13-hole", o && h && !c1.Handles.Keys.Any(k => k.Role == OwnedRole.ImpactOutline && k.SegmentId == q2 && k.Qualifier.StartsWith(rock.Id.ToString("D"), StringComparison.Ordinal)),
                $"outer ring plus a hole -> two polylines, hole drawn as given (clockwise): {Q(kOuter)} {to} ; {Q(kHole)} {th}; outer - hole = {F(net)}; segment 2 ROCK (no rings) draws nothing");
            tr.Commit();
        }

        // ---- Unchanged.
        var c2 = RegenOl();
        Check("E811-outline", c2.Result.Writes.Count() == 0 && c2.Count(ChangeKind.Unchanged, OwnedRole.ImpactOutline) == 5 && c2.Events.Appended.Count == 0 && c2.Events.Erased.Count == 0,
            $"second regen with the same rings: {c2.Kinds}; {c2.Events.Summary} [{string.Join(", ", c2.Events.Modified)}]");

        // ---- Update: the rectangle becomes an L (6 vertices); updated in place, the others untouched.
        var ell = new List<Pt> { new(100, -40), new(350, -40), new(350, 40), new(200, 40), new(200, 0), new(100, 0), new(100, -40) };
        Ol.Impacts[(q1, sed.Id)] = RingImpact(q1, sed.Name, ell);
        var c3 = RegenOl();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var kind = c3.Result.Changes.Single(c => c.Key == kRect).Kind;
            bool r = IsRing(tr, c3.Handles[kRect], kRect, ell, out string t);
            Check("D13-update", r && kind == ChangeKind.Update && c3.Handles[kRect] == c1.Handles[kRect]
                                && c3.Count(ChangeKind.Update, OwnedRole.ImpactOutline) == 1 && c3.Count(ChangeKind.Unchanged, OwnedRole.ImpactOutline) == 4,
                $"ring changed (rectangle -> L, 4 -> 6 vertices): {Q(kRect)} {kind} in place, same handle; {t}; {c3.Kinds}");
            tr.Commit();
        }

        // ---- Erase and recreate: the user erases the hole polyline.
        string holeHandle = c1.Handles[kHole];
        UserEdit($"erased the hole polyline {holeHandle}", tr => ((Polyline)tr.GetObject(WritersModel.Handles.Resolve(_db, holeHandle), OpenMode.ForWrite)).Erase());
        var c4 = RegenOl();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var kind = c4.Result.Changes.Single(c => c.Key == kHole).Kind;
            bool r = IsRing(tr, c4.Handles[kHole], kHole, hole, out string t);
            Check("E802-outline", r && kind == ChangeKind.Create && c4.Handles[kHole] != holeHandle && !Alive(holeHandle) && c4.Has(ModelWriterCodes.ObjectRecreated)
                                  && c4.Count(ChangeKind.Unchanged, OwnedRole.ImpactOutline) == 4,
                $"{Q(kHole)} recreated: {holeHandle} -> {t}; {Problems(c4, ModelWriterCodes.ObjectRecreated)}; {c4.Kinds}");
            tr.Commit();
        }

        // ---- A ring dropped: segment 1 ROCK now has one ring; ring 1 is erased, ring 0 untouched,
        // and the recreated hole is Unchanged at its repaired handle.
        Ol.Impacts[(q1, rock.Id)] = RingImpact(q1, rock.Name, two0);
        var c5 = RegenOl();
        var k5 = c5.Result.Changes.Where(c => c.Key.Role == OwnedRole.ImpactOutline).ToDictionary(c => c.Key, c => c.Kind);
        Check("D13-ringgone", k5[kTwo1] == ChangeKind.Erase && !Alive(c1.Handles[kTwo1]) && k5[kTwo0] == ChangeKind.Unchanged
                              && k5[kHole] == ChangeKind.Unchanged && c5.Handles[kHole] == c4.Handles[kHole] && c5.Has(ModelWriterCodes.SegmentRemovedErased),
            $"ROCK ring count 2 -> 1 on segment 1: {Q(kTwo1)} {k5[kTwo1]} ({c1.Handles[kTwo1]} gone), {Q(kTwo0)} {k5[kTwo0]}, hole {k5[kHole]} at repaired handle {c5.Handles[kHole]}; {Problems(c5, ModelWriterCodes.SegmentRemovedErased)}");

        // ---- Empty list: segment 2 SEDIMENT now impacts nothing; both its rings are erased as owned objects.
        Ol.Impacts[(q2, sed.Id)] = RingImpact(q2, sed.Name);
        var c6 = RegenOl();
        var k6 = c6.Result.Changes.Where(c => c.Key.Role == OwnedRole.ImpactOutline).ToDictionary(c => c.Key, c => c.Kind);
        int ownedOnQ2 = c6.Result.Objects.Count(x => x.Role == OwnedRole.ImpactOutline && x.SegmentId == q2);
        Check("D13-empty", k6[kOuter] == ChangeKind.Erase && k6[kHole] == ChangeKind.Erase && !Alive(c1.Handles[kOuter]) && !Alive(c4.Handles[kHole]) && ownedOnQ2 == 0
                           && k6[kRect] == ChangeKind.Unchanged && k6[kTwo0] == ChangeKind.Unchanged && c6.Result.Problems.Count(p => p.Code == ModelWriterCodes.SegmentRemovedErased) == 2,
            $"segment 2 Outlines empty: {Q(kOuter)} {k6[kOuter]}, {Q(kHole)} {k6[kHole]}; segment 2 owns {ownedOnQ2} outlines in the record; segment 1 {k6[kRect]}/{k6[kTwo0]}; {c6.Kinds}");

        // ---- An unclosed ring is refused in the plan, before anything is written.
        var open = ell.Take(ell.Count - 1).ToList();
        Ol.Impacts[(q1, sed.Id)] = RingImpact(q1, sed.Name, open);
        var bad = RegenOl();
        Check("E541-outline", !bad.Result.Succeeded && bad.Has(ModelWriterCodes.InvalidInput) && bad.Events.Appended.Count + bad.Events.Modified.Count + bad.Events.Erased.Count == 0,
            $"ring with first != last: succeeded={bad.Result.Succeeded}, {bad.Events.Summary}; {Problems(bad, ModelWriterCodes.InvalidInput)}");

        // ---- Everything empty: the run's remaining outlines are erased; the record keeps none.
        foreach (var k in Ol.Impacts.Keys.ToList()) Ol.Impacts[k] = RingImpact(k.Seg, Ol.Analyses.Single(a => a.Id == k.Analysis).Name);
        var c7 = RegenOl();
        int left = c7.Result.Objects.Count(x => x.Role == OwnedRole.ImpactOutline);
        Check("D13-empty", c7.Result.Succeeded && c7.Count(ChangeKind.Erase, OwnedRole.ImpactOutline) == 2 && left == 0 && !Alive(c1.Handles[kRect]) && !Alive(c1.Handles[kTwo0]),
            $"every Outlines list empty: {c7.Kinds}; record holds {left} outlines; of {all.Length} keys created none is alive");
    }
}

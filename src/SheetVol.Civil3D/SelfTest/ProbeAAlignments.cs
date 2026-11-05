// S1 probes: P7 (alignment sub-entity access, Clockwise, analytic offsets against
// PointLocation) and P8 (station equations).

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using static SheetVol.Civil3D.SelfTest.ProbeASettings;
using N = SheetVol.Civil3D.SelfTest.FixtureSpec.Names;

namespace SheetVol.Civil3D.SelfTest;

public static partial class ProbeACommands
{
    /// <summary>One sub-entity as the analytic model sees it, built from API properties only.</summary>
    private sealed record Seg(string Kind, double S0, double S1, Point2d A, Point2d B,
        Point2d Center = default, double Radius = 0, bool Clockwise = false,
        double SpiralA = 0, bool SpiralIn = false);

    [CommandMethod("SVPROBEA_P7", CommandFlags.Modal)]
    public static void P7() => Run("SVPROBEA_P7", (doc, db) =>
    {
        Try("build compound-curve alignment " + CompoundAlignment, () => BuildCompound(db));
        using var tr = db.TransactionManager.StartTransaction();
        var names = new List<string> { N.AlignmentS, N.AlignmentC, N.AlignmentK, N.AlignmentSP };
        foreach (ObjectId id in Civil(db).GetAlignmentIds())
            if (tr.GetObject(id, OpenMode.ForRead) is Alignment a && a.Name == CompoundAlignment) names.Add(a.Name);

        foreach (var name in names)
        {
            var al = Align(tr, db, name);
            Say($"--- {al.Name}: stations {R(al.StartingStation)}..{R(al.EndingStation)}, Entities.Count={al.Entities.Count}");
            var segs = new List<Seg>();
            for (int i = 0; i < al.Entities.Count; i++)
            {
                var e = al.Entities.GetEntityByOrder(i);
                string Nb(Func<int> f) { try { return f().ToString(); } catch (System.Exception ex) { return "threw " + ex.GetType().Name; } }
                Say($"  entity {i}: {e.EntityType} EntityId={e.EntityId} SubEntityCount={e.SubEntityCount} EntityBefore={Nb(() => e.EntityBefore)} EntityAfter={Nb(() => e.EntityAfter)} constraint={e.Constraint1}");
                for (int j = 0; j < e.SubEntityCount; j++)
                {
                    var sub = e[j];
                    string head = $"    [{j}] {sub.SubEntityType} {R(sub.StartStation)}..{R(sub.EndStation)} L={R(sub.Length)} {P(sub.StartPoint)}->{P(sub.EndPoint)} group='{S(() => sub.CurveGroupIndex)}'/'{S(() => sub.CurveGroupSubEntityIndex)}'";
                    switch (sub)
                    {
                        case AlignmentSubEntityLine ln:
                        {
                            var d = sub.EndPoint - sub.StartPoint;
                            Say($"{head} Direction={R(ln.Direction)} (atan2 of the chord = {R(Math.Atan2(d.Y, d.X))}, azimuth from north = {R(Math.PI / 2 - Math.Atan2(d.Y, d.X))})");
                            segs.Add(new Seg("Line", sub.StartStation, sub.EndStation, sub.StartPoint, sub.EndPoint));
                            break;
                        }
                        case AlignmentSubEntityArc arc:
                        {
                            var v0 = sub.StartPoint - arc.CenterPoint;
                            var v1 = sub.EndPoint - arc.CenterPoint;
                            double cross = v0.X * v1.Y - v0.Y * v1.X;
                            Say($"{head} R={R(arc.Radius)} Center={P(arc.CenterPoint)} Clockwise={arc.Clockwise} Delta={S(() => arc.Delta)} StartDirection={S(() => arc.StartDirection)} EndDirection={S(() => arc.EndDirection)} Reverse={S(() => arc.ReverseCurve)} >180={S(() => arc.GreaterThan180)}; geometric sweep (start->end about centre) is {(cross < 0 ? "CLOCKWISE (turns right)" : "COUNTER-CLOCKWISE (turns left)")}");
                            segs.Add(new Seg("Arc", sub.StartStation, sub.EndStation, sub.StartPoint, sub.EndPoint, arc.CenterPoint, arc.Radius, arc.Clockwise));
                            break;
                        }
                        case AlignmentSubEntitySpiral sp:
                        {
                            bool isIn = segs.Count > 0 && segs[^1].Kind == "Line";   // spiral in follows a tangent
                            Say($"{head} A={S(() => sp.A)} RadiusIn={S(() => sp.RadiusIn)} RadiusOut={S(() => sp.RadiusOut)} Direction={S(() => sp.Direction)} Definition={S(() => sp.SpiralDefinition)} CurveType={S(() => sp.CurveType)} StartDirection={S(() => sp.StartDirection)} EndDirection={S(() => sp.EndDirection)} Delta={S(() => sp.Delta)} P={S(() => sp.P)} K={S(() => sp.K)} TotalX={S(() => sp.TotalX)} TotalY={S(() => sp.TotalY)} Compound={S(() => sp.Compound)}");
                            segs.Add(new Seg("Spiral", sub.StartStation, sub.EndStation, sub.StartPoint, sub.EndPoint, SpiralA: sp.A, SpiralIn: isIn));
                            break;
                        }
                        default:
                            Say(head);
                            break;
                    }
                }
                Try($"  entity {i} indexer [{e.SubEntityCount}] (one past the end)", () => Say($"  entity {i}[{e.SubEntityCount}] returned {e[e.SubEntityCount]?.GetType().Name ?? "null"}"));
                if (e is AlignmentSCS scs)
                    Say($"  entity {i} as AlignmentSCS: SpiralIn {R(scs.SpiralIn.StartStation)}..{R(scs.SpiralIn.EndStation)}, Arc R={R(scs.Arc.Radius)} Clockwise={scs.Arc.Clockwise}, SpiralOut {R(scs.SpiralOut.StartStation)}..{R(scs.SpiralOut.EndStation)}");
            }
            Try($"  GetEntityByOrder({al.Entities.Count}) (one past the end)", () => Say($"  GetEntityByOrder({al.Entities.Count}) returned {al.Entities.GetEntityByOrder(al.Entities.Count)?.EntityType.ToString() ?? "null"}"));

            // Analytic offsets vs PointLocation at P7Stations stations, offsets -100, 0, +100,
            // under both readings of Clockwise.
            foreach (bool clockwiseMeansRight in new[] { true, false })
            {
                double max = 0; string worst = "";
                int n = P7Stations;
                for (int k = 0; k < n; k++)
                {
                    double s = al.StartingStation + (al.EndingStation - al.StartingStation) * k / (n - 1);
                    foreach (var off in new[] { OffsetLeft, 0.0, OffsetRight })
                    {
                        var (ax, ay) = Analytic(segs, s, off, clockwiseMeansRight);
                        var (px, py) = At(al, s, off);
                        double d = Math.Sqrt((ax - px) * (ax - px) + (ay - py) * (ay - py));
                        if (d > max) { max = d; worst = $"station {R(s)} offset {F(off)}: analytic ({F(ax)}, {F(ay)}) PointLocation ({F(px)}, {F(py)})"; }
                    }
                }
                Say($"  P7 {al.Name} reading 'Clockwise=true turns right': {clockwiseMeansRight}: max |analytic - PointLocation| over {n} stations x 3 offsets = {E(max)} ({(max <= P7Tolerance ? "within" : "OUTSIDE")} {E(P7Tolerance)}){(max > 0 ? "; worst at " + worst : "")}");
            }
        }
        tr.Commit();
    });

    /// <summary>Centreline + offset from the sub-entity model. Offset sign per the contracts:
    /// negative = left. Left normal of direction θ is (−sin θ, cos θ), so P = C + o·(sin θ, −cos θ).</summary>
    private static (double X, double Y) Analytic(List<Seg> segs, double s, double o, bool clockwiseMeansRight)
    {
        int i = segs.FindIndex(g => s <= g.S1 + 1e-9);
        if (i < 0) i = segs.Count - 1;
        var g = segs[i];
        double cx, cy, th;
        switch (g.Kind)
        {
            case "Line":
            {
                double len = (g.B - g.A).Length, ux = (g.B.X - g.A.X) / len, uy = (g.B.Y - g.A.Y) / len;
                cx = g.A.X + (s - g.S0) * ux; cy = g.A.Y + (s - g.S0) * uy; th = Math.Atan2(uy, ux);
                break;
            }
            case "Arc":
            {
                double dir = (g.Clockwise == clockwiseMeansRight) ? -1.0 : 1.0;   // -1: sweeps clockwise
                double phi0 = Math.Atan2(g.A.Y - g.Center.Y, g.A.X - g.Center.X);
                double phi = phi0 + dir * (s - g.S0) / g.Radius;
                cx = g.Center.X + g.Radius * Math.Cos(phi); cy = g.Center.Y + g.Radius * Math.Sin(phi);
                th = phi + dir * Math.PI / 2;
                break;
            }
            default: // Spiral: clothoid from its infinite-radius end, oriented by the neighbouring line.
            {
                bool isIn = g.SpiralIn;
                var line = isIn ? segs[i - 1] : segs[i + 1];
                double t0 = Math.Atan2(line.B.Y - line.A.Y, line.B.X - line.A.X);
                // Turn sign from the curve's arc neighbour: left turn = +1.
                var arc = isIn ? segs[i + 1] : segs[i - 1];
                double sigma = (arc.Clockwise == clockwiseMeansRight) ? -1.0 : 1.0;
                double a2 = g.SpiralA * g.SpiralA;
                if (isIn)
                {
                    double l = s - g.S0;
                    var (x, y) = Clothoid(l, a2);
                    cx = g.A.X + x * Math.Cos(t0) - sigma * y * Math.Sin(t0);
                    cy = g.A.Y + x * Math.Sin(t0) + sigma * y * Math.Cos(t0);
                    th = t0 + sigma * l * l / (2 * a2);
                }
                else
                {
                    double l = g.S1 - s;
                    var (x, y) = Clothoid(l, a2);
                    double tb = t0 + Math.PI;            // travelling backwards from the ST
                    double sb = -sigma;
                    cx = g.B.X + x * Math.Cos(tb) - sb * y * Math.Sin(tb);
                    cy = g.B.Y + x * Math.Sin(tb) + sb * y * Math.Cos(tb);
                    th = t0 - sigma * l * l / (2 * a2);
                }
                break;
            }
        }
        return (cx + o * Math.Sin(th), cy - o * Math.Cos(th));
    }

    /// <summary>Clothoid local coordinates by Simpson integration of (cos, sin)(t²/2A²).</summary>
    private static (double X, double Y) Clothoid(double l, double a2)
    {
        if (l <= 0) return (0, 0);
        const int n = 2000;
        double h = l / n, x = 0, y = 0;
        for (int k = 0; k <= n; k++)
        {
            double t = k * h, w = (k == 0 || k == n) ? 1 : (k % 2 == 1 ? 4 : 2), ang = t * t / (2 * a2);
            x += w * Math.Cos(ang); y += w * Math.Sin(ang);
        }
        return (x * h / 3, y * h / 3);
    }

    /// <summary>A line, two fixed arcs turning right (R 300 then R 600, a compound curve) and a
    /// line, each attached to the previous entity, to see whether Civil 3D groups them.</summary>
    private static void BuildCompound(Database db)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var civ = Civil(db);
        foreach (ObjectId id in civ.GetAlignmentIds())
            if (tr.GetObject(id, OpenMode.ForRead) is Alignment ex && ex.Name == CompoundAlignment) { Say($"{CompoundAlignment} exists"); return; }
        var styles = civ.Styles.AlignmentStyles;
        var labels = civ.Styles.LabelSetStyles.AlignmentLabelSetStyles;
        var alId = Alignment.Create(civ, CompoundAlignment, ObjectId.Null, db.LayerZero, styles[0], labels[0]);
        var al = (Alignment)tr.GetObject(alId, OpenMode.ForWrite);
        static Point3d Pol(Point2d c, double r, double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180), 0);
        var l1 = al.Entities.AddFixedLine(new Point3d(0, 0, 0), new Point3d(300, 0, 0));
        var c1 = new Point2d(300, -300);
        var a1 = al.Entities.AddFixedCurve(l1.EntityId, Pol(c1, 300, 90), Pol(c1, 300, 75), Pol(c1, 300, 60));
        var e1 = Pol(c1, 300, 60);
        var c2 = new Point2d(e1.X - 600 * Math.Cos(Math.PI / 3), e1.Y - 600 * Math.Sin(Math.PI / 3));
        var a2 = al.Entities.AddFixedCurve(a1.EntityId, Pol(c2, 600, 60), Pol(c2, 600, 50), Pol(c2, 600, 40));
        var e2 = Pol(c2, 600, 40);
        double th = (40 - 90) * Math.PI / 180;
        al.Entities.AddFixedLine(a2.EntityId, e2, new Point3d(e2.X + 200 * Math.Cos(th), e2.Y + 200 * Math.Sin(th), 0));
        Say($"{CompoundAlignment}: built line + arc R300 (CW 30 deg) + arc R600 (CW 20 deg) + line; Entities.Count={al.Entities.Count}, stations {R(al.StartingStation)}..{R(al.EndingStation)}");
        tr.Commit();
    }

    [CommandMethod("SVPROBEA_P8", CommandFlags.Modal)]
    public static void P8() => Run("SVPROBEA_P8", (doc, db) =>
    {
        double[] raws = { 0, 500, 900, 999.99, 1000, 1000.01, 1100, 1200, 1499.99, 1500, 1700, 2000 };
        void Show(Alignment al, string tag)
        {
            Say($"[{tag}] StationEquations.Count={al.StationEquations.Count} EndingStation={R(al.EndingStation)} EndingStationWithEquations={R(al.EndingStationWithEquations)}");
            for (int i = 0; i < al.StationEquations.Count; i++)
            {
                var q = al.StationEquations[i];
                Say($"[{tag}]   equation {i}: RawStationBack={R(q.RawStationBack)} StationBack={R(q.StationBack)} StationAhead={R(q.StationAhead)} EquationType={q.EquationType}");
            }
            foreach (var r in raws)
                Try($"[{tag}]   GetStationStringWithEquations({R(r)})", () => Say($"[{tag}]   raw {R(r),8} -> '{al.GetStationStringWithEquations(r)}'"));
            Try($"[{tag}]   GetStationSet(Equation)", () =>
                Say($"[{tag}]   GetStationSet(Equation): {string.Join("; ", al.GetStationSet(StationTypes.Equation).Select(st => $"raw {R(st.RawStation)} at {P(st.Location)} type {st.StationType}"))}"));
        }

        using var tr = db.TransactionManager.StartTransaction();
        var s = Align(tr, db, N.AlignmentS, OpenMode.ForWrite);
        Show(s, "no equations");
        Try("GetStationStringWithEquations(-10) (before start)", () => Say($"raw -10 -> '{s.GetStationStringWithEquations(-10)}'"));
        Try("GetStationStringWithEquations(2500) (past end)", () => Say($"raw 2500 -> '{s.GetStationStringWithEquations(2500)}'"));

        Try($"StationEquations.Add({R(EqRawBack)}, {R(EqAhead)}, Increasing)", () => s.StationEquations.Add(EqRawBack, EqAhead, StationEquationType.Increasing));
        Show(s, "one equation, back 1000 = ahead 800");
        var (x, y) = At(s, 1100, 0);
        Say($"PointLocation(1100, 0) with the equation = ({F(x)}, {F(y)}): PointLocation takes RAW stations");

        Try($"StationEquations.Add({R(EqRawBack2)}, {R(EqAhead2)}, Increasing)", () => s.StationEquations.Add(EqRawBack2, EqAhead2, StationEquationType.Increasing));
        Show(s, "two equations, + back 1500 = ahead 2000");

        // Displayed -> raw: no managed API on Alignment (no GetRawStation / parse method in
        // AeccDbMgd 13.8). Computed here from the equation table; every region whose displayed
        // range holds the value is a candidate.
        var regions = new List<(double RawFrom, double RawTo, double DispFrom, double Dir)>();
        double rawFrom = s.StartingStation, dispFrom = s.StartingStation, dir = 1;
        var eqs = Enumerable.Range(0, s.StationEquations.Count).Select(i => s.StationEquations[i]).OrderBy(q => q.RawStationBack).ToList();
        foreach (var q in eqs)
        {
            regions.Add((rawFrom, q.RawStationBack, dispFrom, dir));
            rawFrom = q.RawStationBack; dispFrom = q.StationAhead; dir = q.EquationType == StationEquationType.Increasing ? 1 : -1;
        }
        regions.Add((rawFrom, s.EndingStation, dispFrom, dir));
        Say("regions (raw from..to -> displayed from, direction): " + string.Join("; ", regions.Select(g => $"{R(g.RawFrom)}..{R(g.RawTo)} -> {R(g.DispFrom)} {(g.Dir > 0 ? "+" : "-")}")));
        foreach (var d in new[] { 900.0, 1250.0, 1750.0, 2200.0 })
        {
            var cands = new List<double>();
            foreach (var g in regions)
            {
                double raw = g.RawFrom + (d - g.DispFrom) * g.Dir;
                if (raw >= g.RawFrom - 1e-9 && raw <= g.RawTo + 1e-9) cands.Add(raw);
            }
            string strs = string.Join(", ", cands.Select(c => $"raw {R(c)} formats as '{s.GetStationStringWithEquations(c)}'"));
            Say($"displayed {R(d)}: {cands.Count} raw candidate(s){(cands.Count == 0 ? " (falls in a station gap)" : ": " + strs)}");
        }

        while (s.StationEquations.Count > 0) s.StationEquations.Remove(0);
        Try($"StationEquations.Add({R(EqRawBack)}, {R(EqAhead)}, Decreasing)", () => s.StationEquations.Add(EqRawBack, EqAhead, StationEquationType.Decreasing));
        Show(s, "one Decreasing equation, back 1000 = ahead 800");

        while (s.StationEquations.Count > 0) s.StationEquations.Remove(0);
        Say($"equations removed: Count={s.StationEquations.Count}, raw 1100 -> '{s.GetStationStringWithEquations(1100)}'");
        tr.Commit();
    });
}

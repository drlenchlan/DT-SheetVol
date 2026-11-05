// A1 adapters. Owner: A1.

using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Geometry;
using SheetVol.Core.Volumes;

namespace SheetVol.Civil3D.Adapters;

/// <summary>
/// What the construction cross-check ran and found (P7, E204).
/// </summary>
/// <param name="Method">Which check ran: the vertices of C1's <c>BoundaryBuilder</c> band over the whole alignment, or the direct closed-form check when that band could not be built (reason given).</param>
/// <param name="Offset">Offset used on each side, drawing units.</param>
/// <param name="AnalyticPoints">Offset points computed in closed form (lines and arcs) and compared with <c>PointLocation</c>.</param>
/// <param name="PointAtPoints">Offset points on spirals, which C1 takes from <see cref="IAlignmentGeometry.PointAt"/> itself; compared too, so they read 0.</param>
/// <param name="JunctionPointsSkipped">Vertices at kinked junctions: mitres at tangent-tangent angle points (off the perpendicular offset by design) and near-tangent kinks where C1 and PointLocation may evaluate different elements; not compared.</param>
/// <param name="MaxDeviation">Largest distance found, drawing units.</param>
/// <param name="Worst">Where the largest distance was found.</param>
/// <param name="MaxDirectionError">Largest difference, radians, between a converted azimuth and the direction of the element's own geometry.</param>
public sealed record CrossCheckReport(
    string Method, double Offset, int AnalyticPoints, int PointAtPoints, int JunctionPointsSkipped,
    double MaxDeviation, string Worst, double MaxDirectionError);

/// <summary>
/// <see cref="IAlignmentGeometry"/> over a Civil 3D alignment (HANDOFF.md 6.2, section 10 A1).
/// <para><b>Segments</b> come from the sub-entities in path order (P7):
/// <c>Entities.GetEntityByOrder(i)</c> then <c>entity[j]</c>. Lines, arcs and spirals map to
/// <see cref="AlignSeg"/>; directions are converted from Civil's azimuths (clockwise from
/// north) to radians counter-clockwise from +X (<c>pi/2 - azimuth</c>); <c>Clockwise = true</c>
/// is a right turn and is taken as is (P7). Only ordered, contiguous, connected entities are
/// accepted: a gap, an unconnected entity or an unsolved element is SV-E214 with the station.</para>
/// <para><b>PointAt</b> is <c>Alignment.PointLocation</c> (raw stations, negative offset = left,
/// P7, P8). <b>FormatStation</b> is <c>GetStationStringWithEquations</c>; <b>TryParseStation</b>
/// inverts it from the equation table (<see cref="StationTable"/>, E208).</para>
/// <para><b>Cross-check at construction</b> (P7, E204): every offset point C1's
/// <c>BoundaryBuilder</c> computes in closed form over the whole alignment is compared with
/// <c>PointLocation</c>; any distance above <see cref="AdapterConfig.CrossCheckTolerance"/>
/// fails construction with SV-E204, because a wrong direction or turn sense here would put every
/// boundary in the wrong place.</para>
/// <para><b>Lifetime:</b> the adapter holds the alignment opened in the caller's transaction;
/// <see cref="PointAt"/> and <see cref="FormatStation"/> are valid only while that transaction is
/// open (6.10 steps 1 to 4 run inside one).</para>
/// </summary>
public sealed class AlignmentAdapter : IAlignmentGeometry
{
    private readonly Alignment _al;
    private readonly AlignSeg[] _segs;
    private readonly double[] _starts;
    private readonly double _tol;

    private AlignmentAdapter(Alignment al, AlignSeg[] segs, StationTable table, double tol)
    {
        _al = al;
        _segs = segs;
        _starts = segs.Select(s => s.StartStation).ToArray();
        _tol = tol;
        Stations = table;
        Id = al.ObjectId;
        Handle = al.Handle.ToString();
        Name = al.Name;
        StartStation = al.StartingStation;
        EndStation = al.EndingStation;
        CrossCheck = new CrossCheckReport("not run", 0, 0, 0, 0, 0, "", 0);
    }

    /// <summary>The alignment's object id.</summary>
    public ObjectId Id { get; }

    /// <summary>The alignment's handle, hexadecimal, for persistence (6.9).</summary>
    public string Handle { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public double StartStation { get; }

    /// <inheritdoc />
    public double EndStation { get; }

    /// <inheritdoc />
    public IReadOnlyList<AlignSeg> Segments => _segs;

    /// <summary>The construction cross-check's result.</summary>
    public CrossCheckReport CrossCheck { get; private set; }

    /// <summary>The station equation table used by <see cref="TryParseStation"/>.</summary>
    internal StationTable Stations { get; }

    /// <summary>
    /// Finds the alignment by name, then by its stored handle when that handle was stored for the
    /// same name (renamed: SV-E445 warning), and opens it. Neither found is SV-E213.
    /// </summary>
    /// <param name="tr">The caller's transaction.</param>
    /// <param name="db">The drawing.</param>
    /// <param name="name">Alignment name from the run definition.</param>
    /// <param name="stored">Name and handle persisted with the run (6.9), or null on a first run.</param>
    /// <param name="units">Drawing units.</param>
    /// <param name="cfg">Adapter settings.</param>
    /// <param name="geometry">Boundary tolerances; default from <paramref name="units"/>.</param>
    public static Outcome<AlignmentAdapter> Find(Transaction tr, Database db, string name, StoredRef? stored,
        DrawingUnitInfo units, AdapterConfig cfg, GeometryOptions? geometry = null)
    {
        ArgumentNullException.ThrowIfNull(tr);
        ArgumentNullException.ThrowIfNull(db);
        var civ = CivilDocument.GetCivilDocument(db);
        if (civ is null)
            return Outcome<AlignmentAdapter>.Fail(new Problem(AdapterCodes.AlignmentNotFound, Severity.Error, "The drawing has no Civil 3D document."));

        ObjectId byName = ObjectId.Null;
        foreach (ObjectId id in civ.GetAlignmentIds())
        {
            if (id.IsErased) continue;
            if (tr.GetObject(id, OpenMode.ForRead) is Alignment a && string.Equals(a.Name, name, StringComparison.Ordinal)) { byName = id; break; }
        }
        var notes = new List<Problem>();
        // The stored handle stands in for the name only when it was stored for this same name: a
        // run whose alignment name was edited must not fall back to the previous alignment.
        bool storedForThisName = stored is not null && string.Equals(stored.Name, name, StringComparison.OrdinalIgnoreCase);
        if (byName.IsNull && storedForThisName && Handles.TryResolve(db, stored!.Handle, out var byHandle) && tr.GetObject(byHandle, OpenMode.ForRead) is Alignment renamed)
        {
            notes.Add(new Problem(AdapterCodes.SourceRenamed, Severity.Warning,
                $"Alignment '{name}' was not found by name; its stored handle {stored.Handle} is now alignment '{renamed.Name}', which is used.",
                Fix: "The run now refers to the renamed alignment; check it is the intended one."));
            byName = byHandle;
        }
        if (byName.IsNull)
            return Outcome<AlignmentAdapter>.Fail(new Problem(AdapterCodes.AlignmentNotFound, Severity.Error,
                $"Alignment '{name}' is not in the drawing" + (storedForThisName && stored!.Handle is not null ? $" (by name or by stored handle {stored.Handle})." : "."),
                Fix: "Restore the alignment or point the run at another one; nothing was changed."));

        var opened = Open(tr, byName, units, cfg, geometry);
        if (!opened.Succeeded) return Outcome<AlignmentAdapter>.Fail(notes.Concat(opened.Problems));
        return Outcome<AlignmentAdapter>.Ok(opened.Value, notes.Concat(opened.Problems));
    }

    /// <summary>
    /// Opens the alignment, reads its sub-entities, builds the station table and runs the
    /// construction cross-check. Fails with SV-E213, SV-E214 or SV-E204.
    /// </summary>
    /// <param name="tr">The caller's transaction; the adapter is valid only while it is open.</param>
    /// <param name="id">The alignment.</param>
    /// <param name="units">Drawing units; scale the cross-check offset.</param>
    /// <param name="cfg">Adapter settings.</param>
    /// <param name="geometry">Boundary tolerances as the planner will use them; default from <paramref name="units"/>.</param>
    public static Outcome<AlignmentAdapter> Open(Transaction tr, ObjectId id, DrawingUnitInfo units, AdapterConfig cfg, GeometryOptions? geometry = null)
    {
        ArgumentNullException.ThrowIfNull(tr);
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(cfg);
        geometry ??= GeometryOptions.ForUnitScale(units.DrawingUnitsPerFoot);
        if (id.IsNull || id.IsErased || !id.IsValid)
            return Outcome<AlignmentAdapter>.Fail(new Problem(AdapterCodes.AlignmentNotFound, Severity.Error,
                "The run's alignment has been erased.", Fix: "Restore the alignment (UNDO) or point the run at another one; nothing was changed."));
        if (tr.GetObject(id, OpenMode.ForRead) is not Alignment al)
            return Outcome<AlignmentAdapter>.Fail(new Problem(AdapterCodes.AlignmentNotFound, Severity.Error,
                $"Object {id.Handle} is not an alignment."));
        if (al.IsReferenceObject && (!al.IsReferenceValid || al.IsReferenceStale))
            return Outcome<AlignmentAdapter>.Fail(new Problem(AdapterCodes.AlignmentNotFound, Severity.Error,
                $"Alignment '{al.Name}' is a data-shortcut reference that is {(al.IsReferenceValid ? "stale" : "broken")}; SheetVol never computes on stale or broken geometry.",
                Fix: "Synchronize or repair the reference, then regenerate."));

        var problems = new List<Problem>();
        double tol = geometry.StationTolerance;
        var segs = ReadSegments(al, tol, cfg, problems, out double dirError);
        if (problems.Count > 0) return Outcome<AlignmentAdapter>.Fail(problems);

        var adapter = new AlignmentAdapter(al, segs, StationTable.Read(al, tol), tol);
        adapter.CrossCheck = adapter.RunCrossCheck(geometry, units, cfg, problems) with { MaxDirectionError = dirError };
        if (problems.Count > 0) return Outcome<AlignmentAdapter>.Fail(problems);
        return Outcome<AlignmentAdapter>.Ok(adapter);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The station lies outside the alignment by more than the station tolerance.</exception>
    public Pt PointAt(double rawStation, double offset)
    {
        GuardOpen();
        if (!double.IsFinite(rawStation) || rawStation < StartStation - _tol || rawStation > EndStation + _tol)
            throw new ArgumentOutOfRangeException(nameof(rawStation), rawStation, $"Station is outside alignment '{Name}' ({StartStation.ToString("R", CultureInfo.InvariantCulture)} to {EndStation.ToString("R", CultureInfo.InvariantCulture)}).");
        double e = 0, n = 0;
        _al.PointLocation(Math.Clamp(rawStation, StartStation, EndStation), offset, ref e, ref n);
        return new Pt(e, n);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The station lies outside the alignment (the API throws the same; C1 never asks).</exception>
    public string FormatStation(double rawStation)
    {
        GuardOpen();
        if (!double.IsFinite(rawStation) || rawStation < StartStation - _tol || rawStation > EndStation + _tol)
            throw new ArgumentOutOfRangeException(nameof(rawStation), rawStation, $"Station is outside alignment '{Name}'.");
        return _al.GetStationStringWithEquations(Math.Clamp(rawStation, StartStation, EndStation));
    }

    /// <inheritdoc />
    public bool TryParseStation(string text, out double rawStation, out string? error) => Stations.TryParse(text, out rawStation, out error);

    private void GuardOpen()
    {
        if (_al.IsDisposed)
            throw new InvalidOperationException($"The adapter for alignment '{Name}' is used after its transaction ended; open a new adapter inside the current transaction.");
    }

    // ------------------------------------------------------------ reading

    private static AlignSeg[] ReadSegments(Alignment al, double tol, AdapterConfig cfg, List<Problem> problems, out double maxDirError)
    {
        maxDirError = 0;
        var segs = new List<AlignSeg>();
        int total = al.Entities.Count, reached = 0;
        string Where(double s) => SafeStation(al, s);
        Problem Gap(string what) => new(AdapterCodes.AlignmentGap, Severity.Error, $"Alignment '{al.Name}' cannot be used: {what}.", null,
            "Fix the alignment so its entities are solved and connected end to end, then regenerate.");

        if (total == 0)
        {
            problems.Add(Gap("it has no entities"));
            return [];
        }
        for (int i = 0; i < total; i++)
        {
            AlignmentEntity e;
            try { e = al.Entities.GetEntityByOrder(i); }
            catch (ArgumentException) { break; }   // P7: past the connected path the host throws ArgumentException
            reached++;
            for (int j = 0; j < e.SubEntityCount; j++)
            {
                var sub = e[j];
                double s0 = sub.StartStation, s1 = sub.EndStation;
                var a = new Pt(sub.StartPoint.X, sub.StartPoint.Y);
                var b = new Pt(sub.EndPoint.X, sub.EndPoint.Y);
                if (!(double.IsFinite(s0) && double.IsFinite(s1) && Finite(a) && Finite(b)) || !(s1 - s0 > tol))
                {
                    problems.Add(Gap($"the {sub.SubEntityType} at {Where(s0)} is unsolved or has no length"));
                    continue;
                }
                switch (sub)
                {
                    case AlignmentSubEntityLine ln:
                    {
                        double dir = Math.Atan2(b.Y - a.Y, b.X - a.X);
                        maxDirError = Math.Max(maxDirError, Math.Abs(Normalize(FromAzimuth(ln.Direction) - dir)));
                        segs.Add(new AlignSeg(SegKind.Line, s0, s1, a, b, dir, dir));
                        break;
                    }
                    case AlignmentSubEntityArc arc:
                    {
                        var c = new Pt(arc.CenterPoint.X, arc.CenterPoint.Y);
                        double r = arc.Radius;
                        if (!Finite(c) || !(r > 0) || !double.IsFinite(r))
                        {
                            problems.Add(Gap($"the arc at {Where(s0)} has no center or radius"));
                            continue;
                        }
                        bool cw = arc.Clockwise;
                        double d0 = FromAzimuth(arc.StartDirection), d1 = FromAzimuth(arc.EndDirection);
                        // Tangent of the geometry: radial angle turned by -90 degrees on a clockwise arc, +90 otherwise.
                        double turn = cw ? -Math.PI / 2 : Math.PI / 2;
                        double g0 = Math.Atan2(a.Y - c.Y, a.X - c.X) + turn, g1 = Math.Atan2(b.Y - c.Y, b.X - c.X) + turn;
                        double err = Math.Max(Math.Abs(Normalize(d0 - g0)), Math.Abs(Normalize(d1 - g1)));
                        maxDirError = Math.Max(maxDirError, err);
                        // atan2 of (point - center) carries about one coordinate ulp / R of rounding: at
                        // state-plane magnitudes that exceeds 1e-9 rad on curb-return radii, so allow it.
                        double magnitude = Math.Max(Math.Max(Math.Abs(a.X), Math.Abs(a.Y)), Math.Max(Math.Abs(c.X), Math.Abs(c.Y)));
                        double rounding = 8 * (Math.BitIncrement(magnitude) - magnitude) / r;
                        if (err > Math.Max(cfg.DirectionTolerance, rounding))
                            problems.Add(new Problem(AdapterCodes.AlignmentCrossCheck, Severity.Error,
                                $"Alignment '{al.Name}': the arc at {Where(s0)} reports directions that differ from its own geometry by {err:E2} rad (Clockwise={cw}).",
                                Fix: "This points to an adapter defect, not a drafting error. Report it with the drawing."));
                        segs.Add(new AlignSeg(SegKind.Arc, s0, s1, a, b, d0, d1, c, r, cw));
                        break;
                    }
                    case AlignmentSubEntitySpiral sp:
                        segs.Add(new AlignSeg(SegKind.Spiral, s0, s1, a, b, FromAzimuth(sp.StartDirection), FromAzimuth(sp.EndDirection)));
                        break;
                    default:
                        problems.Add(Gap($"the {sub.SubEntityType} at {Where(s0)} is not a line, arc or spiral"));
                        break;
                }
            }
        }
        if (reached < total)
        {
            double end = segs.Count > 0 ? segs[^1].EndStation : al.StartingStation;
            problems.Add(Gap($"{total - reached} of its {total} entities are not connected to its path; the connected path ends at {Where(end)}"));
        }
        for (int k = 1; k < segs.Count; k++)
        {
            var p = segs[k - 1];
            var g = segs[k];
            double dist = Distance(p.End, g.Start);
            if (Math.Abs(g.StartStation - p.EndStation) > tol || dist > cfg.CrossCheckTolerance)
                problems.Add(Gap($"there is a gap at station {Where(g.StartStation)} (the previous element ends at {Where(p.EndStation)}, {dist.ToString("0.######", CultureInfo.InvariantCulture)} apart)"));
        }
        if (segs.Count > 0 && (Math.Abs(segs[0].StartStation - al.StartingStation) > tol || Math.Abs(segs[^1].EndStation - al.EndingStation) > tol))
            problems.Add(Gap($"its entities cover {Where(segs[0].StartStation)} to {Where(segs[^1].EndStation)} but the alignment runs {Where(al.StartingStation)} to {Where(al.EndingStation)}"));
        return segs.ToArray();
    }

    private static string SafeStation(Alignment al, double raw)
    {
        try { return al.GetStationStringWithEquations(raw); }
        catch (System.Exception) { return "raw " + raw.ToString("0.00####", CultureInfo.InvariantCulture); }
    }

    // ------------------------------------------------------------ cross-check (P7, E204)

    private CrossCheckReport RunCrossCheck(GeometryOptions geometry, DrawingUnitInfo units, AdapterConfig cfg, List<Problem> problems)
    {
        double minRadius = double.PositiveInfinity;
        foreach (var g in _segs)
            if (g.Kind == SegKind.Arc) minRadius = Math.Min(minRadius, g.Radius);
        foreach (var r in SpiralRadii()) minRadius = Math.Min(minRadius, r);
        double c = cfg.CrossCheckOffsetFeet * units.DrawingUnitsPerFoot;
        if (double.IsFinite(minRadius)) c = Math.Min(c, 0.5 * minRadius);

        // One segment per element half, so every element is sampled at its ends and middle.
        var defs = new List<SegmentDef>();
        foreach (var g in _segs)
        {
            double mid = 0.5 * (g.StartStation + g.EndStation);
            if (g.EndStation - g.StartStation > 20 * geometry.StationTolerance)
            {
                defs.Add(new SegmentDef(Guid.NewGuid(), "x", g.StartStation, mid, -c, c));
                defs.Add(new SegmentDef(Guid.NewGuid(), "x", mid, g.EndStation, -c, c));
            }
            else defs.Add(new SegmentDef(Guid.NewGuid(), "x", g.StartStation, g.EndStation, -c, c));
        }

        string? why = null;
        BoundaryBuildResult? built = null;
        try { built = BoundaryBuilder.Build(this, defs, -c, c, geometry); }
        catch (ArgumentException ex) { why = ex.Message; }
        if (built is not null)
        {
            var errors = built.Problems.Where(p => p.Severity == Severity.Error).ToList();
            if (errors.Any(p => p.Code == AdapterCodes.AlignmentCrossCheck)) problems.AddRange(errors.Where(p => p.Code == AdapterCodes.AlignmentCrossCheck));
            if (errors.Count > 0) why = $"C1 reported {string.Join(", ", errors.Select(p => p.Code).Distinct())}";
            else if (built.Band.Count != 1) why = $"C1 built {built.Band.Count} band pieces";
            else if (built.Band[0].Tessellated.Count != 2 * built.Stations.Count + 1) why = "the band ring does not have one vertex per station and side";
        }

        CrossCheckReport report = why is null
            ? BandCheck(built!, c, cfg)
            : DirectCheck(c, cfg, $"direct closed form (C1's band over the whole alignment was not built: {why})");
        if (report.MaxDeviation > cfg.CrossCheckTolerance)
            problems.Add(new Problem(AdapterCodes.AlignmentCrossCheck, Severity.Error,
                $"Alignment '{Name}': a computed offset point differs from the alignment's own point by {report.MaxDeviation:E2} (tolerance {cfg.CrossCheckTolerance:E0}) {report.Worst}.",
                Fix: "This points to an adapter defect, not a drafting error. Report it with the drawing."));
        return report;
    }

    private IEnumerable<double> SpiralRadii()
    {
        int total = _al.Entities.Count;
        for (int i = 0; i < total; i++)
        {
            AlignmentEntity e;
            try { e = _al.Entities.GetEntityByOrder(i); }
            catch (ArgumentException) { yield break; }
            for (int j = 0; j < e.SubEntityCount; j++)
            {
                if (e[j] is not AlignmentSubEntitySpiral sp) continue;
                foreach (double r in new[] { sp.RadiusIn, sp.RadiusOut })
                    if (double.IsFinite(r) && r > 0) yield return r;
            }
        }
    }

    private CrossCheckReport BandCheck(BoundaryBuildResult built, double c, AdapterConfig cfg)
    {
        var st = built.Stations;
        var ring = built.Band[0].Tessellated;
        int n = st.Count, analytic = 0, viaPointAt = 0, mitre = 0;
        double max = 0;
        string worst = "";
        for (int k = 0; k < n; k++)
        {
            double s = st[k];
            int e = ElementUsed(s);
            if (IsKinkedJunction(s, c, cfg)) { mitre += 2; continue; }
            foreach (var (vertex, o) in new[] { (ring[k], c), (ring[2 * n - 1 - k], -c) })
            {
                var q = PointAt(s, o);
                double d = Distance(vertex, q);
                if (_segs[e].Kind == SegKind.Spiral) viaPointAt++; else analytic++;
                if (d > max) { max = d; worst = Describe(s, o, e, vertex, q); }
            }
        }
        return new CrossCheckReport($"BoundaryBuilder band over the whole alignment, {n} stations", c, analytic, viaPointAt, mitre, max, worst, 0);
    }

    private CrossCheckReport DirectCheck(double c, AdapterConfig cfg, string method)
    {
        int analytic = 0;
        double max = 0;
        string worst = "";
        for (int e = 0; e < _segs.Length; e++)
        {
            var g = _segs[e];
            if (g.Kind == SegKind.Spiral) continue;
            // Interior stations only: at an element end that meets a kink the two elements' offset
            // points differ by 2c sin(d/2) and PointLocation can match only one of them.
            for (int k = 1; k <= 3; k++)
            {
                double s = g.StartStation + (g.EndStation - g.StartStation) * k / 4;
                foreach (double o in new[] { -c, 0, c })
                {
                    var p = ClosedForm(g, s, o);
                    var q = PointAt(s, o);
                    double d = Distance(p, q);
                    analytic++;
                    if (d > max) { max = d; worst = Describe(s, o, e, p, q); }
                }
            }
        }
        return new CrossCheckReport(method, c, analytic, 0, 0, max, worst, 0);
    }

    /// <summary>C1's closed form (AlignmentModel.Eval): line offset along the right normal; arc at radius R + o on a counter-clockwise arc, R - o on a clockwise one.</summary>
    private static Pt ClosedForm(AlignSeg g, double s, double o)
    {
        if (g.Kind == SegKind.Line)
        {
            double len = Distance(g.Start, g.End), ux = (g.End.X - g.Start.X) / len, uy = (g.End.Y - g.Start.Y) / len;
            double t = (s - g.StartStation) / (g.EndStation - g.StartStation);
            return new Pt(g.Start.X + t * (g.End.X - g.Start.X) + o * uy, g.Start.Y + t * (g.End.Y - g.Start.Y) - o * ux);
        }
        var c = g.Center!.Value;
        double sgn = g.Clockwise ? -1 : 1;
        double phi = Math.Atan2(g.Start.Y - c.Y, g.Start.X - c.X) + sgn * (s - g.StartStation) / g.Radius;
        double r = g.Radius + sgn * o;
        return new Pt(c.X + r * Math.Cos(phi), c.Y + r * Math.Sin(phi));
    }

    /// <summary>The element C1 evaluates at a station node: the following element at a junction.</summary>
    private int ElementUsed(double s)
    {
        int i = Array.BinarySearch(_starts, s + _tol);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, _segs.Length - 1);
    }

    /// <summary>
    /// True at a junction whose change of direction could move an offset point by more than the
    /// tolerance: a line-line angle point (C1 puts a mitre point there, off the perpendicular
    /// offset by design), or a near-tangent kink C1 still treats as tangent (it evaluates the
    /// following element while PointLocation may evaluate the preceding one). Neither vertex is
    /// comparable with a single PointLocation call, so both are skipped and counted.
    /// </summary>
    private bool IsKinkedJunction(double s, double offset, AdapterConfig cfg)
    {
        for (int j = 0; j + 1 < _segs.Length; j++)
        {
            if (Math.Abs(_segs[j].EndStation - s) > _tol) continue;
            return Math.Abs(Normalize(_segs[j + 1].StartDir - _segs[j].EndDir)) * offset > cfg.CrossCheckTolerance;
        }
        return false;
    }

    private string Describe(double s, double o, int e, Pt computed, Pt api) => string.Create(CultureInfo.InvariantCulture,
        $"at station {s:0.######} offset {o:0.###} on the {_segs[e].Kind.ToString().ToLowerInvariant()} (computed ({computed.X:0.######}, {computed.Y:0.######}), PointLocation ({api.X:0.######}, {api.Y:0.######}))");

    // ------------------------------------------------------------ helpers

    /// <summary>Civil azimuth (clockwise from north) to the contract's direction (counter-clockwise from +X), wrapped to (-pi, pi].</summary>
    internal static double FromAzimuth(double azimuth) => Normalize(Math.PI / 2 - azimuth);

    internal static double Normalize(double a)
    {
        a %= 2 * Math.PI;
        if (a <= -Math.PI) a += 2 * Math.PI;
        else if (a > Math.PI) a -= 2 * Math.PI;
        return a;
    }

    private static bool Finite(Pt p) => double.IsFinite(p.X) && double.IsFinite(p.Y);

    private static double Distance(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>Handle text to object id, tolerant of missing or malformed input.</summary>
internal static class Handles
{
    /// <summary>Resolves a hexadecimal handle in <paramref name="db"/> to a live (not erased) object id.</summary>
    public static bool TryResolve(Database db, string? handle, out ObjectId id)
    {
        id = ObjectId.Null;
        if (string.IsNullOrWhiteSpace(handle)) return false;
        if (!long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value)) return false;
        if (!db.TryGetObjectId(new Handle(value), out var found)) return false;
        if (found.IsNull || found.IsErased || !found.IsValid) return false;
        id = found;
        return true;
    }
}

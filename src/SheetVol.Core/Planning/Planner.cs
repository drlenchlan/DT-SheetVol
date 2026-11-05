using SheetVol.Core.Geometry;

namespace SheetVol.Core.Planning;

/// <summary>
/// Planning and boundary checks for one run (HANDOFF.md 6.3, 6.4, 6.10 step 2): parse the
/// break list, plan station segments with identity carried over, build and validate
/// boundaries, check manual polylines, detect overlaps, and build the whole-band pieces.
/// Pure: no volumes, no writes. Construct with options from the drawing's unit scale.
/// <para>Offsets: every station segment carries its own (the segment's
/// <see cref="SegmentOverrides.OffsetLeft"/> and <see cref="SegmentOverrides.OffsetRight"/>
/// where set, else the run's; see <see cref="SegmentPlanner"/>) and is built with them. Unusable
/// run offsets are one run-level E210; an override that leaves a segment without a usable band
/// is an E210 Error on that segment only, and its neighbours still build.</para>
/// </summary>
public sealed class Planner
{
    /// <summary>Creates a planner.</summary>
    /// <param name="options">Settings; use <see cref="PlanningOptions.ForUnitScale"/> with the drawing's units.</param>
    /// <exception cref="ArgumentException">A setting is out of range or the label format does not format; the settings file is wrong, so fail before planning.</exception>
    public Planner(PlanningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Geometry);
        var g = options.Geometry;
        if (!(g.MidOrdinate > 0) || !double.IsFinite(g.MidOrdinate))
            throw new ArgumentException($"MidOrdinate must be a positive length; it is {g.MidOrdinate}.", nameof(options));
        if (!(g.StationTolerance > 0) || !double.IsFinite(g.StationTolerance))
            throw new ArgumentException($"StationTolerance must be a positive length; it is {g.StationTolerance}.", nameof(options));
        if (!(g.MitreLimitDeg > 0 && g.MitreLimitDeg < 180))
            throw new ArgumentException($"MitreLimitDeg must lie between 0 and 180; it is {g.MitreLimitDeg}.", nameof(options));
        if (!(g.OverlapAreaTolerance >= 0) || !double.IsFinite(g.OverlapAreaTolerance))
            throw new ArgumentException($"OverlapAreaTolerance must be zero or a positive area; it is {g.OverlapAreaTolerance}.", nameof(options));
        if (!(options.MinSegmentLength >= 0) || !double.IsFinite(options.MinSegmentLength))
            throw new ArgumentException($"MinSegmentLength must be zero or a positive length; it is {options.MinSegmentLength}.", nameof(options));
        if (options.MaxBreaks < 2)
            throw new ArgumentException($"MaxBreaks must be at least 2; it is {options.MaxBreaks}.", nameof(options));
        try
        {
            _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, options.SegmentLabelFormat, 1);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentNullException)
        {
            throw new ArgumentException($"SegmentLabelFormat '{options.SegmentLabelFormat}' is not a valid format with one argument ({{0}} is the segment number).", nameof(options), ex);
        }
        Options = options;
    }

    /// <summary>The settings this planner uses.</summary>
    public PlanningOptions Options { get; }

    /// <summary>
    /// <see cref="ISheetVolSession.Validate"/> in Core: planning and boundary checks only.
    /// </summary>
    /// <param name="run">The run definition.</param>
    /// <param name="alignment">The run's alignment.</param>
    /// <param name="manualOutlines">Polyline geometry per manual segment GUID, read by the adapter. A manual segment missing here is reported as erased (E311).</param>
    public ValidationResult Validate(RunDefinition run, IAlignmentGeometry alignment, IReadOnlyDictionary<Guid, ManualOutline>? manualOutlines = null) =>
        Plan(run, alignment, manualOutlines).ToValidationResult();

    /// <summary>Plans the run and returns everything the regen pipeline needs.</summary>
    /// <param name="run">The run definition.</param>
    /// <param name="alignment">The run's alignment.</param>
    /// <param name="manualOutlines">Polyline geometry per manual segment GUID; see <see cref="Validate"/>.</param>
    public PlanResult Plan(RunDefinition run, IAlignmentGeometry alignment, IReadOnlyDictionary<Guid, ManualOutline>? manualOutlines = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(alignment);
        var g = Options.Geometry;
        double tol = g.StationTolerance;
        var runProblems = new List<Problem>();
        var segmentProblems = new List<Problem>();

        CheckOffsets(run, runProblems);

        var planned = new List<PlannedSegment>();
        IReadOnlyList<Guid> dropped = [];
        bool manualOnly = run.ManualSegments.Count > 0 && string.IsNullOrWhiteSpace(run.BreakSpec);
        if (run.Driver != SegmentDriver.Stations)
        {
            runProblems.Add(new Problem(ProblemCodes.E300, Severity.Error,
                $"Run '{run.Name}' uses the {run.Driver} driver. Core planning reads break lists only (Stations driver); view frame stations are not planned here yet.",
                null, "Switch the run to the Stations driver, or wait for the view frame driver."));
        }
        else if (!manualOnly)
        {
            var parsed = BreakSpecParser.Parse(run.BreakSpec, alignment, Options);
            runProblems.AddRange(parsed.Problems);
            var plan = SegmentPlanner.Plan(parsed, run, alignment, Options);
            planned.AddRange(plan.Segments);
            dropped = plan.Dropped;
            foreach (var p in plan.Problems)
                (p.SegmentId is null || p.Severity != Severity.Error ? runProblems : segmentProblems).Add(p);
            if (planned.Count == 0 && parsed.Breaks.Count > 0)
                runProblems.Add(new Problem(ProblemCodes.E300, Severity.Error,
                    "The break list produces no segment.", null, "Give at least two breaks with a stretch between them that is not skipped."));
        }

        // Each segment builds with its own offsets (per-segment overrides, rev 2). A segment whose
        // offsets are unusable gets no boundary: E210 on that segment when an override made them
        // so; when they are the run's own, the run-level E210 above already says so.
        var buildable = new List<SegmentDef>();
        foreach (var def in planned.Where(p => p.InRange).Select(p => p.Def))
        {
            if (OffsetsUsable(def.OffsetLeft, def.OffsetRight)) buildable.Add(def);
            else if (!(def.OffsetLeft.Equals(run.OffsetLeft) && def.OffsetRight.Equals(run.OffsetRight)))
                segmentProblems.Add(SegmentOffsetProblem(alignment, def, run));
        }

        var boundaries = new List<Boundary>();
        var built = BoundaryBuilder.Build(alignment, buildable, g);
        boundaries.AddRange(built.Boundaries);
        IReadOnlyList<BandPiece> band = built.Band;
        IReadOnlyList<double> stations = built.Stations;
        foreach (var p in built.Problems) (p.SegmentId is null ? runProblems : segmentProblems).Add(p);

        foreach (var m in run.ManualSegments)
        {
            var outline = manualOutlines is not null && manualOutlines.TryGetValue(m.Id, out var o) ? o : null;
            var b = ManualBoundary(m, outline, g, segmentProblems);
            if (b is not null) boundaries.Add(b);
        }

        var overlaps = new List<Overlap>();
        try
        {
            overlaps.AddRange(OverlapDetector.Find(boundaries, g.OverlapAreaTolerance));
        }
        catch (InvalidOperationException ex)
        {
            runProblems.Add(new Problem(ProblemCodes.E215, Severity.Error,
                $"The overlap check could not run: {ex.Message}", null, "Report this with the drawing; a validated boundary should always triangulate."));
        }

        var labels = planned.ToDictionary(p => p.Def.Id, p => p.Def.Label);
        foreach (var m in run.ManualSegments) labels.TryAdd(m.Id, m.Label);
        var manualIds = run.ManualSegments.Select(m => m.Id).ToHashSet();
        bool stationOverlap = false;
        foreach (var ov in overlaps)
        {
            bool manual = manualIds.Contains(ov.First) || manualIds.Contains(ov.Second);
            stationOverlap |= !manual;
            string a = Describe(ov.First, manualIds, labels), b = Describe(ov.Second, manualIds, labels);
            runProblems.Add(new Problem(manual ? ProblemCodes.E312 : ProblemCodes.E215, Severity.Warning,
                $"{a} and {b} overlap by {Fmt.Num(ov.Area)} square units; that ground is counted in both."
                + (manual ? string.Empty : " The partition check is skipped for this run."),
                ov.First,
                manual ? "Redraw the manual polyline so it does not cover station segments, or accept the double count."
                       : "Move breaks or reduce offsets so the boundaries do not overlap, or accept the overlap; the master table carries a footnote."));
        }

        string? skipReason = stationOverlap
            ? "Station segment boundaries overlap (E215), so the sum of segments cannot equal the band; the partition check is skipped."
            : band.Count == 0 ? "No valid station segment, so there is no band to check." : null;

        var segOrder = planned.Select((p, i) => (p.Def.Id, i)).ToDictionary(t => t.Id, t => t.i);
        var problems = runProblems
            .Concat(segmentProblems.OrderBy(p => p.SegmentId is { } id && segOrder.TryGetValue(id, out int i) ? i : int.MaxValue))
            .ToList();

        return new PlanResult(
            planned.Select(p => p.Def).ToList(),
            boundaries,
            band,
            overlaps,
            dropped,
            stations,
            skipReason is null,
            skipReason,
            problems);
    }

    private static string Describe(Guid id, HashSet<Guid> manual, Dictionary<Guid, string> labels) =>
        (manual.Contains(id) ? "Manual segment " : "Segment ") + (labels.TryGetValue(id, out var l) ? l : id.ToString());

    /// <summary>Offsets are usable when both are numbers and the right exceeds the left by more than the station tolerance (E210).</summary>
    private bool OffsetsUsable(double left, double right) =>
        double.IsFinite(left) && double.IsFinite(right) && right - left > Options.Geometry.StationTolerance;

    private void CheckOffsets(RunDefinition run, List<Problem> problems)
    {
        double l = run.OffsetLeft, r = run.OffsetRight;
        if (OffsetsUsable(l, r)) return;
        problems.Add(new Problem(ProblemCodes.E210, Severity.Error,
            !double.IsFinite(l) || !double.IsFinite(r)
                ? "The offsets are not numbers."
                : $"The left offset ({Fmt.Num(l)}) must be less than the right offset ({Fmt.Num(r)}); offsets are signed, negative to the left. The band has no width.",
            null, "Give a negative or smaller left offset and a larger right offset, for example -100 and 100."));
    }

    /// <summary>E210 on one segment whose offset override leaves it without a usable band (Error, that segment only).</summary>
    private Problem SegmentOffsetProblem(IAlignmentGeometry alignment, SegmentDef def, RunDefinition run)
    {
        string segment = Fmt.Segment(alignment, def, Options.Geometry.StationTolerance);
        string message = !double.IsFinite(def.OffsetLeft) || !double.IsFinite(def.OffsetRight)
            ? $"{segment}: its offset override is not a number. It gets no boundary or volume."
            : $"{segment}: with its offset override the left offset ({Fmt.Num(def.OffsetLeft)}) is not less than the right offset ({Fmt.Num(def.OffsetRight)}); offsets are signed, negative to the left. Its band has no width, so it gets no boundary or volume.";
        string fix = OffsetsUsable(run.OffsetLeft, run.OffsetRight)
            ? $"Change this segment's offsets so the left is less than the right, or clear its override to use the run's offsets ({Fmt.Num(run.OffsetLeft)} and {Fmt.Num(run.OffsetRight)})."
            : "Change this segment's offsets so the left is less than the right; the run's own offsets are not usable either.";
        return new Problem(ProblemCodes.E210, Severity.Error, message, def.Id, fix);
    }

    private static Boundary? ManualBoundary(ManualSegmentDef def, ManualOutline? outline, GeometryOptions g, List<Problem> problems)
    {
        string name = $"Manual segment '{def.Label}' (polyline {def.PolylineHandle})";
        Problem Fail(string what, string fix) => new(ProblemCodes.E311, Severity.Error, $"{name}: {what}. No volume is computed on it.", def.Id, fix);

        if (outline is null || outline.Vertices is null || outline.Vertices.Count == 0)
        {
            problems.Add(Fail("the polyline was not found; it may have been erased", "Redraw the polyline and attach it to the segment again, or remove the manual segment."));
            return null;
        }

        var verts = outline.Vertices.ToList();
        bool closed = outline.Closed;
        if (verts.Count > 1 && verts[0].Point == verts[^1].Point)
        {
            verts.RemoveAt(verts.Count - 1);
            closed = true;
        }
        if (!closed)
        {
            problems.Add(Fail("the polyline is open", "Close the polyline (PEDIT, Close) so it encloses an area."));
            return null;
        }

        var ring = Polygon.TessellateBulges(verts, closed: true, g.MidOrdinate);
        string? reason = BoundaryValidator.Check(ring, requireCounterClockwise: false, minDistinctVertices: 3);
        if (reason is not null)
        {
            problems.Add(Fail(reason, "Redraw the polyline as a simple closed shape that does not cross itself."));
            return null;
        }
        return new Boundary(def.Id, verts, ring);
    }
}

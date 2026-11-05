namespace SheetVol.Core.Tracking;

/// <summary>State of one analysis's owned volume surface at fingerprint time (6.11).</summary>
/// <param name="AnalysisId">The analysis.</param>
/// <param name="IsOutOfDate">The owned TinVolumeSurface reports out of date.</param>
/// <param name="LastTotals">Its whole-surface totals as last read, cubic drawing units, unadjusted.</param>
public sealed record AnalysisSurfaceState(Guid AnalysisId, bool IsOutOfDate, RawVolume LastTotals);

/// <summary>Everything a run's outputs are derived from, read at one moment.</summary>
/// <param name="Run">The run definition as stored.</param>
/// <param name="Alignment">The alignment as read now.</param>
/// <param name="Frames">The view frames as read now, in group order; empty for the Stations driver.</param>
/// <param name="Surfaces">Owned volume surface state per analysis.</param>
/// <param name="ManualSegmentGeometry">Current vertices of each manual segment polyline, WCS drawing units, keyed by manual segment id. A missing key is fingerprinted as missing.</param>
public sealed record FingerprintInput(
    RunDefinition Run,
    IAlignmentGeometry Alignment,
    IReadOnlyList<FrameGeom> Frames,
    IReadOnlyList<AnalysisSurfaceState> Surfaces,
    IReadOnlyDictionary<Guid, IReadOnlyList<PolyVertex>>? ManualSegmentGeometry = null);

/// <summary>
/// A run's source fingerprint: one lower-case hex SHA-256 per component plus
/// <see cref="Value"/> over all of them. Stored with the run (6.9) and compared on open,
/// activation, reference sync and after commands (6.11). Plain strings, so it serializes
/// with System.Text.Json unchanged.
/// </summary>
/// <param name="Format">Canonical format version; a different format always compares stale.</param>
/// <param name="Value">Hash over the format and every component hash. Equal values mean nothing changed.</param>
/// <param name="Alignment">Alignment geometry and displayed stationing.</param>
/// <param name="Definition">Run definition parts that shape outputs: break spec, offsets, analyses, views, overrides, preset, area unit, OHWM.</param>
/// <param name="Frames">View frame stations and corners.</param>
/// <param name="Surfaces">Owned volume surface out-of-date state and last totals, per analysis.</param>
/// <param name="ManualSegments">Manual segment polyline geometry.</param>
public sealed record RunFingerprint(
    string Format, string Value,
    string Alignment, string Definition, string Frames, string Surfaces, string ManualSegments);

/// <summary>Outcome of comparing a stored fingerprint with a current one.</summary>
/// <param name="IsStale">The run's outputs no longer match their sources.</param>
/// <param name="ChangedComponents">Names from <see cref="Fingerprints"/> component constants, in a fixed order.</param>
public sealed record FingerprintComparison(bool IsStale, IReadOnlyList<string> ChangedComponents)
{
    /// <summary>The comparison for an unchanged run.</summary>
    public static FingerprintComparison Current { get; } = new(false, []);
}

/// <summary>
/// Run fingerprints per HANDOFF.md 6.11: SHA-256 over an invariant canonical text of the
/// alignment geometry signature, break spec, offsets, frame signatures and owned volume
/// surface state. Same inputs give the same fingerprint on every run and machine; any change
/// to an input gives a different one. <see cref="RunDefinition.KnownSegments"/> and
/// <see cref="RunDefinition.AutoMode"/> are excluded: the first is regen output, the second
/// changes no output.
/// </summary>
public static class Fingerprints
{
    /// <summary>Canonical format version. Changing the canonical text requires a new value.</summary>
    public const string FormatVersion = "SVFP1";

    /// <summary>Component name: canonical format version.</summary>
    public const string FormatComponent = "format";
    /// <summary>Component name: alignment geometry.</summary>
    public const string AlignmentComponent = "alignment";
    /// <summary>Component name: run definition.</summary>
    public const string DefinitionComponent = "definition";
    /// <summary>Component name: view frames.</summary>
    public const string FramesComponent = "frames";
    /// <summary>Component name: owned volume surfaces.</summary>
    public const string SurfacesComponent = "surfaces";
    /// <summary>Component name: manual segment geometry.</summary>
    public const string ManualSegmentsComponent = "manual";

    /// <summary>Computes the fingerprint of the run's current sources.</summary>
    public static RunFingerprint Compute(FingerprintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Run);
        ArgumentNullException.ThrowIfNull(input.Alignment);

        string alignment = AlignmentSignature(input.Alignment);
        string definition = DefinitionSignature(input.Run);
        string frames = FramesSignature(input.Frames ?? []);
        string surfaces = SurfacesSignature(input.Run, input.Surfaces ?? []);
        string manual = ManualSignature(input.Run, input.ManualSegmentGeometry);

        string value = new CanonicalWriter()
            .Section("run").Add(FormatVersion)
            .Add(alignment).Add(definition).Add(frames).Add(surfaces).Add(manual)
            .Sha256Hex();
        return new RunFingerprint(FormatVersion, value, alignment, definition, frames, surfaces, manual);
    }

    /// <summary>
    /// Compares a stored fingerprint with the current one. No stored fingerprint (never
    /// regenerated) is stale with every component listed.
    /// </summary>
    public static FingerprintComparison Compare(RunFingerprint? stored, RunFingerprint current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (stored is null)
            return new FingerprintComparison(true,
                [AlignmentComponent, DefinitionComponent, FramesComponent, SurfacesComponent, ManualSegmentsComponent]);
        if (!string.Equals(stored.Format, current.Format, StringComparison.Ordinal))
            return new FingerprintComparison(true, [FormatComponent]);

        var changed = new List<string>();
        void Check(string name, string a, string b) { if (!string.Equals(a, b, StringComparison.Ordinal)) changed.Add(name); }
        Check(AlignmentComponent, stored.Alignment, current.Alignment);
        Check(DefinitionComponent, stored.Definition, current.Definition);
        Check(FramesComponent, stored.Frames, current.Frames);
        Check(SurfacesComponent, stored.Surfaces, current.Surfaces);
        Check(ManualSegmentsComponent, stored.ManualSegments, current.ManualSegments);

        bool stale = changed.Count > 0 || !string.Equals(stored.Value, current.Value, StringComparison.Ordinal);
        return stale ? new FingerprintComparison(true, changed) : FingerprintComparison.Current;
    }

    /// <summary>
    /// Alignment geometry signature from <see cref="IAlignmentGeometry.Segments"/>: start and
    /// end station, then per segment its kind, raw stations, end points, directions (radians
    /// counter-clockwise from +X), center, radius and turn sense. The displayed station at
    /// every segment end is included too, so a changed station equation (labels) is caught.
    /// The alignment's name is not geometry and is left out.
    /// </summary>
    public static string AlignmentSignature(IAlignmentGeometry alignment)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        var w = new CanonicalWriter().Section(AlignmentComponent)
            .Add(alignment.StartStation).Add(alignment.EndStation);
        IReadOnlyList<AlignSeg> segments = alignment.Segments ?? [];
        w.Add(segments.Count);
        foreach (AlignSeg s in segments)
        {
            w.AddEnum(s.Kind).Add(s.StartStation).Add(s.EndStation)
             .Add(s.Start).Add(s.End).Add(s.StartDir).Add(s.EndDir)
             .Add(s.Center).Add(s.Radius).Add(s.Clockwise);
        }

        w.Section("labels").Add(alignment.FormatStation(alignment.StartStation));
        foreach (AlignSeg s in segments) w.Add(alignment.FormatStation(s.EndStation));
        w.Add(alignment.FormatStation(alignment.EndStation));
        return w.Sha256Hex();
    }

    /// <summary>Signature of the run definition parts that shape outputs (see <see cref="RunFingerprint.Definition"/>).</summary>
    public static string DefinitionSignature(RunDefinition run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var w = new CanonicalWriter().Section(DefinitionComponent)
            .Add(run.SchemaVersion).Add(run.Name).Add(run.AlignmentName)
            .AddEnum(run.Driver).Add(run.BreakSpec).Add(run.ViewFrameGroupName)
            .Add(run.OffsetLeft).Add(run.OffsetRight)
            .AddEnum(run.Preset).AddEnum(run.AreaUnit);

        IReadOnlyList<AnalysisDef> analyses = run.Analyses ?? [];
        w.Section("analyses").Add(analyses.Count);
        foreach (AnalysisDef a in analyses)
            w.Add(a.Id).Add(a.Name).Add(a.BaseSurface).Add(a.ComparisonSurface).Add(a.CutFactor).Add(a.FillFactor);

        IReadOnlyList<ManualSegmentDef> manual = run.ManualSegments ?? [];
        w.Section("manualdefs").Add(manual.Count);
        foreach (ManualSegmentDef m in manual)
            w.Add(m.Id).Add(m.Label).Add(m.PolylineHandle).Add(m.TargetLayout);

        ViewOptions v = run.Views ?? new ViewOptions();
        w.Section("views").AddEnum(v.Orientation).AddEnum(v.ScaleMode).Add(v.ModelUnitsPerPaperUnit)
         .AddEnum(v.Clip).Add(v.ViewOverlap);
        IReadOnlyList<double> scales = v.StandardScales ?? [];
        w.Add(v.StandardScales is not null).Add(scales.Count);
        foreach (double s in scales) w.Add(s);

        // Dictionaries have no defined order: sort by key text so insertion order never matters.
        var overrides = (run.Overrides ?? new Dictionary<Guid, SegmentOverrides>())
            .OrderBy(kv => kv.Key.ToString("D"), StringComparer.Ordinal).ToArray();
        w.Section("overrides").Add(overrides.Length);
        foreach (var (id, o) in overrides)
        {
            w.Add(id).Add(o.TargetLayout).Add(o.LockView).Add(o.ModelUnitsPerPaperUnit is not null);
            if (o.ModelUnitsPerPaperUnit is double scale) w.Add(scale);
        }

        w.Section("ohwm").Add(run.Ohwm is not null);
        if (run.Ohwm is OhwmSpec ohwm)
        {
            var bySegment = (ohwm.BySegment ?? new Dictionary<Guid, double>())
                .OrderBy(kv => kv.Key.ToString("D"), StringComparer.Ordinal).ToArray();
            w.Add(ohwm.DefaultElevation).Add(bySegment.Length);
            foreach (var (id, elevation) in bySegment) w.Add(id).Add(elevation);
        }
        return w.Sha256Hex();
    }

    /// <summary>Signature of the view frames: per frame its key, raw stations and four corners (6.11).</summary>
    public static string FramesSignature(IReadOnlyList<FrameGeom> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var w = new CanonicalWriter().Section(FramesComponent).Add(frames.Count);
        foreach (FrameGeom f in frames)
        {
            w.Add(f.Key).Add(f.StartStation).Add(f.EndStation);
            foreach (Pt corner in FrameCorners(f)) w.Add(corner);
        }
        return w.Sha256Hex();
    }

    /// <summary>
    /// Corners of a frame rectangle in WCS, drawing units, in the order
    /// (−w/2, −h/2), (+w/2, −h/2), (+w/2, +h/2), (−w/2, +h/2) of the frame's own axes,
    /// rotated by <see cref="FrameGeom.RotationRadians"/> counter-clockwise about its center.
    /// </summary>
    public static Pt[] FrameCorners(FrameGeom frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        double c = Math.Cos(frame.RotationRadians), s = Math.Sin(frame.RotationRadians);
        double hw = frame.Width / 2, hh = frame.Height / 2;
        Pt Corner(double x, double y) => new(frame.Center.X + x * c - y * s, frame.Center.Y + x * s + y * c);
        return [Corner(-hw, -hh), Corner(hw, -hh), Corner(hw, hh), Corner(-hw, hh)];
    }

    /// <summary>Signature of the owned volume surfaces, in the run's analysis order; an analysis with no state is written as missing.</summary>
    public static string SurfacesSignature(RunDefinition run, IReadOnlyList<AnalysisSurfaceState> surfaces)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(surfaces);
        IReadOnlyList<AnalysisDef> analyses = run.Analyses ?? [];
        var w = new CanonicalWriter().Section(SurfacesComponent).Add(analyses.Count);
        foreach (AnalysisDef a in analyses)
        {
            AnalysisSurfaceState? state = surfaces.FirstOrDefault(s => s.AnalysisId == a.Id);
            w.Add(a.Id).Add(state is not null);
            if (state is not null)
                w.Add(state.IsOutOfDate).Add(state.LastTotals.Cut).Add(state.LastTotals.Fill);
        }
        return w.Sha256Hex();
    }

    /// <summary>Signature of the manual segment polylines, in the run's manual segment order.</summary>
    public static string ManualSignature(RunDefinition run, IReadOnlyDictionary<Guid, IReadOnlyList<PolyVertex>>? geometry)
    {
        ArgumentNullException.ThrowIfNull(run);
        IReadOnlyList<ManualSegmentDef> manual = run.ManualSegments ?? [];
        var w = new CanonicalWriter().Section(ManualSegmentsComponent).Add(manual.Count);
        foreach (ManualSegmentDef m in manual)
        {
            w.Add(m.Id);
            if (geometry is not null && geometry.TryGetValue(m.Id, out IReadOnlyList<PolyVertex>? vertices) && vertices is not null)
            {
                w.Add(true).Add(vertices.Count);
                foreach (PolyVertex v in vertices) w.Add(v);
            }
            else w.Add(false);
        }
        return w.Sha256Hex();
    }
}

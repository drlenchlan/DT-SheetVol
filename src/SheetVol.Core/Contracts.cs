// Frozen contracts. HANDOFF.md section 6.2 verbatim, plus the session surface from
// section 6.14 and the three records it told the orchestrator to define in Wave 0
// (RunDefinition, ValidationResult, RegenResult). docs/contracts.md is the
// readable twin; the two must match. A worker that needs a change here stops
// and reports it (HANDOFF.md section 9, stop condition 12.6 after Wave 1).

namespace SheetVol.Core;

// ----------------------------------------------------------------------------
// Section 6.2: geometry, segments, volumes, views, frames, integrator
// ----------------------------------------------------------------------------

public readonly record struct Pt(double X, double Y);
public readonly record struct PolyVertex(Pt Point, double Bulge);   // AutoCAD bulge to the next vertex

public enum SegKind { Line, Arc, Spiral }

public sealed record AlignSeg(
    SegKind Kind,
    double StartStation, double EndStation,        // raw
    Pt Start, Pt End,
    double StartDir, double EndDir,                // radians CCW from +X, direction of travel
    Pt? Center = null, double Radius = 0, bool Clockwise = false);   // Arc only

public interface IAlignmentGeometry
{
    string Name { get; }
    double StartStation { get; }
    double EndStation { get; }
    IReadOnlyList<AlignSeg> Segments { get; }      // ordered, contiguous, connected entities only
    Pt PointAt(double rawStation, double offset);  // negative = left, positive = right
    string FormatStation(double rawStation);       // honors station equations
    bool TryParseStation(string text, out double rawStation, out string? error);
}

public sealed record SegmentDef(
    Guid Id, string Label,
    double StartStation, double EndStation,        // raw, End > Start
    double OffsetLeft, double OffsetRight,         // signed, Left < Right. (-100, +100) is a 200-wide band
    string? SourceKey = null,                      // view frame key, or null
    string? TargetLayout = null);                  // layout that receives this segment's table (A14)

public sealed record Boundary(
    Guid SegmentId,
    IReadOnlyList<PolyVertex> Drawn,               // closed, true arcs
    IReadOnlyList<Pt> Tessellated);                // closed, first == last, used for volumes

public readonly record struct RawVolume(double Cut, double Fill);   // unadjusted, cubic drawing units, both >= 0

public interface IVolumeSource
{
    RawVolume SurfaceTotals { get; }
    RawVolume Bounded(IReadOnlyList<Pt> closedPolygon);
    double Area2D(IReadOnlyList<Pt> closedPolygon);                 // plan area of surface data inside the polygon, square drawing units
}

public sealed record ViewSpec(
    Guid SegmentId,
    Pt ModelCenter, double TwistRadians,
    double ModelUnitsPerPaperUnit,                 // 50 for 1"=50' with feet and inches
    double PaperWidth, double PaperHeight,
    IReadOnlyList<PolyVertex>? ClipInPaper);       // relative to the viewport center

public sealed record FrameGeom(
    string Key, string Name,
    double StartStation, double EndStation,
    Pt Center, double RotationRadians, double Width, double Height,
    string? SheetFile, string? SheetLayout);

public readonly record struct Pt3(double X, double Y, double Z);
public readonly record struct Tri3(Pt3 A, Pt3 B, Pt3 C);

public interface ITriangleSource                       // one per TIN surface
{
    IEnumerable<Tri3> Triangles(Pt windowMin, Pt windowMax);   // visible triangles overlapping the window
}

public sealed record OhwmSpec(double DefaultElevation, IReadOnlyDictionary<Guid, double> BySegment);

public sealed record ImpactResult(
    Guid SegmentId, string Material,
    double ImpactedLength,                             // drawing length units
    double ImpactedArea,                               // square drawing units
    double CutVolume,                                  // cubic drawing units, unadjusted
    IReadOnlyList<(double From, double To)> Intervals, // raw stations that have cut below OHWM
    double CutAboveOhwm, double FillBelowOhwm,         // for QA and export, not in the table
    bool TouchesSearchLimit);

// ----------------------------------------------------------------------------
// Run definition (orchestrator, Wave 0, from sections 1, 3, 6.3, 6.5, 6.6, 6.16)
// ----------------------------------------------------------------------------

public enum SegmentDriver { Stations, ViewFrames }                 // A2: Stations is the default
public enum ViewOrientation { AlongAlignment, North }               // A3
public enum ScaleMode { Fixed, FitStandard }                        // 6.6
public enum ClipMode { None, Boundary }                             // 6.6
public enum AutoMode { Off, Views, All }                            // 6.11, A4: Views is the default
public enum ReportPreset { Standard, HcfcdSegment }                 // 6.16
public enum AreaUnit { SquareFeet, Acres, SquareMetres, Hectares }  // 6.5

/// <summary>One base/comparison pair with its factors. Two analyses that share a pair share one owned volume surface (E411).</summary>
public sealed record AnalysisDef(
    Guid Id, string Name,
    string BaseSurface, string ComparisonSurface,  // surface names; handles are resolved and stored by the adapter
    double CutFactor = 1.0, double FillFactor = 1.0);

/// <summary>A user-drawn closed polyline used as-is and never regenerated (6.3).</summary>
public sealed record ManualSegmentDef(Guid Id, string Label, string PolylineHandle, string? TargetLayout = null);

/// <summary>Per-segment overrides keyed by segment GUID. Everything else comes from the run.</summary>
public sealed record SegmentOverrides(
    string? TargetLayout = null,                   // A14
    double? ModelUnitsPerPaperUnit = null,         // 6.6 per-segment scale override
    bool LockView = false,                         // E608
    double? OffsetLeft = null,                     // rev 2 (D8): the 6.14 per-segment offset fix beside a failing row
    double? OffsetRight = null);

/// <summary>What the regen draws (rev 2, D6: the three checkboxes in 6.14's left panel). Tables in layouts are driven by the template, not by these.</summary>
public sealed record DrawOptions(
    bool BoundaryPolylines = true,
    bool TableAtEachSegment = true,
    bool SummaryTable = true);

public sealed record ViewOptions(
    ViewOrientation Orientation = ViewOrientation.AlongAlignment,
    ScaleMode ScaleMode = ScaleMode.Fixed,
    double ModelUnitsPerPaperUnit = 50,
    IReadOnlyList<double>? StandardScales = null,  // FitStandard candidates, model units per paper unit
    ClipMode Clip = ClipMode.Boundary,
    double ViewOverlap = 0);                       // along the alignment at each end, drawing units

/// <summary>
/// Everything that defines a run. Persisted as JSON in the drawing (6.9) and edited by
/// SVDEFINE, SVANALYSIS, SVBREAK and the dialog alike. KnownSegments is the segment list
/// from the last plan; the planner carries GUIDs forward from it by the identity rules in
/// 6.3 (split keeps the left GUID, merge keeps the left GUID) so layouts and tables survive edits.
/// </summary>
public sealed record RunDefinition(
    string Name,
    string AlignmentName,
    SegmentDriver Driver,
    string BreakSpec,                              // 6.3 grammar; used when Driver == Stations
    string? ViewFrameGroupName,                    // used when Driver == ViewFrames
    double OffsetLeft, double OffsetRight,         // signed, Left < Right
    IReadOnlyList<AnalysisDef> Analyses,
    IReadOnlyList<ManualSegmentDef> ManualSegments,
    IReadOnlyList<SegmentDef> KnownSegments,
    IReadOnlyDictionary<Guid, SegmentOverrides> Overrides,
    ViewOptions Views,
    ReportPreset Preset = ReportPreset.Standard,
    AreaUnit AreaUnit = AreaUnit.SquareFeet,
    OhwmSpec? Ohwm = null,                         // required when Preset == HcfcdSegment
    AutoMode AutoMode = AutoMode.Views,
    int SchemaVersion = 2,                         // rev 2: DrawOptions and offset overrides added
    DrawOptions? Draw = null);                     // null = all on

// ----------------------------------------------------------------------------
// Results
// ----------------------------------------------------------------------------

public enum Severity { Info, Warning, Error }

/// <summary>One reported condition. Code is the stable SV-Exxx code from 6.13; Fix is the plain-words remedy the dialog shows beside the row (6.14).</summary>
public sealed record Problem(string Code, Severity Severity, string Message, Guid? SegmentId = null, string? Fix = null);

/// <summary>
/// Output of ISheetVolSession.Validate: pure Core planning and boundary checks, no volumes,
/// no writes (6.14). Segments is the planned list even when invalid, so the dialog can
/// show every row with its own problem.
/// </summary>
public sealed record ValidationResult(
    bool IsValid,
    IReadOnlyList<SegmentDef> Segments,
    IReadOnlyList<Boundary> Boundaries,            // one per valid segment; a segment with an Error problem has none
    IReadOnlyList<Problem> Problems);

public enum SegmentStatus { Current, OutOfDate, Error }

/// <summary>One grid row: one segment under one analysis (6.14, E916).</summary>
public sealed record SegmentResult(
    Guid SegmentId, string Label,
    double StartStation, double EndStation,        // raw
    Guid AnalysisId,
    double Area2D,                                 // square drawing units (6.5); display unit applied by presentation
    double Coverage,                               // Area2D / boundary area, 0..1
    RawVolume Raw,                                 // unadjusted, cubic drawing units
    double Cut, double Fill, double Net,           // after factors, in the run's volume unit (CY or m3); Net = Cut - Fill (A5)
    ImpactResult? Impact,                          // HcfcdSegment preset only
    SegmentStatus Status,
    IReadOnlyList<Problem> Problems);

/// <summary>SurfaceTotals − band for one analysis (6.5). Present only when above tolerance.</summary>
public sealed record OutsideLimits(Guid AnalysisId, RawVolume Raw);

/// <summary>Output of ISheetVolSession.RegenerateAsync. Succeeded == false means the drawing is untouched for this run (6.10).</summary>
public sealed record RegenResult(
    bool Succeeded,
    string RunName,
    IReadOnlyList<SegmentResult> Segments,
    IReadOnlyList<Problem> Problems,
    IReadOnlyList<OutsideLimits> OutsideLimits,    // rev 2: one per analysis above tolerance (C2's ReconcileResult is per analysis); empty when none
    bool PartitionCheckPassed,
    TimeSpan Elapsed,
    string? CsvPath, string? JsonPath);            // null when export was skipped (E506, E507)

// ----------------------------------------------------------------------------
// Rev 2 (G1 contract review, 2026-11-05): what the dialog needs from the host.
// C4 parked these behind IDialogHost in Presentation/; they belong in the one
// door between UI and engine (6.14), so they move here. Database-side members
// are implemented in Civil3D/Session, editor-side ones in UI/Host.
// ----------------------------------------------------------------------------

/// <summary>Unit system of the active drawing. Drives labels, offered area units and volume units (E914, E917, E1020).</summary>
public enum UnitSystem { Imperial, Metric }

/// <summary>The drawing the dialog is bound to. Name is the drawing's identity, normally its full path; pending edits are keyed by it (E907).</summary>
public sealed record DrawingInfo(string Name, UnitSystem Units);

/// <summary>What the change tracker knows about a run (6.11, E912). IsStale is true when the fingerprint differs from the current sources, or the host cannot tell. LastResult is the last successful regen in this session, from the dialog or a typed SVREGEN; null when none.</summary>
public sealed record RunStatus(bool IsStale, RegenResult? LastResult);

// ----------------------------------------------------------------------------
// Section 6.14: the only door between the UI and the engine
// ----------------------------------------------------------------------------

public interface ISheetVolSession
{
    IReadOnlyList<string> AlignmentNames { get; }
    IReadOnlyList<string> TinSurfaceNames { get; }
    RunDefinition? LoadRun(string runName);
    ValidationResult Validate(RunDefinition run);     // pure Core: planning and boundary checks, no volumes, no writes
    Task<RegenResult> RegenerateAsync(RunDefinition run, IProgress<double> progress, CancellationToken ct);
    void Highlight(Guid? segmentId);
    void ZoomTo(Guid segmentId);
    Task<string?> PickAlignmentAsync();
    Task<double?> PickStationAsync(string alignmentName);
    event EventHandler SourcesChanged;                // active document, object lists, staleness

    // Rev 2 (from C4's IDialogHost)
    DrawingInfo? ActiveDrawing { get; }                                   // null when no drawing is open (E901); re-read on SourcesChanged
    string FormatStation(string alignmentName, double rawStation);        // honors station equations (E208)
    (double Start, double End)? StationRange(string alignmentName);       // raw; null when the alignment is not found
    RunStatus GetRunStatus(string runName);                               // called on open and after every SourcesChanged (E912)
    bool IsCommandActive { get; }                                         // Regenerate disabled while true (E905)
    Task<RegenResult?> ExportAsync(RunDefinition run, CancellationToken ct);   // SVEXPORT: CSV and JSON only, no drawing writes; null when the run has no result yet
}

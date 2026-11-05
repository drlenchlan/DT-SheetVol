# SheetVol — Handoff Plan (v2)

A Civil 3D sheet-segment tool: station-range boundaries along an alignment drive the sheet viewports, and exact cut / fill / net tables are generated for each boundary from a base and a comparison surface.

- **Owner:** Dan Lench (LAN)
- **Orchestrator:** Fable (main Claude Code session in VS Code)
- **Workers:** Opus subagents, one per work package
- **Written:** 2026-11-05. Supersedes v1 of the same date.
- **Status:** not started. Nothing below has been built or run.

**What changed from v1**

1. Pinned to .NET 10.
2. Boundaries are built from true alignment geometry plus signed left/right offsets. They are real drawing objects.
3. Segments can be any mix of lengths. The boundary is the primary object; viewports and volumes both derive from it.
4. Surfaces are picked as base + comparison. The tool creates and owns the volume surface.
5. One command regenerates everything in place. Optional automatic update.
6. The tool can generate layouts and viewports itself, with no view frames.
7. View frames are an optional second driver. The tool adds the frame-to-viewport link that Civil 3D lacks.
8. Section 7 is an edge-case catalog by feature.
9. A modeless dialog front end for phase 1 (section 6.14), added after v2 was first issued.
10. 2D area per segment with a unit choice, SF by default and AC as the alternative (section 6.5).
11. HCFCD segment-table preset: impacted length, impacted area, cut volume and average volume below an OHWM elevation, written into rows 6 to 9 of the HCFCD table for each sheet (sections 6.15 and 6.16). This adds a triangle-overlay integrator to the core library.

---

## 0. Kickoff prompt

Paste into Claude Code at the repo root with the model set to Fable:

```text
Read HANDOFF.md in full. You are the orchestrator described in section 9.
Start at Wave 0. Do not skip gates. Dispatch Opus subagents per section 10,
each in its own git worktree, and keep NOW.md current after every merge.
Stop and report to Dan only at the stop conditions in section 12.
```

---

## 1. Concept

```text
Run
 ├─ Alignment (native or data-shortcut reference)
 ├─ Segment source:  Stations  (break list, any mix of lengths)   ← default
 │                   ViewFrames (an existing view frame group)     ← optional
 ├─ Offsets: left, right (signed, from the alignment)
 ├─ Analyses[]: base surface + comparison surface + cut/fill factors
 └─ Segments[]  ── each one owns ──►  boundary polyline (model space)
                                      layout + plan viewport (clipped or not)
                                      one volume table per analysis (paper space)
                                      match-line marks at its ends
```

One command, `SVREGEN`, rebuilds every derived object in place from the current alignment, surfaces, breaks and frames.

### 1.1 Why the tool owns the segments instead of depending on view frames

Facts from Autodesk's own documentation and forum, checked 2026-11-05:

- View frame groups are not dynamically associated with the sheets made from them. Moving a frame or a match line does not change an existing sheet; Autodesk's instruction is to recreate the sheets.
- A view frame group can be data-shortcut referenced. The reference is a read-only lightweight copy. It still has no tie to any layout viewport.
- A view frame's size cannot be changed. It comes from the template viewport and scale. So "500 ft here, 800 ft there" is only possible inside that fixed frame length, by sliding match lines.
- The .NET API cannot create view frames or sheets (Autodesk forum, accepted answer).
- Users report match lines landing at the wrong place after alignment edits.

So the Stations driver is the default: it handles any mix of lengths, follows alignment edits, and is fully testable without the Civil 3D UI. The ViewFrames driver exists for projects that already use Plan Production. In that mode the tool stores a frame-to-viewport mapping and re-applies the frame's position, rotation and stations to the viewport on every regen. That is the missing link.

### 1.2 How "dynamic" works

| Level | Trigger | What updates | Works when |
|---|---|---|---|
| L1 | `SVREGEN` (ribbon button) | Everything | Always, including `accoreconsole` |
| L2 | A tracked object changes in this session (frame grip edit, match line move, alignment edit, break edit) | Boundaries, viewports, match marks. Volume tables get an `(OUT OF DATE)` marker | Plugin loaded, `SVAUTO` = Views or All |
| L3 | Drawing opened, or a data-shortcut reference is synchronized | Stored fingerprints are compared with current sources; stale runs behave as L2 | Plugin loaded |
| Batch | `tools/regen-sheets.ps1` | `SVREGEN` in each listed drawing through `accoreconsole` | Any time |

Volumes are recomputed automatically only when `SVAUTO` = All, because surface edits are frequent and bounded volumes on large surfaces are slow.

---

## 2. Platform pin

- `SheetVol.Core` and tests: `net10.0`. `SheetVol.Civil3D` and `SheetVol.UI`: `net10.0-windows`, x64.
- `global.json` pins the .NET 10 SDK.
- **Host requirement: Civil 3D 2026.2.2 or later.** That update moved Civil 3D 2026 to the .NET 10 runtime. Civil 3D 2026 below 2026.2.2 hosts .NET 8 and cannot load this plugin. Autodesk states the API did not change in that update.
- Civil 3D 2027 hosts .NET 10 but is a binary break. It gets its own build configuration (`C3D2027`, 2027 DLLs, bundle series R26.0). Build it only if 2027 is installed on the dev machine; otherwise defer.
- Do not use `BinaryFormatter`. Use `System.Text.Json`.

Wave 0 must confirm the installed Civil 3D build number and record it in `NOW.md`.

---

## 3. Assumptions made without Dan's answer

Each is a config value or an isolated module. Dan corrects them in `NOW.md`.

| # | Assumption |
|---|---|
| A1 | Everything runs in one drawing (the sheet drawing) that holds the alignment and both surfaces natively or as data-shortcut references, with layouts in that same drawing. One-layout-per-file output is out of scope for now |
| A2 | Default driver is Stations. ViewFrames is opt-in per run |
| A3 | Viewports read with stationing increasing left to right (twist follows the segment chord). North-up is a config switch |
| A4 | `SVAUTO` default is Views |
| A5 | Net = Cut − Fill, shown as `50 CY (CUT)` / `50 CY (FILL)` |
| A6 | Table total row = sum of the displayed (rounded) rows, so sheets add up to the summary. The unrounded total goes in the export and in a footnote when it differs |
| A7 | Table style, layers, text height, template path and anchor names come from config. Built-in defaults are placeholders until Dan supplies LAN names |
| A8 | Plan-only sheets. Profile viewports and Sheet Set Manager integration are out of scope |
| A9 | The engine has no dialogs and stays fully scriptable. A modeless dialog (`SVDIALOG`, section 6.14) and the ribbon live in a separate UI assembly |
| A10 | "Below OHWM" means the plan area where existing ground (the base surface) is lower than the OHWM elevation. All cut inside that area counts. Excavation under banks that stand above OHWM is not counted; it is reported separately in the export |
| A11 | OHWM is one elevation per run with an optional override per segment. A station-interpolated OHWM profile or an OHWM surface is a later option |
| A12 | HCFCD formats: length in whole feet, area in acres to 2 decimals, volume in whole cubic yards, average to 2 decimals. The template shows `XX` for acres, so the decimals need confirming |
| A13 | In the HCFCD table the tool fills rows 6 to 9 only. Center coordinates and the other-aquatic-feature rows stay manual |
| A14 | Each segment can name the layout its table goes on. With no layout named, the table is placed in model space at the segment |

---

## 4. Scope

**In.** Stations driver with mixed break lists, manual polyline segments, ViewFrames driver, boundary generation from alignment geometry with signed offsets, multiple base/comparison analyses per run, layout and viewport generation from a template, viewport clipping to the boundary, per-sheet paper-space tables, master table, match-line marks, title-block attribute fill, single-command regen, change tracking and auto-update, adoption of existing layouts, CSV/JSON export, batch regen script, ribbon, bundle, modeless dialog, HCFCD segment-table preset with impacts below an OHWM elevation.

**Out.** HCFCD center coordinates and other-aquatic-feature rows, profile or section viewports, Sheet Set Manager, one-DWG-per-sheet output, creating view frames, variable offsets within one segment, material or strata breakdown, mass haul, grid volume surfaces, surfaces or alignments that exist only inside an xref, Civil 3D builds below 2026.2.2.

---

## 5. API facts

### 5.1 Verified against Autodesk's Civil 3D 2026 API reference, the AutoCAD .NET guide, or Autodesk release notes

**Volumes and surfaces**
- `Surface.GetBoundedVolumes(Point3dCollection)` and `(Point3dCollection, double datum)` return `SurfaceVolumeInfo` with `Cut`, `Fill`, `Net`. Polygon must be closed (first point equals last) with more than 3 points, else `ArgumentException`.
- `TinVolumeSurface.Create(string, ObjectId baseId, ObjectId comparisonId)` plus a four-argument overload. The surface is added to the database of the base surface.
- `TinVolumeSurface.GetVolumeProperties()` returns `VolumeSurfaceProperties` (`Adjusted*`/`Unadjusted*` cut, fill, net; `CutFactor`; `FillFactor`; `BaseSurface`; `ComparisonSurface`). `InvalidOperationException` when there are no triangles.
- `TinVolumeSurface.CutFactor` is get/set.
- `Surface.IsOutOfDate`, `Rebuild()`, `AutoRebuild`, `FindElevationAtXY`, `IsVolumeSurface`, `Lock`.
- `Entity.IsReferenceObject`, `IsReferenceStale`, `IsReferenceValid`, `IsReadOnlyReferenceObject`.

**Alignment**
- `Alignment.Entities.GetEntityByOrder(int)` returns entities in path order and ignores disconnected ones. `ArgumentOutOfRangeException` past the end.
- `AlignmentSubEntity`: `StartStation`, `EndStation`, `StartPoint`, `EndPoint`, `Length`. `AlignmentSubEntityArc` adds `CenterPoint`, `Radius`, `Clockwise`, `Delta`, `StartDirection`, `ReverseCurve`, `GreaterThan180`.
- `Alignment.PointLocation(station, offset, ref easting, ref northing)`; `PointNotOnEntityException` outside the alignment. `StationOffset(...)` is the inverse.
- `Alignment.GetStationSet(StationTypes)` returns `Station[]`. `StationTypes` is a flags enum including `GeometryPoint`, `Equation`, `PIPoint`.
- `Alignment.GetStationStringWithEquations(double rawStation)`; `ArgumentException` out of range.

**View frames**
- `ViewFrameGroup.GetViewFrameIds()`, `GetMatchLineIds()`.
- `ViewFrame`: `StartStation`, `EndStation` (raw), `AlignmentId`, `GroupId`, `Name`, `Sheet`, `SheetSet`. No rotation, size or corner properties.
- `MatchLine`: `Station` (raw), `Number`.

**Layouts and viewports**
- `LayoutManager.Current.CreateLayout(name)`. Autodesk's .NET guide has an "import a layout from an external drawing" sample built on `WblockCloneObjects` and `Layout.CopyFrom`.
- `Viewport`: `CustomScale`, `Locked`, `TwistAngle`, `ViewCenter`, `ViewTarget`, `NonRectClipEntityId`, `NonRectClipOn`.

**Hosting**
- `accoreconsole.exe` switches `/i`, `/s`, `/product C3D`, `/l`, `/isolate`, `/p`, `/loadmodule`. Custom code must be in a trusted location.

### 5.2 From community sources only. Treat as hypotheses

- View frame geometry can be read by `Explode` (frame → block → polyline → lines; first line is the top edge, left to right). Autodesk forum, 2014.
- `Viewport.ViewCenter` is in display coordinates, which rotate with the twist. Setting `ViewTarget` to the model point and `ViewCenter` to (0,0) avoids the trap. Autodesk forum.
- `accoreconsole` may need `/ld AecBase.dbx` for Civil objects.

### 5.3 Probes. Answers go in `docs/api-findings.md` with printed evidence

| ID | Question | Needs Dan |
|---|---|---|
| P1 | On a `TinVolumeSurface`, which of `Cut`/`Fill` from `GetBoundedVolumes` is real cut? | |
| P2 | Result units: cubic drawing units or cubic yards? | |
| P3 | Do bounded results include the surface's factors? Is `FillFactor` settable like `CutFactor`? | |
| P4 | Polygon partly or wholly outside the surface, and over holes: result or exception? | |
| P5 | Does winding (CW/CCW) matter? | |
| P6 | `TinVolumeSurface.Create` with data-shortcut base and comparison: allowed? Does it rebuild after the references synchronize? | |
| P7 | Sub-entity access: count and indexer on `AlignmentEntity`, spiral and compound group layouts, meaning of `Clockwise`. Cross-check every analytic offset point against `PointLocation` to 1e-6 | |
| P8 | Station equations: raw-to-displayed and displayed-to-raw conversion; behavior when a displayed station is ambiguous | |
| P9 | Headless: can TIN surfaces, alignments and volume surfaces be created in `accoreconsole /product C3D`? Required switches, `SECURELOAD`, prior `NETLOAD AeccDbMgd.dll`? | |
| P10 | `CivilDocument.GetCivilDocument(db)` vs `CivilApplication.ActiveDocument` in Core Console | |
| P11 | Import a layout from a DWT headless; plot settings and contents intact after save and reopen | |
| P12 | Create and update a viewport without making its layout current: `On`, `ViewTarget`/`ViewCenter`/`TwistAngle`/`CustomScale`, non-rectangular clip. Confirm model-to-paper mapping numerically | |
| P13 | AutoCAD `Table` created and updated in a layout headless | |
| P14 | `ViewFrame`: `Explode` on native and data-shortcut frames; `Sheet` string format; stations of first and last frames; how Civil 3D marks a "Plan" viewport | Yes: needs a drawing with a view frame group |
| P15 | Which events fire for a frame grip edit, match line move, alignment edit and reference synchronize (`Database.ObjectModified`, `Document.CommandEnded`, `Application.Idle`) | Yes: interactive |
| P16 | Installed Civil 3D build is 2026.2.2 or later, and a `net10.0-windows` hello command loads in both `acad.exe` and `accoreconsole.exe` | |
| P17 | Assigning an annotation scale to a viewport; adding a missing scale to the drawing's scale list | |
| P18 | `IsOutOfDate`, `Rebuild()` and `IsReferenceStale` behavior on native vs reference surfaces | |
| P19 | `FindElevationAtXY` outside the surface: exception type (used for coverage sampling) | |
| P20 | Is layout creation rolled back by an aborted transaction and by one `UNDO`? | |
| P22 | Does `Net` from `GetBoundedVolumes(polygon, datum)` change by exactly plan area × datum shift? On Fixture D with a known boundary, `|Net(0) − Net(1)|` should equal the polygon area. Compare with the Volumes Dashboard's 2D Area for the same boundary, including a boundary that runs off the surface | Dashboard comparison only |
| P23 | Reading visible triangles from a `TinSurface`: the API, the cost at 1 million triangles, how hidden triangles, boundaries and masks appear, and whether it works on data-shortcut reference surfaces and corridor surfaces | |
| P24 | AutoCAD `Table`: build the HCFCD layout with merged cells, find a row by its label text, update one cell without disturbing the others, in model space and in a layout | |
| P21 | Modeless WPF window hosted in Civil 3D on .NET 10: how to show it, how to run database work from it in command context with a document lock, how to read the color theme, how to highlight an entity transiently | Yes: interactive |

Reference order for API research: `https://civapidocs.com/2026.htm` first (Dan's standing primary reference), then Autodesk's 2026 API reference and Developer's Guide. Observed behavior on the fixtures overrides documentation.

---

## 6. Design

### 6.1 Solution layout

```text
sheetvol/
  CLAUDE.md, HANDOFF.md, NOW.md, global.json
  .claude/agents/c3d-dev.md
  src/
    SheetVol.Core/            net10.0, zero Autodesk references
      Planning/  Geometry/  Volumes/  Output/  Views/  Tracking/  Presentation/
    SheetVol.Civil3D/         net10.0-windows. AcCoreMgd, AcDbMgd, AecBaseMgd, AeccDbMgd only
      Adapters/  WritersModel/  WritersPaper/  Frames/  Commands/  Session/  Auto/  SelfTest/
    SheetVol.UI/              net10.0-windows, WPF. May reference AcMgd, AdWindows
      Dialog/  Host/  Ribbon/
  tests/SheetVol.Core.Tests/
  tools/                      build.ps1, test-headless.ps1, regen-sheets.ps1, scripts/, fixtures/
  bundle/SheetVol.bundle/
  docs/                       contracts.md, api-findings.md
```

`SheetVol.Civil3D` must never reference `AcMgd.dll`; it has to load in Core Console. Use `Autodesk.AutoCAD.ApplicationServices.Core.Application`. Resolve the Civil 3D install directory from an MSBuild property with a registry fallback.

### 6.2 Frozen contracts (`docs/contracts.md`)

```csharp
namespace SheetVol.Core;

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
    string? SourceKey = null, string? TargetLayout = null);   // TargetLayout: layout that receives this segment's table.                     // view frame key, or null

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
```

The adapters absorb every quirk found by the probes (sign, units, factors, `Clockwise` meaning). Core never sees them.

### 6.3 Break specification (Stations driver)

A comma-separated list. Segments are the intervals between consecutive breaks.

| Token | Meaning |
|---|---|
| `5+00`, `500`, `START`, `END` | One break |
| `A..B@L` | Breaks every `L` from `A` to `B`, including both ends |
| `A..B/N` | `N` equal segments from `A` to `B` |
| `skip A..B` | No segment between `A` and `B` |

Examples: `START, 5+00, 13+00, 18+00, END` gives 500, 800, 500, remainder. `START..20+00@500, 28+00, skip 28+00..31+50, 31+50..END@400` mixes all forms.

`START` and `END` follow the alignment. `SVBREAK` adds, moves or removes a break by pick or typed station and rewrites the list. A segment keeps its GUID across edits: splitting keeps the GUID on the left part, merging keeps the left segment's GUID. Labels and layout numbers are derived, never identity.

A run may also hold **manual segments**: a user-drawn closed polyline with a typed label, used as-is and never regenerated.

### 6.4 Boundary generation (Core.Geometry)

1. Build one global, sorted station list per run: all breaks, all `AlignSeg` ends, arc subdivisions sized so the mid-ordinate on the larger offset radius is at most `MidOrdinate` (default 0.01 drawing units), and spiral subdivisions to the same tolerance using `PointAt`.
2. For each offset line (left, right), compute one vertex per station in that list. Lines and arcs are computed analytically from `AlignSeg`; spirals use `PointAt`. Every segment and the whole-band polygon take their lateral vertices from this one list. This is what makes the partition check exact.
3. Cut lines at a break are the straight segment between the left and right offset points at that station. Computed once per break and shared by both neighbours.
4. `Drawn` keeps arcs as bulges (concentric arc, radius `R ± offset`). `Tessellated` is the vertex list.
5. At a tangent-to-tangent angle point with no curve, join each offset line by mitre (line-line intersection). A break exactly on the angle point cuts along the bisector. Deflection above `MitreLimitDeg` (default 150) is an error.
6. Validate every boundary: closed, at least 4 vertices, no self-intersection, positive area, inner offset radius greater than zero on every arc. Never compute on an invalid boundary.

### 6.5 Analyses and volumes

- An analysis is a name, a base TIN surface, a comparison TIN surface, a cut factor and a fill factor. Picked the same way as in the Volumes Dashboard.
- The tool creates and owns one `TinVolumeSurface` per distinct base/comparison pair, named `SV-<base>-<comparison>`, with a no-display style and layer from config. It reuses an existing one it created; it never edits a volume surface it does not own. Factors are written to the owned surface so the Volumes Dashboard agrees with the tables.
- Per segment and analysis: `Bounded(Tessellated)`, then factors, then unit conversion.
- **Partition check (every regen):** when no two segment boundaries overlap, the sum of segments equals the whole-band polygon within `max(0.5 ft³, 1e-9 × total)`. Failure is a bug: abort, write nothing.
- **Outside-limits row:** `SurfaceTotals − band`. Shown in the master table when above tolerance. With explicit offsets this is expected, not an error.
- **2D area:** per segment and analysis, the plan area of volume-surface data inside the boundary. This is the same quantity the Volumes Dashboard calls 2D Area. Computed exactly from two bounded-volume calls at different datums: `|Net(d1) − Net(d2)| / |d2 − d1|`, because the net volume against a datum changes by area × datum shift. Probe P22 must confirm this before it is relied on. The integrator in 6.15 computes the same area independently, as the plan area where both surfaces exist inside the boundary, and the two must agree on every regen.
- **Area unit:** a run setting. Imperial drawings: `SF` (default) or `AC` (÷ 43,560). Metric drawings: `m²` (default) or `ha` (÷ 10,000). Display decimals from config, default 0 for SF and m², 2 for AC and ha. Raw square drawing units are always stored and exported; the unit only changes display.
- **Coverage:** `Area2D ÷ boundary area`. Below 100% the master table flags the segment so "zero because identical" is distinguishable from "zero because no surface data".

### 6.6 Views (Core.Views)

For each segment, from the display polygon (boundary extended by `ViewOverlap` along the alignment at each end):

- Twist: `−bearing(chord from start to end)` in AlongAlignment mode; 0 in North mode. If start and end coincide, use the tangent at mid-station.
- Model center and required size: oriented bounding box of the display polygon in the rotated frame.
- Scale: `Fixed` (error if the box does not fit the template viewport) or `FitStandard` (largest scale in `StandardScales` that fits). Per-segment override allowed.
- Clip: `None` or `Boundary` (the boundary transformed to paper space, arcs preserved).
- Apply as `ViewTarget = model center`, `ViewCenter = (0,0)`, `TwistAngle`, `CustomScale = 1 / ModelUnitsPerPaperUnit`. Confirm with P12 before relying on it.

In ViewFrames mode the view comes from `FrameGeom` instead: center, rotation and width of the frame; stations from the frame's match lines.

### 6.7 Template-driven paper space

- Config names a DWT and a layout in it. New layouts are clones of that layout.
- Plan viewport: the viewport on layer `SV-VPORT`; if none, the largest floating viewport.
- Table anchor: a block reference named `SV_TABLE_ANCHOR` (insertion point = table top-left). If absent, `TablePaperPoint` from config. If neither, error.
- Optional title-block attribute fill by tag map, for example `STA_FROM`, `STA_TO`, `SHEET_NO`.
- After creation the tool updates viewport view, clip and table cell text only. It never moves a table or viewport the user repositioned on paper, and never deletes a layout.

### 6.8 Outputs

**Per-sheet table**, one 3-row block per analysis:

| EARTHWORK STA 0+00 TO STA 5+00 | |
|---|---|
| EG vs FG | |
| CUT | 100 CY |
| FILL | 50 CY |
| NET | 50 CY (CUT) |

**Master table** (model space or a named layout): `SHEET | BEGIN STA | END STA | 2D AREA | CUT | FILL | NET` per analysis, optional `OUTSIDE LIMITS` row, `TOTAL` row, footer with surfaces, factors and any coverage flags. The area column header carries the unit.

The per-sheet table gets an `AREA` row only when `AreaInSegmentTable` is on in config (default off). CSV and JSON carry the raw area in square drawing units plus the display value and its unit.

**Boundary polylines**, **match-line marks** (line plus `MATCH LINE STA x — SEE SHEET n` text, optional), **CSV and JSON** beside the drawing (invariant culture, unrounded values, schema version).

### 6.9 Persistence and identity

Named Objects Dictionary → `DBDictionary "SHEETVOL"` → one `Xrecord` per run. JSON chunked into strings of 250 characters or fewer per `TypedValue`. Stores the run definition, per-segment handles (boundary, layout, viewport, clip, tables, marks), source references by handle **and** name, `schemaVersion`, and the last source fingerprint. A plugin older than the stored schema refuses to modify the run.

### 6.10 Regen pipeline (`SVREGEN`)

1. Resolve references. Fail early with the missing object's name.
2. Read geometry and frames. Plan segments. Build and validate boundaries. Detect overlaps.
3. Ensure and rebuild owned volume surfaces. Compute volumes. Run the integrator (6.15) for the cross-check and for the preset's quantities. Reconcile.
4. Compute views.
5. Apply in one transaction under a document lock: boundaries, marks, layouts, viewports, tables, master table, persistence.
6. Write exports. A file-write failure is a warning, not a rollback.

Steps 1–4 write nothing. Any failure before commit leaves the drawing untouched. Objects on locked layers that the tool owns are unlocked and relocked. One `UNDO` reverts one regen. If P20 shows layout creation cannot be rolled back, create missing layouts in a first small transaction, then apply atomically.

### 6.11 Change tracking and auto-update (Core.Tracking + Civil3D/Auto)

- Fingerprint per run: alignment geometry signature, break spec, offsets, frame signatures (stations and corners), and per analysis the owned volume surface's `IsOutOfDate` state and last whole-surface totals.
- On document open or activation and after any command that leaves tracked objects modified: compare fingerprints. Different means stale.
- Event wiring is thin: `Database.ObjectModified`/`ObjectErased` only set a dirty flag. Work happens later from `Document.CommandEnded` via a one-shot `Application.Idle`, never inside the modified handler, never during `U`, `UNDO`, `REDO` or the tool's own commands.
- `SVAUTO`: `Off` (report only), `Views` (boundaries, viewports, marks update; tables get `(OUT OF DATE)`), `All`.
- All decision logic is pure functions in Core so it is unit-tested without events.

### 6.12 Commands

| Command | Does |
|---|---|
| `SVDEFINE` | Create or edit a run: alignment, driver, break spec or view frame group, offsets, template options |
| `SVANALYSIS` | Add or remove an analysis: base, comparison, factors |
| `SVBREAK` | Add, move or remove a break; convert a segment to manual |
| `SVADOPT` | Link existing layouts and viewports to segments. Matches by `ViewFrame.Sheet`, then by nearest view center and twist, then asks |
| `SVREGEN` | Regenerate all runs. No prompts. The single click |
| `SVSTATUS` | Clean or stale per run, warnings, coverage flags, orphaned layouts |
| `SVAUTO` | Set auto-update mode |
| `SVEXPORT` | CSV and JSON |
| `SVPURGE` | Delete orphaned layouts after confirmation |
| `SVCLEAR` | Remove a run and its owned model-space objects. Layouts kept unless confirmed |
| `SVSELFTEST` | Build fixtures, run acceptance checks, write `selftest-result.json` |

`SVDIALOG` (in `SheetVol.UI`) opens the modeless dialog described in 6.14.

Ribbon (`SheetVol.UI`): Volumes dialog, Define, **Regenerate** (large button), Status, Auto mode, Export. No engine logic in the UI assembly.

### 6.13 Rules for every worker

- All database access in a transaction; commit explicitly; never dispose objects from `tr.GetObject`.
- Catch AutoCAD and Civil exceptions at the command boundary, print one clear line with a stable code (`SV-Exxx`), abort the transaction.
- No `AcMgd`, WPF, WinForms or message boxes outside `SheetVol.UI`.
- No style, layer, text height or path literals outside config defaults.
- No silent fallbacks and no silently wrong numbers. When in doubt, fail the run and say why.
- Geometry in WCS. Points picked from the user are transformed from the current UCS.

### 6.14 Dialog (`SVDIALOG`)

The phase 1 front end: alignment, offsets, base and comparison surfaces, and an editable station table with mixed lengths, producing volumes. It edits the same run definition as `SVDEFINE`. Nothing the dialog does is unavailable from the command line.

**Pattern.** A modeless, resizable WPF window in the pattern Civil 3D 2027 uses for its Horizontal Regression Analysis vista: a chart on top linked to an editable table, row selection cross-highlighted in the drawing, Zoom to from the table. It follows the AutoCAD color theme with dark and light resource dictionaries. The styling is SheetVol's own in that pattern. Do not copy Autodesk control templates, icons or fonts.

**Design reference.** Four artboards: "Volumes current", "Out of date, one error", "HCFCD preset" and "HCFCD table on the sheet". Dan exports them as PNG to `docs/ui/`. The layout in words, so workers do not depend on the images:

- **Left panel, 328 wide.**
  - Alignment: dropdown, pick-in-drawing button, station range readout.
  - Boundary offsets: left, right, "same on both sides".
  - Surfaces to compare: base, comparison, cut factor, fill factor, "add another comparison".
  - Fill stations by interval: every / from / to, "Fill table".
  - Draw in the drawing: boundary polylines, table at each segment, summary table.
- **Main area.**
  - Strip chart "Cut and fill by segment": segment width proportional to length, fill above the axis, cut below, station labels at each break, selected segment outlined.
  - Segments grid: #, begin station and end station (editable), length, 2D area, cut, fill, net with a CUT or FILL tag, coverage, status chip (Current, Out of date, Error). Total row. One reconciliation line under the grid.
  - The 2D area column header holds a unit dropdown: SF (default) and AC, or m² and ha in a metric drawing. Changing it reformats the grid at once and sets the run's area unit for the drawn tables and export.
  - With the HCFCD preset (6.16) the left panel gains a Report selector and an OHWM elevation field next to the offsets, and the surfaces card is labelled table row, existing, design. The grid columns become: #, begin, end, sheet (layout), OHWM elevation, impacted length, impacted area with the unit dropdown (acres by default), cut volume, average, status. The chart shows cut volume per segment with a strip under it marking the stations that have cut.
  - Grid toolbar: Add station, Pick in drawing, Remove, Zoom to.
- **Footer.** "Regenerate when the alignment or stations change" checkbox, Export CSV, Close, Regenerate (primary).
- **Problems.** A stale run shows one banner above the chart. A failing row is tinted and carries its message directly under it in plain words with the fix beside it (for example a per-segment offset field and Apply). Totals are hidden until every segment has a result.

**Architecture.**

- View models live in `Core/Presentation` with no WPF or Autodesk types (plain `INotifyPropertyChanged`), so they are unit-tested with `dotnet test`.
- `SheetVol.UI/Dialog` holds XAML and thin code-behind only.
- The UI reaches the engine only through `ISheetVolSession`, which is part of the frozen contracts. The orchestrator defines `RunDefinition`, `ValidationResult` and `RegenResult` in Wave 0 from sections 1 and 6.

```csharp
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
}
```

- Database-side members are implemented in `Civil3D/Session`. Editor, pick and highlight members are implemented in `SheetVol.UI/Host`.
- **Live validation.** Every edit re-runs `Validate` only, so station and offset errors appear immediately. Volumes are computed only by Regenerate or auto-regenerate.
- **Document rules.** All database work runs on the main thread in the document's command context under a document lock. The dialog never opens database objects itself. Regenerate is disabled while a command is active. A long regen shows progress and Cancel.

### 6.15 Integrator and impacts below OHWM (Core.Volumes)

**Why an integrator.** The HCFCD rows need cut-only area, cut-only volume and the stations where cut occurs, all limited to ground below an elevation. `GetBoundedVolumes` returns none of those except total cut inside a polygon. The core library therefore integrates the two TINs itself, and Civil 3D's bounded volume becomes the cross-check.

**Inputs.** Visible triangles of the base surface (existing ground) and the comparison surface (design) from `ITriangleSource`, the segment's tessellated boundary, the OHWM elevation for the segment, and `MinCutDepth` (default 0).

**Method.**

1. Translate all coordinates to a local origin first. State-plane coordinates lose precision in area math otherwise.
2. Index the base triangles on a grid. Intersect each comparison triangle with each overlapping base triangle (convex against convex). On every resulting piece both surfaces are planar, so depth `d = base − comparison` is linear.
3. Clip the piece to the segment boundary.
4. Accumulate band totals: plan area, cut volume where `d > 0`, fill volume where `d < 0`.
5. Clip to `base < OHWM`, then to `d > MinCutDepth`. Both are half-plane clips on the piece. On what remains, accumulate impacted area, cut volume (area × `d` at the centroid) and the station interval spanned by its vertices.
6. Impacted length is the total length of the union of those station intervals, clamped to the segment. Gaps shorter than `GapTolerance` (default 0) are bridged.
7. For QA only, also accumulate cut where `base ≥ OHWM` and fill where `base < OHWM`.

Station of a point is computed in Core from `AlignSeg` geometry (closed form on lines and arcs, iterative on spirals) and cross-checked against `Alignment.StationOffset` in the self-test.

**Quantities per segment and material.**

| Quantity | Definition | HCFCD row |
|---|---|---|
| Impacted length | Stations inside the segment that have cut below OHWM, measured along the alignment | 6 |
| Impacted area | Plan area of cut below OHWM | 7 |
| Cut volume | Volume of that cut | 9, first value |
| Average | Cut volume ÷ impacted length, from unrounded values. `N/A` when the length is 0 | 9, second value |

**Checks on every regen.**

- Integrator band cut and fill must equal `GetBoundedVolumes` on the owned volume surface for the same polygon, within `max(0.5 ft³, 1e-4 × total)` on project data and 1e-9 relative on fixtures. A mismatch fails the run and prints both numbers.
- If ground below OHWM reaches a lateral search limit, the segment is flagged: the offsets are too narrow.

The standard cut/fill/net preset keeps `GetBoundedVolumes` as its reported number and uses the integrator for 2D area and as the cross-check.

### 6.16 Report presets and the HCFCD table

A run has a report preset: `Standard` (cut, fill, net, 2D area) or `HcfcdSegment`. The preset selects the quantities, the dialog grid columns, the default area unit (acres for HCFCD) and the table template.

**Table template.** `templates/hcfcd-segment-table.json` describes the HCFCD layout: rows, merges, fixed text and which cells are bound. Table style, layer and text height come from config.

**Binding by label, not by position.** On regen the tool finds each bound value by the label text in its row.

| Label starts with | Value written |
|---|---|
| `TOTAL IMPACTED LENGTH (BELOW OHWM)` | Impacted length, `#,##0` |
| `TOTAL IMPACTED AREA (BELOW OHWM)` | Impacted area in acres, `0.00` |
| The material name, for example `SEDIMENT` | Cut volume `#,##0`, then average `0.00` or `N/A` |

Only those cells are written. Center coordinates, the other-aquatic-feature rows and any row the user adds or removes are left alone. A label that cannot be found is reported for that segment and the remaining values still update.

**Placement.** One table per segment. If the segment names a layout (`TargetLayout`), the table goes in that layout's paper space at the `SV_TABLE_ANCHOR` block or the configured point. Otherwise it goes in model space at the segment. The table is created once and only updated afterwards, so a table the user moved stays where it is.

**More than one material.** Each analysis adds one row under the `TOTAL CUT MATERIAL` header when the table is first created. The row label is the analysis name.

---

## 7. Edge cases by feature

Each row is a requirement. Workers implement the behavior and cover it with the test type shown (U = Core unit test, H = headless self-test, M = Dan's manual check).

### 7.1 Platform

| ID | Case | Required behavior | Test |
|---|---|---|---|
| E101 | Civil 3D 2026 below 2026.2.2 | Cannot load (.NET 8 host). README and bundle notes state the minimum build; Wave 0 verifies the dev machine | M |
| E102 | Civil 3D 2027 | Separate build against 2027 DLLs; never ship the 2026 build to R26.0 | M |
| E103 | Engine loaded in Core Console | Loads and runs every non-UI command | H |
| E104 | Drawing opened without the plugin | All outputs are plain AutoCAD objects and still plot. Stale state is caught on next open with the plugin (fingerprint) | M |
| E105 | Stored schema newer than plugin | Refuse to modify the run; report versions | U |

### 7.2 Alignment geometry and boundaries

| ID | Case | Required behavior | Test |
|---|---|---|---|
| E201 | Offset on the inside of a curve ≥ radius | Error naming segment, curve station and the maximum usable offset | U, H |
| E202 | Boundary self-intersects for any other reason | Error; no volume computed | U |
| E203 | Reverse and compound curves | Bulge sign and inner/outer side switch correctly at the common point | U |
| E204 | Spirals (simple, SCS groups) | Tessellated to `MidOrdinate` from `PointAt`; analytic cross-check on arcs only | U, H |
| E205 | Tangent-tangent angle point | Mitre join; bisector cut when a break sits on it; error above `MitreLimitDeg` | U |
| E206 | Break exactly on PC, PT or another geometry point | No zero-length edge; stations within 1e-6 are merged | U |
| E207 | Start station not zero, negative stations | Work unchanged; all math on raw stations | U |
| E208 | Station equations | Input parsed as displayed station when unambiguous; ambiguous value is rejected with both candidates listed; labels show equation stationing | U, H |
| E209 | Asymmetric offsets, band entirely on one side (both offsets same sign) | Allowed when Left < Right | U |
| E210 | Left ≥ Right, or zero width | Rejected at definition | U |
| E211 | Alignment edited, lengthened or shortened | Breaks keep their station values; breaks beyond the new end are reported and their segments marked invalid, not silently dropped; `END` follows | U, H |
| E212 | Alignment reversed or re-stationed | Fingerprint changes; regen proceeds by station value and prints a warning that stationing changed | H |
| E213 | Alignment erased, or reference broken | Regen fails at step 1; existing outputs untouched | H |
| E214 | Alignment with disconnected or unsolved entities | Error listing the gap station | H |
| E215 | Looping or self-crossing alignment | Pairwise overlap test between non-adjacent boundaries; overlapping area reported per pair; partition check skipped for that run with a stated reason; footnote on master table | U |
| E216 | Metric drawing | Defaults scale with units (interval, tolerance, m³) | U |
| E217 | Non-world UCS active | Output identical to WCS run | H |
| E218 | Very small radius curb-return alignments | Same rules as E201; no special casing | U |
| E219 | Hundreds of segments | Regen stays linear; progress shown; Esc cancels cleanly before commit | M |

### 7.3 Segment definition

| ID | Case | Required behavior | Test |
|---|---|---|---|
| E301 | Mixed lengths in one run | Any break list accepted; each segment independent | U |
| E302 | `A..B@L` leaves a short remainder | Kept as its own segment unless shorter than `MinSegmentLength`, then merged into the previous one. Reported either way | U |
| E303 | `skip` gap | No segment, viewport or table; its volume appears under OUTSIDE LIMITS | U |
| E304 | Overlapping station ranges | Rejected for station segments. Sheet overlap is `ViewOverlap`, which affects the viewport only, never the volume | U |
| E305 | Break outside the alignment | Rejected with the valid range | U |
| E306 | Duplicate or unsorted breaks | Sorted and de-duplicated | U |
| E307 | Break inserted mid-run | Left part keeps GUID, layout and tables; right part is new; later sheets renumber if `RenumberLayouts` is on, using a two-pass rename to avoid collisions | U, H |
| E308 | Break removed | Left segment survives; right segment's boundary, marks and tables are erased; its layout becomes an orphan | H |
| E309 | Segment too long for the viewport at fixed scale | Error stating the maximum length that fits, or auto-select scale in `FitStandard` mode | U |
| E310 | Segment wraps a tight curve (large deflection) | Fit test uses the oriented bounding box, not chord length | U |
| E311 | Manual polyline segment edited by the user | Used as drawn; open, self-intersecting or erased polyline is an error for that segment | H |
| E312 | Manual segment overlaps station segments | Reported as in E215 | U |

### 7.4 Surfaces and volumes

| ID | Case | Required behavior | Test |
|---|---|---|---|
| E401 | Base and comparison are the same surface | Rejected | H |
| E402 | Grid surface, or volume surface, picked as base or comparison | Rejected with the reason | H |
| E403 | Surface with no triangles | Clear error, not an unhandled exception | H |
| E404 | Source surface out of date | Native: rebuild if `RebuildSources` is on, else fail with the surface name. Reference: report stale reference and fail; never compute on stale data | H |
| E405 | Surfaces do not overlap inside a segment | Volumes 0, coverage 0%, flagged | H |
| E406 | Holes or hide boundaries inside a segment | Volume excludes them; coverage below 100% flagged | H |
| E407 | Boundary extends past the surface | Result per P4. If the API fails, clip to the surface border first | H |
| E408 | Surfaces identical in a segment | Exactly 0 displayed; coverage 100% | H |
| E409 | Triangulation noise near zero | Values below `ZeroTolerance` display as 0; exports keep the raw value | U |
| E410 | Cut or fill factor not 1.0 | Applied exactly once; factors shown in the table footer | H |
| E411 | Two analyses share a base/comparison pair | One owned volume surface, reused | H |
| E412 | Owned volume surface erased or renamed by a user | Recreated on regen; name collision resolved with a suffix | H |
| E413 | Rounded rows do not sum to the rounded true total | Total follows A6; footnote shows the unrounded total | U |
| E414 | Net exactly zero | Shown as `0 CY`, no CUT/FILL tag | U |
| E415 | Data-shortcut surfaces | Per P6. If volume surfaces cannot be built from references, stop condition 12.2 | H |
| E416 | Very large surfaces | Progress and cancel; no partial writes | M |
| E417 | Lateral edge slices through material | Volume matches analytic value on Fixture A and B half-band tests | H |
| E418 | Area unit switched | Display only. No recompute; raw area unchanged; drawn tables are marked out of date until regen | U |
| E419 | Surface does not fill the boundary (edge of surface, holes) | 2D area is the surface area inside the boundary, smaller than the boundary area; coverage below 100% | H |
| E420 | Rounded acre rows do not sum to the rounded total | Same rule as volumes (A6): total = sum of displayed rows, unrounded total in export and footnote | U |
| E421 | Two analyses whose surfaces cover different extents | 2D area is per analysis, not per segment alone | H |

### 7.5 Tables and exports

| ID | Case | Required behavior | Test |
|---|---|---|---|
| E501 | Table style or layer from config missing | Fail with the missing name; do not fall back silently | H |
| E502 | Table moved or restyled by the user | Position and style kept; only cell text updates | H |
| E503 | Table erased by the user | Recreated at the anchor | H |
| E504 | Table on a locked or frozen layer | Unlock, update, relock; frozen layers are left frozen | H |
| E505 | Analysis added or removed | Table grows or shrinks by one block in place | H |
| E506 | Drawing never saved | Skip file export with a message; everything else runs | H |
| E507 | Export file locked or folder read-only | Warning; drawing changes are kept | H |
| E508 | Non-US regional settings | Exports use invariant culture; tables use configured format | U |

### 7.6 Layouts and viewports

| ID | Case | Required behavior | Test |
|---|---|---|---|
| E601 | Template file, layout, plan viewport or table anchor missing | Fail before any write, naming what is missing | H |
| E602 | Template layout holds several viewports | Only the plan viewport is driven; others untouched | H |
| E603 | Generated layout name collides, is too long, or has invalid characters | Sanitize and de-duplicate deterministically; report the final name | U |
| E604 | Viewport locked | Unlock, update, relock | H |
| E605 | Annotation scale for the chosen scale missing from the drawing | Add it, then assign (P17) | H |
| E606 | Units pairing (feet/inches, metres/millimetres) | Scale comes only from `ModelUnitsPerPaperUnit`; no unit inference | U |
| E607 | Clip boundary larger than the template viewport | Treated as E309 | U |
| E608 | User resized or moved the viewport on paper | Paper position and size kept; view center, twist, scale and clip are re-applied. Per-segment `LockView` skips even that | H |
| E609 | Per-viewport layer freezes set by the user | Preserved (update in place, never recreate) | H |
| E610 | North-up mode | Twist 0; fit test uses the axis-aligned box | U |
| E611 | Regen run while a layout is active or the cursor is inside a floating viewport | Works; active space restored afterwards | M |
| E612 | Segment removed | Layout becomes an orphan: listed by `SVSTATUS`, never auto-deleted | H |
| E613 | Many layouts in one drawing | Works; README warns about drawing performance past roughly 50 layouts | M |
| E614 | Viewport created headless shows nothing until the layout is activated | Resolved by P12; if unavoidable, document that first activation initializes it | M |
| E615 | Title-block attribute tag missing | Skipped with a note; not an error | H |

### 7.7 View frame driver and linking

| ID | Case | Required behavior | Test |
|---|---|---|---|
| E701 | Frame moved, slid or rotated | Viewport center and twist follow on regen or auto-update | M |
| E702 | Match line moved | Segment stations, boundary and volumes follow; viewport clip follows when clip mode is Boundary | M |
| E703 | Frame inserted into the group | New segment; new layout from the template | M |
| E704 | Frame deleted | Segment removed; layout becomes an orphan | M |
| E705 | View frame group is a data-shortcut reference | Same behavior after synchronize; keyed by name because handles differ per drawing | M |
| E706 | Reference stale or broken | Report and fail that run; nothing changes | M |
| E707 | Frame geometry unreadable (`Explode` fails) | Fallback: solve rotation from extents plus template viewport size; if still ambiguous, stop condition 12.3 | H, M |
| E708 | Frame scale differs from the run's scale | Use the frame's implied scale (template width ÷ frame width) and report the mismatch | U |
| E709 | Layouts already made by Create Sheets | `SVADOPT` maps them; nothing is recreated; other viewports on those sheets untouched | M |
| E710 | `ViewFrame.Sheet` empty or pointing at another file | Fall back to geometric matching, then ask | M |
| E711 | Frames in the group cover different alignments | One run per alignment; mixed group rejected | H |
| E712 | First and last frames | Station range ends at the alignment end or frame end per P14, stated in the table | M |
| E713 | Frame renamed | Keyed by handle when native; reference frames re-matched by station order with a warning | M |

### 7.8 Regeneration, persistence, auto-update

| ID | Case | Required behavior | Test |
|---|---|---|---|
| E801 | Any failure mid-regen | Nothing changes in that run; other runs still process; summary lists each | H |
| E802 | Stored handle is erased or invalid | Object recreated; mapping repaired | H |
| E803 | Source re-referenced so its handle changed | Fall back to name match with a warning | H |
| E804 | `UNDO` after regen | One step reverts the regen; tracker does not fire a new regen during undo | M |
| E805 | Auto-update during a corridor rebuild or other burst of changes | One update after the command ends, not one per modified object | M |
| E806 | Auto-update fails | Mode drops to report-only for that run, message printed once; no retry loop | M |
| E807 | Read-only drawing, or xref-locked | Regen refuses at step 1 | H |
| E808 | Two runs on one alignment | Independent; shared layouts are not supported in one run — use multiple analyses instead | H |
| E809 | Drawing copied or saved under a new name | Works; export files follow the new name | H |
| E810 | Objects copied to another drawing without the dictionary | They are plain objects; no errors on open | M |
| E811 | Regen with nothing changed | No database writes except the timestamp in persistence; tables and viewports keep their handles | H |
| E812 | Batch regen over many drawings | Each drawing isolated; one failure does not stop the batch; per-file result log | H |

### 7.9 Dialog

| ID | Case | Required behavior | Test |
|---|---|---|---|
| E901 | No drawing open, or the active drawing changes | Dialog rebinds to the active drawing; controls disabled with a one-line reason when none is open | M |
| E902 | Drawing has no alignment, or fewer than two TIN surfaces | Empty state naming what is missing; Regenerate disabled | U |
| E903 | Invalid station text in a cell | Inline error on that cell; last valid value kept for validation; no modal box | U |
| E904 | Editing a segment's end station | The next segment's begin follows (shared break). Gaps only through an explicit skip | U |
| E905 | Regenerate pressed while a command is running | Button disabled until the editor is idle | M |
| E906 | Long regen | Progress and Cancel; cancel leaves the drawing unchanged | M |
| E907 | Dialog closed with edits not yet regenerated | Edits kept for that drawing and restored on reopen; nothing written until Regenerate | U |
| E908 | Color theme switched while open | Restyles without reopening | M |
| E909 | 125–200% scaling and mixed-DPI monitors | No clipped text; minimum window size enforced | M |
| E910 | Keyboard only | Full tab order; Enter commits a cell, Esc reverts it; arrow keys move in the grid; every control has an accessible name | M |
| E911 | Row selected | Its boundary is highlighted in the drawing; Zoom to works; highlight cleared on close | M |
| E912 | Drawing changed outside the dialog (alignment edit, surface rebuild, typed `SVREGEN`) | Banner and status chips update from the tracker; values refresh after an external regen | U, M |
| E913 | 200+ segments | Grid virtualized; chart scrolls with the grid selection | M |
| E914 | Metric drawing | Labels and units follow the drawing (m, m³) | U |
| E915 | Core Console | UI assembly is never loaded; engine unaffected | H |
| E916 | More than one comparison | A comparison selector above the grid drives the 2D area, cut, fill and net columns and the chart | U |
| E917 | Area unit dropdown changed | Grid reformats immediately; choice saved with the run; offered units follow the drawing (SF/AC or m²/ha) | U |

### 7.10 Impacts below OHWM and the HCFCD table

| ID | Case | Required behavior | Test |
|---|---|---|---|
| E1001 | OHWM below all existing ground in the segment | Length 0, area 0.00, volume 0, average `N/A`; segment noted as having no ground below OHWM | U |
| E1002 | Ground below OHWM reaches a lateral search limit | Segment flagged "widen the offsets"; values shown but marked | U |
| E1003 | Cut where existing ground is above OHWM (bank work) | Not counted; reported as cut above OHWM in the export | U |
| E1004 | Fill below OHWM | Not counted in rows 6 to 9; reported in the export | U |
| E1005 | Cut in two separate stretches of one segment | Length is the sum of the stretches; both intervals listed in the export | U |
| E1006 | Low ground below OHWM away from the channel (side ditch, pond) that has cut | Counted under the plan-area rule; its station interval appears in the export so it can be seen | U |
| E1007 | Slivers of near-zero cut | `MinCutDepth` and `GapTolerance` from config; the defaults count everything | U |
| E1008 | Curved channel | Length is measured along the alignment, not along the low-flow line | U |
| E1009 | State-plane coordinates | Local-origin translation before area math; result matches the same geometry at the origin within 1e-9 relative | U |
| E1010 | OHWM differs between segments | Per-segment override; no double counting because segments partition by station | U |
| E1011 | Either surface missing in part of the search band | That part contributes nothing; coverage reported | U |
| E1012 | Integrator and Civil 3D bounded volume disagree | Run fails; both numbers printed | H |
| E1013 | Hidden triangles, boundaries, masks | Only visible triangles used, matching what Civil 3D computes on | H |
| E1014 | Very large surfaces | Triangles read only inside the band window; progress and cancel | M |
| E1015 | User deleted or renamed a bound row in the table | That value skipped with a message; other values update | H |
| E1016 | User typed in manual cells or removed a feature row | Preserved exactly | H |
| E1017 | Segment's layout renamed or deleted | Table stays where it is if it still exists; otherwise reported, never silently re-created elsewhere | H |
| E1018 | Two materials | One row each, added only when the table is first created; a later analysis with no row is reported | H |
| E1019 | Average with rounded inputs | Computed from unrounded volume and length | U |
| E1020 | Metric drawing | Labels and units switch (m, ha or m², m³, m³/m) | U |
| E1021 | Surfaces as data-shortcut references or corridor surfaces | Works if P23 confirms triangle access; otherwise stop condition 12.9 | H |

---

## 8. Fixtures and acceptance tests

Fixtures are built in code so expected values are analytic. EG everywhere: flat TIN at z = 100 on corners (−200,−300), (2200,−300), (2200,300), (−200,300). Alignment S: straight from (0,0) to (2000,0), station 0 at x = 0, so left is +y.

**Fixture A (Dan's example).** `FG_A` = EG plus five square pyramids, 90 × 90 base at z = 100 centered on y = 0, apex at the center, breaklines on base edges and apex-to-corner.

| Pyramid | Center x | Apex z | Volume |
|---|---|---|---|
| P1 | 125 | 99.0 | 100 CY cut |
| P2 | 375 | 100.5 | 50 CY fill |
| P3 | 1125 | 99.0 | 100 CY cut |
| P4 | 1375 | 100.5 | 50 CY fill |
| P5 | 1750 | 101.0 | 100 CY fill |

**Fixture B (ridge).** `FG_B` = EG plus breaklines y = −30 (z 100), y = 0 (z 101), y = +30 (z 100) from x = −100 to 2100. Cross-section 30 ft².

**Fixture D (slab).** `FG_D` = EG raised 1.00 everywhere. Fill inside any boundary equals its plan area in ft³. This checks boundary geometry through the real API.

**Fixture H (channel with sediment).** Alignment S. `EG_H` is uniform from x = 0 to 2000: bed at elevation 96.0 for |y| ≤ 10, 3:1 side slopes up to 106.0 at |y| = 40, flat at 106.0 out to |y| = 100. `FG_H` from x = 0 to 1300: bed at 94.0 for |y| ≤ 10, rising to meet `EG_H` at |y| = 16 (elevation 98.0), equal to `EG_H` beyond. From x = 1310 onward `FG_H` equals `EG_H`; the 10 ft between is a transition. Breaklines along every longitudinal line. Where the cut is full it is 52 ft² in section and 32 ft wide. The same triangles are also generated in the Core test project, so T25 to T28 run without Civil 3D.

**Alignment C.** Tangent 300 east, arc R = 300 clockwise Δ = 60° (length 314.159), tangent 300. **Alignment K:** two tangents meeting at a 30° angle point. **Alignment SP:** spiral-curve-spiral.

| Test | Setup | Pass condition (±0.01 CY unless noted) |
|---|---|---|
| T1 | A, `START..END@500`, offsets (−100, +100) | 0–500: C 100, F 50. 500–1000: 0, 0. 1000–1500: C 100, F 50. 1500–2000: C 0, F 100 |
| T2 | A, breaks `0, 500, 1300, 1800, 2000` | C/F: 100/50, 100/0, 0/150, 0/0. Mixed lengths 500, 800, 500, 200 |
| T3 | A, breaks `0, 125, 500` | [0,125]: C 50, F 0. [125,500]: C 50, F 50 |
| T4 | A, 0–500, offsets (−100, 0) | C 50, F 25 (lateral edge through both apexes) |
| T5 | B, `START..END@500`, offsets (−100, +100) | Each F 555.556, C 0 |
| T6 | B, 0–500, offsets (−30, +15) | F 486.111, C 0 |
| T7 | D on C, segment [300, 614.159], offsets (−100, +100) | F 2327.106 CY, relative tolerance 2e-4 |
| T8 | D on C, same segment, offsets (−50, +150) | F 1939.255 CY, relative tolerance 2e-4 |
| T9 | D on C, offsets (−50, +300) | Error E201; no number |
| T10 | D on K and SP | API volume equals Core polygon area within 1e-6 relative; partition check passes |
| T11 | A with factors 1.25 / 0.9 | Output = raw × factor, once; owned surface shows the same factors |
| T12 | A: regen, move P1 apex to 98.0, rebuild, regen | Same table handles; 0–500 cut now 200 CY |
| T13 | A: insert break at 250 | Left segment keeps GUID, layout and table handles; new segment and layout created; later layouts renumbered |
| T14 | A: lengthen alignment to 2500 | `END` segment extends; fingerprint differs before regen, equal after |
| T15 | Views, alignment S, 0–500, offsets ±100, 50 model units per paper unit, template viewport 12 × 5 | Twist 0, center (250, 0), needs 10 × 4, fits |
| T16 | Views, same but 0–800 | Fixed: fit error. FitStandard {20,30,40,50,60,100}: picks 100 |
| T17 | Views, north-running straight alignment | Twist −90°; start of segment maps to the left edge in paper space |
| T18 | Layouts: generate from the code-built test template, save, reopen | Layout count, viewport target/twist/scale, clip vertex count and table text all match the plan |
| T19 | Idempotence: regen twice | Second run changes no handles and no geometry (E811) |
| T20 | Failure atomicity: force an error in segment 3 | Drawing byte-for-byte equivalent in owned objects to before |
| T21 | Core unit suite | All E-rows marked U are covered and green |
| T23 | A, T2's breaks, offsets (−100, +100) | 2D area 100,000 / 160,000 / 100,000 / 40,000 SF, total 400,000. In AC: 2.30 / 3.67 / 2.30 / 0.92, total row 9.19 (sum of displayed rows), export 9.1827. All coverage 100% |
| T24 | D on C, segment [300, 614.159], offsets (−100, +100) | 2D area 62,831.85 SF, relative tolerance 2e-4. A boundary extended past the surface edge returns less than its own area and coverage below 100% |
| T25 | H, OHWM 100.00, breaks `0, 500, 1000`, offsets (−100, +100) | Each segment: length 500, area 16,000 ft² = 0.37 ac, volume 962.963 CY, average 1.93 |
| T26 | H, OHWM 97.00, same segments | Each segment: length 500, area 13,000 ft² = 0.30 ac, volume 907.407 CY, average 1.81 |
| T27 | H, OHWM 95.00, same segments | Length 0, area 0, volume 0, average `N/A` |
| T28 | H, OHWM 100.00, segment [1000, 1500] | Impacted length 310.00 ± 0.01; volume between 577.78 and 597.04 CY |
| T29 | A, B, D and H with no OHWM limit, on alignments S and C | Integrator band cut and fill equal `GetBoundedVolumes` within 1e-9 relative |
| T30 | H, OHWM 100.00, offsets (−12, +12) | Search-limit flag raised (E1002) |
| T31 | H: create HCFCD tables, type into the center-coordinates cell, delete the WETLANDS row, change OHWM to 97.00, regen | Typed text and the deletion are kept; rows 6, 7 and 9 show T26's values; table handles unchanged |
| T22 | Dialog view models against a fake `ISheetVolSession`, loaded with T2's breaks | Grid shows 4 rows with lengths 500, 800, 500, 200; editing row 2's end to 12+00 moves row 3's begin; an offset wider than the curve radius puts row 2 in Error with the E201 message and hides totals |

T1 is Dan's original example. T2 is his mixed-length case.

---

## 9. Orchestration protocol (Fable)

1. Own `HANDOFF.md`, `docs/contracts.md`, `NOW.md`, `global.json`, all csproj and solution files, and every merge.
2. Do Wave 0 personally. Write no feature code after that.
3. One Opus subagent per work package, each in its own git worktree, with: its WP text, `docs/contracts.md`, `docs/api-findings.md` once it exists, sections 6.13, 7 (its rows) and 8 (its tests), and its file-ownership list.
4. Every worker returns: summary, files changed, build output, test output, unresolved items, and which E-rows and T-tests it covered.
5. After each merge: full build, `dotnet test`, headless self-test once G1 has passed, update `NOW.md`.
6. Reject any diff that violates 6.13 or edits files outside ownership.
7. Never pass a gate on a worker's report. Re-run the gate command and read the output.
8. Track coverage: a table in `NOW.md` of every E-row and T-test with status. No gate passes with an uncovered row in its scope.

`.claude/agents/c3d-dev.md`:

```markdown
---
name: c3d-dev
description: Implements one SheetVol work package inside its file-ownership boundary.
model: opus
---
You implement exactly one work package from HANDOFF.md. Edit only the files you
own. Contracts in docs/contracts.md are frozen: if you need a change, stop and
report it. Follow HANDOFF.md section 6.13. Implement the edge-case rows and tests
assigned to you. Build and run tests before returning.
Return: summary, files changed, build output, test output, unresolved items,
E-rows and T-tests covered.
```

---

## 10. Waves, work packages, gates

Each milestone is shippable on its own. Dan can stop after any of them.

### Wave 0 — orchestrator

Repo, solution, three projects plus tests, `global.json`, `CLAUDE.md`, `NOW.md`, worker definition, `docs/contracts.md`, compiling stubs. Confirm Civil 3D build ≥ 2026.2.2 and the .NET 10 SDK.

- **Gate G0:** `dotnet build` and an empty `dotnet test` succeed. P16 answered.

### Wave 1 — eight workers in parallel

| WP | Owns | Delivers |
|---|---|---|
| S1 Probe: volumes, alignment, headless | `Civil3D/SelfTest/ProbeA*.cs`, `tools/scripts/probeA.scr`, `docs/api-findings.md` §A | P1–P10, P18, P19, P22, P23. Working `accoreconsole` invocation in `tools/test-headless.ps1` |
| S2 Probe: layouts, viewports, tables | `Civil3D/SelfTest/ProbeB*.cs`, `tools/scripts/probeB.scr`, `docs/api-findings.md` §B | P11–P13, P17, P20, P24 |
| C1 Core planning + geometry | `Core/Planning/**`, `Core/Geometry/**`, their tests | Break-spec parser, segment identity rules, boundary builder, validation, overlap detection. E2xx, E3xx (U) |
| C2 Core volumes + output | `Core/Volumes/**` except `Integrator/`, `Core/Output/**`, their tests | Reconcile, factors, units, rounding, totals, table models, CSV/JSON, chunker. E4xx, E5xx (U) |
| C3 Core views + tracking | `Core/Views/**`, `Core/Tracking/**`, their tests | View math, fit, clip transform, name sanitizer, fingerprints, auto-update state machine. E6xx, E8xx (U), T15–T17 |
| C4 Core presentation | `Core/Presentation/**`, its tests | Dialog view models per 6.14 against a fake `ISheetVolSession`, both presets. E9xx (U), T22 |
| C5 Core integrator | `Core/Volumes/Integrator/**`, its tests | Overlay integrator and impact quantities per 6.15, station-of-point math, Fixture H triangle generator for tests. E10xx (U), T25–T28 on generated triangles |
| FX Fixtures | `Civil3D/SelfTest/Fixture*.cs` | Fixtures A, B, D, H; alignments S, C, K, SP; a code-built test template. Fixture A first, for S1 |

- **Gate G1:** T21 green, and T25–T28 green on generated triangles. `api-findings.md` answers P1–P13 and P16–P20 with evidence. Probe shows Fixture A totals of 200 CY cut and 200 CY fill after the documented mapping.

### Wave 2 — five workers in parallel (milestones M1 Volumes and M2 Sheets)

| WP | Owns | Delivers |
|---|---|---|
| A1 Adapters | `Civil3D/Adapters/**` | `IAlignmentGeometry` from sub-entities with `PointLocation` cross-check; analyses and owned volume surface lifecycle; `IVolumeSource` with the P1–P3 mapping in one place; `ITriangleSource` per surface, visible triangles only; unit detection |
| A2 Model writers | `Civil3D/WritersModel/**` | Boundary polylines, match marks, model-space and master tables, label-bound template tables per 6.16, outline of the impacted area, persistence store |
| A3 Paper writers | `Civil3D/WritersPaper/**` | Layout cloning from the template, viewport create/update, clip, paper tables, title attributes |
| A4 Commands + pipeline | `Civil3D/Commands/**`, `Civil3D/Session/**`, `tools/scripts/*.scr` (not probes) | Config, regen pipeline per 6.10, database side of `ISheetVolSession`, `SVDEFINE`, `SVANALYSIS`, `SVBREAK`, `SVREGEN`, `SVSTATUS`, `SVEXPORT`, `SVCLEAR`, `SVPURGE`, `SVSELFTEST`. Develops against fakes until A1–A3 merge |
| U2 Dialog | `src/SheetVol.UI/Dialog/**`, `src/SheetVol.UI/Host/**` | XAML for 6.14 bound to the C4 view models, dark and light themes, `SVDIALOG`, editor side of `ISheetVolSession` (pick, highlight, zoom). Runs against the fake session until A4 merges. E9xx (M) |
| S3 Probe: frames, events, modeless UI (small, runs alongside) | `Civil3D/SelfTest/ProbeC*.cs`, `docs/api-findings.md` §C, `docs/dan-probe-steps.md` | `SVPROBEUI` command and a short step list for Dan. P14, P15, P21. If frames are deferred, only P21 is needed now |

Merge order: A1, A2, A3, A4.

- **Gate G2:** headless `SVSELFTEST` passes T1–T20 and T23 through T31. T22 green. Every E-row marked H in 7.2–7.6 and 7.8 is covered. Dan opens `SVDIALOG` in Civil 3D once, runs T2's breaks through it and compares it with `docs/ui/`.

### Wave 3 — three workers in parallel (milestones M3 Link and M4 Ship)

| WP | Owns | Delivers | Depends on |
|---|---|---|---|
| L1 View frame driver | `Civil3D/Frames/**` | `FrameGeom` reader, ViewFrames segment source, `SVADOPT`. E7xx | P14 results, Dan's fixture drawing |
| L2 Auto-update + batch | `Civil3D/Auto/**`, `tools/regen-sheets.ps1` | Event wiring per 6.11, `SVAUTO`, batch script. E804–E806, E812 | P15 results |
| U1 UI + packaging | `src/SheetVol.UI/**`, `bundle/**`, `tools/build.ps1`, `README.md` | Ribbon, `PackageContents.xml` (engine for R25.1; UI only in full AutoCAD), release build, README | — |

- **Gate G3 (Dan, Civil 3D 2026.2.2+, real project):**
  1. One segment's cut and fill match a Volumes Dashboard bounded volume on the same boundary polyline.
  2. A run with mixed lengths produces correctly oriented, clipped viewports with tables on each sheet.
  3. Edit the alignment, click Regenerate: boundaries, viewports and tables follow in place.
  4. ViewFrames run: move a frame and a match line; with `SVAUTO` Views the viewport follows after the grip edit, tables show `(OUT OF DATE)`, Regenerate clears it.
  5. Data-shortcut view frame group: change the source, synchronize in the sheet drawing, viewports follow.
  6. `SVADOPT` links an existing Create Sheets layout set without recreating anything.

---

## 11. Commands for the dev machine

On the dev workstation (Windows, PowerShell), build and unit test:

```powershell
dotnet build .\SheetVol.sln -c Debug
dotnet test .\tests\SheetVol.Core.Tests\SheetVol.Core.Tests.csproj
```

On the dev workstation (Windows, PowerShell), headless self-test. Starting form only; S1 replaces it with what P9 proves:

```powershell
& "C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe" `
  /i ".\tools\fixtures\blank-c3d.dwg" `
  /s ".\tools\scripts\selftest.scr" `
  /product C3D /l en-US `
  /isolate svtest "$env:TEMP\svtest"
```

On the dev workstation (Windows, PowerShell), batch regen over sheet drawings:

```powershell
.\tools\regen-sheets.ps1 -List .\sheets.txt -Log .\regen-log.csv
```

---

## 12. Stop conditions

Stop and report to Dan with evidence. Do not work around these.

1. P1–P3 cannot be pinned down, or Fixture A totals do not reconcile with `GetVolumeProperties`.
2. P6 shows a volume surface cannot be built from data-shortcut surfaces. Options to bring: run in the source drawing and feed sheets through the JSON export, or require native surfaces.
3. P14 shows frame geometry cannot be read reliably on reference frames.
4. P12 shows viewports cannot be driven correctly without activating each layout, and activation is impossible headless.
5. The partition check fails on any fixture.
6. A contract in `docs/contracts.md` must change after Wave 1.
7. The installed Civil 3D is below 2026.2.2.
8. Anything requires changing LAN templates, standards, the LAN DT Framework or deployment.
9. P23 shows triangles cannot be read from reference or corridor surfaces, or the integrator and `GetBoundedVolumes` disagree on any fixture.

---

## 13. What Dan needs to provide

| When | Item |
|---|---|
| Before Wave 0 | Confirm the dev machine and the 2026 install pack are at Civil 3D 2026.2.2 or later |
| During Wave 2 | A small drawing with an alignment and a view frame group made by the Create View Frames wizard, plus a second drawing that data-references it. Save under `tools/fixtures/`. Needed because the API cannot create view frames |
| During Wave 2 | Run `SVPROBEUI` once in full Civil 3D following `docs/dan-probe-steps.md` and return the log |
| Before G3 | LAN sheet template with the plan viewport on `SV-VPORT` (or the layer name to use) and the table anchor block; table style and layer names |
| Before Wave 2 | Export the four design artboards as PNG into `docs/ui/`, after any changes you want to the design |
| Before Wave 2 | The HCFCD segment table as it should look in CAD (table style, text height, column widths), and one sample drawing with real existing and design surfaces plus the OHWM elevation for a reach |
| Before Wave 2 | Confirm assumptions A10 and A12: what counts as "below OHWM", and the decimals for acres |
| Any time | Corrections to section 3; final command names; `SVAUTO` default |

---

## 14. Done means

- G0–G3 passed, each recorded in `NOW.md` with the command output that proved it.
- T1–T21 pass from a clean clone with one script.
- Every E-row has a named test or a signed-off manual check.
- `docs/api-findings.md` records the observed answer to every P item.
- The bundle loads in Civil 3D 2026.2.2+, all commands run, and the Regenerate button works.
- `SVDIALOG` matches the design reference in both themes and passes the E9xx checks.
- README covers: minimum build, the two drivers, break-spec syntax, offsets, analyses, template requirements, auto-update modes, what OUTSIDE LIMITS and coverage flags mean, and what the tool will never do (move user-placed objects, delete layouts, compute on stale references).

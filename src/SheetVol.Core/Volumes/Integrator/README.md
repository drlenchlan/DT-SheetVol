# Integrator

Owner: C5 (HANDOFF.md section 10). Triangle-overlay integrator and impacts (6.15; under the HCFCD preset the base is the OHWM surface), station-of-point math, Fixture H triangle generator.

Entry points (drawing units in and out; depth d = base - comparison, cut where d > 0):

- `OverlayIntegrator.IntegrateBand` - plan area where both surfaces exist, cut, fill inside a closed boundary (T29 cross-check, 2D area).
- `OverlayIntegrator.IntegrateSegment` - band totals plus `ImpactResult` (rows 6 to 9 and the impact outline) for one station segment, and `SegmentImpactResult.BaseArea` (plan area where the base exists inside the boundary).
- Both have a depth overload taking ONE `ITriangleSource` whose Z is already d = base - comparison (a volume-surface analysis; the adapter normalises the sign): `IntegrateBand(depth, boundary, segmentId)` and `IntegrateSegment(depth, alignment, segment, boundary, material, settings)`.
- `StationLocator` - station and offset of a point from `AlignSeg` geometry.
- `StationIntervals.Union` - impacted length with `GapTolerance` bridging.
- `Fixtures.FixtureSurfaces` - EG, FG_A, FG_B, FG_D, EG_H, FG_H as `ITriangleSource`; triangulation stated in the XML docs.

Method notes (rev 3, NOW.md X5): impact cut is the step 5 clip to d > `MinCutDepth` only; there is no
OHWM elevation, no `base < OHWM` clip, no OHWM-elevation snap and no QA accumulators (E1003, E1004 and
E1010 retired). The depth snap (τ, D9) stays: on the impact path a vertex depth within the two
triangles' evaluation error bound (`SurfaceTriangle.Noise`) is exactly 0, so coincident surfaces
triangulated differently report no phantom cut. The search limit (E1002) is raised when an edge of a
step 5 cut piece, deeper than `MinCutDepth` at one end, lies on a lateral edge of the boundary (within
2 x snap). E1001 is a note when no cut is counted in the segment.

Impact outline (`ImpactResult.Outlines`, D13; `OutlineBuilder`): every step 5 piece that survives the
`MinCutDepth` clip and is thicker than snap gives its directed edges (local coordinates, the piece is
counter-clockwise). Vertices within 2 x snap of an earlier one are welded to it; an edge cancels an equal
opposite edge; an edge with another surviving edge's end vertex on it (within 2 x snap, strictly inside)
is split there and cancellation runs again (a left-out sliver or a clip-snapped vertex leaves such a
T-junction). What remains is the union's boundary, area on the left, chained into closed rings (at a
vertex touched twice the walk takes the sharpest left turn, so each part is its own ring). Collinear
vertices (within 2 x snap of the chord) are dropped, rings thinner than 2 x snap are dropped, each ring
starts at its lowest-x then lowest-y vertex, is translated back to world coordinates and closed (first
== last). Outer rings are counter-clockwise and come first (largest first), holes are clockwise and
follow, so the signed ring areas sum to `ImpactedArea` (1e-9 relative in the tests). Empty when nothing
is impacted.

Depth source: steps 2 and 3 reduce to clipping each depth triangle to the boundary's ear triangles;
D = the triangle's plane, the depth snap uses that triangle's own noise bound; steps 4 to 6 and the
outline are shared. Band plan area and `BaseArea` are the plan area of the depth triangles inside the
boundary (a volume surface exists only where both of its surfaces do).

Problem codes are the SV- prefix plus the HANDOFF.md section 7 row (`IntegratorCodes`):
SV-E202 invalid boundary, SV-E403 empty surface, SV-E405 no overlap, SV-E409 degenerate triangles ignored,
SV-E1001 no cut in the segment, SV-E1002 search limit, SV-E1007 invalid settings, SV-E1008 station geometry,
SV-E1011 partial coverage. A result with an Error carries no numbers.

## Rev 3

- `OhwmSpec` parameter, `ImpactQuantities.OhwmFor`, `InvalidOhwm` (SV-E1010), the QA accumulators and the OHWM snap removed; `GroundBelowOhwmArea` is `BaseArea`, `NoGroundBelowOhwm` is `NoCut`.
- `ImpactResult.Outlines` built by edge cancellation over the step 5 pieces (`OutlineBuilder`, method above); depth overloads of `IntegrateBand` and `IntegrateSegment` for a volume-surface source; the T25 to T30 unit analogues run on Fixture H's flat OHWM surface (base) against FG_H with values recomputed as OHWM minus design (`ImpactTests`, `OutlineTests`).

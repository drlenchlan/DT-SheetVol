# Integrator

Owner: C5 (HANDOFF.md section 10). Triangle-overlay integrator and impacts (6.15; under the HCFCD preset the base is the OHWM surface), station-of-point math, Fixture H triangle generator.

Entry points (drawing units in and out; depth d = base - comparison, cut where d > 0):

- `OverlayIntegrator.IntegrateBand` - plan area where both surfaces exist, cut, fill inside a closed boundary (T29 cross-check, 2D area).
- `OverlayIntegrator.IntegrateSegment` - band totals plus `ImpactResult` (rows 6 to 9) for one station segment, and `SegmentImpactResult.BaseArea` (plan area where the base exists inside the boundary).
- `StationLocator` - station and offset of a point from `AlignSeg` geometry.
- `StationIntervals.Union` - impacted length with `GapTolerance` bridging.
- `Fixtures.FixtureSurfaces` - EG, FG_A, FG_B, FG_D, EG_H, FG_H as `ITriangleSource`; triangulation stated in the XML docs.

Method notes (rev 3, NOW.md X5): impact cut is the step 5 clip to d > `MinCutDepth` only; there is no
OHWM elevation, no `base < OHWM` clip, no OHWM-elevation snap and no QA accumulators (E1003, E1004 and
E1010 retired). The depth snap (τ, D9) stays: on the impact path a vertex depth within the two
triangles' evaluation error bound (`SurfaceTriangle.Noise`) is exactly 0, so coincident surfaces
triangulated differently report no phantom cut. The search limit (E1002) is raised when an edge of a
step 5 cut piece, deeper than `MinCutDepth` at one end, lies on a lateral edge of the boundary (within
2 x snap). E1001 is a note when no cut is counted in the segment. `ImpactResult.Outlines` is an empty
list until the rev3-integrator package fills it (D13).

Problem codes are the SV- prefix plus the HANDOFF.md section 7 row (`IntegratorCodes`):
SV-E202 invalid boundary, SV-E403 empty surface, SV-E405 no overlap, SV-E409 degenerate triangles ignored,
SV-E1001 no cut in the segment, SV-E1002 search limit, SV-E1007 invalid settings, SV-E1008 station geometry,
SV-E1011 partial coverage. A result with an Error carries no numbers.

## Rev 3

- `OhwmSpec` parameter, `ImpactQuantities.OhwmFor`, `InvalidOhwm` (SV-E1010), the QA accumulators and the OHWM snap removed; `GroundBelowOhwmArea` is `BaseArea`, `NoGroundBelowOhwm` is `NoCut`; T25 to T30 analogues re-derived (flat-base construction) pending rev3-integrator.

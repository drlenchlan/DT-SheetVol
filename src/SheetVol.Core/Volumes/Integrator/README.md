# Integrator

Owner: C5 (HANDOFF.md section 10). Triangle-overlay integrator and impacts below OHWM (6.15), station-of-point math, Fixture H triangle generator.

Entry points (drawing units in and out; depth d = base - comparison, cut where d > 0):

- `OverlayIntegrator.IntegrateBand` - plan area where both surfaces exist, cut, fill inside a closed boundary (T29 cross-check, 2D area).
- `OverlayIntegrator.IntegrateSegment` - band totals plus `ImpactResult` (rows 6 to 9 and QA) for one station segment.
- `StationLocator` - station and offset of a point from `AlignSeg` geometry.
- `StationIntervals.Union` - impacted length with `GapTolerance` bridging.
- `Fixtures.FixtureSurfaces` - EG, FG_A, FG_B, FG_D, EG_H, FG_H as `ITriangleSource`; triangulation stated in the XML docs.

Problem codes are the SV- prefix plus the HANDOFF.md section 7 row (`IntegratorCodes`):
SV-E202 invalid boundary, SV-E403 empty surface, SV-E405 no overlap, SV-E409 degenerate triangles ignored,
SV-E1001 no ground below OHWM, SV-E1002 search limit, SV-E1007 invalid settings, SV-E1008 station geometry,
SV-E1010 invalid OHWM, SV-E1011 partial coverage. A result with an Error carries no numbers.

# Tracking

Owner: C3 (HANDOFF.md section 10). Fingerprints and the auto-update state machine as pure functions (6.11).

## Rev 3

- Format `SVFP3`: the definition signature drops the ohwm section and hashes each analysis's `VolumeSurface` presence and name; the surfaces component adds, per volume-surface source (`AnalysisSurfaceState.VolumeSurface`, D4), its name, handle and factors after its post-rebuild totals (pair text unchanged, pins unchanged); `KnownSegments` and `AutoMode` stay out.

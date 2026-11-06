# SelfTest

Owner: S1, S2, S3, FX (HANDOFF.md section 10). ProbeA*.cs (S1), ProbeB*.cs (S2), ProbeC*.cs (S3), Fixture*.cs (FX), SVSELFTEST driver (A4).

- `SVFIXTURE T` (FixtureSettingsNames.cs, FX-T): creates every layer, surface style (all components off), AutoCAD table style, text style and block the effective settings name and the drawing lacks; existing ones are reported present and left alone. Not part of ALL. Proof: `tools/scripts/fixtureT.scr` (A, S, T, T, then a run on S with EG vs FG_A and SVREGEN).

## Rev 3

- `SVFIXTURE H` also builds flat OHWM surfaces `OHWM_100`, `OHWM_97`, `OHWM_95` over H's extent (4 points, 2 triangles, elevation probes); the fake world gets the same triangles from `Fixtures.AddOhwmSurfaces` at the start of `SelfTestRunner.Run`. SVSELFTEST T25 to T28, T30 and T31 run base = the OHWM surface at the stated elevation, comparison = FG_H, expected values recomputed as OHWM minus design (`FixtureSpec.Expected`); T27.E1001 keeps the zeros case on [1500, 2000]; T31's OHWM change is a change of base surface, OHWM_100 to OHWM_97. D10 still runs EG_H/FG_H.

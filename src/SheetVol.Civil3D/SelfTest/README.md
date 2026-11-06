# SelfTest

Owner: S1, S2, S3, FX (HANDOFF.md section 10). ProbeA*.cs (S1), ProbeB*.cs (S2), ProbeC*.cs (S3), Fixture*.cs (FX), SVSELFTEST driver (A4).

- `SVFIXTURE T` (FixtureSettingsNames.cs, FX-T): creates every layer, surface style (all components off), AutoCAD table style, text style and block the effective settings name and the drawing lacks; existing ones are reported present and left alone. Not part of ALL. Proof: `tools/scripts/fixtureT.scr` (A, S, T, T, then a run on S with EG vs FG_A and SVREGEN).

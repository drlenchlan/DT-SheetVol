# Probe

Owner: S3 (HANDOFF.md section 10, Wave 2). Probe P21 only: the `SVPROBEUI` modeless WPF
window, its helper commands `SVPROBEUI_WRITE` and `SVPROBEUI_PICK`, and the XAML load test.
Evidence code, not product code. It logs to `%TEMP%\sheetvol\probeUI.log` through
`ProbeCLog` in `SheetVol.Civil3D/SelfTest/ProbeCCommon.cs`. U1 may delete this folder once
section C of `docs/api-findings.md` records Dan's run.

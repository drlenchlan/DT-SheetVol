# Commands

Owner: A4 (HANDOFF.md section 10). SV* commands (6.12) and the command boundary (6.13). HelloCommand.cs is the Wave 0 P16 probe, owned by the orchestrator.

- `SvCommands.cs`: SVDEFINE, SVANALYSIS, SVBREAK, SVREGEN, SVSTATUS, SVEXPORT, SVCLEAR, SVPURGE, SVCONFIG. Every prompt takes typed input (A9).
- `CommandSupport.cs`: the boundary that catches at the command edge with a stable SV-Exxx code, and the prompt helpers.
- SVSELFTEST lives in `SelfTest/SelfTestCommand.cs`.

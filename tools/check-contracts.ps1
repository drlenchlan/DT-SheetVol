# Orchestrator check: docs/contracts.md carries src/SheetVol.Core/Contracts.cs verbatim.
$root = Split-Path $PSScriptRoot -Parent
$cs  = Get-Content (Join-Path $root 'src\SheetVol.Core\Contracts.cs') -Raw
$md  = Get-Content (Join-Path $root 'docs\contracts.md') -Raw
if ($md.Contains($cs.TrimEnd())) { 'contracts.md matches Contracts.cs'; exit 0 }
Write-Error 'docs/contracts.md does not contain Contracts.cs verbatim'; exit 1

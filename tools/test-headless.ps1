<#
.SYNOPSIS
  Run a SheetVol .scr in Civil 3D Core Console (accoreconsole.exe) with a hard timeout.

.DESCRIPTION
  Switches are what probe P9 proved (docs/api-findings.md A.12, MEASURED 2026-11-05 on
  Civil 3D 2026.2.3): /i <drawing> /s <script> /product C3D /l en-US. TIN surfaces,
  volume surfaces and alignments are created headless with these. /product C3D is kept
  but is not required on this seat: without it the AECC modules demand-load when the
  drawing holds Civil objects, or when the script NETLOADs this DLL (it references
  AeccDbMgd). A prior NETLOAD of AeccDbMgd.dll and /ld AecBase.dbx are not needed;
  both were measured and change nothing. /isolate is not used (it hung, NOW.md X3).
  Invocation pattern copied from the Sheet Set Editor's AccoreConsole.RunScript, which
  is live-proven on Civil 3D 2026.2:
    - stdin is redirected and closed, so an unanswered prompt reads EOF instead
      of hanging on the parent's console;
    - LAN_DT_BATCH=1 keeps the LAN DT Framework's per-open hooks silent in the
      worker (the Framework's acad.lsp auto-loads in Core Console on a LAN seat);
    - stdout is UTF-16LE and is decoded here;
    - a timeout kills the worker; exit code -1 means killed.
  Never touches SECURELOAD (a profile-persisted setting). The DLL path in the
  script uses forward slashes.

.PARAMETER Script
  Path to the .scr to run. Default: tools\scripts\selftest.scr.
.PARAMETER Drawing
  Drawing or template to open with /i. Default: tools\fixtures\blank-c3d.dwg when
  it exists, else no /i (Core Console opens its own blank drawing).
.PARAMETER HostExe
  accoreconsole.exe to use. Default: the Civil 3D 2026 install from the registry,
  falling back to C:\Program Files\Autodesk\AutoCAD 2026.
.PARAMETER TimeoutSec
  Kill the worker after this many seconds. Default 600.
.PARAMETER NoProduct
  Omit /product C3D (passed by default).
.PARAMETER LogPath
  Where the decoded console output is written. Default: $env:TEMP\sheetvol\<script>.log.
.PARAMETER ExtraArgs
  Appended verbatim to the accoreconsole command line, for measurements such as P9's
  /ld test. Quote paths with spaces yourself, e.g.
  -ExtraArgs '/ld "C:\Program Files\Autodesk\AutoCAD 2026\ACA\AecBase.dbx"'

.EXAMPLE
  .\tools\test-headless.ps1 -Script .\tools\scripts\p16.scr -Drawing "C:\Program Files\Autodesk\AutoCAD 2026\UserDataCache\Template\map2d.dwt"
#>
[CmdletBinding()]
param(
    [string]$Script = (Join-Path $PSScriptRoot 'scripts\selftest.scr'),
    [string]$Drawing,
    [string]$HostExe,
    [int]$TimeoutSec = 600,
    [switch]$NoProduct,
    [string]$LogPath,
    [string]$BuildDir,
    [string]$Configuration = 'Debug',
    [string]$ExtraArgs
)

$ErrorActionPreference = 'Stop'

if (-not $HostExe) {
    $reg = Get-ItemProperty 'HKLM:\SOFTWARE\Autodesk\AutoCAD\R25.1\ACAD-9100:409' -ErrorAction SilentlyContinue
    $root = if ($reg -and $reg.AcadLocation) { $reg.AcadLocation } else { 'C:\Program Files\Autodesk\AutoCAD 2026' }
    $HostExe = Join-Path $root 'accoreconsole.exe'
}
if (-not (Test-Path $HostExe)) { throw "accoreconsole not found: $HostExe" }
if (-not (Test-Path $Script)) { throw "script not found: $Script" }
$Script = (Resolve-Path $Script).Path

if (-not $Drawing) {
    $blank = Join-Path $PSScriptRoot 'fixtures\blank-c3d.dwg'
    if (Test-Path $blank) { $Drawing = $blank }
}
if ($Drawing -and -not (Test-Path $Drawing)) { throw "drawing not found: $Drawing" }
if ($Drawing) { $Drawing = (Resolve-Path $Drawing).Path }   # the worker runs in another directory

if (-not $LogPath) {
    $dir = Join-Path $env:TEMP 'sheetvol'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $LogPath = Join-Path $dir ([IO.Path]::GetFileNameWithoutExtension($Script) + '.log')
}

# SECURELOAD=1 refuses a NETLOAD from the build folder (MEASURED 2026-11-05:
# "Unable to load ... assembly" from C:\LAN-DT-build). %APPDATA%\Autodesk\
# ApplicationPlugins is implicitly trusted, so the build output is staged into a
# dev bundle folder there (no PackageContents.xml, so the autoloader ignores it)
# and scripts NETLOAD it through the SHEETVOL_DLL environment variable:
#   (command "_.NETLOAD" (getenv "SHEETVOL_DLL"))
# Ask MSBuild where THIS checkout builds to. A worktree builds in-tree while the
# main checkout under the LAN workspace redirects to C:\LAN-DT-build; guessing
# picked the wrong DLL for worktrees (S2 finding, 2026-11-05).
if (-not $BuildDir) {
    $proj = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\SheetVol.Civil3D\SheetVol.Civil3D.csproj'
    $BuildDir = (& dotnet msbuild $proj -getProperty:OutDir -p:Configuration=$Configuration -nologo 2>$null | Select-Object -Last 1).Trim()
    if (-not $BuildDir -or -not [IO.Path]::IsPathRooted($BuildDir)) {
        $BuildDir = Join-Path (Split-Path $proj -Parent) $BuildDir
    }
}
if (-not (Test-Path (Join-Path $BuildDir 'SheetVol.Civil3D.dll'))) { throw "no SheetVol.Civil3D.dll in $BuildDir (build first)" }
# One staging folder per checkout (leaf folder name), so parallel worktrees never
# overwrite each other's DLL mid-run.
$leaf = Split-Path (Split-Path $PSScriptRoot -Parent) -Leaf
$stage = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\SheetVol-dev-$leaf.bundle\Contents"
New-Item -ItemType Directory -Force $stage | Out-Null
# Subfolders too: Templates\ ships beside the DLL since C2, and A2 found it missing
# from the stage (2026-11-05).
$buildRoot = (Resolve-Path $BuildDir).Path.TrimEnd('\')
Get-ChildItem $buildRoot -Recurse -File | Where-Object { $_.Extension -in '.dll', '.pdb', '.json' } |
    ForEach-Object {
        $rel  = $_.FullName.Substring($buildRoot.Length).TrimStart('\')
        $dest = Join-Path $stage $rel
        New-Item -ItemType Directory -Force (Split-Path $dest -Parent) | Out-Null
        Copy-Item $_.FullName $dest -Force
    }
$dllPath = (Join-Path $stage 'SheetVol.Civil3D.dll') -replace '\\', '/'
Write-Host "staged:        $stage"

$argList = @()
if ($Drawing) { $argList += '/i'; $argList += "`"$Drawing`"" }
$argList += '/s'; $argList += "`"$Script`""
if (-not $NoProduct) { $argList += '/product'; $argList += 'C3D' }
$argList += '/l'; $argList += 'en-US'
if ($ExtraArgs) { $argList += $ExtraArgs }   # verbatim; quote paths with spaces yourself

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $HostExe
$psi.Arguments = ($argList -join ' ')
$psi.UseShellExecute = $false
# The LAN Framework's acad.lsp writes a <drawing>_<id>.log into the process's
# working directory. Keep those out of the repo (MEASURED 2026-11-05: nine of
# them landed in the first commit).
$psi.WorkingDirectory = Split-Path $LogPath -Parent
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardInput = $true
$psi.StandardOutputEncoding = [Text.Encoding]::Unicode
$psi.EnvironmentVariables['LAN_DT_BATCH'] = '1'
$psi.EnvironmentVariables['SHEETVOL_DLL'] = $dllPath
# Repo root with forward slashes. A path with spaces cannot sit on a script line
# (the script reader splits on spaces), so scripts build paths in LISP:
#   (strcat (getenv "SHEETVOL_REPO") "/tools/fixtures/x.dwg")
$psi.EnvironmentVariables['SHEETVOL_REPO'] = (Split-Path $PSScriptRoot -Parent) -replace '\\', '/'

Write-Host "accoreconsole: $HostExe"
Write-Host "args:          $($psi.Arguments)"
$sw = [Diagnostics.Stopwatch]::StartNew()
$proc = [Diagnostics.Process]::Start($psi)
try { $proc.StandardInput.Close() } catch { }
$readTask = $proc.StandardOutput.ReadToEndAsync()
$exited = $proc.WaitForExit($TimeoutSec * 1000)
$exitCode = -1
if (-not $exited) {
    try { $proc.Kill() } catch { }
    Write-Warning "killed after $TimeoutSec s"
} else {
    $exitCode = $proc.ExitCode
}
$output = ''
try { if ($readTask.Wait(5000)) { $output = $readTask.Result } } catch { }
$sw.Stop()

$output = $output -replace "`0", ''
[IO.File]::WriteAllText($LogPath, $output, [Text.UTF8Encoding]::new($false))
Write-Host "exit:          $exitCode   elapsed: $([int]$sw.Elapsed.TotalSeconds) s   log: $LogPath"
Write-Host '--- last lines ---'
($output -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 30) | ForEach-Object { Write-Host $_ }
exit $exitCode

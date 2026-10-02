#Requires -Version 7
<#
.SYNOPSIS
  Luban CSV -> C# + binary tables (one run, both outputs).

.DESCRIPTION
  Reads data/luban.conf + data/*.csv and emits:
    - C#      -> src/ProjectSpy.Tables/Gen   (compiled into ProjectSpy.Tables)
    - binary  -> assets/data/tables          (shared load target, see below)

  The binary output directory is deliberately OUTSIDE UnityProject/. Unity cannot
  read files outside Assets/, so stage 7 copies assets/data/tables into
  Assets/StreamingAssets/Tables via tools/sync-assets.ps1. Keeping the canonical
  copy outside Unity means the console projects and tests load the exact same
  bytes the game does, instead of a copy that can drift.

  Luban.dll is expected at tools/luban/Luban.dll (gitignored — see data/README.md).

.PARAMETER Target
  Luban target name from data/luban.conf. Defaults to 'all'.

.EXAMPLE
  pwsh tools/gen.ps1
  pwsh tools/gen.ps1 -Target client
#>

[CmdletBinding()]
param(
    [string] $Target = 'all'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$lubanDll = Join-Path $repoRoot 'tools/luban/Luban.dll'
$dataDir = Join-Path $repoRoot 'data'
$confFile = Join-Path $dataDir 'luban.conf'
$outCodeDir = Join-Path $repoRoot 'src/ProjectSpy.Tables/Gen'
$outDataDir = Join-Path $repoRoot 'assets/data/tables'

# ---- 1. Locate Luban.dll (fail loudly with a download pointer) ---------------
if (-not (Test-Path $lubanDll)) {
    Write-Host ''
    Write-Host 'ERROR: Luban.dll not found.' -ForegroundColor Red
    Write-Host ''
    Write-Host "  Expected at: $lubanDll" -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  Download the Luban release (net8/net9 build, Luban.dll + its deps) from:' -ForegroundColor White
    Write-Host '    https://github.com/focus-creative-games/luban/releases' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '  Then place the files so that this path exists:' -ForegroundColor White
    Write-Host '    tools/luban/Luban.dll' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  Quick start (latest release, zip -> extract):' -ForegroundColor White
    Write-Host '    https://github.com/focus-creative-games/luban/releases/latest' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '  The folder tools/luban/ is gitignored — the DLL is never committed.' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '  This script refuses to continue without it. Silently skipping codegen' -ForegroundColor DarkGray
    Write-Host '  would leave a stale Gen/ folder that no longer matches data/, which is' -ForegroundColor DarkGray
    Write-Host '  worse than a build failure.' -ForegroundColor DarkGray
    exit 1
}

# ---- 2. Sanity-check inputs before invoking Luban ---------------------------
if (-not (Test-Path $confFile)) {
    Write-Host "ERROR: Luban config not found at $confFile" -ForegroundColor Red
    exit 1
}

$tableRegistry = Join-Path $dataDir '__tables__.csv'
if (-not (Test-Path $tableRegistry)) {
    Write-Host "ERROR: Table registry not found at $tableRegistry" -ForegroundColor Red
    exit 1
}

# Every input named by the registry must exist, otherwise Luban fails with a
# message that does not name the missing table clearly enough to act on.
$inputs = Get-Content $tableRegistry -Encoding UTF8 |
    Where-Object { $_ -like ',Tb*' } |
    ForEach-Object { ($_ -split ',')[8] } |
    Where-Object { $_ }
$missing = @()
foreach ($input in $inputs) {
    $path = Join-Path $dataDir $input
    if (-not (Test-Path $path)) { $missing += $input }
}
if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host 'ERROR: these table CSVs are registered in __tables__.csv but missing:' -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "    data/$_" -ForegroundColor Yellow }
    Write-Host ''
    exit 1
}

# ---- 3. Prepare output dirs --------------------------------------------------
# Clear stale generated sources: a file whose table was deleted from the registry
# would otherwise linger in Gen/ and still compile, silently serving dead data.
if (Test-Path $outCodeDir) {
    Get-ChildItem $outCodeDir -Filter '*.cs' -File | Remove-Item -Force
}
if (Test-Path $outDataDir) {
    Get-ChildItem $outDataDir -Filter '*.bytes' -File | Remove-Item -Force
}
New-Item -ItemType Directory -Force -Path $outCodeDir | Out-Null
New-Item -ItemType Directory -Force -Path $outDataDir | Out-Null

# ---- 4. Run Luban ------------------------------------------------------------
Write-Host "==> Running Luban (target: $Target, cs-bin: C# + binary in one pass)..." -ForegroundColor Cyan
& dotnet $lubanDll `
    --conf $confFile `
    -t $Target `
    -c cs-bin `
    -d bin `
    -x outputCodeDir=$outCodeDir `
    -x outputDataDir=$outDataDir

if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host "ERROR: Luban failed with exit code $LASTEXITCODE" -ForegroundColor Red
    Write-Host '  No generated output was committed. Fix the reported CSV/schema error and re-run.' -ForegroundColor DarkGray
    exit $LASTEXITCODE
}

# ---- 5. Report ---------------------------------------------------------------
$csCount = (Get-ChildItem $outCodeDir -Recurse -File -Filter '*.cs').Count
$bytesCount = (Get-ChildItem $outDataDir -Recurse -File -Filter '*.bytes').Count

if ($csCount -eq 0) {
    Write-Host ''
    Write-Host 'ERROR: Luban reported success but produced no C# files.' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host '==> OK' -ForegroundColor Green
Write-Host ("    C#      -> {0} ({1} files)" -f $outCodeDir, $csCount)
Write-Host ("    binary  -> {0} ({1} files)" -f $outDataDir, $bytesCount)
Write-Host ''
Write-Host 'Next:' -ForegroundColor DarkGray
Write-Host '  1. dotnet build ProjectSpy.sln' -ForegroundColor DarkGray
Write-Host '  2. dotnet test  (runs TableValidator against the new tables)' -ForegroundColor DarkGray

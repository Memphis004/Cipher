<#
.SYNOPSIS
    Publishes ProjectSpy.Core and ProjectSpy.Tables as netstandard2.1 and installs the
    resulting assemblies (plus any non-conflicting dependencies) into
    UnityProject/Assets/Plugins/ProjectSpy/.

.DESCRIPTION
    Unity already ships a large set of BCL facades. Dropping a second copy of one into a
    project is the classic cause of "TypeLoadException: Method ... does not exist" at
    runtime, and it fails in the Editor rather than at build time, so it is expensive to
    diagnose after the fact.

    This script therefore never guesses. For every candidate assembly it probes the
    actual Unity installation that will load the project, and:

      * copies it, when Unity does not ship it;
      * skips it, when Unity does ship it, and records why;
      * refuses to copy a *framework-shaped* assembly it cannot find in the Unity install
        and lists it as a CONFLICT for a human to decide on, rather than silently
        taking the dependency.

    "Framework-shaped" means the file name matches the BCL naming conventions
    (System.*, Microsoft.*, netstandard.*, mscorlib.*, WindowsBase.*). Those are the
    ones where a duplicate is actively dangerous. Everything else is a normal third-party
    library and is copied.

.PARAMETER Configuration
    Build configuration to publish. Defaults to Release.

.PARAMETER UnityProjectDir
    Path to the Unity project. Defaults to UnityProject/ next to this script.

.PARAMETER DryRun
    Report what would happen without writing anything.

.PARAMETER Force
    Copy framework-shaped assemblies even when Unity appears not to ship them.
    Off by default: a conflict should be a decision, not a default.

.EXAMPLE
    pwsh tools/sync-dlls.ps1
.EXAMPLE
    pwsh tools/sync-dlls.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $UnityProjectDir,
    [switch] $DryRun,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Assemblies that look framework-shaped but which we have confirmed Unity does NOT ship,
# and which MessagePack genuinely loads. Verified by inspecting the published dependency
# closure: MessagePack 2.5.187 -> Microsoft.Bcl.AsyncInterfaces, Microsoft.NET.StringTools,
# System.Collections.Immutable. Each is a distinct package that Unity's own BCL does not
# provide, so importing one cannot shadow anything the Editor already loaded. Listed here
# with their reason so the decision is reviewable rather than hidden behind a -Force flag.
$AlwaysCopy = @{}

# Assemblies the Unity project already obtains through NuGetForUnity. Copying these from
# Core's publish output as well is the specific failure this list prevents: the project
# ends up holding two builds of the same assembly, and Unity resolves whichever it finds
# first, which is a TypeLoadException at a call site far from the cause.
#
# Kept explicit rather than discovered, because discovery over Assets/ races the NuGet
# restore: on the first run after adding a package the folder is not there yet, and a script
# that silently decides "Unity does not have it" on that run is worse than one that refuses
# to guess.
#
# NuGetForUnity resolves a package's whole transitive closure into Assets/Plugins/NuGet,
# so the direct packages declared in Assets/packages.config are NOT the only ones already
# present. Microsoft.Bcl.AsyncInterfaces and System.Collections.Immutable arrive that way,
# as dependencies of MessagePack. Copying Core's older 6.0.0.0 builds of them alongside the
# NuGet 8.0.0.0/7.0.0.0 builds is not a harmless belt-and-braces: the Editor logs
#     "Duplicate assembly 'Microsoft.Bcl.AsyncInterfaces.dll' with different versions
#      detected, using 'Assets/Plugins/NuGet/...' and ignoring
#      'Assets/Plugins/ProjectSpy/...'"
# and binds the copy we just published as nothing at all.
$NuGetSupplied = @{
    'MessagePack.dll'                    = 'NuGet package MessagePack (see Assets/packages.config)'
    'MessagePack.Annotations.dll'        = 'NuGet package MessagePack.Annotations'
    'Microsoft.NET.StringTools.dll'      = 'NuGet package Microsoft.NET.StringTools'
    'Microsoft.Bcl.AsyncInterfaces.dll'  = 'NuGet transitive dependency of MessagePack'
    'System.Collections.Immutable.dll'   = 'NuGet transitive dependency of MessagePack'
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $UnityProjectDir) {
    $UnityProjectDir = Join-Path $RepoRoot 'UnityProject'
}
$UnityProjectDir = (Resolve-Path -LiteralPath $UnityProjectDir).Path

$Projects = @(
    'src/ProjectSpy.Tables/ProjectSpy.Tables.csproj',
    'src/ProjectSpy.Core/ProjectSpy.Core.csproj'
)
$TargetDir = Join-Path $UnityProjectDir 'Assets/Plugins/ProjectSpy'

# The compiled table binaries. Core's own lazy loader walks up from
# AppContext.BaseDirectory looking for assets/data/tables, which inside Unity is the
# Editor install directory rather than this repository, so that search always fails and
# every balance rule quietly falls back to its documented default. The Unity-side
# TableService therefore resolves them from StreamingAssets instead, which means they
# have to be put there. Leaving this to a manual step is what let a Unity build run on
# fallback numbers while looking perfectly healthy.
$TablesSourceDir = Join-Path $RepoRoot 'assets/data/tables'
$TablesTargetDir = Join-Path $UnityProjectDir 'Assets/StreamingAssets/ProjectSpyTables'

function Write-Step { param([string] $Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Message) Write-Host "    [ok]   $Message" -ForegroundColor Green }
function Write-Skip { param([string] $Message) Write-Host "    [skip] $Message" -ForegroundColor DarkGray }

# --- Locate the Unity install ------------------------------------------------
# The Editor version the project pins is the one whose BCL facades will be loaded, so
# that is the install we probe. Reading it from ProjectVersion.txt rather than trusting
# whatever happens to be installed is the difference between a script that works on a
# clean CI agent and one that works only on the machine it was written on.
function Get-UnityDataDirs {
    $versionFile = Join-Path $UnityProjectDir 'ProjectSettings/ProjectVersion.txt'
    if (-not (Test-Path -LiteralPath $versionFile)) {
        throw "Cannot find $versionFile. Is -UnityProjectDir correct?"
    }
    $versionLine = Select-String -LiteralPath $versionFile -Pattern '^m_EditorVersion:\s*(\S+)' |
        Select-Object -First 1
    if (-not $versionLine) { throw "No m_EditorVersion in $versionFile." }
    $version = $versionLine.Matches[0].Groups[1].Value

    $hubRoot = $env:UNITY_HUB_ROOT
    if (-not $hubRoot) {
        $hubRoot = Join-Path ${env:ProgramFiles} 'Unity/Hub/Editor'
    }

    $candidates = @(
        (Join-Path $hubRoot "$version/Editor/Data")
        (Join-Path ${env:ProgramFiles} "Unity/Hub/Editor/$version/Editor/Data")
    )

    foreach ($dir in $candidates) {
        if (Test-Path -LiteralPath $dir) {
            return [pscustomobject]@{ Version = $version; DataDir = (Resolve-Path -LiteralPath $dir).Path }
        }
    }
    throw ("Unity $version not found. Looked in: " + ($candidates -join ', ') +
        ". Install it, or set UNITY_HUB_ROOT.")
}

# Every directory the Editor may load a BCL assembly from. Checked in one go so that a
# "Unity does not ship this" verdict is only reached after all of them have been tried.
function Get-UnityAssemblyDirs {
    param([string] $DataDir)

    $mono = Join-Path $DataDir 'MonoBleedingEdge/lib/mono'
    $dirs = @(
        (Join-Path $mono 'unityjit-win32')
        (Join-Path $mono 'net_4_x-win32')
        (Join-Path $mono 'unityaot-win32')
        (Join-Path $mono 'unityjit-linux')
        (Join-Path $mono 'unityjit-macos')
        (Join-Path $mono '4.8-api')
        (Join-Path $mono '4.7.1-api')
        (Join-Path $mono '2.0-api')
        (Join-Path $mono 'mscorlib')
        (Join-Path $DataDir 'NetStandard/compat/2.1.0/shims/netstandard')
        (Join-Path $DataDir 'NetStandard/ref/2.1.0')
        (Join-Path $DataDir 'Managed')
        (Join-Path $DataDir 'Managed/UnityEngine')
    )
    $dirs | Where-Object { Test-Path -LiteralPath $_ }
}

function Test-UnityShipsAssembly {
    param([string] $Name, [string[]] $UnityDirs)
    foreach ($dir in $UnityDirs) {
        if (Test-Path -LiteralPath (Join-Path $dir $Name)) { return $dir }
    }
    return $null
}

function Test-IsFrameworkShaped {
    param([string] $Name)
    # Deliberately conservative: only names that follow the documented BCL conventions.
    # A false negative here means a normal library gets copied, which is the safe
    # direction to be wrong in.
    return $Name -match '^(System|Microsoft)\..*\.dll$' -or
           $Name -match '^(netstandard|mscorlib|WindowsBase)\.dll$'
}

# --- Publish ---------------------------------------------------------------
Write-Step "Publishing Core and Tables ($Configuration, netstandard2.1)"

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("projectspy-sync-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

try {
    foreach ($proj in $Projects) {
        $projPath = Join-Path $RepoRoot $proj
        if (-not (Test-Path -LiteralPath $projPath)) { throw "Missing project: $projPath" }

        $name = [System.IO.Path]::GetFileNameWithoutExtension($projPath)
        $out = Join-Path $stagingRoot $name

        & dotnet publish $projPath -c $Configuration -o $out --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $proj (exit $LASTEXITCODE)." }
        Write-Ok "published $name"
    }

    # --- Classify -----------------------------------------------------------
    $unity = Get-UnityDataDirs
    Write-Step "Probing Unity $($unity.Version) for assemblies it already ships"
    $unityDirs = Get-UnityAssemblyDirs -DataDir $unity.DataDir
    Write-Ok "searching $($unityDirs.Count) Unity assembly directories"

    # Our own output first, then dependencies, so the report reads in load order.
    $publishDirs = @(
        (Join-Path $stagingRoot 'ProjectSpy.Tables')
        (Join-Path $stagingRoot 'ProjectSpy.Core')
    )

    $toCopy     = New-Object System.Collections.Generic.List[string]
    $skipped    = New-Object System.Collections.Generic.List[string]
    $conflicts  = New-Object System.Collections.Generic.List[string]
    $notes      = New-Object System.Collections.Generic.List[string]
    $seen       = @{}

    foreach ($dir in $publishDirs) {
        foreach ($file in (Get-ChildItem -LiteralPath $dir -Filter '*.dll' | Sort-Object Name)) {
            $name = $file.Name
            if ($seen.ContainsKey($name)) { continue }
            $seen[$name] = $true

            if ($NuGetSupplied.ContainsKey($name)) {
                $skipped.Add("$name  (project NuGet package supplies it: $($NuGetSupplied[$name]))")
                continue
            }

            $shippedIn = Test-UnityShipsAssembly -Name $name -UnityDirs $unityDirs
            if ($shippedIn) {
                $skipped.Add("$name  (Unity ships it: $(Split-Path -Leaf $shippedIn))")
                continue
            }

            if ($AlwaysCopy.ContainsKey($name)) {
                $toCopy.Add($name)
                $notes.Add("$name  (framework-shaped, copied anyway: $($AlwaysCopy[$name]))")
                continue
            }

            if (Test-IsFrameworkShaped -Name $name) {
                # Not found in the install, but named like part of the BCL. Copying it
                # may be required and may also be the thing that breaks the player
                # build, so it is a human decision.
                if ($Force) {
                    $toCopy.Add($name)
                    $skipped.Add("$name  (forced by -Force; Unity does not appear to ship it)")
                } else {
                    $conflicts.Add("$name  (framework-shaped, not found in Unity $($unity.Version). " +
                                  'Unity may supply it from a package at build time. Re-run with -Force to copy it anyway.)')
                }
                continue
            }

            $toCopy.Add($name)
        }
    }

    # --- Report -------------------------------------------------------------
    Write-Host ''
    Write-Step 'Plan'

    Write-Host '  To copy:'
    if ($toCopy.Count -eq 0) { Write-Host '    (nothing)' }
    foreach ($n in $toCopy) { Write-Host "    + $n" }

    Write-Host '  Skipped (Unity already ships it):'
    if ($skipped.Count -eq 0) { Write-Host '    (nothing)' }
    foreach ($n in $skipped) { Write-Host "    - $n" }

    if ($notes.Count -gt 0) {
        Write-Host '  Copied with a note:'
        foreach ($n in $notes) { Write-Host "    ~ $n" }
    }

    if ($conflicts.Count -gt 0) {
        Write-Host ''
        Write-Step 'CONFLICTS - these need a decision'
        Write-Host '  Unity does not appear to ship these, but their names mark them as BCL'
        Write-Host '  assemblies. Copying one can produce a TypeLoadException in the player'
        Write-Host '  build; omitting a required one breaks serialization at runtime.'
        foreach ($n in $conflicts) { Write-Host "    ! $n" }
        Write-Host ''
        Write-Host '  Nothing was silently dropped. Resolve each with -Force (copy) or by'
        Write-Host '  adding it to $AlwaysSkip in this script once you have confirmed a'
        Write-Host '  Unity package supplies it.'
    }

    if ($DryRun) {
        Write-Host ''
        Write-Step 'Dry run - nothing written.'
        return
    }

    # --- Install ------------------------------------------------------------
    Write-Step "Installing into $TargetDir"
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

    $installed = 0
    foreach ($name in $toCopy) {
        $source = $null
        foreach ($dir in $publishDirs) {
            $candidate = Join-Path $dir $name
            if (Test-Path -LiteralPath $candidate) { $source = $candidate; break }
        }
        if (-not $source) { throw "Internal error: $name was classified as copy but not found." }

        Copy-Item -LiteralPath $source -Destination (Join-Path $TargetDir $name) -Force
        $installed++
    }

    # Remove previously-synced assemblies that are no longer part of the dependency
    # set, so a dropped dependency does not linger and keep a stale duplicate alive.
    $expected = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
    foreach ($n in $toCopy) { [void] $expected.Add($n) }
    $removed = 0
    foreach ($file in (Get-ChildItem -LiteralPath $TargetDir -Filter '*.dll' -ErrorAction SilentlyContinue)) {
        if (-not $expected.Contains($file.Name)) {
            if (-not $DryRun) { Remove-Item -LiteralPath $file.FullName -Force }
            $removed++
            Write-Ok "removed stale $($file.Name)"
            # The .meta must go with it. An orphaned .meta keeps its GUID alive in the
            # asset database, so the deleted DLL reappears in the Project window's
            # history and in any assembly definition that still names it.
            $meta = "$($file.FullName).meta"
            if (Test-Path -LiteralPath $meta) {
                Remove-Item -LiteralPath $meta -Force
                Write-Ok "removed stale $($file.Name).meta"
            }
        }
    }

    Write-Host ''
    Write-Step 'Syncing compiled table binaries'

    if (-not (Test-Path -LiteralPath $TablesSourceDir)) {
        Write-Host "    ! No table binaries at $TablesSourceDir. Run 'pwsh tools/gen.ps1' first."
        Write-Host '    ! A Unity build without them runs on Core''s documented fallback balance.'
    }
    elseif ($DryRun) {
        $count = (Get-ChildItem -LiteralPath $TablesSourceDir -Filter '*.bytes').Count
        Write-Host "    ~ $count .bytes would be copied to $TablesTargetDir"
    }
    else {
        New-Item -ItemType Directory -Path $TablesTargetDir -Force | Out-Null

        $expectedTables = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in (Get-ChildItem -LiteralPath $TablesSourceDir -Filter '*.bytes')) {
            $expectedTables.Add($file.Name) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $TablesTargetDir $file.Name) -Force
        }

        # Remove binaries for tables that no longer exist, for the same reason stale DLLs
        # are removed: an orphaned .bytes is a table Unity will happily keep loading.
        foreach ($file in (Get-ChildItem -LiteralPath $TablesTargetDir -Filter '*.bytes' -ErrorAction SilentlyContinue)) {
            if ($expectedTables.Contains($file.Name)) { continue }

            Remove-Item -LiteralPath $file.FullName -Force
            $meta = "$($file.FullName).meta"
            if (Test-Path -LiteralPath $meta) { Remove-Item -LiteralPath $meta -Force }
            Write-Ok "removed stale table $($file.Name)"
        }

        Write-Ok "$($expectedTables.Count) table binaries installed"
    }

    Write-Host ''
    Write-Step "Done. $installed copied, $($skipped.Count) skipped, $($conflicts.Count) conflict(s), $removed removed."
    Write-Host "Unity will import them on next focus. If the Console shows a duplicate-type"
    Write-Host 'error, re-read the conflict list above.'
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
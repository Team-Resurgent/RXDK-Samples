<#
.SYNOPSIS
    Regenerate the committed multi-config rxdk.project.json for every sample from its .vcxproj.

.DESCRIPTION
    The .vcxproj is the authoritative Windows project file; the committed rxdk.project.json is
    derived from it (and is what RXDK-VSCode loads on non-Windows). This script keeps the two in
    sync: it runs the RxdkGenerateManifest MSBuild target (from the installed "Xbox" platform) for
    Debug and Release across all samples, then merges each pair into the committed multi-config
    manifest. Run it after editing any sample .vcxproj.

    Requires MSBuild (Visual Studio or Build Tools with the C++ workload), the RXDK "Xbox" platform
    installed into VCTargetsPath, and Python 3 (for the deterministic combine step).

.PARAMETER Check
    Don't leave changes: regenerate, then fail (exit 1) if any committed rxdk.project.json differs
    from what was regenerated. Used by CI to catch a .vcxproj edit that forgot to refresh manifests.

.PARAMETER Vs20xxRepo
    Path to a RXDK-VS20XX checkout. When given and the Xbox platform isn't already in VCTargetsPath,
    its VcPlatform\Platforms\Xbox payload is copied in first (writing under Program Files needs an
    elevated/admin shell -- CI runners already are). Locally, install the platform from Visual Studio
    (RXDK: Install Xbox Platform) instead of passing this.

.EXAMPLE
    pwsh scripts\Generate-Manifests.ps1
.EXAMPLE
    pwsh scripts\Generate-Manifests.ps1 -Check -Vs20xxRepo ..\RXDK-VS20XX
#>
[CmdletBinding()]
param(
    [switch]$Check,
    [string]$Vs20xxRepo
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir
$SamplesRoot = Join-Path $RepoRoot 'RxdkSamples'

function Info($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "OK  $m" -ForegroundColor Green }
function Die($m)  { Write-Host "ERROR: $m" -ForegroundColor Red; exit 1 }

# --- Locate MSBuild via vswhere -------------------------------------------------
function Get-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $p = & $vswhere -latest -prerelease -requires Microsoft.Component.MSBuild `
                -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($p) { return $p }
    }
    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    Die "MSBuild not found. Install Visual Studio (or Build Tools) with the C++ workload."
}

function Get-Python {
    foreach ($c in 'python', 'python3', 'py') {
        $cmd = Get-Command $c -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }
    Die "Python 3 not found (needed for the manifest combine step)."
}

# --- Find each VS install's VCTargetsPath\Platforms\Xbox (dirs that ship x64) ----
function Get-XboxPlatformDests {
    $dests = @()
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $dests }
    $installs = & $vswhere -all -prerelease -property installationPath
    foreach ($install in $installs) {
        $install = $install.Trim()
        if (-not $install) { continue }
        $vcRoot = Join-Path $install 'MSBuild\Microsoft\VC'
        if (-not (Test-Path $vcRoot)) { continue }
        foreach ($vc in Get-ChildItem -Directory -Path $vcRoot -Filter 'v1*') {
            if (Test-Path (Join-Path $vc.FullName 'Platforms\x64')) {
                $dests += (Join-Path $vc.FullName 'Platforms\Xbox')
            }
        }
    }
    return $dests
}

function Test-XboxPlatformInstalled {
    foreach ($d in Get-XboxPlatformDests) {
        if (Test-Path (Join-Path $d 'Platform.props')) { return $true }
    }
    return $false
}

function Install-XboxPlatform($vs20xx) {
    $src = Join-Path $vs20xx 'RxdkVs.Package\VcPlatform\Platforms\Xbox'
    if (-not (Test-Path (Join-Path $src 'Platform.props'))) {
        Die "Xbox platform payload not found under '$vs20xx' (expected RxdkVs.Package\VcPlatform\Platforms\Xbox)."
    }
    $dests = Get-XboxPlatformDests
    if ($dests.Count -eq 0) { Die "No Visual Studio C++ targets found to install the Xbox platform into." }
    foreach ($d in $dests) {
        Info "Installing Xbox platform -> $d"
        New-Item -ItemType Directory -Force -Path $d | Out-Null
        robocopy $src $d /E /NFL /NDL /NJH /NJS /R:1 /W:1 | Out-Null
    }
    # robocopy exit codes 0-7 are success; anything >=8 is a real failure.
    if ($LASTEXITCODE -ge 8) { Die "robocopy failed installing the Xbox platform (exit $LASTEXITCODE)." }
    $global:LASTEXITCODE = 0
}

# --- Run ------------------------------------------------------------------------
$msbuild = Get-MSBuild
$python  = Get-Python

if (-not (Test-XboxPlatformInstalled)) {
    if ($Vs20xxRepo) {
        Install-XboxPlatform $Vs20xxRepo
    } else {
        Die "The RXDK 'Xbox' MSBuild platform isn't installed. Install it from Visual Studio (RXDK: Install Xbox Platform), or pass -Vs20xxRepo <path-to-RXDK-VS20XX>."
    }
}

Info "Generating per-configuration manifests (Debug + Release) via MSBuild..."
& $msbuild (Join-Path $ScriptDir 'genmanifests.proj') -t:GenAll -nologo -v:m -m
if ($LASTEXITCODE -ne 0) { Die "MSBuild manifest generation failed (exit $LASTEXITCODE)." }

Info "Combining into committed multi-config rxdk.project.json..."
& $python (Join-Path $ScriptDir 'combine-manifests.py')
if ($LASTEXITCODE -ne 0) { Die "Manifest combine failed (exit $LASTEXITCODE)." }

Info "Generating per-sample VS 2022 solutions..."
& $python (Join-Path $ScriptDir 'gen-solutions.py')
if ($LASTEXITCODE -ne 0) { Die "Solution generation failed (exit $LASTEXITCODE)." }

if ($Check) {
    Info "Checking committed manifests are up to date..."
    Push-Location $RepoRoot
    try {
        # Filter in PowerShell rather than via a git pathspec glob (git doesn't treat ** the way
        # shells do, so a pathspec could silently match nothing and hide real drift).
        $dirty = git status --porcelain -- RxdkSamples |
            Where-Object { $_ -match '(rxdk\.project\.json|\.sln)$' }
    } finally {
        Pop-Location
    }
    if ($dirty) {
        Write-Host ""
        Write-Host "Committed manifests/solutions are OUT OF DATE with their .vcxproj:" -ForegroundColor Red
        $dirty -split "`n" | Where-Object { $_ } | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
        Write-Host ""
        # Show WHY they drifted, so a CI failure is diagnosable from the log rather than a guess.
        Push-Location $RepoRoot
        try {
            $first = (($dirty -split "`n" | Where-Object { $_ } | Select-Object -First 1) -replace '^\s*\S+\s+', '').Trim()
            Write-Host "--- content diff (first drifted file: $first) ---" -ForegroundColor Red
            $diff = git diff --no-color -- $first 2>$null
            if ($diff) {
                # A real content difference: print a bounded slice.
                $diff | Select-Object -First 80 | ForEach-Object { Write-Host $_ }
            } elseif ($first) {
                # git diff is empty but status says modified => the drift is line-endings (or mode)
                # only. Make that explicit and show the raw first line of each side.
                Write-Host "(git diff empty -> drift is LINE-ENDINGS/mode only, not content)" -ForegroundColor Yellow
                $repoLine = ((git show "HEAD:$first" 2>$null) -split "`n" | Select-Object -First 1)
                $workRaw  = [System.IO.File]::ReadAllText((Join-Path $RepoRoot $first))
                $workLine = ($workRaw -split "`n" | Select-Object -First 1)
                Write-Host ("  committed first line ends with CR: {0}" -f ($repoLine -match "`r$"))
                Write-Host ("  regenerated first line ends with CR: {0}" -f ($workLine -match "`r$"))
            }
        } finally {
            Pop-Location
        }
        Write-Host ""
        Die "Run scripts\Generate-Manifests.ps1 and commit the result (see the diff above for the cause)."
    }
    Ok "All committed manifests are in sync."
} else {
    Ok "Manifests regenerated. Review 'git diff' and commit."
}

<#
.SYNOPSIS
  Builds Axiom-Setup-v<version>-win-x64.exe from a clean release package folder.

.DESCRIPTION
  Called by Publish-GitHubRelease.ps1 after the clean package is built, so the installer and the
  release ZIP contain exactly the same files. Needs Inno Setup 6 (preinstalled on GitHub's
  windows-latest runners; locally: winget install JRSoftware.InnoSetup). Prints the installer path.
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$SourceDir,
    [Parameter(Mandatory = $true)][string]$OutputDir
)

$ErrorActionPreference = "Stop"

function Find-Iscc {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    return $null
}

$iscc = Find-Iscc
if (-not $iscc) {
    throw "Inno Setup 6 (ISCC.exe) was not found. Install it with: winget install JRSoftware.InnoSetup"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$script = Join-Path $repoRoot "installer\Axiom.iss"
$SourceDir = [System.IO.Path]::GetFullPath($SourceDir)
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

if (-not (Test-Path -LiteralPath (Join-Path $SourceDir "Malx_AI.exe"))) {
    throw "The package folder has no Malx_AI.exe: $SourceDir"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
& $iscc "/Q" "/DAppVersion=$Version" "/DSourceDir=$SourceDir" "/DOutputDir=$OutputDir" $script
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$installer = Join-Path $OutputDir "Axiom-Setup-v$Version-win-x64.exe"
if (-not (Test-Path -LiteralPath $installer)) {
    throw "Inno Setup finished but the installer is missing: $installer"
}

Write-Output $installer

#Requires -Version 5.1
<#
.SYNOPSIS
  Validates and submits or updates YoMosa2009.Axiom in microsoft/winget-pkgs for a published GitHub Release.

.DESCRIPTION
  Runs New-WingetManifest.ps1 (using a local installer or downloading the GitHub Release asset for v<Version>),
  validates the manifests with `winget validate`, and optionally submits the PR using `wingetcreate`.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\Submit-WingetRelease.ps1 -Version 1.12.0 -ValidateOnly

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\Submit-WingetRelease.ps1 -Version 1.12.0
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$InstallerPath = "",
    [string]$Repository = "YoMosa2009/Axiom",
    [switch]$UpdateExisting,
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-RepoRoot {
    $scriptDir = Split-Path -Parent $PSCommandPath
    return (Resolve-Path (Join-Path $scriptDir "..")).Path
}

function Get-AppVersion {
    param([Parameter(Mandatory = $true)][string]$ProjectPath)

    [xml]$project = Get-Content -LiteralPath $ProjectPath -Raw
    $node = Select-Xml -Xml $project -XPath "//Project/PropertyGroup/Version" | Select-Object -First 1
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.Node.InnerText)) {
        throw "Malx_AI.csproj does not contain a Version value."
    }
    return $node.Node.InnerText.Trim()
}

$repoRoot = Get-RepoRoot
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-AppVersion -ProjectPath (Join-Path $repoRoot "Malx_AI\Malx_AI.csproj")
}
$Version = $Version.Trim().TrimStart('v', 'V')
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Stable WinGet release requires MAJOR.MINOR.PATCH version. Found: $Version"
}

$wingetCmd = Get-Command winget -ErrorAction SilentlyContinue
if (-not $wingetCmd) {
    throw "WinGet CLI (winget.exe) was not found on PATH."
}
$wingetCreateCmd = Get-Command wingetcreate -ErrorAction SilentlyContinue
if (-not $wingetCreateCmd) {
    throw "Windows Package Manager Manifest Creator (wingetcreate.exe) was not found. Install it with: winget install Microsoft.WingetCreate"
}

$installerName = "Axiom-Setup-v$Version-win-x64.exe"
$installerUrl = "https://github.com/$Repository/releases/download/v$Version/$installerName"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("axiom-winget-" + [guid]::NewGuid().ToString("N"))
$manifestDir = Join-Path $tempRoot "manifest"
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

try {
    if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
        $ghCmd = Get-Command gh -ErrorAction SilentlyContinue
        if (-not $ghCmd) {
            throw "GitHub CLI (gh.exe) is required when -InstallerPath is not provided."
        }
        Write-Host "Downloading $installerName from release v$Version ($Repository)..." -ForegroundColor Cyan
        & $ghCmd.Source release download "v$Version" --repo $Repository --pattern $installerName --dir $tempRoot
        if ($LASTEXITCODE -ne 0) {
            throw "gh release download failed for v$Version/$installerName with exit code $LASTEXITCODE."
        }
        $InstallerPath = Join-Path $tempRoot $installerName
    }

    $InstallerPath = [System.IO.Path]::GetFullPath($InstallerPath)
    if (-not (Test-Path -LiteralPath $InstallerPath)) {
        throw "Installer file not found: $InstallerPath"
    }

    $sig = Get-AuthenticodeSignature -LiteralPath $InstallerPath
    if ($sig.Status -eq "Valid") {
        $subject = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { "Valid" }
        Write-Host "Installer code signature valid: $subject" -ForegroundColor Green
    }
    else {
        Write-Warning "Installer Authenticode signature status is '$($sig.Status)'. Unsigned installers may be delayed or rejected during microsoft/winget-pkgs review."
    }

    Write-Host "Generating WinGet manifest for YoMosa2009.Axiom $Version..." -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts\New-WingetManifest.ps1") `
        -Version $Version -InstallerPath $InstallerPath -OutputDir $manifestDir | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "New-WingetManifest.ps1 failed with exit code $LASTEXITCODE."
    }

    Write-Host "Validating WinGet manifest..." -ForegroundColor Cyan
    & $wingetCmd.Source validate --manifest $manifestDir
    if ($LASTEXITCODE -ne 0) {
        throw "winget validate failed with exit code $LASTEXITCODE."
    }

    if ($ValidateOnly) {
        Write-Host "WinGet validation succeeded (ValidateOnly; nothing submitted)." -ForegroundColor Green
        exit 0
    }

    $token = $env:WINGET_TOKEN
    if ([string]::IsNullOrWhiteSpace($token)) {
        $ghCmd = Get-Command gh -ErrorAction SilentlyContinue
        if ($ghCmd) {
            $token = (& $ghCmd.Source auth token 2>$null | Select-Object -First 1)
        }
    }
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "Set WINGET_TOKEN or sign in with `gh auth login` (classic repo/public_repo scope) before submitting to microsoft/winget-pkgs."
    }
    $token = $token.Trim()

    if ($UpdateExisting) {
        Write-Host "Submitting WinGet package update for YoMosa2009.Axiom $Version..." -ForegroundColor Cyan
        & $wingetCreateCmd.Source update YoMosa2009.Axiom --version $Version --urls "$installerUrl|x64" --submit --token $token
    }
    else {
        Write-Host "Submitting WinGet package manifest for YoMosa2009.Axiom $Version..." -ForegroundColor Cyan
        & $wingetCreateCmd.Source submit --token $token $manifestDir
    }
    if ($LASTEXITCODE -ne 0) {
        throw "wingetcreate failed with exit code $LASTEXITCODE."
    }

    Write-Host "WinGet pull request submitted for YoMosa2009.Axiom $Version." -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

#Requires -Version 5.1
<#
.SYNOPSIS
  Tests, packages, and publishes the current Axiom version as a GitHub Release.

.DESCRIPTION
  The version is read from Malx_AI/Malx_AI.csproj. Release notes are extracted from
  the matching CHANGELOG.md entry. The clean publish folder, ZIP, and notes file are
  written under E:\Axiom-Updates by default, then the ZIP is uploaded to GitHub.

  Full publishing requires a clean main branch synchronized with origin/main and an
  authenticated GitHub CLI session. Use -PackageOnly to build the local artifacts
  without creating a tag or GitHub Release.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\Publish-GitHubRelease.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\Publish-GitHubRelease.ps1 -PackageOnly
#>
[CmdletBinding()]
param(
    [string]$OutputRoot = "F:\Axiom-Releases",
    [string]$Repository = "YoMosa2009/Axiom",
    [string]$TargetBranch = "main",
    [switch]$PackageOnly,
    [switch]$SkipTests,
    [switch]$SetUpdateDir
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE."
    }
}

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

    $version = $node.Node.InnerText.Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Stable releases require a MAJOR.MINOR.PATCH version. Found: $version"
    }
    return $version
}

function Get-ChangelogNotes {
    param(
        [Parameter(Mandatory = $true)][string]$ChangelogPath,
        [Parameter(Mandatory = $true)][string]$Version
    )

    # UTF-8 explicitly: Windows PowerShell 5.1 otherwise reads a BOM-less file as the ANSI code page,
    # turning non-ASCII characters in the changelog (ellipses, check marks) into mojibake in the published notes.
    $content = Get-Content -LiteralPath $ChangelogPath -Raw -Encoding UTF8
    $escapedVersion = [regex]::Escape($Version)
    $pattern = "(?ms)^##\s+\[V?$escapedVersion\][^\r\n]*\r?\n(?<body>.*?)(?=^##\s+|\z)"
    $match = [regex]::Match($content, $pattern)
    if (-not $match.Success -or [string]::IsNullOrWhiteSpace($match.Groups['body'].Value)) {
        throw "CHANGELOG.md has no release entry for V$Version."
    }

    return $match.Groups['body'].Value.Trim()
}

function Assert-ReleaseCheckout {
    param(
        [Parameter(Mandatory = $true)][string]$Branch,
        [Parameter(Mandatory = $true)][string]$Repo
    )

    $currentBranch = (& git branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0 -or $currentBranch -ne $Branch) {
        throw "Release publishing must run from branch '$Branch'. Current branch: '$currentBranch'."
    }

    $dirty = @(& git status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect the Git working tree."
    }
    if ($dirty.Count -gt 0) {
        throw "Commit or stash all changes before publishing. The release tag must identify the exact source used to build the ZIP."
    }

    # Out-Null: a PowerShell function returns every uncaptured output line, so without it
    # the gh/git chatter became part of the returned commit and GitHub rejected the release
    # with "target_commitish is invalid".
    Invoke-Checked -Command "gh" -Arguments @("auth", "status", "-h", "github.com") | Out-Null
    Invoke-Checked -Command "git" -Arguments @("fetch", "origin", $Branch) | Out-Null

    $head = (& git rev-parse HEAD).Trim()
    $remoteHead = (& git rev-parse "origin/$Branch").Trim()
    if ($LASTEXITCODE -ne 0 -or $head -ne $remoteHead) {
        throw "Local $Branch is not synchronized with origin/$Branch. Push or pull before publishing."
    }

    $remoteUrl = (& git remote get-url origin).Trim()
    if ($LASTEXITCODE -ne 0 -or $remoteUrl -notmatch [regex]::Escape($Repo)) {
        throw "origin does not point to the expected repository '$Repo'. Found: $remoteUrl"
    }

    return $head
}

$repoRoot = Get-RepoRoot
$projectPath = Join-Path $repoRoot "Malx_AI\Malx_AI.csproj"
$changelogPath = Join-Path $repoRoot "CHANGELOG.md"
$cleanPublishScript = Join-Path $repoRoot "scripts\Publish-CleanRelease.ps1"
$testProject = Join-Path $repoRoot "Malx_AI.Tests\Malx_AI.Tests.csproj"
$version = Get-AppVersion -ProjectPath $projectPath
$tag = "v$version"

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$outputDrive = [System.IO.Path]::GetPathRoot($OutputRoot)
if ([string]::IsNullOrWhiteSpace($outputDrive) -or -not (Test-Path -LiteralPath $outputDrive)) {
    throw "The update output drive is unavailable: $outputDrive"
}

$publishFolder = Join-Path $OutputRoot "Axiom-v$version"
$zipPath = Join-Path $OutputRoot "Axiom-v$version-win-x64-clean.zip"
$installerPath = Join-Path $OutputRoot "Axiom-Setup-v$version-win-x64.exe"
$notesPath = Join-Path $OutputRoot "Axiom-v$version-release-notes.md"
$releaseNotes = Get-ChangelogNotes -ChangelogPath $changelogPath -Version $version

$targetCommit = ""
if (-not $PackageOnly) {
    $targetCommit = Assert-ReleaseCheckout -Branch $TargetBranch -Repo $Repository

    # Windows PowerShell 5.1 wraps a redirected native command's stderr in a terminating
    # ErrorRecord under $ErrorActionPreference = "Stop", even when the command's own exit
    # code is the expected, non-error outcome we're checking for below ("no release yet"
    # is gh's normal not-found result here, not a script failure). Suspend the preference
    # for just this probe so `gh` reporting "not found" doesn't abort the whole publish.
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    & gh release view $tag --repo $Repository *> $null
    $existingReleaseExitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorActionPreference
    if ($existingReleaseExitCode -eq 0) {
        throw "GitHub Release $tag already exists. Bump the Axiom version before publishing another release."
    }
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

# Only redirect this account's in-app updater when explicitly asked. Setting it on every
# publish permanently pointed the developer's own Axiom at the build output drive; when that
# drive later filled up or went away, every in-app update failed with nothing but
# "Update download failed" to go on.
if ($SetUpdateDir) {
    try {
        [Environment]::SetEnvironmentVariable(
            "AXIOM_UPDATE_DIR",
            $OutputRoot,
            [EnvironmentVariableTarget]::User)
        $env:AXIOM_UPDATE_DIR = $OutputRoot
        Write-Host "AXIOM_UPDATE_DIR set to $OutputRoot for this Windows account." -ForegroundColor Yellow
    }
    catch {
        Write-Warning "Could not persist AXIOM_UPDATE_DIR for this Windows account: $($_.Exception.Message)"
    }
}

if (-not $SkipTests) {
    Write-Host "Running Axiom tests..." -ForegroundColor Cyan
    # See the matching comment in Publish-CleanRelease.ps1: disable the shared Roslyn
    # compiler server here too so a wedged VBCSCompiler pipe can't hang the test build.
    Invoke-Checked -Command "dotnet" -Arguments @(
        "test", $testProject, "-c", "Release",
        "-p:UseSharedCompilation=false", "-nodeReuse:false")
}

Write-Host "Building clean Axiom $version package in $OutputRoot..." -ForegroundColor Cyan
Invoke-Checked -Command "powershell" -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $cleanPublishScript, "-OutputDir", $publishFolder)

if (-not (Test-Path -LiteralPath $zipPath)) {
    throw "Expected release ZIP was not produced: $zipPath"
}

# The installer is built from the ZIP's own contents, so both downloads ship identical files.
Write-Host "Building the installer from the release ZIP..." -ForegroundColor Cyan
$installerSource = Join-Path $OutputRoot "Axiom-v$version-installer-source"
if (Test-Path -LiteralPath $installerSource) {
    Remove-Item -LiteralPath $installerSource -Recurse -Force
}
Expand-Archive -LiteralPath $zipPath -DestinationPath $installerSource
try {
    Invoke-Checked -Command "powershell" -Arguments @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $repoRoot "scripts\Build-Installer.ps1"),
        "-Version", $version, "-SourceDir", $installerSource, "-OutputDir", $OutputRoot) | Out-Null
}
finally {
    Remove-Item -LiteralPath $installerSource -Recurse -Force -ErrorAction SilentlyContinue
}
if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Expected installer was not produced: $installerPath"
}

$notesDocument = @"
# Axiom V$version

$releaseNotes

## Updating

Existing Axiom V1.7.2+ installations can install this release from the in-app update notification. V1.7.0 and V1.7.1 users must manually install V1.7.2 once because those builds contain a download-verification lock defect. New users: download **Axiom-Setup-v$version-win-x64.exe** and run it (installs for your Windows account, no admin rights needed). Or download the ZIP, extract the complete folder, and run Malx_AI.exe. Windows may show "Windows protected your PC" because Axiom isn't code-signed yet: click **More info**, then **Run anyway**.
"@
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($notesPath, $notesDocument, $utf8WithoutBom)

if ($PackageOnly) {
    Write-Host "Package-only build complete." -ForegroundColor Green
    Write-Host "ZIP       : $zipPath"
    Write-Host "Installer : $installerPath"
    Write-Host "Notes     : $notesPath"
    exit 0
}

Write-Host "Creating GitHub Release $tag..." -ForegroundColor Cyan
Invoke-Checked -Command "gh" -Arguments @(
    "release", "create", $tag, $zipPath, $installerPath,
    "--repo", $Repository,
    "--target", $targetCommit,
    "--title", "Axiom V$version",
    "--notes-file", $notesPath,
    "--latest")

$releaseUrl = (& gh release view $tag --repo $Repository --json url --jq '.url').Trim()
if ($LASTEXITCODE -ne 0) {
    throw "The release was created, but its final URL could not be read."
}

# Published copies live on GitHub; keep only this version's files locally so release builds
# don't pile up on the drive.
# The unpacked build folder (about 900 MB) is only needed to make the ZIP.
Remove-Item -LiteralPath $publishFolder -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $OutputRoot -Force |
    Where-Object { ($_.Name -like "Axiom-*" -or $_.Name -like "winget-*") -and $_.Name -notlike "*$version*" } |
    ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Removed old local package: $($_.Name)"
    }

Write-Host "Axiom $version published successfully." -ForegroundColor Green
Write-Host "Release  : $releaseUrl"
Write-Host "ZIP      : $zipPath"
Write-Host "Installer: $installerPath"
Write-Host "Notes    : $notesPath"

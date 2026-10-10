<#
.SYNOPSIS
  Writes winget manifests (package YoMosa2009.Axiom) for a published release.

.DESCRIPTION
  Run after Publish-GitHubRelease.ps1. Reads the installer's SHA-256 from the local file and points
  winget at the release's download URL. Then validate and submit:

    winget validate <OutputDir>
    wingetcreate submit --token (gh auth token) <OutputDir>        # first listing

  Later releases can skip this script entirely:

    wingetcreate update YoMosa2009.Axiom --version X.Y.Z --urls <installer URL> --submit --token (gh auth token)

  Submitting opens a public pull request on microsoft/winget-pkgs from the owner's GitHub account,
  so it is only done with the owner's go-ahead.
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][string]$OutputDir
)

$ErrorActionPreference = "Stop"

$id = "YoMosa2009.Axiom"
$schemaVersion = "1.9.0"
$installerName = "Axiom-Setup-v$Version-win-x64.exe"
$installerUrl = "https://github.com/YoMosa2009/Axiom/releases/download/v$Version/$installerName"
$sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $InstallerPath).Hash.ToUpperInvariant()

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)

$versionManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schemaVersion.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schemaVersion
"@

$installerManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schemaVersion.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: inno
Scope: user
InstallModes:
- interactive
- silent
- silentWithProgress
UpgradeBehavior: install
MinimumOSVersion: 10.0.19041.0
ProductCode: '{6F3A2C1E-8B7D-4E5A-9C0F-2D4B6A8E1F37}_is1'
Installers:
- Architecture: x64
  InstallerUrl: $installerUrl
  InstallerSha256: $sha256
ManifestType: installer
ManifestVersion: $schemaVersion
"@

$localeManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schemaVersion.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: YoMosa2009
PublisherUrl: https://github.com/YoMosa2009
PublisherSupportUrl: https://github.com/YoMosa2009/Axiom/issues
Author: Mosa
PackageName: Axiom
PackageUrl: https://github.com/YoMosa2009/Axiom
License: PolyForm Noncommercial 1.0.0
LicenseUrl: https://github.com/YoMosa2009/Axiom/blob/main/LICENSE
Copyright: Copyright (c) 2026 Mosa (YoMosa2009)
ShortDescription: A free, local-first AI assistant and agentic workspace for Windows.
Description: |-
  Axiom runs AI models on your own computer (GGUF through llama.cpp, with NVIDIA GPU acceleration),
  on your own OpenAI-compatible server, or through cloud models with your own key. It includes a
  chat with Project Canvas artifacts, a multi-model Workplace, and Agent Access, a coding agent that
  can run commands and edit files with one-click undo. Your chats and data stay on your computer.
Tags:
- ai
- assistant
- chat
- llm
- local-ai
- llama-cpp
- gguf
- agent
- offline
ReleaseNotesUrl: https://github.com/YoMosa2009/Axiom/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: $schemaVersion
"@

[System.IO.File]::WriteAllText((Join-Path $OutputDir "$id.yaml"), $versionManifest, $utf8)
[System.IO.File]::WriteAllText((Join-Path $OutputDir "$id.installer.yaml"), $installerManifest, $utf8)
[System.IO.File]::WriteAllText((Join-Path $OutputDir "$id.locale.en-US.yaml"), $localeManifest, $utf8)
Write-Output $OutputDir

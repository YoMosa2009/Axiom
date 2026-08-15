#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes a clean Axiom release package with no developer chats/settings/secrets.

.DESCRIPTION
  - Builds/publishes Release win-x64 (self-contained single-file via FolderProfile)
  - Scrubs residual personal runtime files from the publish folder
  - Verifies the package does not contain chats, DB, connector state, or OAuth secrets
  - Creates a versioned zip under artifacts/
  - Emits RUN-CLEAN-SMOKE-TEST.cmd so you can launch with an empty temp profile
	(your %LOCALAPPDATA%\Axiom data is NOT touched and is NOT packaged)

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\Publish-CleanRelease.ps1
#>
[CmdletBinding()]
param(
	[string]$Configuration = "Release",
	[string]$Runtime = "win-x64",
	[string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-RepoRoot {
	$scriptDir = Split-Path -Parent $PSCommandPath
	return (Resolve-Path (Join-Path $scriptDir "..")).Path
}

function Get-AppVersion {
	param([string]$CsprojPath)
	[xml]$xml = Get-Content -LiteralPath $CsprojPath -Raw
	$versionNode = Select-Xml -Xml $xml -XPath "//Project/PropertyGroup/Version" | Select-Object -First 1
	if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.Node.InnerText)) {
		return "0.0.0"
	}
	return $versionNode.Node.InnerText.Trim()
}

function Remove-UserDataFromDirectory {
	param([string]$Root)

	if (-not (Test-Path -LiteralPath $Root)) {
		return
	}

	$dirNames = @(
		"ChatHistory",
		"WebView2",
		"logs",
		"Models",
		"KvStates",
		"CouncilKvStates",
		"WorkplaceExports"
	)

	foreach ($name in $dirNames) {
		$path = Join-Path $Root $name
		if (Test-Path -LiteralPath $path) {
			Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
		}
	}

	$filePatterns = @(
		"axiom_data.db*",
		"mcp_connector_state*",
		"*_client_secret.txt",
		"*_client_id.txt",
		"google_oauth*",
		"todoist_client*",
		"github_oauth*",
		"workplace_session.json*",
		"chat_*.json*",
		"chats_index.json*",
		"*_advanced_state.json*",
		"chat_workspace_state.json*",
		"smart_compaction_settings.json*",
		"*.dpapi"
	)

	foreach ($pattern in $filePatterns) {
		Get-ChildItem -LiteralPath $Root -File -Force -ErrorAction SilentlyContinue |
			Where-Object { $_.Name -like $pattern } |
			Remove-Item -Force -ErrorAction SilentlyContinue

		Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction SilentlyContinue |
			Where-Object { $_.Name -like $pattern } |
			Remove-Item -Force -ErrorAction SilentlyContinue
	}
}

function Remove-NonWindowsRuntimeAssets {
	param([string]$Root)

	# Axiom's release target is win-x64. Some native NuGet packages copy their Linux
	# payload beside the Windows binaries even for an RID-specific publish; those files
	# added roughly 450 MB of unusable data to every release and update download.
	$runtimeRoot = Join-Path $Root "runtimes"
	if (-not (Test-Path -LiteralPath $runtimeRoot)) {
		return
	}

	Get-ChildItem -LiteralPath $runtimeRoot -Directory -Force |
		Where-Object { $_.Name -notlike "win-*" } |
		Remove-Item -Recurse -Force
}

function Write-PackageHelperFiles {
	param([string]$Root)

	$dataLocation = @"
Axiom does not store chats, settings, API keys, Workplace sessions, or connector tokens inside this install folder.
On first launch each user gets an empty profile under:
  %LOCALAPPDATA%\Axiom

Debug/Visual Studio runs use a separate profile:
  %LOCALAPPDATA%\Axiom-Dev

To smoke-test this package without your existing release profile, run:
  RUN-CLEAN-SMOKE-TEST.cmd
"@
	Set-Content -LiteralPath (Join-Path $Root "DATA_LOCATION.txt") -Value $dataLocation -Encoding UTF8

	$smokeCmd = @"
@echo off
setlocal
rem Launches Axiom with an empty temporary profile so this machine's %LOCALAPPDATA%\Axiom data is not used.
set "AXIOM_DATA_DIR=%TEMP%\Axiom-CleanSmoke-%RANDOM%%RANDOM%"
mkdir "%AXIOM_DATA_DIR%" >nul 2>&1
echo Using clean profile: %AXIOM_DATA_DIR%
start "" "%~dp0Malx_AI.exe"
endlocal
"@
	Set-Content -LiteralPath (Join-Path $Root "RUN-CLEAN-SMOKE-TEST.cmd") -Value $smokeCmd -Encoding ASCII
}

function Test-PackageIsClean {
	param([string]$Root)

	$forbiddenNameRegex = [regex]::new(
		'(?i)^(chat_\d+\.json.*|chats_index\.json.*|workplace_session\.json.*|.*_advanced_state\.json.*|chat_workspace_state\.json.*|smart_compaction_settings\.json.*|axiom_data\.db.*|mcp_connector_state.*|.*_client_secret\.txt|.*_client_id\.txt|google_oauth.*|todoist_client.*|github_oauth.*|.*\.dpapi)$',
		[System.Text.RegularExpressions.RegexOptions]::CultureInvariant)

	$forbiddenDirRegex = [regex]::new(
		'(?i)^(ChatHistory|WebView2|logs|KvStates|CouncilKvStates|WorkplaceExports)$',
		[System.Text.RegularExpressions.RegexOptions]::CultureInvariant)

	$hits = New-Object System.Collections.Generic.List[string]

	Get-ChildItem -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
		if ($_.PSIsContainer) {
			if ($forbiddenDirRegex.IsMatch($_.Name)) {
				$hits.Add($_.FullName)
			}
		}
		elseif ($forbiddenNameRegex.IsMatch($_.Name)) {
			$hits.Add($_.FullName)
		}
	}

	return $hits
}

function Write-UpdateManifest {
	param([string]$Root)

	$manifestName = "AXIOM_UPDATE_MANIFEST.txt"
	$manifestPath = Join-Path $Root $manifestName
	if (Test-Path -LiteralPath $manifestPath) {
		Remove-Item -LiteralPath $manifestPath -Force
	}

	$relativeFiles = @(
		Get-ChildItem -LiteralPath $Root -Recurse -File -Force |
			ForEach-Object { $_.FullName.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/') } |
			Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
			Sort-Object -Unique
	)
	$relativeFiles += $manifestName
	Set-Content -LiteralPath $manifestPath -Value ($relativeFiles | Sort-Object -Unique) -Encoding UTF8
}

$repoRoot = Get-RepoRoot
$projectPath = Join-Path $repoRoot "Malx_AI\Malx_AI.csproj"
$profilePath = Join-Path $repoRoot "Malx_AI\Properties\PublishProfiles\FolderProfile.pubxml"

if (-not (Test-Path -LiteralPath $projectPath)) {
	throw "Project not found: $projectPath"
}
if (-not (Test-Path -LiteralPath $profilePath)) {
	throw "Publish profile not found: $profilePath"
}

$version = Get-AppVersion -CsprojPath $projectPath
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
	$OutputDir = Join-Path $repoRoot "artifacts\Axiom-Release"
}

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$artifactsRoot = Split-Path -Parent $OutputDir
$zipPath = Join-Path $artifactsRoot ("Axiom-v{0}-win-x64-clean.zip" -f $version)

Write-Host "=== Axiom clean release publish ===" -ForegroundColor Cyan
Write-Host "Version : $version"
Write-Host "Project : $projectPath"
Write-Host "Output  : $OutputDir"
Write-Host "Zip     : $zipPath"
Write-Host ""

if (Test-Path -LiteralPath $OutputDir) {
	Write-Host "Removing previous publish folder..."
	Remove-Item -LiteralPath $OutputDir -Recurse -Force
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

Write-Host "Embedding local OAuth app credentials into release built-ins (gitignored generated source)..."
$generateScript = Join-Path $repoRoot "scripts\Generate-McpOAuthBuiltIns.ps1"
if (Test-Path -LiteralPath $generateScript) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $generateScript
} else {
    Write-Warning "Generate-McpOAuthBuiltIns.ps1 not found - connectors may require machine SharedOAuth files."
}

Write-Host "Publishing ($Configuration / $Runtime)..."
# Folder publish (not single-file): WinAppSDK single-file fails SxS activation
# with SessionHandleIPCProxyStub.dll duplicate-name errors on startup.
#
# UseSharedCompilation=false / nodeReuse:false: some machines (seen with Visual Studio
# holding the same project open in a live session) can wedge the Roslyn VBCSCompiler
# named-pipe handshake -- the build server process starts, the pipe connects, a
# compilation request is written, and the response never arrives, hanging the publish
# indefinitely with no error. Forcing csc.exe to run as a plain one-shot process (no
# shared server, no MSBuild node reuse) avoids that IPC path entirely. It costs a little
# wall-clock time on a cold compile but a release publish is not the hot inner loop.
dotnet publish $projectPath `
	-c $Configuration `
	-r $Runtime `
	--self-contained true `
	-p:PublishProfile=FolderProfile `
	-p:PublishDir="$OutputDir\" `
	-p:DeleteExistingFiles=true `
	-p:PublishSingleFile=false `
	-p:PublishReadyToRun=true `
	-p:WindowsAppSDKSelfContained=true `
	-p:WindowsPackageType=None `
	-p:UseSharedCompilation=false `
	-nodeReuse:false

if ($LASTEXITCODE -ne 0) {
	throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Scrubbing residual personal runtime files..."
Remove-UserDataFromDirectory -Root $OutputDir
Write-Host "Removing non-Windows native runtime assets..."
Remove-NonWindowsRuntimeAssets -Root $OutputDir
Write-PackageHelperFiles -Root $OutputDir

Write-Host "Verifying package contains no personal data..."
$dirtyList = New-Object System.Collections.Generic.List[string]
foreach ($item in @(Test-PackageIsClean -Root $OutputDir)) {
	if (-not [string]::IsNullOrWhiteSpace([string]$item)) {
		[void]$dirtyList.Add([string]$item)
	}
}
if ($dirtyList.Count -gt 0) {
	Write-Host "ERROR: Personal/runtime data still present in package:" -ForegroundColor Red
	$dirtyList | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
	throw "Clean publish verification failed."
}

Write-Host "Writing updater file manifest..."
Write-UpdateManifest -Root $OutputDir

$exePath = Join-Path $OutputDir "Malx_AI.exe"
if (-not (Test-Path -LiteralPath $exePath)) {
	throw "Published executable missing: $exePath"
}

if (Test-Path -LiteralPath $zipPath) {
	Remove-Item -LiteralPath $zipPath -Force
}

Write-Host "Creating zip..."
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
	$OutputDir,
	$zipPath,
	[System.IO.Compression.CompressionLevel]::Optimal,
	$false)

$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
	if ($archive.Entries.Count -eq 0 -or -not ($archive.Entries.FullName -contains "AXIOM_UPDATE_MANIFEST.txt")) {
		throw "Generated ZIP is incomplete or missing AXIOM_UPDATE_MANIFEST.txt."
	}
}
finally {
	$archive.Dispose()
}

Write-Host ""
Write-Host "Clean package ready." -ForegroundColor Green
Write-Host "Folder : $OutputDir"
Write-Host "Zip    : $zipPath"
Write-Host ""
Write-Host "Notes:" -ForegroundColor Yellow
Write-Host "  - Package is a self-contained folder (not single-file) so WinAppSDK/native deps start correctly."
Write-Host "  - Upload the generated ZIP as a GitHub Release asset and tag it v$version (or V$version)."
Write-Host "  - Axiom 1.7+ can download, verify, swap, and restart this exact ZIP automatically."
Write-Host "  - AXIOM_UPDATE_MANIFEST.txt is required; do not modify the ZIP after publishing."
Write-Host "  - First-time users can still unzip and run Malx_AI.exe (keep all files together)."
Write-Host "  - End users start with an empty %LOCALAPPDATA%\Axiom profile."
Write-Host "  - Your own chats/settings stay on this PC under LocalAppData and are NOT inside the zip."
Write-Host "  - To test the package as a brand-new user on this machine, run:"
Write-Host "      $OutputDir\RUN-CLEAN-SMOKE-TEST.cmd"
Write-Host "  - Settings > General > Local data can reset the active profile if needed."

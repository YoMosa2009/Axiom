#Requires -Version 5.1
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$OutputDir = Join-Path $repoRoot "artifacts\Axiom-Release"
$zipPath = Join-Path $repoRoot "artifacts\Axiom-v1.6.0-win-x64-clean.zip"

if (-not (Test-Path -LiteralPath (Join-Path $OutputDir "Malx_AI.exe"))) {
	throw "Published exe missing at $OutputDir. Run Publish-CleanRelease.ps1 first."
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
	$path = Join-Path $OutputDir $name
	if (Test-Path -LiteralPath $path) {
		Remove-Item -LiteralPath $path -Recurse -Force
	}
}

$patterns = @(
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

foreach ($pattern in $patterns) {
	Get-ChildItem -LiteralPath $OutputDir -Recurse -File -Force -ErrorAction SilentlyContinue |
		Where-Object { $_.Name -like $pattern } |
		Remove-Item -Force -ErrorAction SilentlyContinue
}

$dataLocation = @"
Axiom does not store chats, settings, API keys, Workplace sessions, or connector tokens inside this install folder.
On first launch each user gets an empty profile under:
  %LOCALAPPDATA%\Axiom

Debug/Visual Studio runs use a separate profile:
  %LOCALAPPDATA%\Axiom-Dev

To smoke-test this package without your existing release profile, run:
  RUN-CLEAN-SMOKE-TEST.cmd
"@
Set-Content -LiteralPath (Join-Path $OutputDir "DATA_LOCATION.txt") -Value $dataLocation -Encoding UTF8

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
Set-Content -LiteralPath (Join-Path $OutputDir "RUN-CLEAN-SMOKE-TEST.cmd") -Value $smokeCmd -Encoding ASCII

$forbiddenNameRegex = [regex]::new('(?i)^(chat_\d+\.json.*|chats_index\.json.*|workplace_session\.json.*|.*_advanced_state\.json.*|chat_workspace_state\.json.*|smart_compaction_settings\.json.*|axiom_data\.db.*|mcp_connector_state.*|.*_client_secret\.txt|.*_client_id\.txt|google_oauth.*|todoist_client.*|github_oauth.*|.*\.dpapi)$')
$forbiddenDirRegex = [regex]::new('(?i)^(ChatHistory|WebView2|logs|KvStates|CouncilKvStates|WorkplaceExports)$')
$dirty = New-Object System.Collections.Generic.List[string]

Get-ChildItem -LiteralPath $OutputDir -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
	if ($_.PSIsContainer) {
		if ($forbiddenDirRegex.IsMatch($_.Name)) { [void]$dirty.Add($_.FullName) }
	}
	elseif ($forbiddenNameRegex.IsMatch($_.Name)) {
		[void]$dirty.Add($_.FullName)
	}
}

if ($dirty.Count -gt 0) {
	Write-Host "ERROR: Personal/runtime data still present:" -ForegroundColor Red
	$dirty | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
	throw "Clean package verification failed."
}

if (Test-Path -LiteralPath $zipPath) {
	Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $OutputDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ""
Write-Host "CLEAN PACKAGE READY" -ForegroundColor Green
Write-Host "Folder : $OutputDir"
Write-Host "Zip    : $zipPath"
Get-Item -LiteralPath $zipPath | Format-List FullName, Length, LastWriteTime
Write-Host "Top-level contents:"
Get-ChildItem -LiteralPath $OutputDir |
	Select-Object Name, @{ Name = "SizeMB"; Expression = { if ($_.PSIsContainer) { "dir" } else { [math]::Round($_.Length / 1MB, 2) } } } |
	Format-Table -AutoSize

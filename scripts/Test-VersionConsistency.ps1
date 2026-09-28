#Requires -Version 5.1
<#
.SYNOPSIS
  Fails when the release version surfaces listed in VERSIONING.md disagree.

.DESCRIPTION
  Checks that the <Version> in Malx_AI/Malx_AI.csproj also appears in the Settings
  version label (MainWindow.xaml), the README release badge, and a CHANGELOG.md entry.
  Used by CI on every pull request and before the release workflow publishes.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
[xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot "Malx_AI\Malx_AI.csproj") -Raw
$node = Select-Xml -Xml $project -XPath "//Project/PropertyGroup/Version" | Select-Object -First 1
if ($null -eq $node) {
	throw "Malx_AI.csproj does not contain a Version value."
}
$version = $node.Node.InnerText.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
	throw "Stable releases require a MAJOR.MINOR.PATCH version. Found: $version"
}
$escaped = [regex]::Escape($version)

$checks = @(
	@{ File = "Malx_AI\MainWindow.xaml"; Pattern = "x:Name=`"AppVersionLabel`" Text=`"Version $escaped`""; What = "Settings version label" },
	@{ File = "README.md"; Pattern = "badge/release-V$escaped-"; What = "README release badge" },
	@{ File = "CHANGELOG.md"; Pattern = "(?m)^##\s+\[V?$escaped\]"; What = "CHANGELOG.md entry" }
)

$failures = @()
foreach ($check in $checks) {
	$content = Get-Content -LiteralPath (Join-Path $repoRoot $check.File) -Raw -Encoding UTF8
	if ($content -notmatch $check.Pattern) {
		$failures += "$($check.What) ($($check.File)) does not match version $version."
	}
}

if ($failures.Count -gt 0) {
	$failures | ForEach-Object { Write-Host "ERROR: $_" -ForegroundColor Red }
	throw "Version surfaces are out of sync. See VERSIONING.md."
}

Write-Host "All version surfaces match $version." -ForegroundColor Green

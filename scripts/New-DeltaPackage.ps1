<#
.SYNOPSIS
    Builds a delta update package (.axdelta) between two Axiom release payloads.

.DESCRIPTION
    The package holds only the files of the new release that differ from the base release,
    plus AXIOM_DELTA.json listing the SHA-256 of EVERY file in the new release. Installed
    copies on the base version download it instead of the full ZIP and rebuild the new
    package locally (Malx_AI/UpdateDeltaPackage.cs); any mismatch falls back to the full ZIP.
    The .axdelta extension keeps it invisible to updaters older than this feature.

.EXAMPLE
    .\scripts\New-DeltaPackage.ps1 -NewPayload C:\t\new -BasePayload C:\t\old -BaseVersion 1.14.0 -TargetVersion 1.15.0 -OutputDirectory C:\t
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$NewPayload,
    [Parameter(Mandatory = $true)][string]$BasePayload,
    [Parameter(Mandatory = $true)][string]$BaseVersion,
    [Parameter(Mandatory = $true)][string]$TargetVersion,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Find-PayloadRoot([string]$root) {
    $exe = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter 'Axiom Harness.exe')
    if ($exe.Count -ne 1) { throw "Expected exactly one 'Axiom Harness.exe' under $root, found $($exe.Count)." }
    return $exe[0].DirectoryName
}

function Get-RelativePath([string]$root, [string]$path) {
    return $path.Substring($root.TrimEnd('\').Length + 1).Replace('\', '/')
}

$newRoot = Find-PayloadRoot $NewPayload
$baseRoot = Find-PayloadRoot $BasePayload
$work = Join-Path ([IO.Path]::GetTempPath()) ("AxiomDelta-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null

try {
    $files = [ordered]@{}
    $changed = 0
    $changedBytes = 0L
    foreach ($file in Get-ChildItem -LiteralPath $newRoot -Recurse -File | Sort-Object FullName) {
        $relative = Get-RelativePath $newRoot $file.FullName
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        $files[$relative] = $hash

        $basePath = Join-Path $baseRoot ($relative.Replace('/', '\'))
        $same = (Test-Path -LiteralPath $basePath -PathType Leaf) -and `
            ((Get-FileHash -LiteralPath $basePath -Algorithm SHA256).Hash.ToUpperInvariant() -eq $hash)
        if (-not $same) {
            $destination = Join-Path $work ($relative.Replace('/', '\'))
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
            $changed++
            $changedBytes += $file.Length
        }
    }

    $index = [ordered]@{ base = $BaseVersion; target = $TargetVersion; files = $files }
    $json = $index | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText((Join-Path $work 'AXIOM_DELTA.json'), $json, (New-Object System.Text.UTF8Encoding($false)))

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $name = "Axiom-v$TargetVersion-delta-from-v$BaseVersion.axdelta"
    $output = Join-Path $OutputDirectory $name
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($work, $output, [IO.Compression.CompressionLevel]::Optimal, $false)

    $size = (Get-Item -LiteralPath $output).Length
    Write-Host ("Delta {0}: {1} of {2} files changed ({3:N1} MB raw), package {4:N1} MB." -f `
        $name, $changed, $files.Count, ($changedBytes / 1MB), ($size / 1MB))
    Write-Output $output
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

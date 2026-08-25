param(
    [string]$OutputRoot = "release",
    [string]$Runtime = "win-x64",
    [switch]$IncludeFfmpeg
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$releaseRoot = if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputRoot))
}

function Reset-ReleaseDirectory {
    param([string]$Path)
    $resolved = [System.IO.Path]::GetFullPath($Path)
    $prefix = $releaseRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the release root: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    New-Item -ItemType Directory -Path $resolved | Out-Null
}

$portable = Join-Path $releaseRoot "AichanToolbox-Portable-$Runtime"
$slim = Join-Path $releaseRoot "AichanToolbox-Slim-$Runtime"
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
Reset-ReleaseDirectory $portable
Reset-ReleaseDirectory $slim

& (Join-Path $projectRoot "build.ps1") -Publish -SelfContained -Runtime $Runtime -OutputDirectory $portable -IncludeFfmpeg:$IncludeFfmpeg
if ($LASTEXITCODE -ne 0) { throw "Portable publish failed." }

& (Join-Path $projectRoot "build.ps1") -Publish -Runtime $Runtime -OutputDirectory $slim -IncludeFfmpeg:$IncludeFfmpeg
if ($LASTEXITCODE -ne 0) { throw "Slim publish failed." }

Write-Host "Publish completed:"
Write-Host "  Portable: $portable"
Write-Host "  Slim:     $slim"
if (-not $IncludeFfmpeg) { Write-Host "  FFmpeg:   not included (optional compatibility component)" }

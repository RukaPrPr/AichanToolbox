param(
    [switch]$Publish,
    [switch]$SelfContained,
    [switch]$IncludeFfmpeg,
    [string]$Runtime = "win-x64",
    [string]$OutputDirectory = "publish"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$runtimePatch = "10.0.11"
$includeFfmpegValue = if ($IncludeFfmpeg) { "true" } else { "false" }
$publishPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot ".dotnet_cli"
$env:NUGET_PACKAGES = Join-Path $projectRoot ".nuget_packages"
$env:APPDATA = Join-Path $projectRoot ".appdata"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

Push-Location (Join-Path $projectRoot "frontend")
try {
    if (-not (Test-Path -LiteralPath "node_modules")) {
        pnpm install --frozen-lockfile=false
        if ($LASTEXITCODE -ne 0) { throw "pnpm install failed (exit code $LASTEXITCODE)." }
    }
    pnpm build
    if ($LASTEXITCODE -ne 0) { throw "Frontend build failed (exit code $LASTEXITCODE)." }
}
finally { Pop-Location }

Push-Location (Join-Path $projectRoot "desktop")
try {
    if ($Publish -and $SelfContained) {
        dotnet restore -r $Runtime -p:RuntimeFrameworkVersion=$runtimePatch "-p:IncludeFfmpeg=$includeFfmpegValue" --configfile NuGet.Config
    }
    else {
        dotnet restore "-p:IncludeFfmpeg=$includeFfmpegValue" --configfile NuGet.Config
    }
    if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed (exit code $LASTEXITCODE)." }
    if ($Publish) {
        if ($SelfContained) {
            dotnet publish -c Release -r $Runtime --self-contained true -p:RuntimeFrameworkVersion=$runtimePatch -p:PublishSingleFile=false "-p:IncludeFfmpeg=$includeFfmpegValue" --no-restore -o $publishPath
        }
        else {
            dotnet publish -c Release --self-contained false "-p:IncludeFfmpeg=$includeFfmpegValue" --no-restore -o $publishPath
        }
        if ($LASTEXITCODE -ne 0) { throw "WPF publish failed (exit code $LASTEXITCODE)." }
    }
    else {
        dotnet build -c Release "-p:IncludeFfmpeg=$includeFfmpegValue" --no-restore
        if ($LASTEXITCODE -ne 0) { throw "WPF build failed (exit code $LASTEXITCODE)." }
    }
}
finally { Pop-Location }

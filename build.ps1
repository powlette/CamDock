<#
  One-click build for CamDock.
  Produces a self-contained single-file exe in .\PUBLISH (no .NET runtime install needed on target PCs).
  Requires the .NET 8 SDK on the build machine: https://dotnet.microsoft.com/download/dotnet/8.0
#>
[CmdletBinding()]
param([switch]$NoClean)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "The .NET 8 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run this script again." -ForegroundColor Red
    exit 1
}
$sdks = & dotnet --list-sdks
if (-not ($sdks -match '^(8|9|10)\.')) {
    Write-Host "A .NET 8 (or newer) SDK is required. Installed SDKs:`n$sdks" -ForegroundColor Red
    exit 1
}

if (-not $NoClean) {
    foreach ($d in 'PUBLISH', 'bin', 'obj') { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
}

dotnet publish CamDock.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o PUBLISH
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed (exit code $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $PSScriptRoot 'PUBLISH\CamDock.exe'
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "`nBuilt $exe ($size MB)" -ForegroundColor Green

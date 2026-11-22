<#
.SYNOPSIS
  Builds a self-contained single-file TokenViewer.exe for Windows.

.EXAMPLE
  ./scripts/publish.ps1                 # win-x64, Release
  ./scripts/publish.ps1 -Runtime win-arm64
#>
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "dist/$Runtime"

Write-Host "Running tests..."
dotnet test (Join-Path $root "tests/TokenViewer.Core.Tests") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

Write-Host "Publishing TokenViewer ($Runtime, $Configuration)..."
dotnet publish (Join-Path $root "src/TokenViewer.App/TokenViewer.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

Write-Host ""
Write-Host "Done: $out\TokenViewer.exe"

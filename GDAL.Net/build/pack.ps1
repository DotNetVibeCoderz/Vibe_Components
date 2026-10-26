<#
.SYNOPSIS
    Builds the Rust kernels, runs the tests and packs the GDAL.Net NuGet packages into ./artifacts.
.EXAMPLE
    ./build/pack.ps1
    ./build/pack.ps1 -Version 1.0.1 -SkipTests
#>
param(
    [string] $Version,
    [string] $Configuration = "Release",
    [switch] $SkipTests,
    [switch] $SkipNative
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root "artifacts"
New-Item -ItemType Directory -Force $artifacts | Out-Null

$versionArgs = @()
if ($Version) { $versionArgs = @("-p:Version=$Version") }

if (-not $SkipNative -and (Get-Command cargo -ErrorAction SilentlyContinue)) {
    & (Join-Path $PSScriptRoot "build-native.ps1")
}

if (-not $SkipTests) {
    dotnet test --project (Join-Path $root "tests/GDAL.Net.Tests") -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

$projects = @("src/GDAL.Net", "src/GDAL.Net.Blazor", "src/GDAL.Net.Avalonia")
foreach ($p in $projects) {
    dotnet pack (Join-Path $root $p) -c $Configuration -o $artifacts @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "Pack failed for $p." }
}

Get-ChildItem $artifacts -Filter *.nupkg | Sort-Object LastWriteTime -Descending | Select-Object Name, Length

<#
.SYNOPSIS
    Builds, tests and packs the Cesium.Net NuGet packages into ./artifacts.
.EXAMPLE
    ./build/pack.ps1
    ./build/pack.ps1 -Version 1.0.1
#>
param(
    [string] $Version,
    [string] $Configuration = "Release",
    [switch] $SkipTests
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root "artifacts"
New-Item -ItemType Directory -Force $artifacts | Out-Null

$versionArgs = @()
if ($Version) { $versionArgs = @("-p:Version=$Version") }

if (-not $SkipTests) {
    dotnet test (Join-Path $root "tests/Cesium.Net.Tests") -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

$projects = @("src/Cesium.Net", "src/Cesium.Net.Blazor", "src/Cesium.Net.Avalonia")
foreach ($p in $projects) {
    dotnet pack (Join-Path $root $p) -c $Configuration -o $artifacts @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "Pack failed for $p." }
}

Get-ChildItem $artifacts -Filter *.nupkg | Sort-Object LastWriteTime -Descending | Select-Object Name, Length

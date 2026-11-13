# Packs every Medical.Net NuGet package (libraries, CLI tool, templates) into artifacts/packages.
# Usage: ./build/pack.ps1 [-Version 1.0.0] [-SkipNative] [-SkipTests]
param(
    [string]$Version = "1.0.0",
    [switch]$SkipNative,
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts/packages"
New-Item -ItemType Directory -Force $out | Out-Null

if (-not $SkipNative -and (Get-Command cargo -ErrorAction SilentlyContinue)) { & "$PSScriptRoot/build-native.ps1" }

Push-Location $root
try {
    if (-not $SkipTests) {
        dotnet test tests/Medical.Tests -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
    }
    $projects = @(Get-ChildItem src -Recurse -Filter *.csproj) + (Get-Item tools/Medical.Cli/Medical.Cli.csproj) + (Get-Item templates/Medical.Templates/Medical.Templates.csproj)
    foreach ($p in $projects) {
        dotnet pack $p.FullName -c Release -o $out -p:Version=$Version --nologo
        if ($LASTEXITCODE -ne 0) { throw "Pack failed: $($p.Name)" }
    }
    Get-ChildItem $out -Filter "*.$Version.nupkg" | Select-Object Name, Length | Format-Table -AutoSize
} finally { Pop-Location }

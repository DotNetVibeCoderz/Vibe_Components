# Builds the native kernels (if cargo is available), runs the tests and packs every library into artifacts/packages.
# Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
param(
    [string]$Version = "",
    [switch]$SkipTests,
    [switch]$SkipNative
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (-not $SkipNative -and (Get-Command cargo -ErrorAction SilentlyContinue)) {
        & "$root/native/build.ps1"
    }
    if (-not $SkipTests) {
        dotnet test tests/GnuRadio.Net.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw "Tests failed; not packing." }
    }
    $versionArg = if ($Version) { "-p:Version=$Version" } else { "" }
    $projects = "GnuRadio.Net", "GnuRadio.Net.Blazor", "GnuRadio.Net.Avalonia", "GnuRadio.Net.Hardware", "GnuRadio.Net.ZeroMQ", "GnuRadio.Net.Grc"
    foreach ($p in $projects) {
        dotnet pack "src/$p/$p.csproj" -c Release $versionArg
        if ($LASTEXITCODE -ne 0) { throw "Packing $p failed." }
    }
    Get-ChildItem artifacts/packages -Filter *.nupkg | ForEach-Object { Write-Host $_.Name }
} finally {
    Pop-Location
}

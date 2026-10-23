<#
.SYNOPSIS
    Pushes the packages in ./artifacts to nuget.org.
.DESCRIPTION
    The API key is read from the credentials file (line "Nuget Api Key: ...") or the
    NUGET_API_KEY environment variable. The key is never printed or written anywhere.
.EXAMPLE
    ./build/pack.ps1 -Version 1.0.0
    ./build/publish.ps1 -Version 1.0.0
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $CredentialsFile = "C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt",
    [string] $Source = "https://api.nuget.org/v3/index.json"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root "artifacts"

$key = $env:NUGET_API_KEY
if (-not $key -and (Test-Path $CredentialsFile)) {
    $line = Get-Content $CredentialsFile | Where-Object { $_ -match '^\s*Nuget Api Key\s*:' } | Select-Object -First 1
    if ($line) { $key = ($line -split ':', 2)[1].Trim() }
}
if (-not $key) { throw "NuGet API key not found (set NUGET_API_KEY or check $CredentialsFile)." }

$packages = "Cesium.Net", "Cesium.Net.Blazor", "Cesium.Net.Avalonia" | ForEach-Object { Join-Path $artifacts "$_.$Version.nupkg" }
foreach ($pkg in $packages) {
    if (-not (Test-Path $pkg)) { throw "Missing $pkg — run ./build/pack.ps1 -Version $Version first." }
}

foreach ($pkg in $packages) {
    Write-Host "Pushing $(Split-Path $pkg -Leaf)…"
    dotnet nuget push $pkg --api-key $key --source $Source --skip-duplicate
    if ($LASTEXITCODE -ne 0) { throw "Push failed for $pkg." }
}
Write-Host "Done. Packages appear on nuget.org after validation (usually a few minutes)."

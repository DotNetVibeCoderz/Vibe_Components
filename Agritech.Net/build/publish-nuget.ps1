# Pushes packages from artifacts/packages to nuget.org.
# The API key is read at run time from a credentials file OUTSIDE the repository (never commit keys).
# Usage: ./build/publish-nuget.ps1 [-Version 1.0.0] [-KeyFile C:\path\PackageCredentials.txt] [-WhatIf]
param(
    [string]$Version = "1.0.0",
    [string]$KeyFile = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "PackageCredentials.txt"),
    [string]$Source = "https://api.nuget.org/v3/index.json",
    [switch]$WhatIf
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$pkgs = Get-ChildItem (Join-Path $root "artifacts/packages") -Filter "*.$Version.nupkg"
if (-not $pkgs) { throw "No packages for version $Version. Run ./build/pack.ps1 first." }

$key = $env:NUGET_API_KEY
if (-not $key) {
    if (-not (Test-Path $KeyFile)) { throw "Set NUGET_API_KEY or provide -KeyFile." }
    $text = Get-Content $KeyFile -Raw
    $m = [regex]::Match($text, '(oy2[a-z0-9]{40,})')            # nuget.org API keys start with 'oy2'
    $key = if ($m.Success) { $m.Value } else { ($text -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1).Trim() }
}

foreach ($p in $pkgs) {
    if ($WhatIf) { Write-Host "Would push $($p.Name)"; continue }
    dotnet nuget push $p.FullName --api-key $key --source $Source --skip-duplicate
    if ($LASTEXITCODE -ne 0) { throw "Push failed: $($p.Name)" }
}

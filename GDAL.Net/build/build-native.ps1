<#
.SYNOPSIS
  Builds the Rust kernel library (native/gdalnet-kernels) and copies it into
  src/GDAL.Net/runtimes/<rid>/native so it ships inside the Gravicode.GDAL.Net package.
.EXAMPLE
  ./scripts/build-native.ps1                 # host platform
  ./scripts/build-native.ps1 -Targets x86_64-pc-windows-msvc,aarch64-pc-windows-msvc
#>
param(
    [string[]] $Targets = @()
)

$ErrorActionPreference = 'Stop'

function Invoke-Native([scriptblock] $Command) {
    # cargo writes progress to stderr; don't let Windows PowerShell treat that as an error.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $Command 2>&1 | ForEach-Object { "$_" }
    $ErrorActionPreference = $prev
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE" }
}
$root = Split-Path -Parent $PSScriptRoot
$crate = Join-Path $root 'native/gdalnet-kernels'

$ridMap = @{
    'x86_64-pc-windows-msvc'    = @{ Rid = 'win-x64';     File = 'gdalnet_kernels.dll' }
    'aarch64-pc-windows-msvc'   = @{ Rid = 'win-arm64';   File = 'gdalnet_kernels.dll' }
    'x86_64-unknown-linux-gnu'  = @{ Rid = 'linux-x64';   File = 'libgdalnet_kernels.so' }
    'aarch64-unknown-linux-gnu' = @{ Rid = 'linux-arm64'; File = 'libgdalnet_kernels.so' }
    'x86_64-apple-darwin'       = @{ Rid = 'osx-x64';     File = 'libgdalnet_kernels.dylib' }
    'aarch64-apple-darwin'      = @{ Rid = 'osx-arm64';   File = 'libgdalnet_kernels.dylib' }
}

if ($Targets.Count -eq 0) {
    $hostTriple = (rustc -vV | Select-String '^host:').ToString().Split(' ')[1].Trim()
    $Targets = @($hostTriple)
}

Push-Location $crate
try {
    Invoke-Native { cargo test --release }
    foreach ($t in $Targets) {
        if (-not $ridMap.ContainsKey($t)) { throw "Unsupported target $t" }
        Write-Host "Building $t" -ForegroundColor Cyan
        Invoke-Native { cargo build --release --target $t }
        $info = $ridMap[$t]
        $src = Join-Path $crate "target/$t/release/$($info.File)"
        $dst = Join-Path $root "src/GDAL.Net/runtimes/$($info.Rid)/native"
        New-Item -ItemType Directory -Force $dst | Out-Null
        Copy-Item $src $dst -Force
        Write-Host "  -> $dst/$($info.File)" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}

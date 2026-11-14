# Builds the Rust kernels and copies the binary into src/Electrical.Native/runtimes/<rid>/native
# Usage: ./build/build-native.ps1 [-Target x86_64-unknown-linux-gnu] [-Rid linux-x64]
param(
    [string]$Target = "",
    [string]$Rid = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$crate = Join-Path $root "native/electrical-native"

Push-Location $crate
try {
    if ($Target) { cargo build --release --target $Target } else { cargo build --release }
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
    cargo test --release
    if ($LASTEXITCODE -ne 0) { throw "cargo test failed" }
} finally { Pop-Location }

if (-not $Rid) {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLower()
    if ($IsLinux) { $Rid = "linux-$arch" } elseif ($IsMacOS) { $Rid = "osx-$arch" } else { $Rid = "win-$arch" }
}
$outDir = if ($Target) { Join-Path $crate "target/$Target/release" } else { Join-Path $crate "target/release" }
$file = Get-ChildItem $outDir -File | Where-Object { $_.Name -match '^(lib)?electrical_native\.(dll|so|dylib)$' } | Select-Object -First 1
if (-not $file) { throw "native binary not found in $outDir" }
$dest = Join-Path $root "src/Electrical.Native/runtimes/$Rid/native"
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item $file.FullName $dest -Force
Write-Host "Copied $($file.Name) -> $dest"

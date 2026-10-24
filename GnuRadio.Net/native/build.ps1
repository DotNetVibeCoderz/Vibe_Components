# Builds the optional Rust kernels and stages them where GnuRadio.Net looks for them
# (runtimes/<rid>/native). The managed library works without this step.
param(
    [string]$Rid = ""
)
$ErrorActionPreference = "Stop"
$crate = Join-Path $PSScriptRoot "gnuradio-native"
if (-not $Rid) {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $Rid = "win-$arch"
}
Push-Location $crate
try {
    cargo build --release
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
} finally {
    Pop-Location
}
$dest = Join-Path $PSScriptRoot "..\runtimes\$Rid\native"
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $crate "target\release\gnuradio_native.dll") $dest -Force
Write-Host "Staged gnuradio_native.dll in $dest"

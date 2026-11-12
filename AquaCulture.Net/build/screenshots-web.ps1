# Captures documentation screenshots of the Blazor gallery with headless Chrome/Edge.
# Start the gallery first:  dotnet run --project gallery/AquaCulture.Gallery.Web --urls http://localhost:5390
# WebGL (the 3D pond) needs Chrome's default GPU path: do not add --disable-gpu. Keep the virtual-time budget short (about 12 s):
# a long budget lets the Blazor circuit time out and the reconnect overlay greys the page.
# Usage: ./build/screenshots-web.ps1 [-Url http://localhost:5390] [-Browser path-to-chrome]
param(
    [string]$Url = "http://localhost:5390",
    [string]$Browser = ""
)
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "docs/images"
New-Item -ItemType Directory -Force $out | Out-Null
if (-not $Browser) {
    $Browser = @("C:\Program Files\Google\Chrome\Application\chrome.exe", "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "/usr/bin/google-chrome", "/usr/bin/chromium") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Browser) { throw "No Chrome/Edge found; pass -Browser." }

function Shot([string]$name, [string]$path, [string]$scheme = "light", [int]$height = 1500, [int]$budget = 12000) {
    $file = Join-Path $out "$name.png"
    $pref = if ($scheme -eq "dark") { 0 } else { 1 }   # blink: 0 = dark, 1 = light
    & $Browser --headless=new --hide-scrollbars --window-size=1440,$height --virtual-time-budget=$budget --run-all-compositor-stages-before-draw `
        "--blink-settings=preferredColorScheme=$pref" "--screenshot=$file" "$Url/$path" 2>$null | Out-Null
    Write-Host $file
}

Shot "web-home" "" "light" 1700
Shot "web-home-dark" "" "dark" 1700
Shot "web-home-id" "?lang=id" "light" 1200
$demos = "water","oxygen","hydro","growth","feeding","ai","disease","vision","twin","iot","gis","weather","stock","population","analytics","reporting","agent"
foreach ($d in $demos) { Shot "web-$d" "demo/$d" }
Shot "web-twin-dark" "demo/twin" "dark"

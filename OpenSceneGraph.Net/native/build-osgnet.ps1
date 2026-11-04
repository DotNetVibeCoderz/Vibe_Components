# Builds the osgnet C shim (native/osgnet) against native/third_party/osg-install and stages
# osgnet.dll + OpenSceneGraph runtime into src/OpenSceneGraph.Net.Native/runtimes/win-x64/native.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath
$cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$bld = Join-Path $root 'osgnet/build'
& $cmake -S (Join-Path $root 'osgnet') -B $bld -A x64
if ($LASTEXITCODE) { throw 'configure failed' }
& $cmake --build $bld --config Release -- /m /v:minimal
if ($LASTEXITCODE) { throw 'build failed' }
$dest = Join-Path $root '../src/OpenSceneGraph.Net.Native/runtimes/win-x64/native'
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $bld 'Release/osgnet.dll') $dest -Force
$osgBin = Join-Path $root 'third_party/osg-install/bin'
Copy-Item (Join-Path $osgBin '*.dll') $dest -Force
$plugins = Join-Path $dest 'osgPlugins-3.6.5'
New-Item -ItemType Directory -Force $plugins | Out-Null
Copy-Item (Join-Path $osgBin 'osgPlugins-3.6.5/*.dll') $plugins -Force
$fast = Join-Path $root 'osgnet-fast/target/release/osgnet_fast.dll'
if (Test-Path $fast) { New-Item -ItemType Directory -Force (Join-Path $root '../src/OpenSceneGraph.Net/runtimes/win-x64/native') | Out-Null; Copy-Item $fast (Join-Path $root '../src/OpenSceneGraph.Net/runtimes/win-x64/native/') -Force }
Write-Host "staged native runtime in $dest"

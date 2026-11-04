# Builds OpenSceneGraph 3.6.5 (Release, x64) into native/third_party/osg-install.
# Usage: powershell -ExecutionPolicy Bypass -File native/build-osg.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'third_party/osg-src'
$bld = Join-Path $root 'third_party/osg-build'
$ins = Join-Path $root 'third_party/osg-install'
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (-not (Test-Path $src)) { git clone --depth 1 --branch OpenSceneGraph-3.6.5 https://github.com/openscenegraph/OpenSceneGraph.git $src }
# Patch: _FPOSOFF was removed from recent MSVC STL (breaks the osga plugin)
$osga = Join-Path $src 'src/osgPlugins/osga/OSGA_Archive.cpp'
(Get-Content $osga -Raw).Replace('- _FPOSOFF( position );', '- (std::streamoff)( position );') | Set-Content $osga -NoNewline
& $cmake -S $src -B $bld -A x64 `
  -DCMAKE_INSTALL_PREFIX="$ins" `
  -DBUILD_OSG_APPLICATIONS=OFF -DBUILD_OSG_EXAMPLES=OFF -DBUILD_DOCUMENTATION=OFF `
  "-DCMAKE_POLICY_VERSION_MINIMUM=3.5" -DOSG_MSVC_VERSIONED_DLL=OFF -DWIN32_USE_MP=ON -DCMAKE_CXX_STANDARD=14 "-DCMAKE_CXX_FLAGS=/DWIN32 /D_WINDOWS /W3 /GR /EHsc /D_HAS_STD_BYTE=0"
if ($LASTEXITCODE) { throw 'configure failed' }
& $cmake --build $bld --config Release --target INSTALL -- /m /v:minimal
if ($LASTEXITCODE) { throw 'build failed' }

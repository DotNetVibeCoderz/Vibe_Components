$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath
$cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
& $cmake --build "$PSScriptRoot/third_party/osg-build" --config Release --target INSTALL -- /m /v:minimal
exit $LASTEXITCODE

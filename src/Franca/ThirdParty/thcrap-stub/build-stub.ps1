# Builds franca-thcrap-stub.exe (x86, /MT) next to this script.
# MSVC discovery mirrors src/Loader/build.ps1.
$ErrorActionPreference = 'Stop'
if ($env:VSCMD_ARG_TGT_ARCH -ne 'x86') {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio C++ x86 tools and Windows SDK.' }
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$vs) { throw 'C++ x86 toolchain not found.' }
    Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x86 -host_arch=x64' | Out-Null
}
$out = $PSScriptRoot
& cl.exe /nologo /O2 /W4 /WX /wd4996 /MT /utf-8 /Brepro (Join-Path $PSScriptRoot 'thcrap-stub.c') `
    /link /NOLOGO /SUBSYSTEM:WINDOWS /MACHINE:X86 /INCREMENTAL:NO /Brepro /DYNAMICBASE /NXCOMPAT `
    "/OUT:$(Join-Path $out 'franca-thcrap-stub.exe')" kernel32.lib user32.lib
if ($LASTEXITCODE) { throw 'thcrap-stub.c compilation failed' }
Remove-Item -LiteralPath (Join-Path $out 'thcrap-stub.obj') -ErrorAction SilentlyContinue
$hash = (Get-FileHash -LiteralPath (Join-Path $out 'franca-thcrap-stub.exe')).Hash
Write-Output "Built $out/franca-thcrap-stub.exe (SHA256 $hash)."

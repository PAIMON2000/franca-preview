param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'build'))
$ErrorActionPreference = 'Stop'
if ($env:VSCMD_ARG_TGT_ARCH -ne 'x64') {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio C++ x64 tools and Windows SDK.' }
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$vs) { throw 'C++ x64 toolchain not found.' }
    Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
}
$out = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $out | Out-Null
$systemDll = Join-Path ([Environment]::SystemDirectory) 'winhttp.dll'
$exports = @(& dumpbin.exe /nologo /exports $systemDll | ForEach-Object {
    if ($_ -match '^\s*(\d+)\s+[0-9A-F]+\s+[0-9A-F]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:$|=)') {
        [pscustomobject]@{Ordinal=[int]$Matches[1];Name=$Matches[2]}
    }
})
if ($LASTEXITCODE -ne 0 -or $exports.Count -lt 40) { throw 'Unable to enumerate System32 WinHTTP exports.' }
$def = [Collections.Generic.List[string]]::new()
$def.Add('LIBRARY winhttp'); $def.Add('EXPORTS'); $def.Add('FrancaLoaderWait')
$c = [Collections.Generic.List[string]]::new()
$c.Add('const char* const canary_names[] = {')
$asm = [Collections.Generic.List[string]]::new()
$asm.Add('EXTERN canary_resolve:PROC'); $asm.Add('.code')
for ($i=0; $i -lt $exports.Count; $i++) {
    $e=$exports[$i]; $private = if ($e.Name -in @('DllCanUnloadNow','DllGetClassObject')) { ' PRIVATE' } else { '' }; $def.Add("$($e.Name)=proxy_$i @$($e.Ordinal)$private"); $c.Add('"'+$e.Name+'",')
    $asm.Add(@"
proxy_$i PROC FRAME
    sub rsp, 136
    .allocstack 136
    .endprolog
    mov [rsp+32], rcx
    mov [rsp+40], rdx
    mov [rsp+48], r8
    mov [rsp+56], r9
    movdqu [rsp+64], xmm0
    movdqu [rsp+80], xmm1
    movdqu [rsp+96], xmm2
    movdqu [rsp+112], xmm3
    mov ecx, $i
    call canary_resolve
    mov r11, rax
    mov rcx, [rsp+32]
    mov rdx, [rsp+40]
    mov r8, [rsp+48]
    mov r9, [rsp+56]
    movdqu xmm0, [rsp+64]
    movdqu xmm1, [rsp+80]
    movdqu xmm2, [rsp+96]
    movdqu xmm3, [rsp+112]
    add rsp, 136
    jmp r11
proxy_$i ENDP
"@)
}
$c.Add('};'); $c.Add("const unsigned int canary_count = $($exports.Count);")
$asm.Add('END')
[IO.File]::WriteAllLines((Join-Path $out 'exports.def'),$def)
[IO.File]::WriteAllLines((Join-Path $out 'exports.c'),$c)
[IO.File]::WriteAllLines((Join-Path $out 'exports.asm'),$asm)
& cl.exe /nologo /c /O2 /W4 /WX /wd4191 /MT /utf-8 /Brepro (Join-Path $PSScriptRoot 'loader.c') "/Fo$out/loader.obj"
if ($LASTEXITCODE) { throw 'loader.c compilation failed' }
& cl.exe /nologo /c /O2 /W4 /WX /MT /Brepro (Join-Path $out 'exports.c') "/Fo$out/exports.obj"
if ($LASTEXITCODE) { throw 'exports.c compilation failed' }
& ml64.exe /nologo /c "/Fo$out/stubs.obj" (Join-Path $out 'exports.asm')
if ($LASTEXITCODE) { throw 'x64 forwarding assembly failed' }
& link.exe /nologo /DLL /MACHINE:X64 /INCREMENTAL:NO /Brepro /DYNAMICBASE /NXCOMPAT /GUARD:CF "/DEF:$out/exports.def" "/OUT:$out/winhttp.dll" (Join-Path $out 'loader.obj') (Join-Path $out 'exports.obj') (Join-Path $out 'stubs.obj') kernel32.lib
if ($LASTEXITCODE) { throw 'Native link failed' }
@{Version='0.1.0-experimental';Architecture='x64';Toolset=$env:VCToolsVersion;WindowsSDK=$env:WindowsSDKVersion;SystemWinHttpSHA256=(Get-FileHash -LiteralPath $systemDll).Hash;Exports=$exports.Count;OutputSHA256=(Get-FileHash -LiteralPath (Join-Path $out 'winhttp.dll')).Hash} | ConvertTo-Json | Set-Content (Join-Path $out 'build-info.json')
Write-Output "Built $out/winhttp.dll ($($exports.Count) forwarded exports)."

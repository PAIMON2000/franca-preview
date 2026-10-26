param([string]$ArtifactDirectory = (Join-Path $PSScriptRoot '../../tests/artifacts/loader'))
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$out=Join-Path $PSScriptRoot 'build'
$artifacts=[IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Force $artifacts | Out-Null
& cl.exe /nologo /O2 /W4 /WX /wd4191 /MT /utf-8 (Join-Path $PSScriptRoot 'tests/host.c') "/Fo$out/host.obj" "/Fe$out/host.exe" /link /INCREMENTAL:NO ntdll.lib
if($LASTEXITCODE) {throw 'Probe host compilation failed'}
& cl.exe /nologo /LD /O2 /W4 /WX /MT (Join-Path $PSScriptRoot 'tests/coreclr.c') "/Fo$out/fixture.obj" "/Fe$out/fixture.dll" /link /INCREMENTAL:NO
if($LASTEXITCODE) {throw 'CoreCLR fixture compilation failed'}
& cl.exe /nologo /LD /O2 /W4 /WX /wd4505 /MT /DFRANCA_MISSING_EXPORT (Join-Path $PSScriptRoot 'tests/coreclr.c') "/Fo$out/missing.obj" "/Fe$out/missing.dll" /link /INCREMENTAL:NO
if($LASTEXITCODE) {throw 'Missing-export fixture compilation failed'}
& dotnet build (Join-Path $PSScriptRoot 'tests/ManagedFixture/Franca.LoaderFixture.csproj') -c Release --nologo
if($LASTEXITCODE) {throw 'Managed fixture build failed'}
$cases=@(
 @{Name='no-config';Expected=12}, @{Name='disabled';Expected=2}, @{Name='no-opt-in';Expected=2},
 @{Name='inherited-disabled';Expected=2}, @{Name='inherited-initialized';Expected=2},
 @{Name='missing-target';Expected=14}, @{Name='missing-corlib';Expected=15},
 @{Name='missing-coreclr';Expected=16}, @{Name='missing-export';Expected=17},
 @{Name='initialize';Expected=19}, @{Name='delegate';Expected=20}, @{Name='entry';Expected=21},
 @{Name='success';Expected=1}, @{Name='Unicode-中文路径';Expected=1}, @{Name='real-coreclr';Expected=1}, @{Name='real-coreclr-中文';Expected=1}
)
$root=Join-Path ([IO.Path]::GetTempPath()) ('franca-loader-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$failed=0
try {
 foreach($case in $cases) {
  $name=$case.Name; $dir=Join-Path $root $name
  New-Item -ItemType Directory -Path $dir | Out-Null
  Copy-Item -LiteralPath (Join-Path $out 'host.exe') -Destination $dir
  Copy-Item -LiteralPath (Join-Path $out 'winhttp.dll') -Destination $dir
  if($name -ne 'missing-target') {Set-Content -LiteralPath (Join-Path $dir 'target.dll') -Value 'fixture'}
  if($name -ne 'missing-corlib') {New-Item -ItemType Directory -Path (Join-Path $dir 'dotnet') | Out-Null}
  if($name -ne 'missing-coreclr' -and $name -ne 'missing-corlib') {
   $fixture=if($name -eq 'missing-export') {'missing.dll'} else {'fixture.dll'}
   Copy-Item -LiteralPath (Join-Path $out $fixture) -Destination (Join-Path $dir 'dotnet/coreclr.dll')
  }
  $enabled=if($name -eq 'disabled') {'false'} else {'true'}
  $optin=if($name -eq 'no-opt-in') {'false'} else {'true'}
  $target='target.dll'; $runtime='dotnet\coreclr.dll'; $corlib='dotnet'
  if($name -like 'real-coreclr*') {
   $framework=Get-ChildItem (Join-Path $env:ProgramFiles 'dotnet/shared/Microsoft.NETCore.App') -Directory | Where-Object Name -Match '^9\.' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
   if(!$framework) {throw 'A real .NET 9 runtime is required for the CoreCLR smoke test'}
   $runtime=Join-Path $framework.FullName 'coreclr.dll'; $corlib=$framework.FullName
   Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'tests/ManagedFixture/bin/Release/net9.0/Franca.LoaderFixture.dll') -Destination $dir
   $target='Franca.LoaderFixture.dll'
  }
  if($name -ne 'no-config') { @"
[General]
enabled=$enabled
target_assembly=$target
ignore_disable_switch=false
[Il2Cpp]
coreclr_path=$runtime
corlib_dir=$corlib
[Franca]
experimental_bootstrap=$optin
"@ | Set-Content -LiteralPath (Join-Path $dir 'doorstop_config.ini') }
  $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $dir 'host.exe'),[string]$case.Expected)
  $start.WorkingDirectory=$dir; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
  $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
  foreach($key in @('DOORSTOP_DISABLE','DOORSTOP_INITIALIZED','FRANCA_FIXTURE_FAULT')) {$start.Environment.Remove($key) | Out-Null}
  if($name -eq 'inherited-disabled') {$start.Environment['DOORSTOP_DISABLE']='1'}
  if($name -eq 'inherited-initialized') {$start.Environment['DOORSTOP_INITIALIZED']='TRUE'}
  if($name -in @('initialize','delegate','entry')) {$start.Environment['FRANCA_FIXTURE_FAULT']=$name}
  $process=[Diagnostics.Process]::Start($start)
  if(!$process.WaitForExit(15000)) {$process.Kill();throw "Native probe timeout: $name"}
  $output=$process.StandardOutput.ReadToEnd(); $errorOutput=$process.StandardError.ReadToEnd()
  $log=Join-Path $dir 'BepInEx/franca-loader.log'
  if(Test-Path -LiteralPath $log) {Copy-Item -LiteralPath $log -Destination (Join-Path $artifacts "$name.log")}
  $marker=if($name -like 'real-coreclr*') {'managed-entrypoint.ok'} else {'native-entrypoint.ok'}
  $ok=$process.ExitCode -eq 0 -and ((Test-Path -LiteralPath (Join-Path $dir $marker)) -eq ($case.Expected -eq 1))
  $label=if($ok) {'PASS'} else {'FAIL'}
  Write-Output "RESULT`tLOADER`t$name`t$label"
  $output+$errorOutput | Set-Content -LiteralPath (Join-Path $artifacts "$name.stdout.txt")
  if(!$ok) {$failed++;Write-Output $output;Write-Output $errorOutput;if(Test-Path -LiteralPath $log) {Get-Content -LiteralPath $log}}
  $process.Dispose()
 }
 Copy-Item -LiteralPath (Join-Path $out 'build-info.json') -Destination $artifacts
} finally {
 $resolved=[IO.Path]::GetFullPath($root)
 if(!$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) -or !(Split-Path $resolved -Leaf).StartsWith('franca-loader-')) {throw 'Unsafe fixture cleanup target'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
if($failed) {throw "$failed native Loader cases failed"}

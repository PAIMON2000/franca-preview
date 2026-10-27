param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('franca-standalone-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$testExe = Join-Path $testDirectory 'Franca.exe'
Copy-Item -LiteralPath $Executable -Destination $testExe
function Invoke-Probe([string]$Name) {
    $outputPath = Join-Path $testDirectory "$Name.txt"
    $errorPath = Join-Path $testDirectory "$Name-error.txt"
    $process = Start-Process -FilePath $testExe -ArgumentList "--probe-$Name" -WorkingDirectory $testDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath
    if (!$process.WaitForExit(45000)) { Stop-Process -Id $process.Id; throw "Probe timed out: $Name ($testDirectory)" }
    $outputText = [IO.File]::ReadAllText($outputPath)
    if ($process.ExitCode -ne 0 -or $outputText -notmatch "RESULT\s" -or $outputText -match "\tFAIL" -or (Get-Item $errorPath).Length) {
        throw "Probe failed: $Name ($testDirectory)"
    }
    Write-Output "$Name PASS"
}
Invoke-Probe 'startup'
if (Test-Path (Join-Path $testDirectory 'franca-settings.json')) { throw 'Settings unexpectedly bundled.' }
$assets = @(Get-ChildItem (Join-Path $testDirectory 'ThirdParty') -Recurse -File)
if ($assets.Count -lt 30) { throw 'Missing distribution assets.' }
foreach ($file in $assets) {
    $relative = [IO.Path]::GetRelativePath($testDirectory, $file.FullName)
    $source = Join-Path $repository "src/Franca/$relative"
    if ((Get-FileHash $source).Hash -ne (Get-FileHash $file.FullName).Hash) { throw "Asset mismatch: $relative" }
}
$settingsPath = Join-Path $testDirectory 'franca-settings.json'
$sentinelSettings = '{"SourceLanguage":"ja","TargetLanguage":"zh-CN","Endpoint":"BingTranslate"}'
[IO.File]::WriteAllText($settingsPath, $sentinelSettings)
$workingTree = Join-Path $testDirectory 'thcrap'
New-Item -ItemType Directory -Path $workingTree | Out-Null
[IO.File]::WriteAllText((Join-Path $workingTree 'sentinel.txt'), 'keep-generated-patches')
$damagedAsset = Join-Path $testDirectory 'ThirdParty/thtk/thdat.exe'
[IO.File]::WriteAllText($damagedAsset, 'simulate interrupted extraction')
Invoke-Probe 'release-update'
if ((Get-FileHash $damagedAsset).Hash -ne (Get-FileHash (Join-Path $repository 'src/Franca/ThirdParty/thtk/thdat.exe')).Hash) { throw 'Asset repair failed.' }
if ([IO.File]::ReadAllText($settingsPath) -ne $sentinelSettings) { throw 'Settings changed.' }
if ([IO.File]::ReadAllText((Join-Path $workingTree 'sentinel.txt')) -ne 'keep-generated-patches') { throw 'Working patches changed.' }
Write-Output "Standalone assets ($($assets.Count)), repair and mutable-data preservation PASS"
Write-Output "Evidence: $testDirectory"

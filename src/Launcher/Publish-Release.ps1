param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$project = Join-Path $repository 'src/Franca/Franca.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$displayName = [string]$projectXml.Project.PropertyGroup.InformationalVersion
if ($displayName -notmatch '^Franca[0-9.]+-[0-9]+$') { throw 'Invalid release display name.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repository "dist/$displayName" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
# Publish to a clean staging directory: no developer settings, keys, packs or logs.
$staging = Join-Path ([IO.Path]::GetTempPath()) ('franca-publish-' + [guid]::NewGuid().ToString('N'))
dotnet publish $project -c Release -p:PublishProfile=Standalone -o $staging
if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE). Staging retained: $staging" }
$files = @(Get-ChildItem -LiteralPath $staging -Recurse -File)
if ($files.Count -ne 1 -or $files[0].Name -ne 'Franca.exe') {
    throw "Expected a standalone EXE only. Inspect staging: $staging"
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$destination = Join-Path $OutputDirectory "$displayName.exe"
Copy-Item -LiteralPath $files[0].FullName -Destination $destination
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$destination.sha256", "$hash  $displayName.exe`n", [Text.UTF8Encoding]::new($false))
Get-Item -LiteralPath $destination | Select-Object FullName, Length
Write-Output "SHA256: $hash"
Write-Output "Staging retained: $staging"

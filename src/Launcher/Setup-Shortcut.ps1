# Recreate the local shortcut after cloning or moving the repository.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$exe = Join-Path $repoRoot 'src/Franca/bin/Release/net9.0-windows/Franca.exe'
$icon = Join-Path $repoRoot 'src/Franca/Assets/Brand/franca-taskbar.ico'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw 'Build first: dotnet build src/Franca/Franca.csproj -c Release'
}
if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) { throw "Missing icon: $icon" }
$shortcutName = 'Franca.lnk'
$shell = New-Object -ComObject WScript.Shell
try {
    $shortcut = $shell.CreateShortcut((Join-Path $repoRoot $shortcutName))
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = Split-Path -Parent $exe
    $shortcut.IconLocation = "$icon,0"
    $shortcut.Description = 'Launch Franca'
    $shortcut.Save()
    Write-Output (Join-Path $repoRoot $shortcutName)
}
finally {
    if ($null -ne $shortcut) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
}

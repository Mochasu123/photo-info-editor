# Repoints the existing desktop PhotoInfoEditor shortcut to the current
# repository Debug build. Run after `dotnet build .\PhotoLocationEditor.sln -c Debug`.
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcut = Get-ChildItem -LiteralPath $desktop -Filter '*.lnk' -ErrorAction SilentlyContinue |
    Where-Object { $_.BaseName -like '*PhotoInfoEditor*' -or $_.BaseName -like '*Photo Info Editor*' } |
    Select-Object -First 1
if (-not $shortcut) {
    Write-Host "No PhotoInfoEditor shortcut found on the desktop."
    exit 1
}
$shortcutPath = $shortcut.FullName

$exe = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\src\PhotoLocationEditor.App\bin\Debug\net8.0-windows')).Path 'PhotoInfoEditor.exe'
if (-not (Test-Path $exe)) {
    Write-Host "Debug exe not found: $exe"
    exit 1
}

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcutPath)
$old = $lnk.TargetPath
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = Split-Path $exe
$lnk.IconLocation = "$exe,0"
$lnk.Save()

Write-Host "Shortcut updated:"
Write-Host "  $old"
Write-Host "  -> $exe"

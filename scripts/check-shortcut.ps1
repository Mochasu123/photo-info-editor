# Inspects the PhotoInfoEditor desktop shortcut and reports whether it points
# at an installed copy or a development build.
$desktop = [Environment]::GetFolderPath('Desktop')
$candidates = @()
if (Test-Path $desktop) {
    $candidates = Get-ChildItem -LiteralPath $desktop -Filter '*.lnk' |
        Where-Object { $_.BaseName -like '*PhotoInfoEditor*' -or $_.BaseName -like '*Photo Info Editor*' } |
        ForEach-Object { $_.FullName }
}

$shell = New-Object -ComObject WScript.Shell
$found = $false
foreach ($path in $candidates) {
    if (Test-Path $path) {
        $lnk = $shell.CreateShortcut($path)
        Write-Host "Shortcut : $path"
        Write-Host "Target   : $($lnk.TargetPath)"
        Write-Host "WorkDir  : $($lnk.WorkingDirectory)"
        Write-Host "Args     : $($lnk.Arguments)"
        $target = $lnk.TargetPath
        if ($target -match 'CodeApply\\photo-location-editor|bin\\(Debug|Release)') {
            Write-Host "Verdict  : DEVELOPMENT BUILD (points into the repo/bin folder)"
        } elseif ($target -match 'C:\\tool\\Photo Info Editor|Program Files') {
            Write-Host "Verdict  : INSTALLED COPY"
        } else {
            Write-Host "Verdict  : UNKNOWN (manual shortcut)"
        }
        $found = $true
    }
}

if (-not $found) {
    Write-Host "No PhotoInfoEditor shortcut found on the desktop."
}

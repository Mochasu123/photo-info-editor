$items = Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'MATLAB' }
foreach ($item in $items) {
    $key = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$($item.PSChildName)"
    Write-Host "Removing: $key"
    Remove-Item -Path $key -Recurse -Force
    Write-Host "Removed!"
}
Write-Host "Done"

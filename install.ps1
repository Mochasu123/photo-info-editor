robocopy "C:\Workspace\CodeApply\photo-location-editor\dist\publish" "C:\tool\Photo Info Editor" /E /NFL /NDL
Write-Host "Copied files"

$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut("C:\Users\whoca\Desktop\Photo Info Editor.lnk")
$sc.TargetPath = "C:\tool\Photo Info Editor\PhotoInfoEditor.exe"
$sc.Save()
Write-Host "Desktop shortcut done"

$sm = "C:\Users\whoca\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Photo Info Editor"
New-Item -ItemType Directory -Force -Path $sm | Out-Null
$sc2 = $ws.CreateShortcut("$sm\Photo Info Editor.lnk")
$sc2.TargetPath = "C:\tool\Photo Info Editor\PhotoInfoEditor.exe"
$sc2.Save()
Write-Host "Start menu done"

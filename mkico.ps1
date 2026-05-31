[Reflection.Assembly]::LoadWithPartialName('System.Drawing') | Out-Null
$img = [Drawing.Image]::FromFile("$PSScriptRoot/src/PhotoLocationEditor.App/appicon.png")
$bmp = New-Object Drawing.Bitmap($img, 256, 256)
$h = $bmp.GetHicon()
$ico = [Drawing.Icon]::FromHandle($h)
$fs = [IO.File]::Create("$PSScriptRoot/src/PhotoLocationEditor.App/appicon.ico")
$ico.Save($fs)
$fs.Close()
$ico.Dispose()
$bmp.Dispose()
$img.Dispose()
Write-Host "ICO created OK"

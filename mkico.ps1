Add-Type -AssemblyName System.Drawing

$srcPath = Join-Path $PSScriptRoot "src\PhotoLocationEditor.App\appicon.png"
$icoPath = Join-Path $PSScriptRoot "src\PhotoLocationEditor.App\appicon.ico"

$src = [System.Drawing.Image]::FromFile($srcPath)
$sizes = @(16, 24, 32, 48, 64, 128, 256)

$pngFrames = New-Object System.Collections.Generic.List[byte[]]
foreach ($sz in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $sz, $sz)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngFrames.Add($ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
}
$src.Dispose()

$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICONDIR header: reserved(0), type(1=ICO), count
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $dim = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
    $len = [uint32]$pngFrames[$i].Length
    $bw.Write($dim)         # width
    $bw.Write($dim)         # height
    $bw.Write([byte]0)      # color palette
    $bw.Write([byte]0)      # reserved
    $bw.Write([uint16]1)    # planes
    $bw.Write([uint16]32)   # bpp
    $bw.Write($len)         # size in bytes
    $bw.Write([uint32]$offset)
    $offset += [int]$len
}

foreach ($bytes in $pngFrames) {
    $bw.Write($bytes)
}

$bw.Flush()
$bw.Dispose()
$fs.Dispose()
Write-Host "Multi-res ICO created OK: $icoPath ($($sizes -join ',') px)"

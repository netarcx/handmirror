Add-Type -AssemblyName System.Drawing

function New-RoundedPath {
    param([float]$x, [float]$y, [float]$w, [float]$h, [float]$r)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-CameraPng {
    param([int]$size)
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $S = [float]$size

    $bodyBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 240, 240, 245))
    $bodyEdge  = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 80, 80, 90)), ([Math]::Max(1.0, $S * 0.015))

    # Viewfinder hump on top
    $hump = New-RoundedPath ($S * 0.30) ($S * 0.16) ($S * 0.40) ($S * 0.16) ($S * 0.04)
    $g.FillPath($bodyBrush, $hump)
    $g.DrawPath($bodyEdge, $hump)

    # Body
    $body = New-RoundedPath ($S * 0.05) ($S * 0.30) ($S * 0.90) ($S * 0.58) ($S * 0.08)
    $g.FillPath($bodyBrush, $body)
    $g.DrawPath($bodyEdge, $body)

    # Outer lens ring
    $lensSize = $S * 0.44
    $lensX = ($S - $lensSize) / 2
    $lensY = $S * 0.39
    $lensOuter = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 90, 90, 100))
    $g.FillEllipse($lensOuter, $lensX, $lensY, $lensSize, $lensSize)

    # Middle ring
    $m = $S * 0.04
    $lensMid = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 30, 38))
    $g.FillEllipse($lensMid, $lensX + $m, $lensY + $m, $lensSize - 2*$m, $lensSize - 2*$m)

    # Inner lens (blueish glass)
    $innerSize = $S * 0.20
    $innerX = ($S - $innerSize) / 2
    $innerY = $lensY + ($lensSize - $innerSize) / 2
    $lensInner = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 60, 110, 170))
    $g.FillEllipse($lensInner, $innerX, $innerY, $innerSize, $innerSize)

    # Lens highlight
    $hlBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 255, 255, 255))
    $hlW = $S * 0.07
    $hlH = $S * 0.05
    $g.FillEllipse($hlBrush, $innerX + $S * 0.015, $innerY + $S * 0.018, $hlW, $hlH)

    # Flash light
    $flashBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 205, 70))
    $flashSize = $S * 0.07
    $g.FillEllipse($flashBrush, $S * 0.78, $S * 0.355, $flashSize, $flashSize)

    $g.Dispose()
    $bodyBrush.Dispose(); $bodyEdge.Dispose()
    $lensOuter.Dispose(); $lensMid.Dispose(); $lensInner.Dispose()
    $hlBrush.Dispose(); $flashBrush.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = @(16, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) { $pngs += ,(New-CameraPng -size $s) }

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + ($sizes.Count * 16)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $png  = $pngs[$i]
    $dim  = if ($size -ge 256) { [byte]0 } else { [byte]$size }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$png.Length)
    $bw.Write([UInt32]$offset)
    $offset += $png.Length
}
foreach ($png in $pngs) { $bw.Write($png) }
$bw.Flush()

$target = Join-Path (Split-Path $PSScriptRoot -Parent) 'icon.ico'
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
Write-Output "Wrote $target ($($out.Length) bytes)"

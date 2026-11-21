Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot 'icons'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

function New-BoltIcon {
    param([string]$Path, [System.Drawing.Color]$BoltColor)

    $size = 32
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $bgBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 28, 33, 44))
    $borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 67, 78, 96), 1.2)
    $g.FillEllipse($bgBrush, 1, 1, 29, 29)
    $g.DrawEllipse($borderPen, 1, 1, 29, 29)

    $points = @(
        (New-Object System.Drawing.PointF(19.5, 4.0)),
        (New-Object System.Drawing.PointF(9.5, 18.0)),
        (New-Object System.Drawing.PointF(15.0, 18.0)),
        (New-Object System.Drawing.PointF(12.5, 28.0)),
        (New-Object System.Drawing.PointF(23.0, 13.0)),
        (New-Object System.Drawing.PointF(17.0, 13.0))
    )
    $boltBrush = New-Object System.Drawing.SolidBrush($BoltColor)
    $g.FillPolygon($boltBrush, [System.Drawing.PointF[]]$points)

    $hIcon = $bmp.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($hIcon)
    $stream = [System.IO.File]::Create($Path)
    $icon.Save($stream)
    $stream.Close()

    $icon.Dispose()
    $boltBrush.Dispose()
    $borderPen.Dispose()
    $bgBrush.Dispose()
    $g.Dispose()
    $bmp.Dispose()
}

New-BoltIcon -Path (Join-Path $outDir 'idle.ico') -BoltColor ([System.Drawing.Color]::FromArgb(255, 196, 205, 219))
New-BoltIcon -Path (Join-Path $outDir 'active.ico') -BoltColor ([System.Drawing.Color]::FromArgb(255, 65, 225, 105))

Get-ChildItem $outDir | Select-Object Name, Length

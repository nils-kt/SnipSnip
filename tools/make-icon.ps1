# Erzeugt Assets\app.ico (mehrere Größen, PNG-komprimiert).
param([string]$Out = (Join-Path $PSScriptRoot '..\Assets\app.ico'))

Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $f = $s / 256.0

    # Abgerundetes Quadrat mit Verlauf
    $m = 8 * $f; $w = $s - 2 * $m; $d = 2 * 60 * $f
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, $d, $d, 180, 90)
    $path.AddArc($m + $w - $d, $m, $d, $d, 270, 90)
    $path.AddArc($m + $w - $d, $m + $w - $d, $d, $d, 0, 90)
    $path.AddArc($m, $m + $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($s, $s)), ([System.Drawing.Color]::FromArgb(255, 79, 140, 255)), ([System.Drawing.Color]::FromArgb(255, 140, 92, 255))
    $g.FillPath($brush, $path)

    # Auswahl-Ecken
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.6, 20 * $f))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $a = 66 * $f; $b = 190 * $f; $l = 42 * $f
    $P = { param($x, $y) [System.Drawing.PointF]::new($x, $y) }
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((& $P $a ($a + $l)), (& $P $a $a), (& $P ($a + $l) $a)))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((& $P ($b - $l) $a), (& $P $b $a), (& $P $b ($a + $l))))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((& $P $b ($b - $l)), (& $P $b $b), (& $P ($b - $l) $b)))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((& $P ($a + $l) $b), (& $P $a $b), (& $P $a ($b - $l))))

    # Fadenkreuz in der Mitte
    if ($s -ge 24) {
        $c = 128 * $f; $k = 22 * $f
        $g.DrawLine($pen, $c, $c - $k, $c, $c + $k)
        $g.DrawLine($pen, $c - $k, $c, $c + $k, $c)
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $images += , @($s, $ms.ToArray())
}

New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img[0]; $data = $img[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img[1]) }
$bw.Close()
Write-Host "Icon geschrieben: $Out"

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assets = Join-Path $root "assets"
$out = Join-Path $assets "CodexFloat.ico"
$preview = Join-Path $assets "CodexFloat-logo-256.png"
New-Item -ItemType Directory -Force -Path $assets | Out-Null

Add-Type -AssemblyName System.Drawing

function New-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $scale = $Size / 256.0
    function Scale-IconValue([double]$v) { return [single]($v * $scale) }
    function New-IconRect([double]$x, [double]$y, [double]$w, [double]$h) {
        return [System.Drawing.RectangleF]::new((Scale-IconValue $x), (Scale-IconValue $y), (Scale-IconValue $w), (Scale-IconValue $h))
    }

    $bgRect = New-IconRect 20 20 216 216
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $bgRect, ([System.Drawing.Color]::FromArgb(31, 55, 68)), ([System.Drawing.Color]::FromArgb(6, 21, 20)), 45
    $g.FillEllipse($bg, $bgRect)
    $bg.Dispose()

    $ringBase = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(38, 255, 255, 255)), (Scale-IconValue 16)
    $ringBase.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $ringBase.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($ringBase, (New-IconRect 45 45 166 166), -180, 180)
    $ringBase.Dispose()

    $ring = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-IconRect 45 45 166 166), ([System.Drawing.Color]::FromArgb(150, 255, 240)), ([System.Drawing.Color]::FromArgb(255, 95, 109)), 45
    $ringPen = New-Object System.Drawing.Pen $ring, (Scale-IconValue 16)
    $ringPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $ringPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($ringPen, (New-IconRect 45 45 166 166), 0, 138)
    $ringPen.Dispose()
    $ring.Dispose()

    $clipPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $clipPath.AddEllipse((New-IconRect 48 48 160 160))
    $oldClip = $g.Clip
    $g.SetClip($clipPath)

    $waterRect = New-IconRect 42 124 172 92
    $water = New-Object System.Drawing.Drawing2D.LinearGradientBrush $waterRect, ([System.Drawing.Color]::FromArgb(123, 246, 219)), ([System.Drawing.Color]::FromArgb(8, 123, 114)), 90
    $g.FillRectangle($water, $waterRect)
    $water.Dispose()

    $wave1 = New-Object System.Drawing.Drawing2D.GraphicsPath
    $wave1.StartFigure()
    $wave1.AddBezier((Scale-IconValue 36), (Scale-IconValue 127), (Scale-IconValue 54), (Scale-IconValue 113), (Scale-IconValue 72), (Scale-IconValue 113), (Scale-IconValue 90), (Scale-IconValue 127))
    $wave1.AddBezier((Scale-IconValue 90), (Scale-IconValue 127), (Scale-IconValue 108), (Scale-IconValue 141), (Scale-IconValue 126), (Scale-IconValue 141), (Scale-IconValue 144), (Scale-IconValue 127))
    $wave1.AddBezier((Scale-IconValue 144), (Scale-IconValue 127), (Scale-IconValue 162), (Scale-IconValue 113), (Scale-IconValue 180), (Scale-IconValue 113), (Scale-IconValue 198), (Scale-IconValue 127))
    $wave1.AddLine((Scale-IconValue 228), (Scale-IconValue 127), (Scale-IconValue 228), (Scale-IconValue 156))
    $wave1.AddLine((Scale-IconValue 228), (Scale-IconValue 156), (Scale-IconValue 36), (Scale-IconValue 156))
    $wave1.CloseFigure()
    $waveBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(122, 181, 255, 241))
    $g.FillPath($waveBrush, $wave1)
    $waveBrush.Dispose()
    $wave1.Dispose()

    $wave2 = New-Object System.Drawing.Drawing2D.GraphicsPath
    $wave2.StartFigure()
    $wave2.AddBezier((Scale-IconValue 28), (Scale-IconValue 145), (Scale-IconValue 48), (Scale-IconValue 133), (Scale-IconValue 68), (Scale-IconValue 133), (Scale-IconValue 88), (Scale-IconValue 145))
    $wave2.AddBezier((Scale-IconValue 88), (Scale-IconValue 145), (Scale-IconValue 108), (Scale-IconValue 157), (Scale-IconValue 128), (Scale-IconValue 157), (Scale-IconValue 148), (Scale-IconValue 145))
    $wave2.AddBezier((Scale-IconValue 148), (Scale-IconValue 145), (Scale-IconValue 168), (Scale-IconValue 133), (Scale-IconValue 188), (Scale-IconValue 133), (Scale-IconValue 208), (Scale-IconValue 145))
    $wave2.AddLine((Scale-IconValue 232), (Scale-IconValue 145), (Scale-IconValue 232), (Scale-IconValue 220))
    $wave2.AddLine((Scale-IconValue 232), (Scale-IconValue 220), (Scale-IconValue 28), (Scale-IconValue 220))
    $wave2.CloseFigure()
    $waveBrush2 = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(142, 26, 179, 156))
    $g.FillPath($waveBrush2, $wave2)
    $waveBrush2.Dispose()
    $wave2.Dispose()

    $g.Clip = $oldClip
    $oldClip.Dispose()
    $clipPath.Dispose()

    $innerPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(42, 255, 255, 255)), (Scale-IconValue 4)
    $g.DrawEllipse($innerPen, (New-IconRect 48 48 160 160))
    $innerPen.Dispose()

    $white = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), (Scale-IconValue 18)
    $white.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $white.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($white, (New-IconRect 81 82 84 88), 116, 128)
    $g.DrawLine($white, (Scale-IconValue 139), (Scale-IconValue 128), (Scale-IconValue 185), (Scale-IconValue 128))
    $white.Dispose()

    $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillEllipse($dot, (New-IconRect 178 119 18 18))
    $dot.Dispose()

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $bmp = New-IconBitmap -Size $size
    if ($size -eq 256) {
        $bmp.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
    $ms.Dispose()
    $bmp.Dispose()
}

$fs = [System.IO.File]::Open($out, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$frames.Count)

$offset = 6 + (16 * $frames.Count)
foreach ($frame in $frames) {
    $bw.Write([byte]($(if ($frame.Size -eq 256) { 0 } else { $frame.Size })))
    $bw.Write([byte]($(if ($frame.Size -eq 256) { 0 } else { $frame.Size })))
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$frame.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $frame.Bytes.Length
}

foreach ($frame in $frames) {
    $bw.Write($frame.Bytes)
}

$bw.Dispose()
$fs.Dispose()
Write-Host "Generated: $out"
Write-Host "Generated: $preview"

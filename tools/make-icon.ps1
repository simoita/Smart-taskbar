Add-Type -AssemblyName System.Drawing

$bmp = New-Object System.Drawing.Bitmap 256, 256
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$inset = 16
$r = 54
$path.AddArc($inset, $inset, $r * 2, $r * 2, 180, 90)
$path.AddArc(256 - $inset - $r * 2, $inset, $r * 2, $r * 2, 270, 90)
$path.AddArc(256 - $inset - $r * 2, 256 - $inset - $r * 2, $r * 2, $r * 2, 0, 90)
$path.AddArc($inset, 256 - $inset - $r * 2, $r * 2, $r * 2, 90, 90)
$path.CloseFigure()

$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point 0, 0),
    (New-Object System.Drawing.Point 256, 256),
    [System.Drawing.Color]::FromArgb(139, 92, 246),
    [System.Drawing.Color]::FromArgb(34, 211, 238))
$g.FillPath($brush, $path)

$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$g.FillEllipse($white, 92, 92, 28, 28)
$g.FillEllipse($white, 136, 92, 28, 28)
$g.FillEllipse($white, 92, 136, 28, 28)
$g.FillEllipse($white, 136, 136, 28, 28)

$stream = New-Object System.IO.MemoryStream
$bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $stream.ToArray()

$target = Join-Path $PSScriptRoot "..\src\Nebula\Assets\nebula.ico"
$fs = [System.IO.File]::Create($target)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]1)
$bw.Write([Byte]0)
$bw.Write([Byte]0)
$bw.Write([Byte]0)
$bw.Write([Byte]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]32)
$bw.Write([UInt32]$png.Length)
$bw.Write([UInt32]22)
$bw.Write($png)
$bw.Close()
$fs.Close()

Write-Output ("icon written: " + (Get-Item $target).Length + " bytes")

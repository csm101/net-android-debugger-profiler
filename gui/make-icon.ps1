# Draws NapGui.ico. The mark is what the two products do, in one shape: three rising bars
# (a profile) and a breakpoint dot (a debugger), on the dark ground the splash screen uses.
#
# It is drawn here rather than downloaded so that the repository owns it outright - an icon
# from a stock library would carry its own license into an MIT repository - and so that the
# small sizes can be simplified instead of scaled down into mud.
#
#   powershell -File make-icon.ps1            writes NapGui.ico beside this script
#
# The icon is then compiled into NapGui.RES by build-gui.cmd, which the program includes
# with {$R *.res}. MAINICON has to be the first icon in the resource script: that is the
# name Windows and the VCL take as the application's icon.
param([string]$Out = (Join-Path $PSScriptRoot 'NapGui.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-Face([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)

  # The ground: the same near-black the splash screen uses, rounded like an app tile.
  $pad = [Math]::Max(1, [int]($size * 0.04))
  $radius = [Math]::Max(2, [int]($size * 0.22))
  $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad))
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $radius * 2
  $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
  $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
  $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
  $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
  $path.CloseFigure()
  $ground = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 32, 33, 36))
  $g.FillPath($ground, $path)

  # Three rising bars: a profile, read left to right.
  $barColours = @(
    [System.Drawing.Color]::FromArgb(255, 86, 141, 255),
    [System.Drawing.Color]::FromArgb(255, 79, 186, 200),
    [System.Drawing.Color]::FromArgb(255, 91, 199, 129)
  )
  $barWidth = $size * 0.14
  $gap = $size * 0.07
  $left = $size * 0.20
  $bottom = $size * 0.76
  $heights = @(($size * 0.18), ($size * 0.30), ($size * 0.42))
  for ($i = 0; $i -lt 3; $i++) {
    $brush = New-Object System.Drawing.SolidBrush($barColours[$i])
    $x = $left + $i * ($barWidth + $gap)
    $g.FillRectangle($brush, $x, ($bottom - $heights[$i]), $barWidth, $heights[$i])
    $brush.Dispose()
  }

  # The breakpoint: one red dot, where a debugger puts it - in the margin, at the top.
  $dot = $size * 0.20
  $dotBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 232, 74, 74))
  $g.FillEllipse($dotBrush, ($size * 0.62), ($size * 0.14), $dot, $dot)
  $dotBrush.Dispose()

  $ground.Dispose(); $path.Dispose(); $g.Dispose()
  return $bmp
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bytes = $ms.ToArray()
  $ms.Dispose()
  return $bytes
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = New-Object System.Collections.Generic.List[byte[]]
foreach ($size in $sizes) {
  $bmp = New-Face $size
  $images.Add([byte[]](Get-PngBytes $bmp))
  $bmp.Dispose()
}

# ICONDIR, one ICONDIRENTRY per image, then the images themselves. Every image is a PNG,
# which every Windows since Vista reads in an icon of any size.
$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([UInt16]0)                 # reserved
$writer.Write([UInt16]1)                 # type: icon
$writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $size = $sizes[$i]
  $writer.Write([Byte]$(if ($size -ge 256) { 0 } else { $size }))
  $writer.Write([Byte]$(if ($size -ge 256) { 0 } else { $size }))
  $writer.Write([Byte]0)                 # colours in palette
  $writer.Write([Byte]0)                 # reserved
  $writer.Write([UInt16]1)               # colour planes
  $writer.Write([UInt16]32)              # bits per pixel
  $writer.Write([UInt32]$images[$i].Length)
  $writer.Write([UInt32]$offset)
  $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image, 0, $image.Length) }
$writer.Flush()
[IO.File]::WriteAllBytes($Out, $stream.ToArray())
$writer.Dispose(); $stream.Dispose()
"wrote $Out ($((Get-Item $Out).Length) bytes, $($sizes.Count) sizes)"

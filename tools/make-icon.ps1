# Draws src/SoundpadCompanion/Assets/app.ico: a cyan magnifier with sound bars in the lens, on a dark tile.
# Sizes 16-48 are stored as 32-bit DIBs (read by everything), 256 as PNG.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\SoundpadCompanion\Assets\app.ico'))

Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Dark rounded tile
    $r = 56 * $s
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc(0, 0, $r, $r, 180, 90)
    $tile.AddArc($size - $r - 1, 0, $r, $r, 270, 90)
    $tile.AddArc($size - $r - 1, $size - $r - 1, $r, $r, 0, 90)
    $tile.AddArc(0, $size - $r - 1, $r, $r, 90, 90)
    $tile.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x16, 0x19, 0x1C))), $tile)

    $cyan = [System.Drawing.Color]::FromArgb(255, 0x22, 0xD3, 0xEE)
    $ring = [Math]::Max(1.6, 20 * $s)
    $pen = New-Object System.Drawing.Pen $cyan, $ring
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'

    # Lens and handle
    $cx = 110 * $s; $cy = 110 * $s; $rad = 66 * $s
    $g.DrawEllipse($pen, [single]($cx - $rad), [single]($cy - $rad), [single](2 * $rad), [single](2 * $rad))
    $handle = New-Object System.Drawing.Pen $cyan, ([Math]::Max(2.2, 30 * $s))
    $handle.StartCap = 'Round'; $handle.EndCap = 'Round'
    $g.DrawLine($handle, [single](160 * $s), [single](160 * $s), [single](212 * $s), [single](212 * $s))

    # Sound bars inside the lens (skipped at 16 px, where they turn to mush)
    if ($size -ge 24) {
        $bar = New-Object System.Drawing.Pen $cyan, ([Math]::Max(1.5, 14 * $s))
        $bar.StartCap = 'Round'; $bar.EndCap = 'Round'
        foreach ($b in @(@(-30, 18), @(0, 34), @(30, 22))) {
            $x = $cx + $b[0] * $s; $h = $b[1] * $s
            $g.DrawLine($bar, [single]$x, [single]($cy - $h), [single]$x, [single]($cy + $h))
        }
    }
    $g.Dispose()
    return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $maskStride = [int]([Math]::Ceiling($w / 32.0) * 4)
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]($w * $h * 4 + $maskStride * $h)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $bw.Write((New-Object byte[] ($maskStride * $h)))   # AND mask unused: alpha does the work
    $bw.Flush()
    return , $ms.ToArray()   # the comma stops PowerShell unrolling the array into single bytes
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    if ($size -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $images.Add($ms.ToArray())
    } else {
        $images.Add([byte[]](Get-DibBytes $bmp))
    }
    $bmp.Dispose()
}

New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Dispose()
"Wrote $Out ($((Get-Item $Out).Length) bytes)"

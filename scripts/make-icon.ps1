Add-Type -AssemblyName System.Drawing

$iconRoot = Join-Path $PSScriptRoot "..\src\mirr\assets"
New-Item -ItemType Directory -Force $iconRoot | Out-Null

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @{}

foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 256.0
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $radius = 56 * $s
    $tile.AddArc(0, 0, $radius * 2, $radius * 2, 180, 90)
    $tile.AddArc($size - $radius * 2, 0, $radius * 2, $radius * 2, 270, 90)
    $tile.AddArc($size - $radius * 2, $size - $radius * 2, $radius * 2, $radius * 2, 0, 90)
    $tile.AddArc(0, $size - $radius * 2, $radius * 2, $radius * 2, 90, 90)
    $tile.CloseFigure()

    $tileBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point(0, $size)),
        [System.Drawing.Color]::FromArgb(255, 26, 28, 33),
        [System.Drawing.Color]::FromArgb(255, 16, 17, 21))
    $graphics.FillPath($tileBrush, $tile)

    $accent = [System.Drawing.Color]::FromArgb(255, 91, 157, 255)
    $ghost = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(90, 91, 157, 255), (11 * $s))
    $ghost.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $front = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 232, 237, 246), (11 * $s))
    $front.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $phone = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pw = 108 * $s
    $ph = 196 * $s
    $pr = 34 * $s
    $px = 62 * $s
    $py = 30 * $s
    $phone.AddArc($px, $py, $pr * 2, $pr * 2, 180, 90)
    $phone.AddArc($px + $pw - $pr * 2, $py, $pr * 2, $pr * 2, 270, 90)
    $phone.AddArc($px + $pw - $pr * 2, $py + $ph - $pr * 2, $pr * 2, $pr * 2, 0, 90)
    $phone.AddArc($px, $py + $ph - $pr * 2, $pr * 2, $pr * 2, 90, 90)
    $phone.CloseFigure()

    $shift = New-Object System.Drawing.Drawing2D.Matrix
    $shift.Translate((34 * $s), 0)
    $graphics.Transform = $shift
    $graphics.DrawPath($ghost, $phone)
    $identity = New-Object System.Drawing.Drawing2D.Matrix
    $graphics.Transform = $identity
    $graphics.DrawPath($front, $phone)

    $dot = New-Object System.Drawing.SolidBrush($accent)
    $graphics.FillEllipse($dot, ($px + $pw / 2 - 8 * $s), ($py + $ph - 26 * $s), (16 * $s), (16 * $s))

    $graphics.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$size] = $stream.ToArray()
    $stream.Dispose()
    $bitmap.Dispose()
}

$outPath = Join-Path $iconRoot "mirr.ico"
$headerSize = 6 + 16 * $sizes.Count
$offset = $headerSize
$entries = @()
foreach ($size in $sizes) {
    $entries += , @($size, $pngs[$size].Length, $offset)
    $offset += $pngs[$size].Length
}

$output = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($output)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$sizes.Count)

foreach ($entry in $entries) {
    $size = $entry[0]
    $length = $entry[1]
    $imageOffset = $entry[2]
    $writer.Write([Byte]$(if ($size -ge 256) { 0 } else { $size }))
    $writer.Write([Byte]$(if ($size -ge 256) { 0 } else { $size }))
    $writer.Write([Byte]0)
    $writer.Write([Byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$length)
    $writer.Write([UInt32]$imageOffset)
}

foreach ($entry in $entries) {
    $writer.Write($pngs[$entry[0]])
}

[System.IO.File]::WriteAllBytes($outPath, $output.ToArray())
$writer.Dispose()
$output.Dispose()
Write-Host "icon written to $outPath"

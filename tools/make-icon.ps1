# Generates icon.ico (thermometer on an orange rounded square) with PNG frames at the usual Windows sizes.
# Run from the repo root:  pwsh tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square background.
    $r = [single]($s * 0.22); $d = 2 * $r; $m = [single]($s * 0.03); $w = [single]($s - 2 * $m)
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bg.AddArc($m, $m, $d, $d, 180, 90); $bg.AddArc($m + $w - $d, $m, $d, $d, 270, 90)
    $bg.AddArc($m + $w - $d, $m + $w - $d, $d, $d, 0, 90); $bg.AddArc($m, $m + $w - $d, $d, $d, 90, 90)
    $bg.CloseFigure()
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#eb6834'))
    $g.FillPath($brush, $bg)

    # Thermometer: stem + bulb, white.
    $white = [System.Drawing.Brushes]::White
    $stemW = [single]($s * 0.20); $stemX = [single]($s / 2 - $stemW / 2)
    $stemTop = [single]($s * 0.16); $stemBottom = [single]($s * 0.62)
    $stem = New-Object System.Drawing.Drawing2D.GraphicsPath
    $stem.AddArc($stemX, $stemTop, $stemW, $stemW, 180, 180)
    $stem.AddLine($stemX + $stemW, $stemTop + $stemW / 2, $stemX + $stemW, $stemBottom)
    $stem.AddLine($stemX, $stemBottom, $stemX, $stemTop + $stemW / 2)
    $stem.CloseFigure()
    $g.FillPath($white, $stem)
    $bulbD = [single]($s * 0.36)
    $g.FillEllipse($white, [single]($s / 2 - $bulbD / 2), [single]($s * 0.52), $bulbD, $bulbD)

    # Mercury: orange core so the glyph reads as a thermometer, not a key.
    if ($s -ge 24) {
        $coreW = [single]($stemW * 0.42)
        $g.FillRectangle($brush, [single]($s / 2 - $coreW / 2), [single]($s * 0.36), $coreW, [single]($s * 0.30))
        $coreD = [single]($bulbD * 0.55)
        $g.FillEllipse($brush, [single]($s / 2 - $coreD / 2), [single]($s * 0.52 + ($bulbD - $coreD) / 2), $coreD, $coreD)
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = foreach ($s in $sizes) { , (New-Frame $s) }

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$frames[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot '..\icon.ico'), $out.ToArray())
Write-Host "icon.ico written ($($out.Length) bytes)"

# 生成 app.ico（蓝色显示器 + 电源符号），多尺寸 PNG 内嵌
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot 'app.ico'
$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([Drawing.Color]::Transparent)
    $k = $s / 32.0
    $blue = [Drawing.Color]::FromArgb(59, 130, 246)
    $pen = New-Object Drawing.Pen $blue, ([Math]::Max(1.2, 2.2 * $k))
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $r = [Drawing.RectangleF]::new(3 * $k, 5 * $k, 26 * $k, 17 * $k); $rad = 3 * $k
    $path.AddArc($r.X, $r.Y, $rad * 2, $rad * 2, 180, 90); $path.AddArc($r.Right - $rad * 2, $r.Y, $rad * 2, $rad * 2, 270, 90)
    $path.AddArc($r.Right - $rad * 2, $r.Bottom - $rad * 2, $rad * 2, $rad * 2, 0, 90); $path.AddArc($r.X, $r.Bottom - $rad * 2, $rad * 2, $rad * 2, 90, 90); $path.CloseFigure()
    $g.FillPath([Drawing.Brushes]::White, $path); $g.DrawPath($pen, $path)
    $g.DrawLine($pen, 16 * $k, 22 * $k, 16 * $k, 26 * $k); $g.DrawLine($pen, 10 * $k, 27 * $k, 22 * $k, 27 * $k)
    $pp = New-Object Drawing.Pen $blue, ([Math]::Max(1.2, 2 * $k)); $pp.StartCap = 'Round'; $pp.EndCap = 'Round'
    $g.DrawArc($pp, 11.5 * $k, 9 * $k, 9 * $k, 9 * $k, -60, 300); $g.DrawLine($pp, 16 * $k, 8 * $k, 16 * $k, 13 * $k)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}
$fs = [IO.File]::Create($out); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$d.Length); $bw.Write([uint32]$offset); $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Close(); "ok $out"

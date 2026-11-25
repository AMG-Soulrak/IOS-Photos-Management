# Genera el logo (Assets\logo.png, 256 px) y el icono (Assets\app.ico: 16, 24, 32, 48, 64, 128 y 256 px) de IOS Photos.
# Dibujo propio y original hecho con System.Drawing: cuadrado redondeado azul, una «foto» (marco con montaña y sol)
# y una flecha hacia abajo (copiar al PC). Sin imágenes de terceros.
#
# Uso (Windows PowerShell 5.1 o PowerShell 7 en Windows):  .\tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot "..\src\IPhonePhotos.App\Assets"

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2
  $p.AddArc($x, $y, $d, $d, 180, 90)
  $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
  $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure()
  return $p
}

function New-Logo([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.Clear([System.Drawing.Color]::Transparent)
  $s = $size / 256.0

  # Fondo: degradado azul en un cuadrado redondeado.
  $bg = New-RoundedRect (8*$s) (8*$s) (240*$s) (240*$s) (52*$s)
  $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), ([System.Drawing.Color]::FromArgb(255, 30, 136, 229)), ([System.Drawing.Color]::FromArgb(255, 0, 72, 160))
  $g.FillPath($grad, $bg)

  # Foto: marco blanco con una montaña y un sol.
  $frame = New-RoundedRect (52*$s) (46*$s) (152*$s) (112*$s) (14*$s)
  $g.FillPath([System.Drawing.Brushes]::White, $frame)
  $sky = New-RoundedRect (64*$s) (58*$s) (128*$s) (88*$s) (8*$s)
  $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 210, 232, 252))), $sky)
  $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 183, 3))), 150*$s, 66*$s, 26*$s, 26*$s)
  $mountain = @(
    (New-Object System.Drawing.PointF (64*$s), (146*$s)),
    (New-Object System.Drawing.PointF (108*$s), (90*$s)),
    (New-Object System.Drawing.PointF (136*$s), (122*$s)),
    (New-Object System.Drawing.PointF (154*$s), (104*$s)),
    (New-Object System.Drawing.PointF (192*$s), (146*$s)))
  $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 46, 125, 50))), [System.Drawing.PointF[]]$mountain)

  # Flecha hacia abajo (copiar al PC).
  $white = [System.Drawing.Brushes]::White
  $g.FillRectangle($white, 116*$s, 166*$s, 24*$s, 30*$s)
  $arrow = @(
    (New-Object System.Drawing.PointF (92*$s), (192*$s)),
    (New-Object System.Drawing.PointF (164*$s), (192*$s)),
    (New-Object System.Drawing.PointF (128*$s), (226*$s)))
  $g.FillPolygon($white, [System.Drawing.PointF[]]$arrow)

  $g.Dispose()
  return $bmp
}

# Logo de la ventana.
$logo = New-Logo 256
$logo.Save((Join-Path $assets "logo.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()

# Icono: varias resoluciones, cada una como PNG dentro del .ico (formato admitido desde Windows Vista).
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($n in $sizes) {
  $b = New-Logo $n
  $ms = New-Object System.IO.MemoryStream
  $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $b.Dispose()
  , $ms.ToArray()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $n = $sizes[$i]; $dim = if ($n -ge 256) { 0 } else { $n }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([UInt16]1); $w.Write([UInt16]32)
  $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets "app.ico"), $out.ToArray())
Write-Host "Generados logo.png y app.ico en $assets"

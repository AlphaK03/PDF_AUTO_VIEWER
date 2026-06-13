# Genera PdfAutoViewer/app.ico: fondo azul con el texto "PDF" en blanco.
# Produce un .ico clasico multi-resolucion (16/32/48/256 px, 32 bpp BGRA),
# compatible con la barra de titulo, la barra de tareas y el Explorador.
# Se ejecuta como scriptblock en memoria para no depender de la directiva
# de ejecucion de scripts (.ps1) del equipo.
Add-Type -AssemblyName System.Drawing

$outIco = Join-Path (Get-Location).Path 'PdfAutoViewer\app.ico'
$blue   = [System.Drawing.Color]::FromArgb(30, 90, 192)
$sizes  = @(16, 32, 48, 256)

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad    = [int]($size * 0.06)
    $radius = [int]($size * 0.19)
    $rect   = New-Object System.Drawing.Rectangle $pad, $pad, ($size - 2*$pad), ($size - 2*$pad)
    $path   = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d      = [Math]::Max(2, $radius * 2)
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.SolidBrush $blue
    $g.FillPath($brush, $path)

    $fontPx = [single]($size * 0.30)
    $font   = New-Object System.Drawing.Font 'Arial', $fontPx, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt    = New-Object System.Drawing.StringFormat
    $fmt.Alignment     = 'Center'
    $fmt.LineAlignment = 'Center'
    $g.DrawString('PDF', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 0, $size, $size), $fmt)
    $g.Dispose()
    return $bmp
}

# Convierte un Bitmap a una imagen DIB de 32 bpp (formato BMP dentro del .ico).
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    # BITMAPINFOHEADER (40 bytes). Alto = 2*h para incluir la mascara AND.
    $bw.Write([UInt32]40)
    $bw.Write([Int32]$w)
    $bw.Write([Int32]($h * 2))
    $bw.Write([UInt16]1)        # planos
    $bw.Write([UInt16]32)       # bits por pixel
    $bw.Write([UInt32]0)        # sin compresion
    $bw.Write([UInt32]0)        # tamano de imagen (0 valido)
    $bw.Write([Int32]0); $bw.Write([Int32]0)   # resolucion
    $bw.Write([UInt32]0); $bw.Write([UInt32]0) # paleta

    # Pixeles XOR (BGRA, filas de abajo hacia arriba).
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([Byte]$c.B); $bw.Write([Byte]$c.G); $bw.Write([Byte]$c.R); $bw.Write([Byte]$c.A)
        }
    }
    # Mascara AND (1 bpp). Todo 0 => usar el canal alfa. Filas alineadas a 32 bits.
    $rowBytes = [int]([Math]::Floor(($w + 31) / 32) * 4)
    for ($y = 0; $y -lt $h; $y++) {
        for ($b = 0; $b -lt $rowBytes; $b++) { $bw.Write([Byte]0) }
    }
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return ,$bytes
}

# Genera los DIB de cada tamano.
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $frames += [pscustomobject]@{ Size = $s; Data = (Get-DibBytes $bmp) }
    $bmp.Dispose()
}

# Escribe el contenedor .ico.
$fs = [System.IO.File]::Create($outIco)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)               # reservado
$bw.Write([UInt16]1)               # tipo: icono
$bw.Write([UInt16]$frames.Count)   # numero de imagenes

$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bw.Write([Byte]$dim)          # ancho
    $bw.Write([Byte]$dim)          # alto
    $bw.Write([Byte]0)             # colores
    $bw.Write([Byte]0)             # reservado
    $bw.Write([UInt16]1)           # planos
    $bw.Write([UInt16]32)          # bits por pixel
    $bw.Write([UInt32]$f.Data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $f.Data.Length
}
foreach ($f in $frames) { $bw.Write($f.Data) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()

Write-Host "Icono generado: $outIco ($($frames.Count) tamanos: $($sizes -join ', '))"

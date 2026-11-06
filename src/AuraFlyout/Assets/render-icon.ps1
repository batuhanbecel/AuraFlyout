# Renders Assets\AuraFlyout.ico and Assets\AuraFlyout.png from the vector icon in Assets\AppIcon.xaml.
# Run after changing AppIcon.xaml:  powershell -NoProfile -STA -File Assets\render-icon.ps1
# 16 - 40 px come from AppIconSmall (drawn for small sizes), 48 px and up from AppIcon.
# The .ico holds 32-bit BMP images below 256 px (what every part of Windows reads) and a PNG at 256 px.
param([string]$Dir = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Xaml

$dict = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $Dir 'AppIcon.xaml')))

function Render([int]$size) {
    $img = if ($size -le 40) { $dict['AppIconSmall'] } else { $dict['AppIcon'] }
    $dv = New-Object Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.DrawImage($img, (New-Object Windows.Rect 0, 0, $size, $size))
    $dc.Close()
    $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    # icons want straight (not premultiplied) alpha
    return New-Object Windows.Media.Imaging.FormatConvertedBitmap $rtb, ([Windows.Media.PixelFormats]::Bgra32), $null, 0
}

function Png($bmp) {
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    return , $ms.ToArray()
}

function Dib($bmp, [int]$size) {
    $stride = $size * 4
    $pixels = New-Object byte[] ($stride * $size)
    $bmp.CopyPixels($pixels, $stride, 0)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    # BITMAPINFOHEADER; the height counts the colour and the mask rows
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]($stride * $size + $maskStride * $size)); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $stride, $stride) } # bottom-up
    $w.Write((New-Object byte[] ($maskStride * $size))) # AND mask unused: the alpha decides
    $w.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = Render $s
    , @($s, $(if ($s -ge 256) { Png $bmp } else { Dib $bmp $s }))
}

$ico = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $ico
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($i in $images) {
    $s = $i[0]; $data = $i[1]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)) # 0 means 256
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$data.Length); $w.Write([int]$offset)
    $offset += $data.Length
}
foreach ($i in $images) { $w.Write($i[1]) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $Dir 'AuraFlyout.ico'), $ico.ToArray())
[IO.File]::WriteAllBytes((Join-Path $Dir 'AuraFlyout.png'), (Png (Render 256)))
"AuraFlyout.ico ($($sizes -join ', ') px) and AuraFlyout.png (256 px) written to $Dir"

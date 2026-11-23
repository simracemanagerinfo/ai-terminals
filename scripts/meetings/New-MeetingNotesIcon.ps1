[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Asset creato durante la compilazione. ICO multiuso basato su PNG 256x256:
# Windows Explorer, eseguibile, taskbar e finestra WPF.
$size = 256
$bitmap = [System.Drawing.Bitmap]::new($size, $size)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$graphics.Clear([System.Drawing.Color]::Transparent)

function Get-RoundedPath([int]$x,[int]$y,[int]$w,[int]$h,[int]$r) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $path.AddArc($x,$y,$d,$d,180,90)
    $path.AddArc($x+$w-$d,$y,$d,$d,270,90)
    $path.AddArc($x+$w-$d,$y+$h-$d,$d,$d,0,90)
    $path.AddArc($x,$y+$h-$d,$d,$d,90,90)
    $path.CloseFigure()
    return $path
}

$navy = [System.Drawing.ColorTranslator]::FromHtml('#142E42')
$teal = [System.Drawing.ColorTranslator]::FromHtml('#188A8A')
$light = [System.Drawing.ColorTranslator]::FromHtml('#EAF8F5')
$white = [System.Drawing.Color]::White
$shadow = [System.Drawing.ColorTranslator]::FromHtml('#0E2333')
$backgroundBrush = [System.Drawing.SolidBrush]::new($navy)
$tealBrush = [System.Drawing.SolidBrush]::new($teal)
$paperBrush = [System.Drawing.SolidBrush]::new($light)
$shadowBrush = [System.Drawing.SolidBrush]::new($shadow)
$whiteBrush = [System.Drawing.SolidBrush]::new($white)
$pen = [System.Drawing.Pen]::new($teal, 11)
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

try {
    $outer = Get-RoundedPath 10 10 236 236 52
    $graphics.FillPath($backgroundBrush, $outer)
    $outer.Dispose()

    $accent = Get-RoundedPath 30 30 196 196 38
    $graphics.FillPath($tealBrush, $accent)
    $accent.Dispose()

    # Foglio/verbale sullo sfondo.
    $sheetShadow = Get-RoundedPath 74 47 122 174 15
    $graphics.FillPath($shadowBrush, $sheetShadow)
    $sheetShadow.Dispose()
    $sheet = Get-RoundedPath 68 43 122 174 15
    $graphics.FillPath($paperBrush, $sheet)
    $sheet.Dispose()

    # Tracce grafiche simili a una forma d'onda, leggibili a 32px.
    foreach ($bar in @(
        @(89,110,35), @(111,94,67), @(133,80,95),
        @(155,96,65), @(177,111,34)
    )) {
        $x = [int]$bar[0]
        $cy = 132
        $half = [int]($bar[2] / 2)
        $graphics.DrawLine($pen,$x,$cy-$half,$x,$cy+$half)
    }
    # Un segno di testo alla base del documento.
    $graphics.FillRectangle($tealBrush,91,183,74,8)

    [IO.Directory]::CreateDirectory((Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath)))) | Out-Null
    $png = [IO.MemoryStream]::new()
    try {
        $bitmap.Save($png,[System.Drawing.Imaging.ImageFormat]::Png)
        $payload = $png.ToArray()

        # Header ICO + una entry PNG, con 256 rappresentato da byte 0.
        $ico = [IO.MemoryStream]::new()
        try {
            $writer = [IO.BinaryWriter]::new($ico)
            try {
                $writer.Write([UInt16]0)
                $writer.Write([UInt16]1)
                $writer.Write([UInt16]1)
                $writer.Write([byte]0)  # 256 px
                $writer.Write([byte]0)
                $writer.Write([byte]0)  # palette
                $writer.Write([byte]0)
                $writer.Write([UInt16]1)
                $writer.Write([UInt16]32)
                $writer.Write([UInt32]$payload.Length)
                $writer.Write([UInt32]22)
                $writer.Write($payload)
                $writer.Flush()
                [IO.File]::WriteAllBytes($OutputPath,$ico.ToArray())
            }
            finally { $writer.Dispose() }
        }
        finally { $ico.Dispose() }
    }
    finally { $png.Dispose() }
    Write-Host "[OK] Icona Meeting Notes generata: $OutputPath"
}
finally {
    $pen.Dispose()
    $whiteBrush.Dispose()
    $shadowBrush.Dispose()
    $paperBrush.Dispose()
    $tealBrush.Dispose()
    $backgroundBrush.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

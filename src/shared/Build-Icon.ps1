[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'

# ICO native Windows con PNG approvato incorporato. Nessun pacchetto esterno.
$png = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Source).Path)
if ($png.Length -lt 24) { throw "Immagine PNG troppo piccola: $Source" }
$signature = [byte[]]@(137,80,78,71,13,10,26,10)
for ($i=0; $i -lt $signature.Length; $i++) {
    if ($png[$i] -ne $signature[$i]) {
        throw "Immagine sorgente non e un PNG valido: $Source"
    }
}
$width  = [int](($png[16] -shl 24) -bor ($png[17] -shl 16) -bor ($png[18] -shl 8) -bor $png[19])
$height = [int](($png[20] -shl 24) -bor ($png[21] -shl 16) -bor ($png[22] -shl 8) -bor $png[23])
if ($width -ne $height -or $width -le 0 -or $width -gt 256) {
    throw "L'icona deve avere dimensioni quadrate tra 1 e 256 pixel: $width x $height"
}
$dir = Split-Path -Parent $Destination
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$stream = [IO.File]::Open($Destination, [IO.FileMode]::Create, [IO.FileAccess]::Write)
try {
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        # ICONDIR (6 bytes), ICONDIRENTRY (16 bytes), PNG originale.
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]1)
        $pixelSize = if ($width -eq 256) { [byte]0 } else { [byte]$width }
        $writer.Write($pixelSize)
        $writer.Write($pixelSize)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$png.Length)
        $writer.Write([uint32]22)
        $writer.Write([byte[]]$png)
    }
    finally { $writer.Dispose() }
}
finally { $stream.Dispose() }
Write-Host "[OK] Icona EXE generata dalla PNG ufficiale: $Destination" -ForegroundColor Green

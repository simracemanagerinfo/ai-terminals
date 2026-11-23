[CmdletBinding()]
param(
    [string]$Destination = "$env:LOCALAPPDATA\AI-Terminals\MeetingNotes\Models\ggml-base.bin"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$expected = '60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe'
$source = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin'

$parent = Split-Path -Parent $Destination
New-Item -ItemType Directory -Force -Path $parent | Out-Null
if (Test-Path -LiteralPath $Destination -PathType Leaf) {
    $present = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($present -eq $expected) {
        Write-Host "[OK] Modello multilingua già presente: $Destination" -ForegroundColor Green
        return
    }
    throw "File preesistente non riconosciuto. Non lo sovrascrivo: $Destination"
}

$temp = Join-Path $parent ('.ggml-base-' + [guid]::NewGuid().ToString('N') + '.download')
try {
    Write-Host "Scarico il modello Whisper base multilingua (~148 MB) da Hugging Face." -ForegroundColor Cyan
    Write-Host "Download iniziale via Internet, trascrizione successiva solo locale." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $source -OutFile $temp -MaximumRedirection 5 -TimeoutSec 900 -ErrorAction Stop | Out-Null
    $actual = (Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw 'Hash SHA256 del modello non valido. Download eliminato.' }
    Move-Item -LiteralPath $temp -Destination $Destination -ErrorAction Stop
    Write-Host "[OK] Modello verificato: $Destination" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temp) {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectPath,
    [string]$SessionId
)

$ErrorActionPreference = 'Stop'
try {
    $directory = Get-Item -LiteralPath $ProjectPath -ErrorAction Stop
    if ($directory -isnot [IO.DirectoryInfo]) { throw 'Il percorso non e una cartella.' }

    if ($SessionId) {
        $parsed = [Guid]::Empty
        if (-not [Guid]::TryParse($SessionId, [ref]$parsed)) {
            throw "ID Codex non valido: $SessionId"
        }
    }

    Set-Location -LiteralPath $directory.FullName
    $codex = Get-Command codex.cmd, codex.exe, codex -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $codex) { throw 'Codex CLI non trovato nel PATH.' }

    $Host.UI.RawUI.WindowTitle = "Codex Terminal :: $($directory.Name)"
    Write-Host "[Codex Terminal] Progetto: $($directory.FullName)" -ForegroundColor Cyan

    if ($SessionId) {
        Write-Host "[Codex Terminal] Riprendo sessione $SessionId" -ForegroundColor Cyan
        & $codex.Source resume $SessionId
    }
    else {
        & $codex.Source
    }
    $exitCode = $LASTEXITCODE
    if ($null -eq $exitCode) { $exitCode = 0 }
    Write-Host "[Codex Terminal] Processo terminato ($exitCode)." -ForegroundColor DarkYellow
    exit $exitCode
}
catch {
    Write-Host "[Codex Terminal] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

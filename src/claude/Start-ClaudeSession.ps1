[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectPath,
    [string]$SessionId,
    [string]$NewSessionId
)

$ErrorActionPreference = 'Stop'
try {
    if ($SessionId -and $NewSessionId) { throw 'Usare -SessionId oppure -NewSessionId, non entrambi.' }
    foreach ($id in @($SessionId, $NewSessionId)) {
        if (-not $id) { continue }
        $parsed = [Guid]::Empty
        if (-not [Guid]::TryParse($id, [ref]$parsed)) {
            throw "ID Claude non valido: $id"
        }
    }

    $directory = Get-Item -LiteralPath $ProjectPath -ErrorAction Stop
    if ($directory -isnot [IO.DirectoryInfo]) { throw 'Il percorso non e una cartella.' }
    Set-Location -LiteralPath $directory.FullName

    # Riutilizza il tema gia installato, senza avviare il vecchio selettore.
    $theme = Join-Path $env:LOCALAPPDATA 'PowerShellCustomization\terminal\Start-CyberProfile.ps1'
    if (Test-Path -LiteralPath $theme -PathType Leaf) {
        & $theme -Theme claude_code
    }
    $claude = Get-Command claude.cmd, claude -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $claude) { throw 'Claude Code non trovato nel PATH. Verifica nvm e il profilo PowerShell.' }

    $Host.UI.RawUI.WindowTitle = "Claude Terminal :: $($directory.Name)"
    Write-Host "[Claude Terminal] Progetto: $($directory.FullName)" -ForegroundColor DarkYellow
    if ($SessionId) {
        Write-Host "[Claude Terminal] Riprendo sessione $SessionId" -ForegroundColor DarkYellow
        & $claude.Source --resume $SessionId
    }
    elseif ($NewSessionId) {
        Write-Host "[Claude Terminal] Nuova sessione $NewSessionId" -ForegroundColor DarkYellow
        & $claude.Source --session-id $NewSessionId
    }
    else {
        & $claude.Source
    }

    $claudeExitCode = $LASTEXITCODE
    if ($null -eq $claudeExitCode) { $claudeExitCode = 0 }
    Write-Host "[Claude Terminal] Claude terminato (codice $claudeExitCode)." -ForegroundColor DarkYellow
    exit $claudeExitCode
}
catch {
    Write-Host "[Claude Terminal] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

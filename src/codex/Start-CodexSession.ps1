[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectPath,
    [string]$SessionId,
    [string]$CompletionSignalPath
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

    $options = @()
    if (-not [string]::IsNullOrWhiteSpace($CompletionSignalPath)) {
        $notifyScript = Join-Path $PSScriptRoot 'Notify-CodexTurn.ps1'
        if (-not (Test-Path -LiteralPath $notifyScript -PathType Leaf)) {
            throw "Hook notifiche Codex mancante: $notifyScript"
        }
        # TOML literal strings preserve backslashes in Windows paths.
        # This override is scoped to this Codex process; config.toml is untouched.
        $argv = @('pwsh.exe', '-NoLogo', '-NoProfile', '-NonInteractive',
                  '-File', $notifyScript, [IO.Path]::GetFullPath($CompletionSignalPath))
        $tomlArgs = foreach ($value in $argv) {
            if ($value.Contains("'")) {
                '"' + $value.Replace('\', '\\').Replace('"', '\"') + '"'
            } else {
                "'" + $value + "'"
            }
        }
        $options = @('-c', ('notify=[' + ($tomlArgs -join ',') + ']'))
    }

    if ($SessionId) {
        Write-Host "[Codex Terminal] Riprendo sessione $SessionId" -ForegroundColor Cyan
        & $codex.Source @options resume $SessionId
    }
    else {
        & $codex.Source @options
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

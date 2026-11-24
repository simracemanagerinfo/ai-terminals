[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectPath,
    [string]$SessionId,
    [string]$CompletionSignalPath,
    [ValidateSet('en','it')][string]$UiLanguage = 'en'
)

$ErrorActionPreference = 'Stop'
function L([string]$English, [string]$Italian) {
    if ($UiLanguage -eq 'it') { return $Italian }
    return $English
}
try {
    $directory = Get-Item -LiteralPath $ProjectPath -ErrorAction Stop
    if ($directory -isnot [IO.DirectoryInfo]) { throw (L 'The path is not a directory.' 'Il percorso non e una cartella.') }

    if ($SessionId) {
        $parsed = [Guid]::Empty
        if (-not [Guid]::TryParse($SessionId, [ref]$parsed)) {
            throw (L "Invalid Codex session ID: $SessionId" "ID Codex non valido: $SessionId")
        }
    }

    Set-Location -LiteralPath $directory.FullName
    $codex = Get-Command codex.cmd, codex.exe, codex -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $codex) { throw (L 'Codex CLI not found in PATH.' 'Codex CLI non trovato nel PATH.') }

    $Host.UI.RawUI.WindowTitle = "Codex Terminal :: $($directory.Name)"
    Write-Host (L "[Codex Terminal] Project: $($directory.FullName)" "[Codex Terminal] Progetto: $($directory.FullName)") -ForegroundColor Cyan

    $options = @()
    if (-not [string]::IsNullOrWhiteSpace($CompletionSignalPath)) {
        $notifyScript = Join-Path $PSScriptRoot 'Notify-CodexTurn.ps1'
        if (-not (Test-Path -LiteralPath $notifyScript -PathType Leaf)) {
            throw (L "Missing Codex notification hook: $notifyScript" "Hook notifiche Codex mancante: $notifyScript")
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
        # Native TUI notifications cover interactive questions and approvals.
        # The host sees OSC 9 even for a background session in this window.
        # Keep completion on the existing structured external hook.
        $options = @('-c', ('notify=[' + ($tomlArgs -join ',') + ']'),
                     '-c', "tui.notifications=['approval-requested','plan-mode-prompt','user-input-requested','async-question']",
                     '-c', "tui.notification_method='osc9'",
                     '-c', "tui.notification_condition='always'")
    }

    if ($SessionId) {
        Write-Host (L "[Codex Terminal] Resuming session $SessionId" "[Codex Terminal] Riprendo sessione $SessionId") -ForegroundColor Cyan
        & $codex.Source @options resume $SessionId
    }
    else {
        & $codex.Source @options
    }
    $exitCode = $LASTEXITCODE
    if ($null -eq $exitCode) { $exitCode = 0 }
    Write-Host (L "[Codex Terminal] Process finished ($exitCode)." "[Codex Terminal] Processo terminato ($exitCode).") -ForegroundColor DarkYellow
    exit $exitCode
}
catch {
    Write-Host "[Codex Terminal] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

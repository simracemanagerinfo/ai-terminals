[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectPath,
    [string]$SessionId,
    [string]$NewSessionId,
    [string]$CompletionSignalPath
)

$ErrorActionPreference = 'Stop'
$settingsFile = $null
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
    $options = @()
    if (-not [string]::IsNullOrWhiteSpace($CompletionSignalPath)) {
        $notifyScript = Join-Path $PSScriptRoot 'Notify-ClaudeTurn.ps1'
        if (-not (Test-Path -LiteralPath $notifyScript -PathType Leaf)) {
            throw "Hook Claude mancante: $notifyScript"
        }
        # Session-scoped --settings: do not write ~/.claude/settings.json.
        # Stop-hook input is received via stdin and never persisted.
        $cmd = 'pwsh.exe -NoLogo -NoProfile -NonInteractive -File "' +
            $notifyScript + '" -SignalPath "' +
            [IO.Path]::GetFullPath($CompletionSignalPath) + '"'
        $settings = @{
            hooks = @{ Stop = @(@{ hooks = @(@{
                type = 'command'
                command = $cmd
                timeout = 5
            }) }) }
        } | ConvertTo-Json -Depth 8
        # Claude's Windows .cmd shim can strip quotes from inline JSON.
        # --settings explicitly supports a file path; persist the temporary
        # JSON only while this CLI process is running.
        $settingsFile = Join-Path ([IO.Path]::GetTempPath()) (
            'aiterminals-claude-settings-' + [Guid]::NewGuid().ToString('N') + '.json')
        Set-Content -LiteralPath $settingsFile -Value $settings -Encoding utf8 -NoNewline
        # Validate the exact bytes Claude will read, not only the in-memory JSON.
        $validated = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json -AsHashtable
        if (-not $validated['hooks']['Stop'][0]['hooks'][0]['command']) {
            throw 'Configurazione hook Claude non valida.'
        }
        $options = @('--settings', $settingsFile)
    }

    if ($SessionId) {
        Write-Host "[Claude Terminal] Riprendo sessione $SessionId" -ForegroundColor DarkYellow
        & $claude.Source @options --resume $SessionId
    }
    elseif ($NewSessionId) {
        Write-Host "[Claude Terminal] Nuova sessione $NewSessionId" -ForegroundColor DarkYellow
        & $claude.Source @options --session-id $NewSessionId
    }
    else {
        & $claude.Source @options
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
finally {
    if ($settingsFile -and (Test-Path -LiteralPath $settingsFile -PathType Leaf)) {
        Remove-Item -LiteralPath $settingsFile -Force -ErrorAction SilentlyContinue
    }
}

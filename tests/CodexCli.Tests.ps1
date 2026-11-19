$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP ('codex-cli-test-' + [guid]::NewGuid().ToString('N'))
$project = Join-Path $root 'workspace'
$bin = Join-Path $root 'bin'
$originalPath = $env:PATH
$originalLog = $env:CLAUDE_TERMINAL_TEST_LOG
$wrapper = Join-Path $PSScriptRoot '..\src\codex\Start-CodexSession.ps1'
try {
    New-Item -ItemType Directory -Force -Path $project,$bin | Out-Null
    $mock = Join-Path $bin 'codex.cmd'
    Set-Content -LiteralPath $mock -Encoding ascii -Value @(
        '@echo off',
        'echo Args:%* > "%CLAUDE_TERMINAL_TEST_LOG%"',
        'exit /b 0'
    )
    $env:PATH = "$bin;$originalPath"
    $env:CLAUDE_TERMINAL_TEST_LOG = Join-Path $root 'captured.txt'
    & pwsh.exe -NoLogo -NoProfile -File $wrapper -ProjectPath $project | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Codex launch failed' }
    if (((Get-Content -LiteralPath $env:CLAUDE_TERMINAL_TEST_LOG -Raw).Trim()) -ne 'Args:') {
        throw 'Codex new session must not invent a UUID'
    }

    $id = [guid]::NewGuid().ToString('D')
    Remove-Item -LiteralPath $env:CLAUDE_TERMINAL_TEST_LOG -Force
    & pwsh.exe -NoLogo -NoProfile -File $wrapper -ProjectPath $project -SessionId $id | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Codex resume failed' }
    if (((Get-Content -LiteralPath $env:CLAUDE_TERMINAL_TEST_LOG -Raw).Trim()) -ne "Args:resume $id") {
        throw 'Codex resume did not pass the expected session ID'
    }
    # Session-scoped notifier must not change the user's global Codex config.
    $signal = Join-Path $root 'completion signals.txt'
    $hook = Join-Path $PSScriptRoot '..\src\codex\Notify-CodexTurn.ps1'
    Remove-Item -LiteralPath $env:CLAUDE_TERMINAL_TEST_LOG -Force
    & pwsh.exe -NoLogo -NoProfile -File $wrapper -ProjectPath $project -CompletionSignalPath $signal | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Codex notify launch failed' }
    $captured = (Get-Content -LiteralPath $env:CLAUDE_TERMINAL_TEST_LOG -Raw).Trim()
    if (-not $captured.Contains('notify=[') -or
        -not $captured.Contains('Notify-CodexTurn.ps1') -or
        -not $captured.Contains('completion signals.txt')) {
        throw "Codex did not receive its private notifier: $captured"
    }

    Remove-Item -LiteralPath $env:CLAUDE_TERMINAL_TEST_LOG -Force
    & pwsh.exe -NoLogo -NoProfile -File $wrapper -ProjectPath $project -SessionId $id -CompletionSignalPath $signal | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Codex resume with notify failed' }
    $captured = (Get-Content -LiteralPath $env:CLAUDE_TERMINAL_TEST_LOG -Raw).Trim()
    if (-not $captured.Contains("resume $id") -or -not $captured.Contains('notify=[')) {
        throw "Codex resume lost notifier or session ID: $captured"
    }

    # Hook records ONLY completion IDs; no prompt, reply or private data.
    $threadId = [guid]::NewGuid().ToString('D')
    $completed = "{`"type`":`"agent-turn-complete`",`"turn-id`":`"turn-1`",`"thread-id`":`"$threadId`",`"client`":`"codex-tui`",`"last-assistant-message`":`"secret`"}"
    # Codex appends the JSON payload as an unnamed final argument.
    & pwsh.exe -NoLogo -NoProfile -File $hook $signal $completed
    & pwsh.exe -NoLogo -NoProfile -File $hook -SignalPath $signal -EventJson '{"type":"approval-requested","turn-id":"turn-2"}'
    & pwsh.exe -NoLogo -NoProfile -File $hook -SignalPath $signal -EventJson '{"type":"agent-turn-complete","turn-id":"subagent","client":"codex-exec"}'
    $lines = @(Get-Content -LiteralPath $signal)
    if ($lines.Count -ne 1 -or $lines[0] -ne "turn-1|$threadId" -or
        (Get-Content -LiteralPath $signal -Raw).Contains('secret')) {
        throw 'Codex hook leaked content or captured a non-TUI completion'
    }

    $global:LASTEXITCODE = 0
    Write-Host '[PASS] Codex CLI new chat/resume, private notify hook and event filtering' -ForegroundColor Green
}
finally {
    $env:PATH = $originalPath
    $env:CLAUDE_TERMINAL_TEST_LOG = $originalLog
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

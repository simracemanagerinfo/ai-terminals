# Test del launcher senza installare Claude e senza contattare servizi esterni.
# Simula claude.cmd e controlla i parametri inviati per nuova chat e resume.
$ErrorActionPreference = 'Stop'
$temp = Join-Path $env:TEMP ('claude-terminal-cli-tests-' + [Guid]::NewGuid().ToString('N'))
$launcher = Join-Path $PSScriptRoot '..\src\claude\Start-ClaudeSession.ps1'
$originalPath = $env:PATH
$originalLog = $env:CLAUDE_TERMINAL_TEST_LOG
$originalChecker = $env:CLAUDE_SETTINGS_CHECKER
$originalCheckLog = $env:CLAUDE_SETTINGS_CHECK_LOG

try {
    $project = Join-Path $temp 'test project'
    $bin = Join-Path $temp 'bin'
    New-Item -ItemType Directory -Force -Path $project, $bin | Out-Null
    $mock = Join-Path $bin 'claude.cmd'
    Set-Content -LiteralPath $mock -Encoding ascii -Value @(
        '@echo off'
        'echo Args:%* > "%CLAUDE_TERMINAL_TEST_LOG%"'
        'if /I not "%~1"=="--settings" exit /b 0'
        'pwsh.exe -NoLogo -NoProfile -File "%CLAUDE_SETTINGS_CHECKER%" -SettingsPath "%~2"'
        'exit /b %ERRORLEVEL%'
    )
    $env:PATH = "$bin;$originalPath"
    $env:CLAUDE_TERMINAL_TEST_LOG = Join-Path $temp 'claude-args.txt'
    $env:CLAUDE_SETTINGS_CHECKER = Join-Path $PSScriptRoot 'Inspect-ClaudeSettings.ps1'
    $env:CLAUDE_SETTINGS_CHECK_LOG = Join-Path $temp 'settings-checked.txt'
    $newId = [guid]::NewGuid().ToString('D')
    $oldId = [guid]::NewGuid().ToString('D')

    function Assert-Invocation([string[]]$Arguments, [string]$Expected, [string]$Name) {
        Remove-Item -LiteralPath $env:CLAUDE_TERMINAL_TEST_LOG -Force -ErrorAction SilentlyContinue
        & pwsh.exe -NoLogo -NoProfile -File $launcher -ProjectPath $project @Arguments | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "$Name exited with $LASTEXITCODE" }
        if (-not (Test-Path -LiteralPath $env:CLAUDE_TERMINAL_TEST_LOG)) {
            throw "$Name did not invoke the mock CLI"
        }
        $actual = (Get-Content -LiteralPath $env:CLAUDE_TERMINAL_TEST_LOG -Raw).Trim()
        if ($actual -ne ('Args:' + $Expected)) {
            throw "$Name wrong arguments: expected '$Expected', received '$actual'"
        }
    }

    Assert-Invocation -Arguments @('-NewSessionId', $newId) -Expected "--session-id $newId" -Name 'New session'
    Assert-Invocation -Arguments @('-SessionId', $oldId) -Expected "--resume $oldId" -Name 'Resume session'
    Assert-Invocation -Arguments @() -Expected '' -Name 'Manual Claude CLI'

    Remove-Item -LiteralPath $env:CLAUDE_TERMINAL_TEST_LOG -Force -ErrorAction SilentlyContinue
    & pwsh.exe -NoLogo -NoProfile -File $launcher -ProjectPath $project -SessionId $oldId -NewSessionId $newId | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'Conflicting IDs were accepted' }
    if (Test-Path -LiteralPath $env:CLAUDE_TERMINAL_TEST_LOG) {
        throw 'Conflicting IDs reached Claude CLI'
    }
    # Per-process Stop hook is passed only to this Claude CLI invocation.
    $signal = Join-Path $temp 'claude-completions.signals'
    $hook = Join-Path $PSScriptRoot '..\src\claude\Notify-ClaudeTurn.ps1'
    Remove-Item -LiteralPath $env:CLAUDE_TERMINAL_TEST_LOG -Force -ErrorAction SilentlyContinue
    & pwsh.exe -NoLogo -NoProfile -File $launcher -ProjectPath $project -NewSessionId $newId -CompletionSignalPath $signal | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Claude notify-enabled launcher failed' }
    $captured = (Get-Content -LiteralPath $env:CLAUDE_TERMINAL_TEST_LOG -Raw).Trim()
    if (-not $captured.Contains('--settings') -or
        -not $captured.Contains('.json') -or
        -not $captured.Contains('--session-id')) {
        throw "Claude settings file not passed to CLI: $captured"
    }
    $validatedPaths = @(Get-Content -LiteralPath $env:CLAUDE_SETTINGS_CHECK_LOG)
    if ($validatedPaths.Count -ne 1 -or
        (Test-Path -LiteralPath $validatedPaths[0])) {
        throw 'Claude new-chat temporary settings JSON was not validated or cleaned up'
    }
    Remove-Item -LiteralPath $env:CLAUDE_TERMINAL_TEST_LOG -Force
    & pwsh.exe -NoLogo -NoProfile -File $launcher -ProjectPath $project -SessionId $oldId -CompletionSignalPath $signal | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Claude notify-enabled resume failed' }
    $captured = (Get-Content -LiteralPath $env:CLAUDE_TERMINAL_TEST_LOG -Raw).Trim()
    if (-not $captured.Contains('--settings') -or -not $captured.Contains("--resume $oldId")) {
        throw "Claude resume lost Stop hook or session ID: $captured"
    }
    $validatedPaths = @(Get-Content -LiteralPath $env:CLAUDE_SETTINGS_CHECK_LOG)
    if ($validatedPaths.Count -ne 2 -or
        $validatedPaths[0] -eq $validatedPaths[1] -or
        (Test-Path -LiteralPath $validatedPaths[1])) {
        throw 'Claude resume settings JSON was not validated or uniquely cleaned up'
    }

    # The marker file must never receive private reply text.
    '{"hook_event_name":"Stop","session_id":"example","last_assistant_message":"secret"}' |
        & pwsh.exe -NoLogo -NoProfile -File $hook -SignalPath $signal
    '{"hook_event_name":"SubagentStop","session_id":"example"}' |
        & pwsh.exe -NoLogo -NoProfile -File $hook -SignalPath $signal
    $lines = @(Get-Content -LiteralPath $signal)
    if ($lines.Count -ne 1 -or $lines[0] -notmatch '^[0-9a-f]{32}$' -or
        (Get-Content -LiteralPath $signal -Raw).Contains('secret')) {
        throw 'Claude Stop hook did not record a private completion marker'
    }

    # Il caso negativo lascia LASTEXITCODE=1 nel processo di test, che GitHub
    # Actions interpretera come fallimento anche se tutte le asserzioni sono OK.
    $global:LASTEXITCODE = 0
    Write-Host '[PASS] Claude session launch/resume, private Stop hook and completion markers.' -ForegroundColor Green
}
finally {
    $env:PATH = $originalPath
    $env:CLAUDE_TERMINAL_TEST_LOG = $originalLog
    $env:CLAUDE_SETTINGS_CHECKER = $originalChecker
    $env:CLAUDE_SETTINGS_CHECK_LOG = $originalCheckLog
    Remove-Item -LiteralPath $temp -Force -Recurse -ErrorAction SilentlyContinue
}

# Test del launcher senza installare Claude e senza contattare servizi esterni.
# Simula claude.cmd e controlla i parametri inviati per nuova chat e resume.
$ErrorActionPreference = 'Stop'
$temp = Join-Path $env:TEMP ('claude-terminal-cli-tests-' + [Guid]::NewGuid().ToString('N'))
$launcher = Join-Path $PSScriptRoot '..\src\claude\Start-ClaudeSession.ps1'
$originalPath = $env:PATH
$originalLog = $env:CLAUDE_TERMINAL_TEST_LOG

try {
    $project = Join-Path $temp 'test project'
    $bin = Join-Path $temp 'bin'
    New-Item -ItemType Directory -Force -Path $project, $bin | Out-Null
    $mock = Join-Path $bin 'claude.cmd'
    Set-Content -LiteralPath $mock -Encoding ascii -Value @(
        '@echo off'
        'echo Args:%* > "%CLAUDE_TERMINAL_TEST_LOG%"'
        'exit /b 0'
    )
    $env:PATH = "$bin;$originalPath"
    $env:CLAUDE_TERMINAL_TEST_LOG = Join-Path $temp 'claude-args.txt'
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
    # Il caso negativo lascia LASTEXITCODE=1 nel processo di test, che GitHub
    # Actions interpretera come fallimento anche se tutte le asserzioni sono OK.
    $global:LASTEXITCODE = 0
    Write-Host '[PASS] Claude --session-id, --resume, manual launch and conflict prevention.' -ForegroundColor Green
}
finally {
    $env:PATH = $originalPath
    $env:CLAUDE_TERMINAL_TEST_LOG = $originalLog
    Remove-Item -LiteralPath $temp -Force -Recurse -ErrorAction SilentlyContinue
}

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
    $global:LASTEXITCODE = 0
    Write-Host '[PASS] Codex interactive CLI new chat and resume arguments' -ForegroundColor Green
}
finally {
    $env:PATH = $originalPath
    $env:CLAUDE_TERMINAL_TEST_LOG = $originalLog
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

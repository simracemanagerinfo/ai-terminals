[CmdletBinding()]
param([Parameter(Mandatory)][string]$SettingsPath)

$ErrorActionPreference = 'Stop'
try {
    if (-not (Test-Path -LiteralPath $SettingsPath -PathType Leaf)) {
        throw "Temporary Claude settings file missing: $SettingsPath"
    }
    $settings = Get-Content -LiteralPath $SettingsPath -Raw -Encoding utf8 |
        ConvertFrom-Json -AsHashtable
    $stop = $settings['hooks']['Stop']
    if (@($stop).Count -ne 1 -or
        @($stop[0]['hooks']).Count -ne 1 -or
        $stop[0]['hooks'][0]['type'] -ne 'command' -or
        -not $stop[0]['hooks'][0]['command'].Contains('Notify-ClaudeTurn.ps1')) {
        throw 'Claude Stop hook was not correctly serialized as settings JSON'
    }
    if ($settings['hooks']['PreToolUse'][0]['matcher'] -ne '^AskUserQuestion$' -or
        $settings['hooks']['Notification'][0]['matcher'] -notmatch 'permission_prompt' -or
        -not $settings['hooks']['PreToolUse'][0]['hooks'][0]['command'].Contains('Notify-ClaudeTurn.ps1')) {
        throw 'Claude attention hooks were not serialized correctly'
    }
    if (-not $env:CLAUDE_SETTINGS_CHECK_LOG) { throw 'Test log not configured' }
    Add-Content -LiteralPath $env:CLAUDE_SETTINGS_CHECK_LOG -Value $SettingsPath -Encoding utf8
    exit 0
}
catch {
    Write-Error $_
    exit 21
}

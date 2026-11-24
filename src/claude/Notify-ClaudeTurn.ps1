[CmdletBinding()]
param([Parameter(Mandatory)][string]$SignalPath)

# Claude Code Stop hooks pass session/event JSON on stdin.
# The response contents are intentionally discarded.
$ErrorActionPreference = 'Stop'
try {
    if (-not [IO.Path]::IsPathFullyQualified($SignalPath)) { return }
    $folder = [IO.Path]::GetDirectoryName($SignalPath)
    if (-not [IO.Directory]::Exists($folder)) { return }

    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { return }
    $event = $raw | ConvertFrom-Json -AsHashtable
    $token = [Guid]::NewGuid().ToString('N')
    switch ($event['hook_event_name']) {
        'Stop' { }
        'PreToolUse' {
            if ($event['tool_name'] -ne 'AskUserQuestion') { return }
            $token = 'input:' + $token
        }
        'Notification' {
            if ($event['notification_type'] -notin @('permission_prompt','elicitation_dialog','elicitation_url_dialog')) { return }
            $token = 'input:' + $token
        }
        default { return }
    }
    # Persist only opaque event markers, never the question or tool input.
    Add-Content -LiteralPath $SignalPath -Value $token -Encoding utf8
}
catch {
    # Hook delivery failures should never interrupt an interactive session.
    exit 0
}

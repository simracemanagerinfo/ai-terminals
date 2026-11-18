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
    if ($event['hook_event_name'] -ne 'Stop') { return }

    # One new token for each completed reply in this window, independent
    # of the session ID (which is stable across many replies).
    Add-Content -LiteralPath $SignalPath -Value ([Guid]::NewGuid().ToString('N')) -Encoding utf8
}
catch {
    # Hook delivery failures should never interrupt an interactive session.
    exit 0
}

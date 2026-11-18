[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string]$SignalPath,
    [Parameter(Mandatory, Position = 1)][string]$EventJson
)

# Invoked by Codex CLI via its per-process "notify" hook.
# Never store the prompt, the reply or any credential: only one turn identifier.
$ErrorActionPreference = 'Stop'
try {
    $notification = $EventJson | ConvertFrom-Json -AsHashtable
    if ($notification['type'] -ne 'agent-turn-complete') { return }

    # An external hook can also run in delegated clients. Ignore known
    # non-interactive senders if Codex supplies the client metadata.
    $client = [string]$notification['client']
    if ($client -and $client -ne 'codex-tui') { return }

    $turnId = [string]$notification['turn-id']
    if ([string]::IsNullOrWhiteSpace($turnId) -or $turnId.Length -gt 128) { return }
    if (-not [IO.Path]::IsPathFullyQualified($SignalPath)) { return }
    $folder = [IO.Path]::GetDirectoryName($SignalPath)
    if (-not [IO.Directory]::Exists($folder)) { return }

    Add-Content -LiteralPath $SignalPath -Value $turnId -Encoding utf8
}
catch {
    # A notification must never break an interactive Codex session.
    exit 0
}

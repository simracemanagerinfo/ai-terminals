[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Claude','Codex')][string]$Provider
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Text.UTF8Encoding]::new($false)

try {
    $prompt = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($prompt)) { throw 'Prompt vuoto.' }

    if ($Provider -eq 'Claude') {
        $cli = Get-Command claude.cmd, claude.exe, claude -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $cli) { throw 'Claude CLI non trovato nel PATH.' }
        # I tool vengono rimossi dal contesto: il modello deve rispondere con solo JSON.
        $cliArgs = @('-p','--no-session-persistence','--tools','','--output-format','text')
    }
    else {
        $cli = Get-Command codex.cmd, codex.exe, codex -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $cli) { throw 'Codex CLI non trovato nel PATH.' }
        # Read-only riduce il rischio di scrittura, ma non isola tutte le letture
        # del filesystem. Valutare la policy aziendale prima di inviare dati reali.
        $cliArgs = @('exec','--sandbox','read-only','--skip-git-repo-check','-')
    }
    $output = @($prompt | & $cli.Source @cliArgs)
    $status = $LASTEXITCODE
    if ($null -eq $status) { $status = 0 }
    if ($status -ne 0) { exit $status }
    [Console]::Out.Write(($output -join [Environment]::NewLine))
    exit 0
}
catch {
    [Console]::Error.WriteLine('Avvio del provider fallito. Controllare versione, login e permessi del CLI.')
    exit 1
}

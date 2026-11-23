[CmdletBinding()]
param(
    [string]$Destination,
    [string]$Repository = 'simracemanagerinfo/ai-terminals'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Destination) {
    $project = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $Destination = Join-Path $project 'out\MeetingNotes'
}
$parent = Split-Path -Parent $Destination
New-Item -ItemType Directory -Force -Path $parent | Out-Null

$gh = Get-Command gh.exe, gh -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $gh) {
    throw 'GitHub CLI (gh) non trovato. Usa dotnet publish per compilare localmente.'
}
& $gh.Source auth status --hostname github.com 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI non autenticato: esegui gh auth login.' }

$runJson = & $gh.Source run list --repo $Repository --workflow meeting-notes-tests.yml --branch feature/meeting-notes --status success --limit 1 --json databaseId,headSha,conclusion | Out-String
if ($LASTEXITCODE -ne 0) { throw 'Ricerca build GitHub Actions fallita.' }
$latest = @($runJson | ConvertFrom-Json)
if ($latest.Count -eq 0 -or -not $latest[0].databaseId) {
    throw 'Nessuna build Meeting Notes riuscita trovata sul branch.'
}
$runId = [long]$latest[0].databaseId
Write-Host "Scarico MeetingNotes dalla build $runId (commit $($latest[0].headSha))" -ForegroundColor Cyan

$staging = Join-Path $parent ('.MeetingNotes-stage-' + [guid]::NewGuid().ToString('N'))
$backup = Join-Path $parent ('.MeetingNotes-backup-' + [guid]::NewGuid().ToString('N'))
$backupExists = $false
try {
    if (Get-Process -Name 'MeetingNotes' -ErrorAction SilentlyContinue) {
        throw 'Chiudi MeetingNotes.exe prima di aggiornare.'
    }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    & $gh.Source run download $runId --repo $Repository --name MeetingNotes-win-x64 --dir $staging
    if ($LASTEXITCODE -ne 0) { throw 'Download del pacchetto non riuscito.' }

    foreach ($path in @('MeetingNotes.exe','scripts\Invoke-MeetingMinutes.ps1','scripts\Invoke-MeetingProvider.ps1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $path) -PathType Leaf)) {
            throw "File richiesto assente nel pacchetto: $path"
        }
    }
    if (Test-Path -LiteralPath $Destination) {
        if (-not (Test-Path -LiteralPath (Join-Path $Destination 'MeetingNotes.exe') -PathType Leaf)) {
            throw 'Destinazione esistente non riconosciuta come installazione Meeting Notes.'
        }
        Rename-Item -LiteralPath $Destination -NewName (Split-Path -Leaf $backup) -ErrorAction Stop
        $backupExists = $true
    }
    try {
        Rename-Item -LiteralPath $staging -NewName (Split-Path -Leaf $Destination) -ErrorAction Stop
    }
    catch {
        if ($backupExists -and -not (Test-Path -LiteralPath $Destination)) {
            Rename-Item -LiteralPath $backup -NewName (Split-Path -Leaf $Destination)
            $backupExists = $false
        }
        throw
    }
    Write-Host "[OK] MeetingNotes.exe: $Destination" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($backupExists -and (Test-Path -LiteralPath $Destination)) {
        Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
    }
}

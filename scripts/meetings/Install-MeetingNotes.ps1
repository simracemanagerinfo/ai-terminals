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
$Destination = [IO.Path]::GetFullPath($Destination)
$parent = Split-Path -Parent $Destination
New-Item -ItemType Directory -Force -Path $parent | Out-Null

function Get-AuthorizedGitHubToken {
    if ($env:GITHUB_TOKEN) { return [string]$env:GITHUB_TOKEN }
    if ($env:GH_TOKEN) { return [string]$env:GH_TOKEN }

    $gh = Get-Command gh.exe, gh -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($gh) {
        $candidate = @(& $gh.Source auth token --hostname github.com 2>$null)
        if ($LASTEXITCODE -eq 0 -and $candidate.Count -gt 0 -and $candidate[0]) {
            return [string]$candidate[0]
        }
    }
    $git = Get-Command git.exe, git -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($git) {
        # Git Credential Manager può riutilizzare l'accesso usato da git pull.
        # Non stampare le righe: contengono le credenziali in chiaro.
        $request = "protocol=https" + [Environment]::NewLine +
            "host=github.com" + [Environment]::NewLine + [Environment]::NewLine
        $entries = @($request | & $git.Source credential fill 2>$null)
        if ($LASTEXITCODE -eq 0) {
            foreach ($entry in $entries) {
                if ($entry -cmatch '^password=(.+)$') { return [string]$Matches[1] }
            }
        }
    }
    return $null
}

function Publish-Locally {
    param([string]$StagingPath, [string]$ProjectRoot)
    $dotnet = Get-Command dotnet.exe, dotnet -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $dotnet) {
        throw 'Nessun token GitHub disponibile e .NET SDK assente. Autenticare Git Credential Manager oppure installare il .NET 10 SDK.'
    }
    $projectFile = Join-Path $ProjectRoot 'src\meetings\MeetingNotes.csproj'
    if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) { throw 'Progetto MeetingNotes.csproj non trovato.' }
    & $dotnet.Source publish $projectFile -c Release -r win-x64 --self-contained true -o $StagingPath
    if ($LASTEXITCODE -ne 0) { throw 'Compilazione MeetingNotes.exe non riuscita.' }
}

if (Get-Process -Name 'MeetingNotes' -ErrorAction SilentlyContinue) {
    throw 'Chiudi MeetingNotes.exe prima di aggiornare.'
}

$staging = Join-Path $parent ('.MeetingNotes-stage-' + [guid]::NewGuid().ToString('N'))
$backup = Join-Path $parent ('.MeetingNotes-backup-' + [guid]::NewGuid().ToString('N'))
$archive = Join-Path $parent ('.MeetingNotes-' + [guid]::NewGuid().ToString('N') + '.zip')
$oldMoved = $false
$installed = $false
try {
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    $token = Get-AuthorizedGitHubToken
    if ($token) {
        try {
            $headers = @{
                Accept = 'application/vnd.github+json'
                Authorization = "Bearer $token"
                'User-Agent' = 'MeetingNotes-Installer'
                'X-GitHub-Api-Version' = '2022-11-28'
            }
            $token = $null
            $branch = [uri]::EscapeDataString('feature/meeting-notes')
            $runsUri = "https://api.github.com/repos/$Repository/actions/runs?branch=$branch&status=success&per_page=30"
            $runs = Invoke-RestMethod -Uri $runsUri -Headers $headers -TimeoutSec 45
            $latest = @($runs.workflow_runs | Where-Object {
                $_.conclusion -eq 'success' -and $_.path -match 'meeting-notes-tests\.yml$'
            } | Sort-Object created_at -Descending | Select-Object -First 1)
            if ($latest.Count -ne 1) { throw 'Non esiste una build Meeting Notes riuscita.' }
            $runId = [long]$latest[0].id
            $index = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/actions/runs/$runId/artifacts?per_page=100" -Headers $headers -TimeoutSec 45
            $artifact = @($index.artifacts | Where-Object {
                $_.name -eq 'MeetingNotes-win-x64' -and -not $_.expired
            } | Select-Object -First 1)
            if ($artifact.Count -ne 1) { throw "Nessun pacchetto Meeting Notes per la build $runId." }
            if ([long]$artifact[0].size_in_bytes -le 0 -or [long]$artifact[0].size_in_bytes -gt 512MB) {
                throw 'Dimensione del pacchetto anomala.'
            }

            Write-Host "Download build $runId dal branch feature/meeting-notes..." -ForegroundColor Cyan
            $zipUri = "https://api.github.com/repos/$Repository/actions/artifacts/$($artifact[0].id)/zip"
            Invoke-WebRequest -Uri $zipUri -Headers $headers -OutFile $archive -TimeoutSec 240 -MaximumRedirection 5 -ErrorAction Stop | Out-Null
            Expand-Archive -LiteralPath $archive -DestinationPath $staging -Force
        }
        finally {
            $headers = $null
            $token = $null
        }
    }
    else {
        Write-Warning 'GitHub CLI/credenziali non disponibili: provo la compilazione locale.'
        $root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
        Publish-Locally -StagingPath $staging -ProjectRoot $root
    }

    foreach ($required in @(
        'MeetingNotes.exe',
        'scripts\Invoke-MeetingMinutes.ps1',
        'scripts\Invoke-MeetingProvider.ps1',
        'transcriber\MeetingTranscriber.exe'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $required) -PathType Leaf)) {
            throw "Pacchetto incompleto, manca: $required. La versione precedente resta intatta."
        }
    }
    if (Test-Path -LiteralPath $Destination) {
        if (-not (Test-Path -LiteralPath (Join-Path $Destination 'MeetingNotes.exe') -PathType Leaf)) {
            throw 'La cartella di destinazione esiste, ma non è una installazione Meeting Notes gestita.'
        }
        Rename-Item -LiteralPath $Destination -NewName (Split-Path -Leaf $backup) -ErrorAction Stop
        $oldMoved = $true
    }
    try {
        Rename-Item -LiteralPath $staging -NewName (Split-Path -Leaf $Destination) -ErrorAction Stop
        $installed = $true
        Write-Host "[OK] Meeting Notes installato in: $Destination" -ForegroundColor Green
    }
    catch {
        if ($oldMoved -and -not (Test-Path -LiteralPath $Destination)) {
            Rename-Item -LiteralPath $backup -NewName (Split-Path -Leaf $Destination)
            $oldMoved = $false
        }
        throw
    }
}
finally {
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
    if ($installed -and $oldMoved -and (Test-Path -LiteralPath $backup)) {
        try { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning "Backup precedente da rimuovere manualmente: $backup" }
    }
}

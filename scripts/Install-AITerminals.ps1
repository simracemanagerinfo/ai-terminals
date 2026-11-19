[CmdletBinding()]
param(
    [ValidateSet('Claude','Codex','Both')][string]$App = 'Codex',
    [long]$RunId = 0
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Installa solo negli stessi due percorsi fissi; nessuna nuova copia permanente.
$repo = 'simracemanagerinfo/ai-terminals'
$root = Join-Path $env:LOCALAPPDATA 'PowerShellCustomization'
New-Item -ItemType Directory -Path $root -Force | Out-Null

function Get-GitHubToken {
    if ($env:GITHUB_TOKEN) { return [string]$env:GITHUB_TOKEN }

    $gh = Get-Command gh.exe, gh -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($gh) {
        $candidate = @(& $gh.Source auth token --hostname github.com 2>$null)
        if ($LASTEXITCODE -eq 0 -and $candidate.Count -gt 0 -and $candidate[0]) {
            return [string]$candidate[0]
        }
    }

    $git = Get-Command git.exe, git -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($git) {
        $inputLines = "protocol=https" + [Environment]::NewLine +
            "host=github.com" + [Environment]::NewLine + [Environment]::NewLine
        $lines = @($inputLines | & $git.Source credential fill 2>$null)
        if ($LASTEXITCODE -eq 0) {
            foreach ($line in $lines) {
                if ($line -cmatch '^password=(.+)$') { return $Matches[1] }
            }
        }
    }
    throw 'Credenziali GitHub non disponibili. Autentica gh o Git Credential Manager; non scaricare ZIP manualmente.'
}

# Public GitHub Releases can be fetched anonymously. Do not require
# GitHub CLI or tokens on a fresh Windows installation.
$headers = @{
    Accept = 'application/vnd.github+json'
    'User-Agent' = 'AI-Terminals-Installer'
    'X-GitHub-Api-Version' = '2022-11-28'
}
function Use-GitHubAuthentication {
    $token = Get-GitHubToken
    $headers.Authorization = "Bearer $token"
    $token = $null
}
if ($RunId -gt 0) { Use-GitHubAuthentication }

function Select-Build {
    if ($RunId -gt 0) {
        $selected = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/actions/runs/$RunId" -Headers $headers -TimeoutSec 45
        return $selected
    }
    foreach ($branch in @('main','feature/split-claude-codex')) {
        $url = "https://api.github.com/repos/$repo/actions/runs?branch=$([uri]::EscapeDataString($branch))&status=success&per_page=20"
        $runs = Invoke-RestMethod -Uri $url -Headers $headers -TimeoutSec 45
        $candidate = @($runs.workflow_runs | Where-Object {
            $_.status -eq 'completed' -and $_.conclusion -eq 'success' -and
            $_.path -match 'build-windows\.yml$'
        } | Sort-Object created_at -Descending)
        if ($candidate.Count -gt 0) { return $candidate[0] }
    }
    throw "Nessuna build Windows riuscita disponibile su GitHub."
}

$release = $null
# Per l'installazione stabile preferiamo la GitHub Release permanente.
# Un RunId esplicito permette ancora di testare un artifact GitHub Actions.
if ($RunId -eq 0) {
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers $headers -TimeoutSec 45 -ErrorAction Stop
    }
    catch {
        if ($null -eq $_.Exception.Response -or
            [int]$_.Exception.Response.StatusCode -ne 404) { throw }
    }
}

if ($null -eq $release) {
    if (-not $headers.ContainsKey('Authorization')) { Use-GitHubAuthentication }
    $run = Select-Build
    if ($run.status -ne 'completed' -or $run.conclusion -ne 'success') {
        throw "Il workflow $($run.id) non e riuscito: installazione annullata."
    }
    $index = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/actions/runs/$($run.id)/artifacts?per_page=100" -Headers $headers -TimeoutSec 45
}
else {
    $checksumAsset = @($release.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' } | Select-Object -First 1)
    if ($checksumAsset.Count -ne 1) {
        throw "GitHub Release $($release.tag_name) priva di SHA256SUMS.txt"
    }
    # I download delle release pubbliche non ricevono il token GitHub.
    $checksumText = (Invoke-WebRequest -Uri $checksumAsset[0].browser_download_url -TimeoutSec 45 -ErrorAction Stop).Content
}
$apps = if ($App -eq 'Both') { @('Claude','Codex') } else { @($App) }

foreach ($agent in $apps) {
    $exeName = "${agent}Terminal.exe"
    $artifactName = "$($agent)Terminal-win-x64"
    if ($null -ne $release) {
        $artifact = @($release.assets | Where-Object {
            $_.name -eq "$artifactName.zip" -and $_.state -eq 'uploaded'
        } | Select-Object -First 1)
        if ($artifact.Count -ne 1) {
            throw "Pacchetto non disponibile nella release $($release.tag_name): $artifactName.zip"
        }
    }
    else {
        $artifact = @($index.artifacts | Where-Object {
            $_.name -eq $artifactName -and -not $_.expired
        } | Select-Object -First 1)
        if ($artifact.Count -ne 1) {
            throw "Artifact non disponibile: $artifactName, run $($run.id)"
        }
    }
    if ([long]$artifact[0].size_in_bytes -le 0 -or [long]$artifact[0].size_in_bytes -gt 512MB) {
        throw "Dimensione anomala per $artifactName. Operazione annullata."
    }
    if (Get-Process -Name "${agent}Terminal" -ErrorAction SilentlyContinue) {
        throw "Chiudi $exeName prima di aggiornarlo: i processi attivi non vengono terminati automaticamente."
    }

    $dest = Join-Path $root ("${agent}Terminal")
    if ((Test-Path -LiteralPath $dest) -and
        -not (Test-Path -LiteralPath (Join-Path $dest $exeName) -PathType Leaf)) {
        throw "La directory esiste ma non sembra un'installazione gestita: $dest. Non la modifico."
    }
    $uuid = [guid]::NewGuid().ToString('N')
    $stageName = ".${agent}Terminal-stage-$uuid"
    $backupName = ".${agent}Terminal-backup-$uuid"
    $stage = Join-Path $root $stageName
    $backup = Join-Path $root $backupName
    $archive = Join-Path $root (".${agent}Terminal-$uuid.zip")
    $backedUp = $false
    $installed = $false

    try {
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        if ($null -ne $release) {
            Write-Host "GitHub Release $($release.tag_name): $artifactName.zip" -ForegroundColor Cyan
            Invoke-WebRequest -Uri $artifact[0].browser_download_url -OutFile $archive -MaximumRedirection 5 -TimeoutSec 240 -ErrorAction Stop | Out-Null

            $checksumPattern = '^(?<hash>[0-9a-fA-F]{64})\s+' + [regex]::Escape("$artifactName.zip") + '$'
            $lines = @($checksumText -split '\r?\n' | Where-Object { $_ -match $checksumPattern } | Select-Object -First 1)
            if ($lines.Count -ne 1) {
                throw "Checksum mancante nella release per $artifactName.zip."
            }
            $expectedHash = ($lines[0] -split '\s+')[0].ToLowerInvariant()
            $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actualHash -ne $expectedHash) {
                throw "Checksum SHA256 non corrispondente per $artifactName.zip."
            }
        }
        else {
            $uri = "https://api.github.com/repos/$repo/actions/artifacts/$($artifact[0].id)/zip"
            Write-Host "GitHub Actions: $artifactName (run $($run.id))" -ForegroundColor Cyan
            Invoke-WebRequest -Uri $uri -Headers $headers -OutFile $archive -MaximumRedirection 5 -TimeoutSec 240 -ErrorAction Stop | Out-Null
        }
        Expand-Archive -LiteralPath $archive -DestinationPath $stage -Force
        $required = @(
            $exeName,
            'wwwroot\index.html',
            "scripts\Start-${agent}Session.ps1",
            "scripts\Notify-${agent}Turn.ps1",
            "assets\$($agent.ToLowerInvariant())-terminal.png"
        )
        foreach ($file in $required) {
            if (-not (Test-Path -LiteralPath (Join-Path $stage $file) -PathType Leaf)) {
                throw "Pacchetto incompleto per $agent (manca $file). Versione precedente conservata."
            }
        }
        if (Test-Path -LiteralPath $dest) {
            Rename-Item -LiteralPath $dest -NewName $backupName -ErrorAction Stop
            $backedUp = $true
        }
        Rename-Item -LiteralPath $stage -NewName ("${agent}Terminal") -ErrorAction Stop
        $installed = $true
        Write-Host "[OK] $exeName aggiornato in $dest" -ForegroundColor Green
    }
    catch {
        if ($backedUp -and -not (Test-Path -LiteralPath $dest)) {
            Rename-Item -LiteralPath $backup -NewName ("${agent}Terminal") -ErrorAction Stop
            $backedUp = $false
        }
        throw
    }
    finally {
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
        if ($installed -and $backedUp -and (Test-Path -LiteralPath $backup)) {
            try { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction Stop }
            catch { Write-Warning "Vecchio backup da rimuovere manualmente: $backup" }
        }
    }
}
Write-Host '[OK] Nessun alias aggiunto. Puoi fissare gli EXE alla barra delle applicazioni.' -ForegroundColor Green

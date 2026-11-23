$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$source = Join-Path $PSScriptRoot '..\scripts\Install-AITerminals.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path -LiteralPath $source).Path, [ref]$tokens, [ref]$errors
)
if ($errors.Count -gt 0) { throw "Invalid installer syntax: $($errors | Out-String)" }
$helper = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Get-InstallerArtifactSize'
}, $false)
if ($null -eq $helper) { throw 'Installer size validation helper missing' }

# Evaluate exactly the helper that the installer calls; do not run any
# network requests or install applications during this isolated test.
. ([scriptblock]::Create($helper.Extent.Text))

$release = [pscustomobject]@{
    name = 'ClaudeTerminal-win-x64.zip'
    size = 83475528L
    state = 'uploaded'
}
$actions = [pscustomobject]@{
    name = 'ClaudeTerminal-win-x64'
    size_in_bytes = 83475528L
    expired = $false
}
if ((Get-InstallerArtifactSize -Artifact $release -FromRelease $true) -ne 83475528L) {
    throw 'GitHub Releases size field was not read correctly'
}
if ((Get-InstallerArtifactSize -Artifact $actions -FromRelease $false) -ne 83475528L) {
    throw 'GitHub Actions size_in_bytes field was not read correctly'
}

foreach ($test in @(
    @{ artifact = $release; release = $false; case = 'wrong Release API field' },
    @{ artifact = $actions; release = $true; case = 'wrong Actions API field' },
    @{ artifact = [pscustomobject]@{ size = 0 }; release = $true; case = 'empty release asset' },
    @{ artifact = [pscustomobject]@{ size_in_bytes = 0 }; release = $false; case = 'empty artifact' },
    @{ artifact = [pscustomobject]@{ size = 536870913L }; release = $true; case = 'oversized release asset' },
    @{ artifact = [pscustomobject]@{ size_in_bytes = 536870913L }; release = $false; case = 'oversized artifact' }
)) {
    $rejected = $false
    try {
        [void](Get-InstallerArtifactSize -Artifact $test.artifact -FromRelease $test.release)
    }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Installer did not reject $($test.case)" }
}

Write-Host '[PASS] GitHub Release and Actions artifact size fields and limits' -ForegroundColor Green

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$source = Join-Path $PSScriptRoot '..\scripts\Install-AITerminals.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path -LiteralPath $source).Path, [ref]$tokens, [ref]$errors
)
if ($errors.Count -gt 0) { throw "Invalid installer syntax: $($errors | Out-String)" }

# Import only two helper functions, never execute the installer during
# parser tests. The final integration step exercises the actual remote
# checksum asset and verifies both public release archive entries.
foreach ($name in @('Read-ReleaseChecksumManifest', 'Get-ReleaseExpectedHash')) {
    $functionAst = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq $name
    }, $false)
    if ($null -eq $functionAst) { throw "Function missing: $name" }
    . ([scriptblock]::Create($functionAst.Extent.Text))
}

$claudeName = 'ClaudeTerminal-win-x64.zip'
$codexName = 'CodexTerminal-win-x64.zip'
$a = 'a' * 64
$b = 'b' * 64
$fixture = "$a  $claudeName`n$b  $codexName`r`n"
if ((Get-ReleaseExpectedHash -Manifest $fixture -AssetName $claudeName) -ne $a) {
    throw 'Valid LF checksum not parsed correctly'
}
if ((Get-ReleaseExpectedHash -Manifest $fixture -AssetName $codexName) -ne $b) {
    throw 'Valid CRLF checksum not parsed correctly'
}
foreach ($broken in @(
    "missing",
    "$a  unrelated.zip",
    "$a  $claudeName`n$b  $claudeName",
    "$a  $claudeName.exe"
)) {
    $rejected = $false
    try {
        [void](Get-ReleaseExpectedHash -Manifest $broken -AssetName $claudeName)
    }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid or duplicated checksum accepted' }
}
Write-Host '[PASS] Checksum parser: LF, CRLF, missing and duplicate entries'

# Test the real public asset: GitHub may serve SHA256SUMS.txt with a
# non-text MIME type, so .Content is not a safe way to read its lines.
$url = 'https://github.com/simracemanagerinfo/ai-terminals/releases/download/v1.1.1/SHA256SUMS.txt'
$manifest = Read-ReleaseChecksumManifest -Url $url
foreach ($name in @($claudeName, $codexName, 'AI-Terminals-Setup.exe')) {
    $hash = Get-ReleaseExpectedHash -Manifest $manifest -AssetName $name
    if ($hash -notmatch '^[0-9a-f]{64}$') {
        throw "Published checksum invalid: $name"
    }
    Write-Host "[PASS] Published release checksum found: $name"
}

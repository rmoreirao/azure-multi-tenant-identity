[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = '24.22.1'
$archive = "node-v$version-win-x64.zip"
$tools = Join-Path $root '.tools'
$nodeHome = Join-Path $tools "node-v$version-win-x64"

if (-not (Test-Path (Join-Path $nodeHome 'node.exe'))) {
    New-Item -ItemType Directory -Path $tools -Force | Out-Null
    $release = "https://nodejs.org/dist/v$version"
    $checksums = (Invoke-WebRequest "$release/SHASUMS256.txt").Content
    if ($checksums -is [byte[]]) {
        $checksums = [Text.Encoding]::UTF8.GetString($checksums)
    }
    $entry = @($checksums -split "`n" | Where-Object {
        $_.Trim() -match "^[a-fA-F0-9]{64}\s+$([regex]::Escape($archive))$"
    })
    if ($entry.Count -ne 1) {
        throw "No unique official checksum found for $archive."
    }
    $expected = ($entry[0].Trim() -split '\s+')[0]
    $download = Join-Path $tools $archive
    Invoke-WebRequest "$release/$archive" -OutFile $download
    $actual = (Get-FileHash $download -Algorithm SHA256).Hash
    if ($actual -ne $expected) {
        throw "Node archive checksum mismatch. Do not execute $download."
    }
    Expand-Archive -LiteralPath $download -DestinationPath $tools
    Remove-Item -LiteralPath $download
}

$actualVersion = & (Join-Path $nodeHome 'node.exe') --version
if ($LASTEXITCODE -ne 0 -or $actualVersion -ne "v$version") {
    throw "Expected Node v$version in $nodeHome."
}
Write-Host "Verified project-scoped Node $actualVersion. Global Node configuration was not changed."

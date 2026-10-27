Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepositoryRoot = Split-Path $PSScriptRoot -Parent

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string] $Command,
        [Parameter(ValueFromRemainingArguments)][string[]] $Arguments
    )
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command exited with code $LASTEXITCODE."
    }
}

function Use-ProjectNode {
    $nodeHome = Join-Path $script:RepositoryRoot '.tools\node-v24.22.1-win-x64'
    if (-not (Test-Path (Join-Path $nodeHome 'node.exe'))) {
        throw 'Run scripts\setup-tools.ps1 first. Global Node settings will not be changed.'
    }
    $env:PATH = "$nodeHome;$env:PATH"
    $version = & (Join-Path $nodeHome 'node.exe') --version
    if ($version -ne 'v24.22.1') { throw "Unexpected project Node version: $version." }
}

function Import-LocalConfiguration {
    $path = Join-Path $script:RepositoryRoot '.env'
    if (-not (Test-Path $path)) { return }
    foreach ($line in Get-Content -LiteralPath $path) {
        if ($line -match '^\s*(#.*)?$') { continue }
        if ($line -notmatch '^(ExternalId|Database|Azure|Application)__([A-Za-z][A-Za-z0-9_]*?)=(.*)$') {
            throw 'Unsupported .env entry. Only explicitly allowed server configuration keys are accepted.'
        }
        $key = "$($Matches[1])__$($Matches[2])"
        $value = $Matches[3].Trim()
        if ($value.StartsWith('"') -and $value.EndsWith('"')) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        if ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($key, 'Process'))) {
            [Environment]::SetEnvironmentVariable($key, $value, 'Process')
        }
    }
}

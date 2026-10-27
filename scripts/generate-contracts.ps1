[CmdletBinding()]
param([switch] $Check)

. (Join-Path $PSScriptRoot 'Common.ps1')
Use-ProjectNode
$output = Join-Path $script:RepositoryRoot 'artifacts\contract-generation'
$previousExport = $env:KANBAN_EXPORT_CONTRACTS
Push-Location $script:RepositoryRoot
try {
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    foreach ($name in @('openapi.json', 'mcp-tools.json', 'api.d.ts')) {
        $path = Join-Path $output $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    $env:KANBAN_EXPORT_CONTRACTS = '1'
    $testArguments = @(
        'test', 'tests\Kanban.SafetyTests', '--configuration', 'Release', '--no-restore',
        '--filter', 'FullyQualifiedName~IdentityBoundaryTests',
        '--', 'RunConfiguration.TreatNoTestsAsError=true'
    )
    Invoke-Checked -Command dotnet -Arguments $testArguments
    foreach ($name in @('openapi.json', 'mcp-tools.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $name))) {
            throw "The isolated contract export did not produce '$name'."
        }
    }
    $generate = @(
        'exec', '--offline', '--', 'openapi-typescript',
        (Join-Path $output 'openapi.json'), '-o', (Join-Path $output 'api.d.ts')
    )
    Invoke-Checked -Command npm -Arguments $generate
    $files = @(
        @{ source = 'openapi.json'; target = 'docs\contracts\openapi.json' }
        @{ source = 'mcp-tools.json'; target = 'docs\contracts\mcp-tools.json' }
        @{ source = 'api.d.ts'; target = 'client\src\generated\api.d.ts' }
    )
    foreach ($file in $files) {
        $source = Join-Path $output $file.source
        $target = Join-Path $script:RepositoryRoot $file.target
        if ($Check) {
            if (-not (Test-Path -LiteralPath $target) -or
                [IO.File]::ReadAllText($source).Replace("`r`n", "`n") -cne [IO.File]::ReadAllText($target).Replace("`r`n", "`n")) {
                throw "Generated contract '$($file.target)' is missing or stale. Run scripts\generate-contracts.ps1."
            }
        } else {
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $target
        }
    }
    Write-Host 'OpenAPI, generated TypeScript, and official-SDK MCP schemas verified using the isolated host; no Azure database writes.'
} finally {
    $env:KANBAN_EXPORT_CONTRACTS = $previousExport
    Pop-Location
}

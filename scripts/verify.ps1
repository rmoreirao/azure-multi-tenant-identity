[CmdletBinding()]
param(
    [switch] $CandidateOnly,
    [switch] $Sql,
    [string] $SqlTarget
)

. (Join-Path $PSScriptRoot 'Common.ps1')
Use-ProjectNode
if (-not $CandidateOnly -and (-not $Sql -or $SqlTarget -ne 'tcp:sql-kanban-poc-2rza5wd265s3g.database.windows.net,1433')) {
    throw 'Full local safety verification requires -Sql and -SqlTarget with the exact approved server. -CandidateOnly is a limited static/identity check.'
}
$previousSql = $env:KANBAN_SQL_TESTS
$previousTarget = $env:KANBAN_SQL_TARGET

Push-Location $script:RepositoryRoot
try {
    Invoke-Checked dotnet build Kanban.slnx --configuration Release --no-restore
    Invoke-Checked npm run lint
    Invoke-Checked npm run typecheck
    Invoke-Checked npm run build
    $testArguments = @('test', 'tests\Kanban.SafetyTests', '--configuration', 'Release', '--no-build', '--no-restore')
    if ($CandidateOnly) {
        $testArguments += @('--filter', 'FullyQualifiedName~IdentityBoundaryTests|FullyQualifiedName~ContractSafetyTests')
        Invoke-Checked -Command dotnet -Arguments $testArguments
        & (Join-Path $PSScriptRoot 'generate-contracts.ps1') -Check
        Write-Warning 'Candidate static and local identity-boundary checks only. The other five safety groups, deployed acceptance, and real MCP client compatibility are not verified.'
    }
    else {
        Import-LocalConfiguration
        $env:KANBAN_SQL_TESTS = '1'
        $env:KANBAN_SQL_TARGET = $SqlTarget
        $discovered = & dotnet test 'tests\Kanban.SafetyTests' --configuration Release --no-build --no-restore --list-tests
        if ($LASTEXITCODE -ne 0) { throw 'Cannot discover the required six safety groups.' }
        $requiredGroups = @(
            'IsolationSafetyTests', 'RoleSafetyTests', 'RevocationSafetyTests',
            'IdentityBoundaryTests', 'MutationSafetyTests', 'ConcurrencySafetyTests'
        )
        $missing = @($requiredGroups | Where-Object { ($discovered -join "`n") -notmatch "\.$_\." })
        if ($missing.Count -gt 0) {
            throw "Full PoC safety gate is incomplete. Missing discovered groups: $($missing -join ', '). Use -CandidateOnly only for the explicitly limited identity candidate."
        }
        $results = Join-Path $script:RepositoryRoot 'artifacts\test-results'
        $testArguments += @('--logger', 'trx;LogFileName=product-safety.trx', '--results-directory', $results,
            '--', 'RunConfiguration.TreatNoTestsAsError=true')
        Invoke-Checked -Command dotnet -Arguments $testArguments
        [xml] $report = Get-Content -LiteralPath (Join-Path $results 'product-safety.trx') -Raw
        $counters = $report.TestRun.ResultSummary.Counters
        if ([int]$counters.failed -ne 0 -or [int]$counters.notExecuted -ne 0 -or
            [int]$counters.total -ne [int]$counters.passed) {
            throw 'Full local safety verification requires every discovered test to pass, with no skips.'
        }
        & (Join-Path $PSScriptRoot 'generate-contracts.ps1') -Check
        @{
            verifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
            localSafetyChecksPassed = $true
            passed = [int]$counters.passed
            skipped = [int]$counters.notExecuted
            sqlTarget = $SqlTarget
            sqlCredential = 'explicit developer/operator; runtime RLS permissions also checked using EXECUTE AS'
            hostedRuntimeConnectionVerified = $false
            interactiveBrowserAccepted = $false
            nativeClientsAccepted = $false
            webAssemblySha256 = (Get-FileHash (Join-Path $script:RepositoryRoot 'src\Kanban.Web\bin\Release\net10.0\Kanban.Web.dll')).Hash
            testResultsSha256 = (Get-FileHash (Join-Path $results 'product-safety.trx')).Hash
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $script:RepositoryRoot 'artifacts\local-safety.json') -Encoding utf8
    }
}
finally {
    $env:KANBAN_SQL_TESTS = $previousSql
    $env:KANBAN_SQL_TARGET = $previousTarget
    Pop-Location
}

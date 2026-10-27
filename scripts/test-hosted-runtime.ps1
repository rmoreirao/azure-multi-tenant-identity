[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ArtifactPath,
    [Parameter(Mandatory)][string] $SqlTarget,
    [switch] $Apply
)

. (Join-Path $PSScriptRoot 'Common.ps1')
if ($SqlTarget -ne 'tcp:sql-kanban-poc-2rza5wd265s3g.database.windows.net,1433') {
    throw 'The private runtime probe requires the exact approved SQL target.'
}
$archive = (Resolve-Path -LiteralPath $ArtifactPath).Path
$releaseDirectory = Split-Path $archive -Parent
$approvedRoot = [IO.Path]::GetFullPath((Join-Path $script:RepositoryRoot 'artifacts\releases')).TrimEnd('\') + '\'
if (-not $archive.StartsWith($approvedRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetExtension($archive) -ne '.zip') {
    throw 'Use an existing immutable product release under artifacts\releases.'
}
$release = Get-Content -LiteralPath "$archive.json" -Raw | ConvertFrom-Json
if ($release.stage -ne 'kanban-product-candidate' -or
    $release.releaseId -notmatch '^kanban-product-[A-Za-z0-9-]{1,100}$' -or
    (Get-FileHash -LiteralPath $archive).Hash -ne $release.artifactSha256) {
    throw 'The product artifact identity or checksum is invalid.'
}
if (-not $Apply) {
    Write-Host "Private managed-identity component probe for $($release.releaseId)."
    Write-Host 'With -Apply, temporarily replace and then restore the existing startup command; do not enable self-service or change the deployed ZIP.'
    return
}

$subscription = '67bfd3c6-6d7e-4a79-9a85-19f4a6928d7c'
$group = 'rg-kanban-poc-swc'
$name = 'app-kanban-poc-2rza5wd265s3g'
$base = "https://$name.azurewebsites.net"
$scm = "https://$name.scm.azurewebsites.net"
$resource = "/subscriptions/$subscription/resourceGroups/$group/providers/Microsoft.Web/sites/$name"
$accountText = & az account show --subscription $subscription --query '{id:id,tenantId:tenantId}' --output json --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify the approved Azure account.' }
$account = $accountText | ConvertFrom-Json
if ($account.id -ne $subscription -or $account.tenantId -ne 'f1640304-f816-412c-9e6f-4b6b818cf492') {
    throw 'The probe cannot target another subscription or hosting directory.'
}
$configurationText = & az webapp config show --subscription $subscription --resource-group $group --name $name --query '{startup:appCommandLine,stack:linuxFxVersion}' --output json --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the existing application startup command.' }
$configuration = $configurationText | ConvertFrom-Json
if ($configuration.startup -ne 'dotnet Kanban.Web.dll' -or $configuration.stack -ne 'DOTNETCORE|10.0') {
    throw 'The probe requires the reviewed, unmodified .NET application startup command.'
}
foreach ($protocol in @('scm', 'ftp')) {
    $policy = & az rest --method get --url "https://management.azure.com$resource/basicPublishingCredentialsPolicies/$protocol`?api-version=2024-11-01" --query properties.allow --output tsv --only-show-errors
    if ($LASTEXITCODE -ne 0 -or $policy -ne 'false') { throw 'Basic publishing credentials must remain disabled.' }
}
$flagsText = & az webapp config appsettings list --subscription $subscription --resource-group $group --name $name --query "[?name=='Application__SelfServiceEnabled' || name=='Application:SelfServiceEnabled'].value" --output json --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify the existing self-service flag.' }
if (@($flagsText | ConvertFrom-Json | Where-Object { $_ -ne 'false' }).Count -ne 0) {
    throw 'This component-only probe requires self-service to remain disabled.'
}
if ((Invoke-RestMethod "$base/release.json" -TimeoutSec 30).releaseId -ne $release.releaseId) {
    throw 'The active release is not the selected immutable candidate.'
}

$run = [Guid]::NewGuid().ToString('N')
$directory = Join-Path $releaseDirectory "runtime-probe-$run"
$publish = Join-Path $directory 'runner'
New-Item -ItemType Directory -Path $directory | Out-Null
Use-ProjectNode
Invoke-Checked dotnet publish (Join-Path $script:RepositoryRoot 'tests\Kanban.SafetyTests') --configuration Release --no-restore --output $publish --verbosity quiet
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($file in @('Kanban.Core.dll', 'Kanban.Infrastructure.dll', 'Kanban.Web.dll')) {
        $entries = @($zip.Entries | Where-Object { $_.FullName -eq $file })
        $expected = @($release.files | Where-Object { $_.path -eq $file })
        if ($entries.Count -ne 1 -or $expected.Count -ne 1) {
            throw "The selected immutable release is missing a unique $file."
        }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], (Join-Path $publish $file), $true)
        if ((Get-FileHash -LiteralPath (Join-Path $publish $file)).Hash -ne $expected[0].sha256) {
            throw "The archived $file does not match the release manifest."
        }
    }
}
finally { $zip.Dispose() }
@{
    parallelizeTestCollections = $false
    maxParallelThreads = 1
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'xunit.runner.json') -Encoding utf8
Import-LocalConfiguration
$preflightSql = $env:KANBAN_SQL_TESTS
$preflightTarget = $env:KANBAN_SQL_TARGET
try {
    $env:KANBAN_SQL_TESTS = '1'
    $env:KANBAN_SQL_TARGET = $SqlTarget
    Invoke-Checked -Command dotnet -Arguments @(
        'vstest', (Join-Path $publish 'Kanban.SafetyTests.dll'),
        "--ResultsDirectory:$directory", '--logger:trx;LogFileName=runtime-assembly-safety.trx'
    )
    [xml] $preflight = Get-Content -LiteralPath (Join-Path $directory 'runtime-assembly-safety.trx') -Raw
    $counts = $preflight.TestRun.ResultSummary.Counters
    if ([int]$counts.failed -ne 0 -or [int]$counts.notExecuted -ne 0 -or
        [int]$counts.total -lt 30 -or [int]$counts.total -ne [int]$counts.passed) {
        throw 'Every safety check must pass against the exact archived runtime before hosting changes.'
    }
    foreach ($required in @(
        'IsolationSafetyTests', 'RoleSafetyTests', 'RevocationSafetyTests', 'IdentityBoundaryTests',
        'MutationSafetyTests', 'ConcurrencySafetyTests', 'ProductBoundaryTests', 'ContractSafetyTests'
    )) {
        if ("Kanban.SafetyTests.$required" -notin @($preflight.TestRun.TestDefinitions.UnitTest.TestMethod.className)) {
            throw "The archived runtime preflight did not exercise $required."
        }
    }
}
finally {
    $env:KANBAN_SQL_TESTS = $preflightSql
    $env:KANBAN_SQL_TARGET = $preflightTarget
}
$startup = @'
#!/bin/sh
set -u
cd '/home/data/acceptance/__RUN__' || exit 1
if (set -C; : > started) 2>/dev/null; then
    dotnet Kanban.SafetyTests.dll --hosted-runtime-probe '__RUN__' '__RELEASE__' > probe.log 2>&1
    echo "$?" > exit-code
fi
cd /home/site/wwwroot || exit 1
exec dotnet Kanban.Web.dll
'@
$startup = $startup.Replace('__RUN__', $run).Replace('__RELEASE__', $release.releaseId).Replace("`r`n", "`n")
[IO.File]::WriteAllText((Join-Path $publish 'start.sh'), $startup + "`n", [Text.UTF8Encoding]::new($false))
$probeZip = Join-Path $directory 'runner.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($publish, $probeZip)

$access = & az account get-access-token --subscription $subscription --resource https://management.azure.com/ --query accessToken --output tsv --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Cannot authorize the private artifact upload.' }
$headers = @{ Authorization = "Bearer $access" }
$remote = "$scm/api/vfs/data/acceptance/$run"
$startupAttempted = $false
$restored = $false
$cleaned = $false
$uploaded = $false
$hasFixture = $false
$remoteRemoved = $false
$previousSql = $env:KANBAN_SQL_TESTS
$previousTarget = $env:KANBAN_SQL_TARGET
function Receive-ProbeFile([string] $File) {
    try {
        Invoke-WebRequest "$remote/$File" -Headers $headers -OutFile (Join-Path $directory $File) -TimeoutSec 20
        return $true
    }
    catch [Microsoft.PowerShell.Commands.HttpResponseException] {
        if ([int]$_.Exception.Response.StatusCode -eq 404) { return $false }
        throw
    }
}
function Wait-OriginalApplication {
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(5)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $health = Invoke-WebRequest "$base/health" -TimeoutSec 20
            if ($health.StatusCode -eq 200 -and
                (Invoke-RestMethod "$base/release.json" -TimeoutSec 20).releaseId -eq $release.releaseId) { return }
        }
        catch [Microsoft.PowerShell.Commands.HttpResponseException] {
            if ([int]$_.Exception.Response.StatusCode -notin @(502, 503)) { throw }
        }
        catch [System.Threading.Tasks.TaskCanceledException] {
            Write-Warning 'Application warm-up request timed out; retrying within the bounded readiness window.'
        }
        Start-Sleep -Seconds 5
    }
    throw 'The original application did not become ready after startup restoration.'
}
try {
    Invoke-WebRequest "$scm/api/zip/data/acceptance/$run/" -Method Put -Headers $headers -InFile $probeZip -ContentType 'application/zip' -TimeoutSec 180 | Out-Null
    $uploaded = $true
    $startupAttempted = $true
    Invoke-Checked az webapp config set --subscription $subscription --resource-group $group --name $name --startup-file "/bin/sh /home/data/acceptance/$run/start.sh" --output none --only-show-errors
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(5)
    $result = $null
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Receive-ProbeFile 'probe-results.json') {
            $result = Get-Content -LiteralPath (Join-Path $directory 'probe-results.json') -Raw | ConvertFrom-Json
            if ($result.status -in @('passed', 'failed')) { break }
        }
        Start-Sleep -Seconds 5
    }
    if ($null -eq $result -or $result.status -ne 'passed') {
        throw 'The private runtime component probe did not pass. Inspect its preserved report and log; this is not authenticated acceptance.'
    }
}
finally {
    try {
        if ($startupAttempted) {
            Invoke-Checked az webapp config set --subscription $subscription --resource-group $group --name $name --startup-file $configuration.startup --output none --only-show-errors
            $actual = & az webapp config show --subscription $subscription --resource-group $group --name $name --query appCommandLine --output tsv --only-show-errors
            if ($LASTEXITCODE -ne 0 -or $actual -ne $configuration.startup) {
                throw 'Startup restoration is unverified. Keep the private runner directory for recovery.'
            }
            $restored = $true
            Wait-OriginalApplication
        }
    }
    finally {
        try {
            if ($uploaded) {
                $hasFixture = Receive-ProbeFile 'fixture.json'
                $null = Receive-ProbeFile 'probe-results.json'
                $null = Receive-ProbeFile 'probe.log'
                $null = Receive-ProbeFile 'exit-code'
                if ($hasFixture) {
                    if (-not (Test-Path -LiteralPath (Join-Path $directory 'exit-code'))) {
                        throw 'Probe termination is unverified. Preserve its fixture manifest; do not race cleanup against a running probe.'
                    }
                    Import-LocalConfiguration
                    $env:KANBAN_SQL_TESTS = '1'
                    $env:KANBAN_SQL_TARGET = $SqlTarget
                    Invoke-Checked dotnet (Join-Path $publish 'Kanban.SafetyTests.dll') --cleanup-hosted-runtime-probe (Join-Path $directory 'fixture.json')
                    $cleaned = $true
                    Copy-Item -LiteralPath (Join-Path $script:RepositoryRoot "artifacts\sql-safety\$run.json") -Destination (Join-Path $directory 'cleanup.json')
                }
                if ($restored -and ($cleaned -or -not $hasFixture)) {
                    Invoke-WebRequest "$remote/?recursive=true" -Method Delete -Headers ($headers + @{ 'If-Match' = '*' }) -TimeoutSec 60 | Out-Null
                    $remoteRemoved = $true
                }
            }
        }
        finally {
            @{
                runId = $run; releaseId = $release.releaseId
                artifactSha256 = $release.artifactSha256
                startupRestored = $restored; fixturesCleaned = $cleaned
                fixtureManifestPresent = $hasFixture
                privateCloudRunnerRemoved = $remoteRemoved
                selfServiceEnabled = $false; productAccepted = $false
                evidenceKind = 'hosted-managed-identity-component'
            } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'execution.json') -Encoding utf8
            $env:KANBAN_SQL_TESTS = $previousSql
            $env:KANBAN_SQL_TARGET = $previousTarget
            $headers = $null
            $access = $null
        }
    }
}
Write-Host "Private hosted runtime evidence: $directory"
Write-Host 'The deployed ZIP and self-service flag were not changed. Real browser and native-client acceptance remains pending.'

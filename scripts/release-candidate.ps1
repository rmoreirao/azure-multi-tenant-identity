[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(ParameterSetName = 'Build')]
    [switch] $Product,
    [Parameter(ParameterSetName = 'Build')]
    [string] $SqlTarget,
    [Parameter(Mandatory, ParameterSetName = 'Deploy')]
    [string] $ArtifactPath,
    [Parameter(ParameterSetName = 'Deploy')]
    [switch] $Apply
)

. (Join-Path $PSScriptRoot 'Common.ps1')
$releaseDirectory = Join-Path $script:RepositoryRoot 'artifacts\releases'

if ($PSCmdlet.ParameterSetName -eq 'Build') {
    if ($Product) {
        & (Join-Path $PSScriptRoot 'verify.ps1') -Sql -SqlTarget $SqlTarget
    } else {
        & (Join-Path $PSScriptRoot 'verify.ps1') -CandidateOnly
    }
    $testedRuntime = @{}
    foreach ($file in @('Kanban.Core.dll', 'Kanban.Infrastructure.dll', 'Kanban.Web.dll')) {
        $testedRuntime[$file] = (Get-FileHash -LiteralPath (
            Join-Path $script:RepositoryRoot "tests\Kanban.SafetyTests\bin\Release\net10.0\$file")).Hash
    }
    $stage = if ($Product) { 'kanban-product-candidate' } else { 'identity-feasibility' }
    $prefix = if ($Product) { 'kanban-product' } else { 'identity-candidate' }
    $releaseId = "$prefix-$([DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ'))-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    $directory = Join-Path $releaseDirectory $releaseId
    $publish = Join-Path $directory 'publish'
    New-Item -ItemType Directory -Path $directory | Out-Null
    if ($Product) {
        Copy-Item -LiteralPath (Join-Path $script:RepositoryRoot 'artifacts\local-safety.json') -Destination (Join-Path $directory 'local-safety.json')
        Import-LocalConfiguration
        Invoke-Checked dotnet ef migrations script --idempotent --project (Join-Path $script:RepositoryRoot 'src\Kanban.Infrastructure') `
            --startup-project (Join-Path $script:RepositoryRoot 'src\Kanban.Web') --configuration Release --no-build `
            --output (Join-Path $directory 'reviewed-migrations.sql')
    }
    Invoke-Checked dotnet publish (Join-Path $script:RepositoryRoot 'src\Kanban.Web') --configuration Release --no-restore --output $publish
    foreach ($file in $testedRuntime.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $publish $file)).Hash -ne $testedRuntime[$file]) {
            throw "Published '$file' differs from the runtime used by the passing safety checks. Nothing was packaged."
        }
    }
    foreach ($required in @('Kanban.Web.dll', 'Kanban.Web.runtimeconfig.json', 'wwwroot\index.html')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) {
            throw "Candidate publish output is missing '$required'."
        }
    }
    if (Get-ChildItem -LiteralPath $publish -Recurse -Force -File |
        Where-Object { $_.Name -in @('.env', 'secrets.json') }) {
        throw 'A local configuration/credential file was found in publish output; nothing was packaged.'
    }
    $marker = @{
        releaseId = $releaseId
        stage = $stage
        productAccepted = $false
        mcpResourceScopesAligned = $true
        webAssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $publish 'Kanban.Web.dll') -Algorithm SHA256).Hash
    }
    $marker | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'wwwroot\release.json') -Encoding utf8
    $contracts = Join-Path $publish 'wwwroot\contracts'
    New-Item -ItemType Directory -Path $contracts | Out-Null
    foreach ($name in @('openapi.json', 'mcp-tools.json')) {
        Copy-Item -LiteralPath (Join-Path $script:RepositoryRoot "docs\contracts\$name") -Destination $contracts
    }
    $files = @(Get-ChildItem -LiteralPath $publish -Recurse -File | ForEach-Object {
        @{
            path = [IO.Path]::GetRelativePath($publish, $_.FullName)
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    $archive = Join-Path $directory "$releaseId.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    @{
        releaseId = $releaseId
        stage = $stage
        localSafetyChecksPassed = [bool]$Product
        localSafetyEvidenceSha256 = if ($Product) { (Get-FileHash (Join-Path $directory 'local-safety.json')).Hash } else { $null }
        reviewedMigrationsSha256 = if ($Product) { (Get-FileHash (Join-Path $directory 'reviewed-migrations.sql')).Hash } else { $null }
        schemaVersion = if ($Product) {
            (Get-ChildItem (Join-Path $script:RepositoryRoot 'src\Kanban.Infrastructure\Migrations\*.cs') |
                Where-Object { $_.Name -match '^\d+_[^.]+\.cs$' } | Sort-Object Name | Select-Object -Last 1).BaseName
        } else { $null }
        candidateStaticChecksPassed = $true
        identityBoundaryChecksPassed = $true
        contractChecksPassed = $true
        mcpResourceScopesAligned = $true
        testedRuntimeAssemblies = $testedRuntime
        safetyAcceptancePassed = $false
        createdAt = [DateTimeOffset]::UtcNow.ToString('O')
        sdk = (& dotnet --version)
        artifactSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        files = $files
        lockfiles = @(Get-Item -LiteralPath @(
            (Join-Path $script:RepositoryRoot 'package-lock.json')
            (Join-Path $script:RepositoryRoot 'src\Kanban.Web\packages.lock.json')
            (Join-Path $script:RepositoryRoot 'src\Kanban.Infrastructure\packages.lock.json')
        ) | ForEach-Object {
            @{ path = [IO.Path]::GetRelativePath($script:RepositoryRoot, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
        })
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$archive.json" -Encoding utf8
    Write-Host "Immutable $stage artifact: $archive"
    Write-Host 'Local verification does not imply hosted browser or native-client acceptance.'
    return
}

$archive = (Resolve-Path -LiteralPath $ArtifactPath).Path
$approvedRoot = [IO.Path]::GetFullPath($releaseDirectory).TrimEnd('\') + '\'
if (-not $archive.StartsWith($approvedRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetExtension($archive) -ne '.zip') {
    throw 'Deploy only an explicit ZIP built under this repository artifacts\releases directory.'
}
$manifest = Get-Content -LiteralPath "$archive.json" -Raw | ConvertFrom-Json
if ($manifest.stage -notin @('identity-feasibility', 'kanban-product-candidate') -or -not $manifest.candidateStaticChecksPassed -or
    $manifest.PSObject.Properties['identityBoundaryChecksPassed']?.Value -ne $true -or
    $manifest.safetyAcceptancePassed -or
    (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $manifest.artifactSha256) {
    throw 'Candidate artifact stage, check record, or SHA-256 does not match. Nothing was deployed.'
}
if ($manifest.stage -eq 'kanban-product-candidate' -and (
    -not $manifest.localSafetyChecksPassed -or
    (Get-FileHash (Join-Path (Split-Path $archive -Parent) 'local-safety.json')).Hash -ne $manifest.localSafetyEvidenceSha256 -or
    (Get-FileHash (Join-Path (Split-Path $archive -Parent) 'reviewed-migrations.sql')).Hash -ne $manifest.reviewedMigrationsSha256)) {
    throw 'The product candidate local safety or reviewed migration evidence is missing or changed.'
}
$subscription = '67bfd3c6-6d7e-4a79-9a85-19f4a6928d7c'
$group = 'rg-kanban-poc-swc'
$name = 'app-kanban-poc-2rza5wd265s3g'
$resource = "/subscriptions/$subscription/resourceGroups/$group/providers/Microsoft.Web/sites/$name"
Write-Host "Release candidate: $($manifest.releaseId)"
Write-Host "Destination: https://$name.azurewebsites.net"
Write-Host "Artifact SHA-256: $($manifest.artifactSha256)"
if (-not $Apply) {
    Write-Host 'Preview only. Use -Apply to deploy this exact existing artifact using Entra authentication.'
    return
}
$accountText = & az account show --subscription $subscription --query '{id:id,tenantId:tenantId}' --output json --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify the approved deployment subscription.' }
$account = $accountText | ConvertFrom-Json
if ($account.id -ne $subscription -or $account.tenantId -ne 'f1640304-f816-412c-9e6f-4b6b818cf492') {
    throw 'The selected deployment subscription/directory does not match the approved environment.'
}
foreach ($protocol in @('scm', 'ftp')) {
    $policyText = & az rest --method get --url "https://management.azure.com$resource/basicPublishingCredentialsPolicies/$protocol`?api-version=2024-11-01" --query properties.allow --output tsv --only-show-errors
    if ($LASTEXITCODE -ne 0 -or $policyText -ne 'false') {
        throw 'SCM and FTP basic publishing must remain disabled. No credentials will be enabled as a deployment fallback.'
    }
}
$settingsText = & az webapp config appsettings list --subscription $subscription --resource-group $group --name $name --query "[?name=='WEBSITE_RUN_FROM_PACKAGE' || name=='SCM_DO_BUILD_DURING_DEPLOYMENT'].{name:name,value:value}" --output json --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify atomic package deployment settings.' }
$settings = @($settingsText | ConvertFrom-Json)
if (@($settings | Where-Object { $_.name -eq 'WEBSITE_RUN_FROM_PACKAGE' -and $_.value -eq '1' }).Count -ne 1 -or
    @($settings | Where-Object { $_.name -eq 'SCM_DO_BUILD_DURING_DEPLOYMENT' -and $_.value -eq 'false' }).Count -ne 1) {
    throw 'Apply the infrastructure package settings before publishing: run the immutable ZIP read-only with remote builds disabled.'
}
$access = & az account get-access-token --subscription $subscription --resource https://management.azure.com/ --query accessToken --output tsv --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Cannot acquire Entra access for the supported ZIP deployment API.' }
try {
    $scm = "https://$name.scm.azurewebsites.net"
    $headers = @{ Authorization = "Bearer $access" }
    # OneDeploy extracted files on this Linux host despite the package setting; use ZIP deploy explicitly.
    $upload = Invoke-WebRequest -Uri "$scm/api/zipdeploy?isAsync=true&deployer=$($manifest.releaseId)" -Method Post -InFile $archive `
        -ContentType 'application/zip' -Headers $headers -MaximumRedirection 0 -TimeoutSec 300
    if ([int]$upload.StatusCode -ne 202) { throw 'ZIP deployment did not return the expected asynchronous acknowledgement.' }
    $statusUri = [Uri]::new([Uri]$scm, [string]$upload.Headers['Location'])
    $statusQuery = [Web.HttpUtility]::ParseQueryString($statusUri.Query)
    if ($statusUri.Scheme -ne 'https' -or $statusUri.Port -ne 443 -or
        $statusUri.Host -ne "$name.scm.azurewebsites.net" -or
        $statusUri.AbsolutePath -ne '/api/deployments/latest' -or $statusUri.UserInfo -or $statusUri.Fragment -or
        $statusQuery.AllKeys.Count -ne 2 -or $statusQuery['deployer'] -ne $manifest.releaseId -or
        $statusQuery['time'] -cnotmatch '^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}Z$') {
        throw 'ZIP deployment returned an unexpected status endpoint; no authenticated redirect will be followed.'
    }
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(5)
    do {
        $deployment = Invoke-RestMethod -Uri $statusUri -Headers $headers -MaximumRedirection 0 -TimeoutSec 30
        if ($deployment.status -notin @(0, 1, 2, 3, 4)) { throw 'ZIP deployment returned an unrecognized deployment state.' }
        if ($deployment.status -eq 3 -or ($deployment.status -eq 4 -and $deployment.complete)) { break }
        Start-Sleep -Seconds 5
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($deployment.status -ne 4 -or -not $deployment.complete -or $deployment.deployer -ne $manifest.releaseId) {
        throw "ZIP deployment '$($deployment.id)' did not complete successfully within the bounded wait."
    }
    $packages = "$scm/api/vfs/data/SitePackages/"
    $active = (Invoke-WebRequest -Uri "${packages}packagename.txt" -Headers $headers -MaximumRedirection 0 -TimeoutSec 30).Content.Trim()
    if ($active -notmatch '^[A-Za-z0-9_.-]+\.zip$') { throw 'The active package is not an expected ZIP filename.' }
    $client = [Net.Http.HttpClient]::new()
    $client.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $access)
    $cancel = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(2))
    try {
        $stream = $client.GetStreamAsync(($packages + [Uri]::EscapeDataString($active)), $cancel.Token).GetAwaiter().GetResult()
        try {
            $hostedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashDataAsync($stream, $cancel.Token).GetAwaiter().GetResult())
        } finally { $stream.Dispose() }
    } finally { $cancel.Dispose(); $client.Dispose() }
    if ($hostedHash -ne $manifest.artifactSha256) {
        throw 'The active hosted ZIP differs from the exact verified local artifact.'
    }
} finally { $access = $null; $headers = $null }
Invoke-Checked az webapp restart --subscription $subscription --resource-group $group --name $name --only-show-errors
@{
    releaseId = $manifest.releaseId
    artifactSha256 = $manifest.artifactSha256
    resourceId = $resource
    deploymentMode = 'local-run-from-package'
    activePackageName = $active
    exactActivePackageVerified = $true
    workerRestartRequested = $true
    deployment = @{
        id = $deployment.id
        status = $deployment.status
        complete = $deployment.complete
        endTime = $deployment.end_time
    }
    deployedAt = [DateTimeOffset]::UtcNow.ToString('O')
    liveAcceptanceVerified = $false
} | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path (Split-Path $archive -Parent) 'deployment.json') -Encoding utf8
Write-Host 'Exact hosted candidate ZIP verified and worker restart requested. Run validate-candidate.ps1 -WaitForReady for activation and hosted checks; real sign-in/client acceptance remains separate.'

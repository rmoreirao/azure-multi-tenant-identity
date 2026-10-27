[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ArtifactPath,
    [switch] $WaitForReady
)

. (Join-Path $PSScriptRoot 'Common.ps1')
$archive = (Resolve-Path -LiteralPath $ArtifactPath).Path
$approvedRoot = (Join-Path $script:RepositoryRoot 'artifacts\releases').TrimEnd('\') + '\'
if (-not $archive.StartsWith($approvedRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetExtension($archive) -ne '.zip') {
    throw 'Validate an exact candidate ZIP under this repository artifacts\releases directory.'
}
$manifest = Get-Content -LiteralPath "$archive.json" -Raw | ConvertFrom-Json
if ($manifest.stage -notin @('identity-feasibility', 'kanban-product-candidate') -or
    $manifest.PSObject.Properties['identityBoundaryChecksPassed']?.Value -ne $true -or
    (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $manifest.artifactSha256) {
    throw 'Candidate stage, local identity-check record, or artifact hash does not match.'
}
$identity = Get-Content -LiteralPath (Join-Path $script:RepositoryRoot 'artifacts\identity-applications.json') -Raw | ConvertFrom-Json
if ($identity.tenantId -ne '0f6188cc-b440-43f2-87a5-35d9811f8c7c') {
    throw 'Client registration evidence belongs to a different directory.'
}
$origin = 'https://app-kanban-poc-2rza5wd265s3g.azurewebsites.net'
$checks = [Collections.Generic.List[object]]::new()
$startupObservations = [Collections.Generic.List[object]]::new()
if ($WaitForReady) {
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(5)
    $activated = $false
    do {
        try {
            $warmup = Invoke-WebRequest -Uri "$origin/release.json" -Headers @{ 'Cache-Control' = 'no-cache' } `
                -SkipHttpErrorCheck -MaximumRedirection 0 -TimeoutSec 15
            $startupObservations.Add(@{ observedAt = [DateTimeOffset]::UtcNow.ToString('O'); status = [int]$warmup.StatusCode })
            if ([int]$warmup.StatusCode -eq 200) {
                $marker = $warmup.Content | ConvertFrom-Json
                if ($marker.releaseId -eq $manifest.releaseId) { $activated = $true; break }
                Write-Host 'The worker still serves another release; waiting for exact candidate activation.'
            }
            elseif ([int]$warmup.StatusCode -in @(404, 500, 502, 503, 504)) {
                Write-Host "Candidate activation is not ready (HTTP $([int]$warmup.StatusCode)); no acceptance recorded."
            }
            else { throw "Unexpected activation response: HTTP $([int]$warmup.StatusCode)." }
        }
        catch [Net.Http.HttpRequestException], [OperationCanceledException] {
            $startupObservations.Add(@{ observedAt = [DateTimeOffset]::UtcNow.ToString('O'); networkError = $_.Exception.GetType().Name })
            Write-Host 'Candidate activation request failed or timed out; no acceptance recorded.'
        }
        Start-Sleep -Seconds 5
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $activated) { throw 'The exact candidate did not activate within five minutes. Hosted validation failed.' }
}
function Check-Response {
    param([string] $Path, [int] $ExpectedStatus, [string] $Method = 'GET', [hashtable] $Headers = @{}, [string] $Body)
    $request = @{
        Uri = "$origin$Path"
        Method = $Method
        Headers = $Headers
        SkipHttpErrorCheck = $true
        MaximumRedirection = 0
        TimeoutSec = 90
    }
    if ($Body) { $request.Body = $Body; $request.ContentType = 'application/json' }
    $response = Invoke-WebRequest @request
    if ([int]$response.StatusCode -ne $ExpectedStatus) {
        throw "Candidate $Method $Path returned $([int]$response.StatusCode), expected $ExpectedStatus."
    }
    $checks.Add(@{ method = $Method; path = $Path; status = [int]$response.StatusCode })
    return $response
}

$releaseResponse = Check-Response '/release.json' 200
$release = $releaseResponse.Content | ConvertFrom-Json
$assembly = @($manifest.files | Where-Object { $_.path -eq 'Kanban.Web.dll' })
if ($release.releaseId -ne $manifest.releaseId -or $release.productAccepted -ne $false -or
    $release.stage -ne $manifest.stage -or $assembly.Count -ne 1 -or
    $release.webAssemblySha256 -ne $assembly[0].sha256) {
    throw 'Actual hosted release marker does not match the exact limited candidate artifact.'
}
$health = Check-Response '/health' 200
if ($health.Content -ne 'Healthy') { throw 'Actual hosted SQL/session-key health is not Healthy.' }
Check-Response '/api/session' 401 | Out-Null
Check-Response '/api/antiforgery' 401 | Out-Null
Check-Response '/api/not-implemented' 404 | Out-Null
if ($manifest.stage -eq 'kanban-product-candidate') {
    Check-Response '/api/v1/companies' 401 | Out-Null
    Check-Response '/api/v1/companies' 401 -Method POST -Body '{"name":"Anonymous creation must fail"}' | Out-Null
    Check-Response '/api/v1/invitations/accept' 401 -Method POST -Body '{"token":"invalid"}' | Out-Null
}
Check-Response '/api/session' 401 -Headers @{ Authorization = 'Bearer invalid' } | Out-Null
$contractsVerified = $false
if ($manifest.PSObject.Properties['contractChecksPassed']?.Value -eq $true) {
    if ($releaseResponse.Headers['Cache-Control'] -notcontains 'no-store') {
        throw 'The release marker must not be cached during activation checks.'
    }
    $index = Check-Response '/' 200
    if ($index.Headers['Cache-Control'] -notcontains 'no-cache') { throw 'The browser entry page must revalidate after releases.' }
    $fallback = Check-Response '/candidate-preview' 200
    if ($fallback.Content -cne $index.Content -or $fallback.Headers['Cache-Control'] -notcontains 'no-cache') {
        throw 'SPA fallback must serve the same revalidated browser entry page.'
    }
    $asset = @($manifest.files | Where-Object { $_.path -like 'wwwroot\assets\*.js' } | Select-Object -First 1)
    if ($asset.Count -ne 1) { throw 'The candidate manifest contains no content-hashed JavaScript asset.' }
    $assetResponse = Check-Response ($asset[0].path.Substring('wwwroot'.Length).Replace('\', '/')) 200
    if (($assetResponse.Headers['Cache-Control'] -join ', ') -notmatch 'max-age=31536000' -or
        ($assetResponse.Headers['Cache-Control'] -join ', ') -notmatch 'immutable') {
        throw 'Content-hashed frontend assets are not using the intended immutable cache policy.'
    }
    $publishedOpenApi = $null
    foreach ($name in @('openapi.json', 'mcp-tools.json')) {
        $file = @($manifest.files | Where-Object { $_.path -eq "wwwroot\contracts\$name" })
        if ($file.Count -ne 1) { throw "The candidate manifest is missing the exact published '$name' snapshot." }
        $snapshot = Check-Response "/contracts/$name" 200
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($snapshot.Content)))
        if ($hash -ne $file[0].sha256) { throw "Hosted '$name' differs from the exact packaged contract." }
        if ($snapshot.Headers['Cache-Control'] -notcontains 'no-cache') { throw "Hosted '$name' must revalidate after releases." }
        if ($name -eq 'openapi.json') { $publishedOpenApi = [Text.Json.Nodes.JsonNode]::Parse($snapshot.Content) }
    }
    $liveOpenApi = Check-Response '/openapi/v1.json' 200
    if (-not [Text.Json.Nodes.JsonNode]::DeepEquals($publishedOpenApi, [Text.Json.Nodes.JsonNode]::Parse($liveOpenApi.Content))) {
        throw 'The actual hosted OpenAPI document differs from the generated snapshot used for the TypeScript client.'
    }
    if ($manifest.stage -eq 'kanban-product-candidate') {
        $document = $liveOpenApi.Content | ConvertFrom-Json
        if (-not $document.paths.PSObject.Properties['/api/v1/companies'] -or
            -not $document.paths.PSObject.Properties['/api/v1/companies/{companyId}/teams/{teamId}/tasks']) {
            throw 'The hosted product business endpoints are missing from the exact published contracts.'
        }
    }
    Check-Response '/contracts/not-implemented' 404 | Out-Null
    $contractsVerified = $true
}
$metadataResponse = Check-Response '/.well-known/oauth-protected-resource/mcp' 200
$metadata = $metadataResponse.Content | ConvertFrom-Json
$resourceAligned = $manifest.PSObject.Properties['mcpResourceScopesAligned']?.Value -eq $true
$scopePrefix = if ($resourceAligned) { "$origin/mcp" } else { "api://$($identity.applications.api.clientId)" }
$scopes = @("$scopePrefix/tasks.read", "$scopePrefix/tasks.write", 'offline_access')
if ($resourceAligned -and $release.PSObject.Properties['mcpResourceScopesAligned']?.Value -ne $true) {
    throw 'The hosted marker does not identify the resource-aligned MCP candidate.'
}
$issuer = "https://$($identity.tenantId).ciamlogin.com/$($identity.tenantId)/v2.0"
if ($metadata.resource -ne "$origin/mcp" -or @($metadata.authorization_servers).Count -ne 1 -or
    $metadata.authorization_servers[0] -ne $issuer -or
    (Compare-Object @($metadata.scopes_supported | Sort-Object) @($scopes | Sort-Object))) {
    throw 'Hosted MCP resource, issuer, or scopes do not match the actual registrations.'
}
$initialize = @{
    jsonrpc = '2.0'; id = 1; method = 'initialize'
    params = @{
        protocolVersion = '2025-06-18'
        capabilities = @{}
        clientInfo = @{ name = 'candidate-boundary-validation'; version = '1' }
    }
} | ConvertTo-Json -Depth 6 -Compress
$accept = 'application/json, text/event-stream'
$anonymous = Check-Response '/mcp' 401 -Method POST -Body $initialize -Headers @{ Accept = $accept }
if ($anonymous.Headers['WWW-Authenticate'] -notmatch [Regex]::Escape("$origin/.well-known/oauth-protected-resource/mcp")) {
    throw 'MCP challenge does not advertise the exact protected-resource metadata endpoint.'
}
Check-Response '/mcp' 401 -Method POST -Body $initialize -Headers @{ Accept = $accept; Authorization = 'Bearer invalid' } | Out-Null
Check-Response '/mcp' 403 -Method POST -Body $initialize -Headers @{ Accept = $accept; Origin = 'https://untrusted.invalid' } | Out-Null
$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(90)
try {
    $challenge = $client.GetAsync("$origin/auth/login").GetAwaiter().GetResult()
    try {
        $location = $challenge.Headers.Location
        if ([int]$challenge.StatusCode -ne 302 -or $null -eq $location -or
            -not $location.IsAbsoluteUri -or $location.Scheme -ne 'https' -or
            $location.Host -ne 'msrmoreiraoexternalid.ciamlogin.com' -or
            $location.AbsolutePath -ne "/$($identity.tenantId)/oauth2/v2.0/authorize") {
            throw 'Browser login did not initiate the actual registered HTTPS provider authorization flow.'
        }
        $query = [Web.HttpUtility]::ParseQueryString($location.Query)
        if ($query['client_id'] -ne $identity.applications.web.clientId -or
            $query['redirect_uri'] -ne "$origin/signin-oidc" -or
            $query['response_type'] -ne 'code' -or
            $query['code_challenge_method'] -ne 'S256' -or
            $query['code_challenge'] -cnotmatch '^[A-Za-z0-9_-]{43}$' -or
            [string]::IsNullOrWhiteSpace($query['state']) -or
            [string]::IsNullOrWhiteSpace($query['nonce']) -or
            -not [string]::IsNullOrEmpty($query['client_secret'])) {
            throw 'Browser authorization request does not match the client, callback, code/PKCE, state, and nonce requirements.'
        }
        $checks.Add(@{ method = 'GET'; path = '/auth/login'; status = 302 })
    } finally { $challenge.Dispose() }
} finally { $client.Dispose(); $handler.Dispose() }
@{
    verifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
    origin = $origin
    releaseId = $release.releaseId
    artifactSha256 = $manifest.artifactSha256
    startupObservations = $startupObservations.ToArray()
    checks = $checks.ToArray()
    mcpMetadata = $metadata
    hostedManagedIdentityDependenciesHealthy = $true
    localIdentityBoundaryChecksPassed = $true
    browserPkceChallengeVerified = $true
    publishedContractSnapshotsVerified = $contractsVerified
    providerSignInVerified = $false
    allThreeClientsVerified = $false
    productAcceptanceVerified = $false
} | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $script:RepositoryRoot $(if ($manifest.stage -eq 'kanban-product-candidate') { 'artifacts\product-live-validation.json' } else { 'artifacts\candidate-live-validation.json' })) -Encoding utf8
foreach ($check in $checks) { Write-Host "$($check.method) $($check.path): $($check.status)" }
Write-Host 'Exact hosted candidate, dependency health, MCP metadata, and unauthenticated boundaries verified.'
Write-Warning 'This smoke check does not claim authenticated hosted product journeys or all-three-native-client acceptance.'

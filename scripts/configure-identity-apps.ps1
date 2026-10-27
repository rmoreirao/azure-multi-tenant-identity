[CmdletBinding()]
param(
    [switch] $Apply,
    [switch] $McpResourceOnly,
    [Uri[]] $VsCodeRedirectUris = @(),
    [Uri[]] $CopilotAppRedirectUris = @(),
    [Uri[]] $ClaudeCodeRedirectUris = @()
)

. (Join-Path $PSScriptRoot 'Common.ps1')
Import-LocalConfiguration

$tenant = '0f6188cc-b440-43f2-87a5-35d9811f8c7c'
$artifactDirectory = Join-Path $script:RepositoryRoot 'artifacts'
$statePath = Join-Path $artifactDirectory 'identity-applications.json'
$outputs = Get-Content (Join-Path $artifactDirectory 'resource-outputs.json') -Raw | ConvertFrom-Json
if ($outputs.vaultName.value -ne 'kv-kanban-2rza5wd265s3g' -or
    $outputs.publicBaseUrl.value -ne 'https://app-kanban-poc-2rza5wd265s3g.azurewebsites.net') {
    throw 'Application setup must use the actual approved PoC vault and hosted origin.'
}
$origin = $outputs.publicBaseUrl.value
$mcpResource = "$origin/mcp"
$managedTag = 'kanban-poc:azure-multi-tenant-identity'
$state = if (Test-Path -LiteralPath $statePath) {
    Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable
} else {
    @{ tenantId = $tenant; applications = @{} }
}
if ($state.tenantId -ne $tenant) { throw 'Identity state belongs to another directory.' }

$tokenText = & az account get-access-token --tenant $tenant --resource-type ms-graph --output json --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'The existing operator Graph session is required for application setup.' }
$token = $tokenText | ConvertFrom-Json
$payload = $token.accessToken.Split('.')[1].Replace('-', '+').Replace('_', '/')
$payload = $payload.PadRight($payload.Length + (4 - $payload.Length % 4) % 4, '=')
$claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
if ($claims.tid -ne $tenant -or $claims.appid -ne '04b07795-8ddb-461a-bbee-02f9e1bf7b46' -or
    $claims.wids -notcontains '62e90394-69f5-4237-9190-012177145e10' -or
    $claims.scp.Split(' ') -notcontains 'Application.ReadWrite.All' -or
    $claims.scp.Split(' ') -notcontains 'DelegatedPermissionGrant.ReadWrite.All') {
    throw 'Use the existing Azure CLI Global Administrator session with its preauthorized application/consent permissions.'
}
$headers = @{ Authorization = "Bearer $($token.accessToken)" }
$state.configuredByPrincipalId = $claims.oid

function Invoke-Graph {
    param([string] $Path, [string] $Method = 'GET', [hashtable] $Body)
    $uri = if ($Path.StartsWith('https://', [StringComparison]::Ordinal)) {
        [Uri] $Path
    } else {
        [Uri] "https://graph.microsoft.com/v1.0/$Path"
    }
    if ($uri.Host -ne 'graph.microsoft.com' -or $uri.Scheme -ne 'https' -or $uri.Port -ne 443) {
        throw 'Graph requests must stay on the approved Microsoft Graph endpoint.'
    }
    $request = @{ Uri = $uri; Method = $Method; Headers = $headers; TimeoutSec = 60 }
    if ($Body) {
        $request.ContentType = 'application/json'
        $request.Body = ConvertTo-Json -InputObject $Body -Depth 20 -Compress
    }
    Invoke-RestMethod @request
}

function Get-GraphCollection([string] $Path) {
    do {
        $page = Invoke-Graph $Path
        foreach ($item in $page.value) { $item }
        $next = $page.PSObject.Properties['@odata.nextLink']
        $Path = if ($next) { [string] $next.Value } else { '' }
    } while ($Path)
}

function Save-State {
    $state.updatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    $state | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $statePath -Encoding utf8
}

function Get-ManagedApplication([string] $Key, [string] $Name) {
    if ($state.applications.ContainsKey($Key)) {
        $application = Invoke-Graph "applications/$($state.applications[$Key].objectId)"
    } else {
        $filter = [Uri]::EscapeDataString("displayName eq '$Name'")
        $matches = @(Get-GraphCollection "applications?`$filter=$filter")
        if ($matches.Count -gt 1 -or
            ($matches.Count -eq 1 -and $matches[0].tags -notcontains $managedTag)) {
            throw "Application '$Name' is ambiguous or is not managed by this deployment."
        }
        $application = if ($matches.Count -eq 1) { $matches[0] } else {
            Invoke-Graph -Path 'applications' -Method POST -Body @{
                displayName = $Name
                signInAudience = 'AzureADMyOrg'
                tags = @($managedTag)
                api = @{ requestedAccessTokenVersion = 2 }
            }
        }
    }
    if ($application.tags -notcontains $managedTag -or $application.displayName -ne $Name -or
        $application.signInAudience -ne 'AzureADMyOrg') {
        throw "Recorded application '$Key' no longer matches the managed single-directory registration."
    }
    if (-not $state.applications.ContainsKey($Key)) { $state.applications[$Key] = @{} }
    $state.applications[$Key].objectId = $application.id
    $state.applications[$Key].clientId = $application.appId
    $state.applications[$Key].displayName = $application.displayName
    Save-State
    $application
}

function Get-ServicePrincipal([string] $AppId) {
    $filter = [Uri]::EscapeDataString("appId eq '$AppId'")
    $principals = @(Get-GraphCollection "servicePrincipals?`$filter=$filter")
    if ($principals.Count -gt 1) { throw 'Application has ambiguous enterprise service principals.' }
    if ($principals.Count -eq 1) { return $principals[0] }
    Invoke-Graph -Path 'servicePrincipals' -Method POST -Body @{ appId = $AppId }
}

function Get-ApiIdentifiers($Application) {
    @(@($Application.identifierUris) + @("api://$($Application.appId)", $mcpResource) |
        Where-Object { $_ } | Sort-Object -Unique)
}

function Assert-NativeRedirects([Uri[]] $Uris) {
    foreach ($uri in $Uris) {
        if (-not $uri.IsAbsoluteUri -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
            ($uri.Scheme -ne 'https' -and
                -not ($uri.Scheme -eq 'http' -and $uri.Host -in @('localhost', '127.0.0.1')))) {
            throw 'Supply only observed exact HTTPS or HTTP loopback native-client callbacks without queries/fragments.'
        }
    }
}
Assert-NativeRedirects $VsCodeRedirectUris
Assert-NativeRedirects $CopilotAppRedirectUris
Assert-NativeRedirects $ClaudeCodeRedirectUris

if ($McpResourceOnly) {
    if ($VsCodeRedirectUris.Count -or $CopilotAppRedirectUris.Count -or $ClaudeCodeRedirectUris.Count -or
        $state.applications.api.clientId -ne '242a2b31-70b7-4591-8131-dca9eb53e6d0' -or
        $state.applications.api.objectId -ne 'c7864419-edd8-439e-be32-d3c7c12dbf39') {
        throw 'Resource-only alignment requires the existing approved API and no callback arguments.'
    }
    $api = Invoke-Graph "applications/$($state.applications.api.objectId)"
    if ($api.appId -ne $state.applications.api.clientId -or $api.tags -notcontains $managedTag -or
        $api.signInAudience -ne 'AzureADMyOrg' -or $api.api.requestedAccessTokenVersion -ne 2 -or
        "api://$($api.appId)" -notin $api.identifierUris) {
        throw 'The existing managed v2 API registration changed; inspect it before alignment.'
    }
    foreach ($name in @('tasks.read', 'tasks.write')) {
        $scope = @($api.api.oauth2PermissionScopes | Where-Object value -eq $name)
        if ($scope.Count -ne 1 -or -not $scope[0].isEnabled -or $scope[0].type -ne 'User') {
            throw 'The existing delegated task scopes must remain enabled and unchanged.'
        }
    }
    $identifiers = @(Get-ApiIdentifiers $api)
    Write-Host "Align only the existing API resource alias: $mcpResource"
    Write-Host 'Retain existing identifiers, scope IDs, grants, callbacks, credentials, browser configuration and identity policies.'
    if (-not $Apply) {
        Write-Host 'Preview only. No provider or local configuration changed.'
        return
    }
    $beforeApi = [Text.Json.Nodes.JsonNode]::Parse(($api.api | ConvertTo-Json -Depth 20 -Compress))
    Invoke-Graph -Path "applications/$($api.id)" -Method PATCH -Body @{ identifierUris = $identifiers } | Out-Null
    $after = Invoke-Graph "applications/$($api.id)"
    $afterApi = [Text.Json.Nodes.JsonNode]::Parse(($after.api | ConvertTo-Json -Depth 20 -Compress))
    if ($after.appId -ne $api.appId -or
        (Compare-Object @($identifiers | Sort-Object) @($after.identifierUris | Sort-Object)) -or
        -not [Text.Json.Nodes.JsonNode]::DeepEquals($beforeApi, $afterApi)) {
        throw 'API resource alignment readback differs from the intended identifier-only change.'
    }
    @{
        alignedAt = [DateTimeOffset]::UtcNow.ToString('O')
        tenantId = $tenant
        appId = $api.appId
        resource = $mcpResource
        previousIdentifierUris = @($api.identifierUris)
        identifierUris = @($after.identifierUris)
        delegatedScopes = @($after.api.oauth2PermissionScopes | Select-Object id, value, isEnabled, type)
        scopeIdsAndApiConfigurationUnchanged = $true
        identifierOnlyPatch = $true
        authenticationCompleted = $false
    } | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath (Join-Path $artifactDirectory 'mcp-resource-registration.json') -Encoding utf8
    $state.applications.api.identifierUris = @($after.identifierUris)
    $state.applications.api.resourceUri = $mcpResource
    Save-State
    Write-Host 'Existing API alias aligned and read back; no permission, callback, credential or identity-policy changes.'
    return
}

Write-Host "External ID directory: $tenant"
Write-Host "Hosted browser origin: $origin"
Write-Host 'Manage one API, one confidential browser app, and three secret-free public-client candidates.'
Write-Host 'Native callbacks stay empty unless explicitly supplied from observed/documented client behavior.'
Write-Host 'Consent is for tasks.read/tasks.write on this API only; it does not grant application company/team access.'
if (-not $Apply) {
    Write-Host 'Preview only. No registrations, credentials, consent, or local configuration changed.'
    return
}
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null

$api = Get-ManagedApplication 'api' 'Kanban PoC API'
$scopes = @($api.api.oauth2PermissionScopes)
foreach ($name in @('tasks.read', 'tasks.write')) {
    $matches = @($scopes | Where-Object { $_.value -eq $name })
    if ($matches.Count -gt 1) { throw "Duplicate API scope '$name'." }
    if ($matches.Count -eq 0) {
        $scopes += @{
            id = [Guid]::NewGuid().ToString()
            value = $name
            type = 'User'
            isEnabled = $true
            adminConsentDisplayName = "Access Kanban $name operations"
            adminConsentDescription = 'Use employee-delegated Kanban operations, subject to current application access grants.'
            userConsentDisplayName = "Use Kanban $name operations"
            userConsentDescription = 'Use your permitted Kanban operations. Your company and team grants still apply.'
        }
    } elseif (-not $matches[0].isEnabled -or $matches[0].type -ne 'User') {
        throw "The managed '$name' scope has changed; inspect it before continuing."
    }
}
Invoke-Graph -Path "applications/$($api.id)" -Method PATCH -Body @{
    identifierUris = @(Get-ApiIdentifiers $api)
    api = @{ requestedAccessTokenVersion = 2; oauth2PermissionScopes = $scopes }
} | Out-Null
$apiPrincipal = Get-ServicePrincipal $api.appId
$state.applications.api.servicePrincipalId = $apiPrincipal.id
Save-State

$web = Get-ManagedApplication 'web' 'Kanban PoC Browser'
$webRedirects = @(
    "$origin/signin-oidc", "$origin/signout-callback-oidc",
    'https://localhost:7143/signin-oidc', 'https://localhost:7143/signout-callback-oidc'
)
Invoke-Graph -Path "applications/$($web.id)" -Method PATCH -Body @{
    web = @{
        redirectUris = $webRedirects
        implicitGrantSettings = @{ enableAccessTokenIssuance = $false; enableIdTokenIssuance = $false }
    }
} | Out-Null
$webPrincipal = Get-ServicePrincipal $web.appId
$state.applications.web.servicePrincipalId = $webPrincipal.id
$state.applications.web.redirectUris = $webRedirects
Save-State

$clients = @(
    @{ key = 'vscode'; name = 'Kanban PoC VS Code'; redirects = $VsCodeRedirectUris }
    @{ key = 'copilotApp'; name = 'Kanban PoC Copilot App'; redirects = $CopilotAppRedirectUris }
    @{ key = 'claudeCode'; name = 'Kanban PoC Claude Code'; redirects = $ClaudeCodeRedirectUris }
)
foreach ($client in $clients) {
    $application = Get-ManagedApplication $client.key $client.name
    if (@($application.passwordCredentials).Count -gt 0 -or @($application.keyCredentials).Count -gt 0) {
        throw 'Public employee clients must not have a shared secret or certificate credential.'
    }
    if (@($application.requiredResourceAccess | Where-Object { $_.resourceAppId -ne $api.appId }).Count -gt 0) {
        throw 'The managed public client has unexpected permissions for another API; inspect them before continuing.'
    }
    $redirects = @($application.publicClient.redirectUris)
    if ($client.redirects.Count -gt 0) { $redirects = @($client.redirects | ForEach-Object { $_.AbsoluteUri }) }
    Invoke-Graph -Path "applications/$($application.id)" -Method PATCH -Body @{
        isFallbackPublicClient = $true
        publicClient = @{ redirectUris = $redirects }
        requiredResourceAccess = @(@{
            resourceAppId = $api.appId
            resourceAccess = @($scopes | Where-Object { $_.value -in @('tasks.read', 'tasks.write') } |
                ForEach-Object { @{ id = $_.id; type = 'Scope' } })
        })
    } | Out-Null
    $principal = Get-ServicePrincipal $application.appId
    $filter = [Uri]::EscapeDataString("clientId eq '$($principal.id)'")
    $grants = @(Get-GraphCollection "oauth2PermissionGrants?`$filter=$filter" |
        Where-Object { $_.resourceId -eq $apiPrincipal.id -and $_.consentType -eq 'AllPrincipals' })
    if ($grants.Count -gt 1) { throw 'Multiple API consent grants exist for a managed client.' }
    if ($grants.Count -eq 0) {
        Invoke-Graph -Path 'oauth2PermissionGrants' -Method POST -Body @{
            clientId = $principal.id
            resourceId = $apiPrincipal.id
            consentType = 'AllPrincipals'
            scope = 'tasks.read tasks.write'
        } | Out-Null
    } elseif (($grants[0].scope.Split(' ', [StringSplitOptions]::RemoveEmptyEntries) | Sort-Object -Unique) -join ' ' -ne 'tasks.read tasks.write') {
        throw 'Managed native-client API consent differs from the intended two delegated scopes.'
    }
    $state.applications[$client.key].servicePrincipalId = $principal.id
    $state.applications[$client.key].redirectUris = $redirects
    $state.applications[$client.key].callbackConfigured = ($redirects.Count -gt 0)
    Save-State
}

$vaultToken = & az account get-access-token --tenant 'f1640304-f816-412c-9e6f-4b6b818cf492' --resource https://vault.azure.net --query accessToken --output tsv --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Cannot acquire the existing operator Key Vault credential.' }
$vaultHeaders = @{ Authorization = "Bearer $vaultToken" }
$secretUri = "https://$($outputs.vaultName.value).vault.azure.net/secrets/browser-client-secret?api-version=7.4"
$secretResponse = Invoke-WebRequest -Uri $secretUri -Headers $vaultHeaders -SkipHttpErrorCheck -TimeoutSec 60
if ([int]$secretResponse.StatusCode -eq 200) {
    $secret = $secretResponse.Content | ConvertFrom-Json
    if ($secret.tags.webClientId -ne $web.appId -or
        @($web.passwordCredentials | Where-Object { $_.keyId -eq $secret.tags.passwordKeyId }).Count -ne 1 -or
        $secret.attributes.exp -le [DateTimeOffset]::UtcNow.AddDays(1).ToUnixTimeSeconds()) {
        throw 'The existing managed browser credential is mismatched or expiring; explicit rotation is required.'
    }
} elseif ([int]$secretResponse.StatusCode -eq 404) {
    $credential = Invoke-Graph -Path "applications/$($web.id)/addPassword" -Method POST -Body @{
        passwordCredential = @{
            displayName = 'kanban-poc-browser'
            endDateTime = [DateTimeOffset]::UtcNow.AddDays(90).ToString('O')
        }
    }
    $state.applications.web.passwordKeyId = $credential.keyId
    Save-State
    $secret = Invoke-RestMethod -Uri $secretUri -Method PUT -Headers $vaultHeaders -ContentType 'application/json' -TimeoutSec 60 -Body (
        @{
            value = $credential.secretText
            contentType = 'Entra confidential browser client credential'
            attributes = @{ enabled = $true; exp = ([DateTimeOffset]$credential.endDateTime).ToUnixTimeSeconds() }
            tags = @{ application = 'kanban-poc'; webClientId = $web.appId; passwordKeyId = $credential.keyId }
        } | ConvertTo-Json -Depth 10 -Compress
    )
} else {
    throw "Cannot inspect the managed Key Vault browser credential: HTTP $($secretResponse.StatusCode)."
}
$state.applications.web.passwordKeyId = $secret.tags.passwordKeyId
$state.applications.web.secretUri = $secret.id
Save-State
@{ 'ExternalId:WebClientSecret' = $secret.value } | ConvertTo-Json -Compress |
    & dotnet user-secrets set --project (Join-Path $script:RepositoryRoot 'src\Kanban.Web')
if ($LASTEXITCODE -ne 0) { throw 'Cannot save the browser credential in local ASP.NET User Secrets.' }
$secret = $null
$credential = $null
$secretResponse = $null

$values = @{
    ExternalId__WebClientId = $web.appId
    ExternalId__ApiClientId = $api.appId
    ExternalId__ApiAudience = $api.appId
    ExternalId__PublicClientIds = ($clients | ForEach-Object { $state.applications[$_.key].clientId }) -join ','
    Azure__WebClientSecretUri = $state.applications.web.secretUri
}
$envPath = Join-Path $script:RepositoryRoot '.env'
$lines = [Collections.Generic.List[string]]::new()
$seen = @{}
foreach ($line in Get-Content -LiteralPath $envPath) {
    if ($line -match '^([A-Za-z][A-Za-z0-9_]*)=' -and $values.ContainsKey($Matches[1])) {
        $key = $Matches[1]
        if ($seen.ContainsKey($key)) { throw "Duplicate local configuration key '$key'." }
        $lines.Add("$key=$($values[$key])")
        $seen[$key] = $true
    } else { $lines.Add($line) }
}
foreach ($key in $values.Keys) {
    if (-not $seen.ContainsKey($key)) { $lines.Add("$key=$($values[$key])") }
}
$lines | Set-Content -LiteralPath $envPath -Encoding utf8
Write-Host 'Managed application IDs and secret references saved. The secret value is only in Key Vault and local User Secrets.'
Write-Host 'User flow, MFA policy, observed callbacks, real sign-in, and all-three-client support are not yet verified.'

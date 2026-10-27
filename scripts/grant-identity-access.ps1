[CmdletBinding()]
param(
    [switch] $Apply,
    [switch] $RestorePreviousConsent
)

. (Join-Path $PSScriptRoot 'Common.ps1')

if (-not $RestorePreviousConsent) {
    throw 'AADSTS65002 prevents Azure CLI from minting the requested policy scopes. Do not grant more consent or relogin as a workaround. Use a supported Microsoft Graph PowerShell delegated session; use -RestorePreviousConsent to undo the recorded unusable grant.'
}

$tenant = '0f6188cc-b440-43f2-87a5-35d9811f8c7c'
$cliAppId = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'

$tokenText = & az account get-access-token --tenant $tenant --resource-type ms-graph -o json --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'The existing operator Graph sign-in is required.' }
$token = $tokenText | ConvertFrom-Json
$payload = $token.accessToken.Split('.')[1].Replace('-', '+').Replace('_', '/')
$payload = $payload.PadRight($payload.Length + (4 - $payload.Length % 4) % 4, '=')
$claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
if ($claims.tid -ne $tenant -or $claims.appid -ne $cliAppId -or
    $claims.wids -notcontains '62e90394-69f5-4237-9190-012177145e10' -or
    $claims.scp.Split(' ') -notcontains 'DelegatedPermissionGrant.ReadWrite.All') {
    throw 'Use the existing Azure CLI Global Administrator session in the approved External ID directory.'
}
$headers = @{ Authorization = "Bearer $($token.accessToken)" }
$directory = Join-Path $script:RepositoryRoot 'artifacts'
$path = Join-Path $directory 'operator-graph-consent.json'
$existing = $null
if (Test-Path -LiteralPath $path) {
    $existing = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($existing.tenantId -ne $tenant -or $existing.clientAppId -ne $cliAppId -or
        $existing.principalId -ne $claims.oid) {
        throw 'Existing consent evidence belongs to a different operator or directory.'
    }
}

function Invoke-Graph {
    param([string] $Path, [string] $Method = 'GET', [hashtable] $Body)
    $request = @{
        Uri = "https://graph.microsoft.com/v1.0/$Path"
        Method = $Method
        Headers = $headers
        TimeoutSec = 60
    }
    if ($Body) {
        $request.ContentType = 'application/json'
        $request.Body = ConvertTo-Json -InputObject $Body -Depth 15 -Compress
    }
    Invoke-RestMethod @request
}

if ($RestorePreviousConsent) {
    if (-not $existing) { throw 'No recorded operator consent exists to restore.' }
    if ($existing.PSObject.Properties['restoredAt']) {
        Write-Host 'The recorded operator consent has already been restored.'
        return
    }
    $grant = Invoke-Graph "oauth2PermissionGrants/$($existing.grantId)"
    if ($grant.clientId -ne $existing.clientObjectId -or
        $grant.resourceId -ne $existing.resourceObjectId -or
        $grant.principalId -ne $existing.principalId -or
        $grant.consentType -ne 'Principal' -or $grant.scope -ne $existing.managedScope) {
        throw 'The recorded consent changed; no grant was removed or overwritten.'
    }
    Write-Host 'Restore only the recorded operator-specific Graph consent. Retain the shared official Azure CLI enterprise application.'
    if (-not $Apply) {
        Write-Host 'Preview only. Use -RestorePreviousConsent -Apply to restore the recorded previous scopes.'
        return
    }
    if ($existing.grantCreatedByThisRun) {
        Invoke-Graph -Path "oauth2PermissionGrants/$($existing.grantId)" -Method DELETE | Out-Null
    } elseif ($existing.previousScope) {
        Invoke-Graph -Path "oauth2PermissionGrants/$($existing.grantId)" -Method PATCH -Body @{ scope = $existing.previousScope } | Out-Null
    } else {
        throw 'Previous consent evidence is inconsistent; no consent was changed.'
    }
    $existing | Add-Member -NotePropertyName restoredAt -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O'))
    $existing | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding utf8
    Write-Host 'Recorded operator consent restored; first-party Azure CLI enterprise application retained.'
    return
}

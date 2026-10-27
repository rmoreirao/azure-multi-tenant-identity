[CmdletBinding()]
param(
    [switch] $Provision,
    [switch] $ApplyInfrastructure,
    [Parameter(Mandatory)][ValidatePattern('^\d{1,3}(\.\d{1,3}){3}$')][string] $DeveloperIp,
    [hashtable] $VerifiedAppOutboundRules = @{}
)

. (Join-Path $PSScriptRoot 'Common.ps1')
Import-LocalConfiguration

$subscription = '67bfd3c6-6d7e-4a79-9a85-19f4a6928d7c'
$group = 'rg-kanban-poc-swc'
$location = 'swedencentral'
$operator = '4abf75c1-8a34-4138-a0a5-651cf36ca396'
$template = Join-Path $script:RepositoryRoot 'infra\main.bicep'

$ip = [Net.IPAddress]::Parse($DeveloperIp)
if ($ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
    $DeveloperIp -eq '0.0.0.0' -or $DeveloperIp -eq '255.255.255.255') {
    throw 'An explicit approved IPv4 workstation egress address is required.'
}

Write-Host "Subscription: $subscription"
Write-Host "Resource group: $group ($location)"
Write-Host "SQL firewall workstation address: $DeveloperIp"
Write-Host 'User-approved resource-group policy exception: SecurityControl=Ignore (synthetic PoC only).'
$accountText = & az account show --subscription $subscription --query '{id:id,tenantId:tenantId}' -o json --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Cannot access the approved subscription.' }
$account = $accountText | ConvertFrom-Json
if ($account.id -ne $subscription -or $account.tenantId -ne 'f1640304-f816-412c-9e6f-4b6b818cf492') {
    throw 'Unexpected subscription/directory. No deployment was made.'
}

$exists = & az group exists --name $group --subscription $subscription --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Cannot check the approved resource group.' }
if ($exists -ne 'true') {
    if (-not $Provision) {
        throw 'Resource group does not exist. Use -Provision to create only the approved PoC group.'
    }
    Invoke-Checked az group create --name $group --location $location --subscription $subscription --output none --only-show-errors
}

$artifactDirectory = Join-Path $script:RepositoryRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$resourceOutputPath = Join-Path $artifactDirectory 'resource-outputs.json'
$ips = @()
if (Test-Path -LiteralPath $resourceOutputPath) {
    $previous = Get-Content -LiteralPath $resourceOutputPath -Raw | ConvertFrom-Json
    $ips = @($previous.outboundIps.value.Split(',') | Where-Object { $_ } | Sort-Object -Unique)
}
function Assert-NarrowAddresses([string[]] $Addresses) {
    if ($Addresses.Count -gt 255) { throw 'The narrow SQL firewall allowlist exceeds its supported bounded size.' }
    foreach ($address in $Addresses) {
        $parsed = [Net.IPAddress]::Parse($address)
        if ($parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
            $address -eq '0.0.0.0' -or $address -eq '255.255.255.255') {
            throw 'App Service must advertise explicit individual IPv4 outbound addresses.'
        }
    }
}
Assert-NarrowAddresses $ips
Assert-NarrowAddresses (@($ips) + @($VerifiedAppOutboundRules.Values))
if ($VerifiedAppOutboundRules.Count -gt 0) {
    foreach ($name in $VerifiedAppOutboundRules.Keys) {
        if ($name -cnotmatch '^app-outbound-[0-9]+$') {
            throw 'Verified application-egress rules must use distinct app-outbound-N names, not workstation or advertised app-service rule names.'
        }
    }
    if (-not (Test-Path -LiteralPath $resourceOutputPath) -or
        $previous.appName.value -ne 'app-kanban-poc-2rza5wd265s3g') {
        throw 'Verify exceptional application egress only against the already provisioned approved app.'
    }
    $possibleText = & az webapp show --subscription $subscription --resource-group $group --name $previous.appName.value --query possibleOutboundIpAddresses --output tsv --only-show-errors
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($possibleText)) {
        throw 'Cannot verify the possible outbound addresses for the approved application.'
    }
    $possible = $possibleText.Split(',')
    foreach ($address in $VerifiedAppOutboundRules.Values) {
        if ($possible -notcontains $address) {
            throw "Explicitly approved application egress '$address' is not in this app's possible-outbound set."
        }
    }
    Write-Host "Explicitly approved additional application rules: $($VerifiedAppOutboundRules | ConvertTo-Json -Compress)"
}
$parameters = @{
    operatorObjectId = @{ value = $operator }
    developerIp = @{ value = $DeveloperIp }
    location = @{ value = $location }
    appOutboundIps = @{ value = $ips }
    verifiedAppOutboundRules = @{ value = $VerifiedAppOutboundRules }
    configureSqlFirewall = @{ value = ($ips.Count -gt 0) }
}
if ($env:ExternalId__WebClientId) { $parameters.webClientId = @{ value = $env:ExternalId__WebClientId } }
if ($env:ExternalId__ApiClientId) { $parameters.apiClientId = @{ value = $env:ExternalId__ApiClientId } }
if ($env:ExternalId__PublicClientIds) { $parameters.publicClientIds = @{ value = $env:ExternalId__PublicClientIds } }
if ($env:Azure__WebClientSecretUri) { $parameters.webClientSecretUri = @{ value = $env:Azure__WebClientSecretUri } }
$parameterPath = Join-Path $artifactDirectory 'infrastructure.parameters.json'
function Save-InfrastructureParameters {
    @{
        '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion = '1.0.0.0'
        parameters = $parameters
    } | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $parameterPath -Encoding utf8
}
Save-InfrastructureParameters

if ($ApplyInfrastructure) {
    Invoke-Checked az tag update --resource-id "/subscriptions/$subscription/resourceGroups/$group" --operation Merge --tags SecurityControl=Ignore --subscription $subscription --output none --only-show-errors
}
Invoke-Checked az deployment group what-if --name kanban-poc --resource-group $group --subscription $subscription --template-file $template --parameters "@$parameterPath" --result-format ResourceIdOnly --only-show-errors
if (-not $ApplyInfrastructure) {
    Write-Host 'Preview only. No application infrastructure changes applied.'
    return
}

Invoke-Checked az deployment group create --name kanban-poc --resource-group $group --subscription $subscription --template-file $template --parameters "@$parameterPath" --output none --only-show-errors
$outputText = & az deployment group show --name kanban-poc --resource-group $group --subscription $subscription --query properties.outputs --output json --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the successful infrastructure deployment outputs.' }
$outputs = $outputText | ConvertFrom-Json
$outputText | Set-Content -LiteralPath $resourceOutputPath -Encoding utf8
$ips = @($outputs.outboundIps.value.Split(',') | Where-Object { $_ } | Sort-Object -Unique)
if ($ips.Count -eq 0) { throw 'App Service outbound addresses are missing; SQL access remains closed to the app.' }
Assert-NarrowAddresses $ips
Assert-NarrowAddresses (@($ips) + @($VerifiedAppOutboundRules.Values))
$parameters.appOutboundIps.value = $ips
$network = & az sql server show --name $outputs.sqlServerName.value --resource-group $group --subscription $subscription --query publicNetworkAccess -o tsv --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify SQL public network state.' }
if ($network -ne 'Enabled') {
    throw "SQL publicNetworkAccess is '$network', not 'Enabled'. Check the approved resource-group SecurityControl=Ignore tag and inherited policies. No broad firewall will be attempted."
}
$parameters.configureSqlFirewall = @{ value = $true }
Save-InfrastructureParameters
Invoke-Checked az deployment group create --name kanban-poc --resource-group $group --subscription $subscription --template-file $template --parameters "@$parameterPath" --output none --only-show-errors
$ruleText = & az sql server firewall-rule list --server $outputs.sqlServerName.value --resource-group $group --subscription $subscription --output json --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify the actual SQL firewall allowlist.' }
$rules = @($ruleText | ConvertFrom-Json)
$expected = @((@($DeveloperIp) + $ips + @($VerifiedAppOutboundRules.Values)) | Sort-Object -Unique)
$actual = @($rules | ForEach-Object {
    if ($_.startIpAddress -ne $_.endIpAddress) { throw 'SQL has an unexpected IP range rather than individual approved addresses.' }
    $_.startIpAddress
} | Sort-Object -Unique)
if ($actual.Count -eq 0 -or (Compare-Object $expected $actual)) {
    throw 'The actual SQL firewall differs from the approved individual workstation/App Service addresses.'
}
foreach ($name in $VerifiedAppOutboundRules.Keys) {
    $matching = @($rules | Where-Object { $_.name -ceq $name })
    if ($matching.Count -ne 1 -or $matching[0].startIpAddress -ne $VerifiedAppOutboundRules[$name]) {
        throw 'An explicitly approved application-egress rule does not match its reviewed name and address.'
    }
}
$finalServerText = & az sql server show --name $outputs.sqlServerName.value --resource-group $group --subscription $subscription --query '{state:state,publicNetworkAccess:publicNetworkAccess}' --output json --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify final SQL server state after firewall deployment.' }
$finalServer = $finalServerText | ConvertFrom-Json
if ($finalServer.state -ne 'Ready' -or $finalServer.publicNetworkAccess -ne 'Enabled') {
    throw 'SQL did not retain the approved public network configuration after the final deployment.'
}

$outputText | Set-Content -LiteralPath (Join-Path $artifactDirectory 'infrastructure.json') -Encoding utf8
Write-Host "Infrastructure available: $($outputs.publicBaseUrl.value)"
Write-Host 'Public SQL is Enabled with only the approved individual workstation/App Service firewall addresses.'
Write-Host 'No application artifact or schema migration was deployed by this infrastructure step.'

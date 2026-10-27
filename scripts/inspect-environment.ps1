[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'Common.ps1')
$subscription = '67bfd3c6-6d7e-4a79-9a85-19f4a6928d7c'
$tenant = 'f1640304-f816-412c-9e6f-4b6b818cf492'
$group = 'rg-kanban-poc-swc'
$region = 'swedencentral'
$origin = 'https://app-kanban-poc-2rza5wd265s3g.azurewebsites.net'

function Get-AzureJson([string[]] $Arguments) {
    Invoke-Checked -Command az -Arguments ($Arguments + @('--output', 'json', '--only-show-errors')) | ConvertFrom-Json
}

function Get-RetailMeter([string] $Filter, [string] $Product, [string] $Sku, [string] $Unit) {
    $uri = 'https://prices.azure.com/api/retail/prices?$filter=' + [Uri]::EscapeDataString($Filter)
    $page = Invoke-RestMethod -Uri $uri -MaximumRedirection 0 -TimeoutSec 45
    if ($page.NextPageLink) { throw 'The regional price query is incomplete; narrow its filter before estimating cost.' }
    $matches = @($page.Items | Where-Object {
        $_.productName -ceq $Product -and $_.skuName -ceq $Sku -and $_.type -ceq 'Consumption'
    })
    if ($matches.Count -ne 1 -or $matches[0].unitOfMeasure -cne $Unit -or
        $matches[0].currencyCode -cne 'USD' -or [decimal]$matches[0].retailPrice -le 0) {
        throw "The exact USD consumption meter for '$Product / $Sku' is missing or ambiguous."
    }
    return @{
        source = $uri
        product = $Product
        sku = $Sku
        unit = $Unit
        retailPrice = [decimal]$matches[0].retailPrice
        currency = 'USD'
        effectiveStartDate = $matches[0].effectiveStartDate
    }
}

Write-Host "Read-only environment inspection: subscription $subscription, resource group $group."
Write-Host 'No infrastructure, firewall, permissions, provider settings, or billing configuration will be changed.'
$account = Get-AzureJson @('account', 'show', '--subscription', $subscription, '--query', '{id:id,tenantId:tenantId}')
if ($account.id -ne $subscription -or $account.tenantId -ne $tenant) { throw 'Unexpected Azure subscription or directory.' }
$resources = @(Get-AzureJson @(
    'resource', 'list', '--subscription', $subscription, '--resource-group', $group,
    '--query', '[].{name:name,type:type,location:location,kind:kind,sku:sku}'
))
$databases = @($resources | Where-Object { $_.type -eq 'Microsoft.Sql/servers/databases' -and $_.name -notlike '*/master' })
if ($databases.Count -ne 1 -or $databases[0].name -ne 'sql-kanban-poc-2rza5wd265s3g/kanban') {
    throw 'The resource group does not contain exactly the one approved application database.'
}
$plan = Get-AzureJson @(
    'rest', '--method', 'get',
    '--url', "https://management.azure.com/subscriptions/$subscription/resourceGroups/$group/providers/Microsoft.Web/serverfarms/asp-kanban-poc-2rza5wd265s3g?api-version=2024-11-01",
    '--query', '{sku:sku,reserved:properties.reserved,location:location}'
)
$database = Get-AzureJson @(
    'sql', 'db', 'show', '--subscription', $subscription, '--resource-group', $group,
    '--server', 'sql-kanban-poc-2rza5wd265s3g', '--name', 'kanban',
    '--query', '{serviceObjective:currentServiceObjectiveName,status:status,maxSizeBytes:maxSizeBytes}'
)
$monitoring = Get-AzureJson @(
    'rest', '--method', 'get',
    '--url', "https://management.azure.com/subscriptions/$subscription/resourceGroups/$group/providers/Microsoft.OperationalInsights/workspaces/log-kanban-poc-2rza5wd265s3g?api-version=2023-09-01",
    '--query', '{retentionInDays:properties.retentionInDays,sku:properties.sku.name,dailyQuotaGb:properties.workspaceCapping.dailyQuotaGb}'
)
if ($plan.sku.name -ne 'B1' -or $plan.sku.capacity -ne 1 -or -not $plan.reserved -or
    ([string]$plan.location).Replace(' ', '') -ne $region) {
    throw "Expected one Linux B1 instance in Sweden Central; actual SKU '$($plan.sku.name)', capacity '$($plan.sku.capacity)', Linux '$($plan.reserved)', location '$($plan.location)'."
}
if ($database.serviceObjective -ne 'S0' -or $database.status -ne 'Online' -or $database.maxSizeBytes -ne 5368709120) {
    throw "Expected an online S0 database capped at 5 GiB; actual objective '$($database.serviceObjective)', status '$($database.status)', bytes '$($database.maxSizeBytes)'."
}
if ($monitoring.sku -ne 'PerGB2018' -or $monitoring.retentionInDays -ne 30 -or $monitoring.dailyQuotaGb -ne 0.1) {
    throw "Expected PerGB2018 monitoring, 30-day retention, and a 0.1-GB daily ingestion cap; actual '$($monitoring.sku)', '$($monitoring.retentionInDays)', '$($monitoring.dailyQuotaGb)'."
}
$health = Invoke-WebRequest -Uri "$origin/health" -SkipHttpErrorCheck -MaximumRedirection 0 -TimeoutSec 90
if ([int]$health.StatusCode -ne 200 -or $health.Content -ne 'Healthy') { throw 'Hosted dependency health is not healthy.' }
$release = Invoke-RestMethod -Uri "$origin/release.json" -MaximumRedirection 0 -TimeoutSec 30
if ($release.stage -ne 'identity-feasibility' -or $release.productAccepted -ne $false) {
    throw 'The hosted release marker does not identify the explicitly limited identity candidate.'
}

$appPrice = Get-RetailMeter `
    -Filter "armRegionName eq '$region' and serviceName eq 'Azure App Service' and skuName eq 'B1'" `
    -Product 'Azure App Service Basic Plan - Linux' -Sku 'B1' -Unit '1 Hour'
$sqlPrice = Get-RetailMeter `
    -Filter "armRegionName eq '$region' and serviceName eq 'SQL Database' and skuName eq 'S0'" `
    -Product 'SQL Database Single Standard' -Sku 'S0' -Unit '1/Day'
$hours = [decimal]730
$appMonthly = [decimal]$appPrice.retailPrice * $hours
$sqlMonthly = [decimal]$sqlPrice.retailPrice * $hours / 24

$query = @{
    type = 'ActualCost'
    timeframe = 'MonthToDate'
    dataset = @{
        granularity = 'None'
        aggregation = @{ totalCost = @{ name = 'PreTaxCost'; function = 'Sum' } }
        filter = @{ dimensions = @{ name = 'ResourceGroupName'; operator = 'In'; values = @($group) } }
        grouping = @(@{ type = 'Dimension'; name = 'ResourceType' })
    }
}
$directory = Join-Path $script:RepositoryRoot 'artifacts'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$temporary = Join-Path $directory "environment-cost-query-$([Guid]::NewGuid().ToString('N')).json"
try {
    $query | ConvertTo-Json -Depth 8 -Compress | Set-Content -LiteralPath $temporary -Encoding utf8
    $billing = Get-AzureJson @(
        'rest', '--method', 'post',
        '--url', "https://management.azure.com/subscriptions/$subscription/providers/Microsoft.CostManagement/query?api-version=2025-03-01",
        '--headers', 'Content-Type=application/json', '--body', "@$temporary",
        '--query', '{columns:properties.columns,rows:properties.rows}'
    )
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
if (($billing.columns.name -join ',') -cne 'PreTaxCost,ResourceType,Currency') {
    throw 'The scoped billing query returned an unexpected output shape.'
}
$report = @{
    observedAt = [DateTimeOffset]::UtcNow.ToString('O')
    subscriptionId = $subscription
    resourceGroup = $group
    resources = $resources
    appServicePlan = $plan
    applicationDatabase = $database
    systemMasterDatabaseIsNotAnAdditionalApplicationDatabase = $true
    monitoring = $monitoring
    releaseId = $release.releaseId
    hostedDependencyHealthVerified = $true
    estimatedMonthlyFixedCompute = @{
        currency = 'USD'
        hours = $hours
        days = $hours / 24
        appService = [Math]::Round($appMonthly, 2)
        sqlDatabase = [Math]::Round($sqlMonthly, 2)
        total = [Math]::Round(($appMonthly + $sqlMonthly), 2)
        meters = @($appPrice, $sqlPrice)
        excluded = @('Key Vault operations', 'monitoring ingestion and retention', 'network egress', 'backup overages',
            'External ID and MFA usage', 'taxes')
        retailEstimateNotInvoiceOrSpendingCap = $true
    }
    scopedMonthToDateActualCost = $billing
    emptyBillingRowsDoNotProveZeroCost = $true
    monitoringDailyQuotaIsNotABillingLimit = $true
    stoppingOnlyTheWebAppDoesNotStopProvisionedPlanOrSqlCharges = $true
    resourcesOrPermissionsChanged = $false
    productAcceptanceVerified = $false
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $directory 'environment-operations.json') -Encoding utf8
Write-Host "Fixed compute retail estimate: USD $([Math]::Round(($appMonthly + $sqlMonthly), 2)) per 730 hours, before listed variable charges and taxes."
Write-Host "Scoped month-to-date cost rows: $(@($billing.rows).Count). Empty results are not evidence of zero charges."
Write-Host 'Resource inventory, actual tier/cap settings, health, retail meters, and billing-query results recorded in artifacts\environment-operations.json.'
Write-Warning 'This read-only operational snapshot does not complete provider sign-in, native-client validation, or product acceptance.'

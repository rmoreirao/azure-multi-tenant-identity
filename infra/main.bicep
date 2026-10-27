targetScope = 'resourceGroup'

param location string = resourceGroup().location
param operatorObjectId string
param operatorDisplayName string = 'Kanban PoC operator'
param developerIp string
param appOutboundIps array = []
param verifiedAppOutboundRules object = {}
param configureSqlFirewall bool = false
param externalIdTenantName string = 'msrmoreiraoexternalid'
param externalIdTenantId string = '0f6188cc-b440-43f2-87a5-35d9811f8c7c'
param webClientId string = ''
param apiClientId string = ''
param publicClientIds string = ''
param webClientSecretUri string = ''

var suffix = uniqueString(resourceGroup().id)
var tags = {
  application: 'kanban-poc'
  dataClassification: 'synthetic-only'
  deployment: 'workstation-bicep'
}
var secretsUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var secretsOfficer = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
var encryptionUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'e147488a-f6f5-4113-8e2d-b22465e65bf6')

resource plan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: 'asp-kanban-poc-${suffix}'
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: 'B1'
    tier: 'Basic'
    capacity: 1
  }
  properties: {
    reserved: true
  }
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-kanban-poc-${suffix}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: json('0.1') }
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-kanban-poc-${suffix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: 'kv-kanban-${suffix}'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
  }
}

resource protectionKey 'Microsoft.KeyVault/vaults/keys@2024-11-01' = {
  parent: vault
  name: 'data-protection'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: ['wrapKey', 'unwrapKey']
    attributes: { enabled: true }
  }
}

resource sql 'Microsoft.Sql/servers@2025-01-01' = {
  name: 'sql-kanban-poc-${suffix}'
  location: location
  tags: tags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'User'
      login: operatorDisplayName
      sid: operatorObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sql
  name: 'kanban'
  location: location
  tags: tags
  sku: { name: 'S0', tier: 'Standard', capacity: 10 }
  properties: {
    maxSizeBytes: 5368709120
    zoneRedundant: false
  }
}

resource developerFirewall 'Microsoft.Sql/servers/firewallRules@2023-08-01' = if (configureSqlFirewall) {
  parent: sql
  name: 'approved-workstation'
  properties: { startIpAddress: developerIp, endIpAddress: developerIp }
}

resource outboundFirewall 'Microsoft.Sql/servers/firewallRules@2023-08-01' = [for (ip, index) in appOutboundIps: if (configureSqlFirewall) {
  parent: sql
  name: 'app-service-${index}'
  properties: { startIpAddress: ip, endIpAddress: ip }
}]

resource verifiedOutboundFirewall 'Microsoft.Sql/servers/firewallRules@2023-08-01' = [for rule in items(verifiedAppOutboundRules): if (configureSqlFirewall) {
  parent: sql
  name: rule.key
  properties: { startIpAddress: rule.value, endIpAddress: rule.value }
}]

resource app 'Microsoft.Web/sites@2024-11-01' = {
  name: 'app-kanban-poc-${suffix}'
  location: location
  tags: tags
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      http20Enabled: true
      appCommandLine: 'dotnet Kanban.Web.dll'
    }
  }
}

resource appSettings 'Microsoft.Web/sites/config@2024-11-01' = {
  parent: app
  name: 'appsettings'
  properties: {
    ASPNETCORE_ENVIRONMENT: 'Production'
    ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true'
    WEBSITE_RUN_FROM_PACKAGE: '1'
    SCM_DO_BUILD_DURING_DEPLOYMENT: 'false'
    APPLICATIONINSIGHTS_CONNECTION_STRING: insights.properties.ConnectionString
    Application__PublicBaseUrl: 'https://${app.properties.defaultHostName}'
    ExternalId__TenantName: externalIdTenantName
    ExternalId__TenantId: externalIdTenantId
    ExternalId__Authority: 'https://${externalIdTenantName}.ciamlogin.com/${externalIdTenantId}/v2.0'
    ExternalId__Issuer: 'https://${externalIdTenantId}.ciamlogin.com/${externalIdTenantId}/v2.0'
    ExternalId__WebClientId: webClientId
    ExternalId__ApiClientId: apiClientId
    ExternalId__ApiAudience: apiClientId
    ExternalId__PublicClientIds: publicClientIds
    ExternalId__WebClientSecret: empty(webClientSecretUri) ? '' : '@Microsoft.KeyVault(SecretUri=${webClientSecretUri})'
    Azure__HostingTenantId: subscription().tenantId
    Azure__UseManagedIdentity: 'true'
    Azure__DataProtectionKeyUri: '${vault.properties.vaultUri}keys/data-protection'
    Database__ConnectionString: 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Database=${database.name};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30'
  }
}

resource scmBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-11-01' = {
  parent: app
  name: 'scm'
  properties: { allow: false }
}

resource ftpBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-11-01' = {
  parent: app
  name: 'ftp'
  properties: { allow: false }
}
resource appSecretReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, app.id, secretsUser)
  scope: vault
  properties: {
    roleDefinitionId: secretsUser
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource appEncryptionUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(protectionKey.id, app.id, encryptionUser)
  scope: protectionKey
  properties: {
    roleDefinitionId: encryptionUser
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource operatorSecretWriter 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, operatorObjectId, secretsOfficer)
  scope: vault
  properties: {
    roleDefinitionId: secretsOfficer
    principalId: operatorObjectId
    principalType: 'User'
  }
}

resource operatorEncryptionUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(protectionKey.id, operatorObjectId, encryptionUser)
  scope: protectionKey
  properties: {
    roleDefinitionId: encryptionUser
    principalId: operatorObjectId
    principalType: 'User'
  }
}

output appName string = app.name
output publicBaseUrl string = 'https://${app.properties.defaultHostName}'
output appPrincipalId string = app.identity.principalId
output outboundIps string = app.properties.outboundIpAddresses
output sqlServerName string = sql.name
output sqlHost string = sql.properties.fullyQualifiedDomainName
output databaseName string = database.name
output vaultName string = vault.name
output protectionKeyUri string = '${vault.properties.vaultUri}keys/data-protection'
output sqlResourceId string = sql.id

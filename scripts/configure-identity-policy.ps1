[CmdletBinding()]
param(
    [switch] $Apply,
    [switch] $PreflightOnly,
    [switch] $UserFlowOnly,
    [switch] $UseDeviceCode
)

. (Join-Path $PSScriptRoot 'Common.ps1')
if ($PreflightOnly -and ($Apply -or $UserFlowOnly)) {
    throw 'Choose either read-only -PreflightOnly or a configuration mode, not both.'
}
$tenant = '0f6188cc-b440-43f2-87a5-35d9811f8c7c'
$directory = Join-Path $script:RepositoryRoot 'artifacts'
$path = Join-Path $directory 'identity-applications.json'
$state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
if ($state.tenantId -ne $tenant) { throw 'Identity application state belongs to another directory.' }
$expectedOperator = if ($state.ContainsKey('configuredByPrincipalId')) {
    $state.configuredByPrincipalId
} else {
    (Get-Content (Join-Path $directory 'operator-graph-consent.json') -Raw | ConvertFrom-Json).principalId
}
$scopes = @(
    'User.Read'
    'EventListener.ReadWrite.All'
    'Policy.Read.All'
    'Policy.ReadWrite.ConditionalAccess'
    'Policy.ReadWrite.AuthenticationMethod'
)
$flowName = 'Kanban PoC password sign-in and verified email signup'
$flowDescription = 'Managed by azure-multi-tenant-identity; synthetic-data Kanban PoC only.'
$policyName = 'Kanban PoC: require MFA for browser and API'
$signInApps = @('web', 'vscode', 'copilotApp', 'claudeCode' | ForEach-Object { $state.applications[$_].clientId })
$protectedApps = @($state.applications.web.clientId, $state.applications.api.clientId)
if ($signInApps.Count -ne 4 -or @($signInApps | Sort-Object -Unique).Count -ne 4 -or
    @($signInApps | Where-Object { -not [Guid]::TryParse($_, [ref]([Guid]::Empty)) }).Count -gt 0) {
    throw 'All four actual sign-in application IDs are required before configuring provider policy.'
}

Write-Host "External ID policy directory: $tenant"
Write-Host 'Use official Microsoft Graph PowerShell with a process-scoped, employee-delegated operator session.'
Write-Host "Delegated setup scopes: $($scopes -join ', ')"
Write-Host 'Inspect provider settings; only -Apply can configure signup/sign-in, email OTP availability, and app-scoped MFA.'
Write-Host 'Existing security defaults, other Conditional Access policies, and method exclusions will not be disabled or removed.'
if ($UserFlowOnly) {
    Write-Host 'Identity-only mode: create/link the PoC signup/sign-in flow while retaining enabled Security Defaults and all authentication-method/Conditional Access settings. Strict MFA acceptance remains pending.'
}
if (-not $Apply -and -not $PreflightOnly) {
    Write-Host 'Preview only. No authentication prompt or policy write was initiated.'
    return
}

Import-Module Microsoft.Graph.Authentication -RequiredVersion 2.20.0 -ErrorAction Stop
$connected = $false
try {
    $context = Get-MgContext
    if ($null -eq $context) {
        $connection = @{ TenantId = $tenant; Scopes = $scopes; ContextScope = 'Process'; NoWelcome = $true }
        if ($UseDeviceCode) { $connection.UseDeviceCode = $true }
        Connect-MgGraph @connection -ErrorAction Stop
        $context = Get-MgContext
        $connected = $null -ne $context
    }
    if ($null -eq $context) {
        throw 'Microsoft Graph sign-in returned without an authenticated context. No provider preflight or policy write was attempted.'
    }
    if ($context.TenantId -ne $tenant -or $context.AuthType -ne 'Delegated') {
        throw "Graph sign-in returned tenant '$($context.TenantId)' with authentication type '$($context.AuthType)', not a delegated session in the approved directory. No provider preflight or policy write was attempted."
    }
    if (@($scopes | Where-Object { $context.Scopes -notcontains $_ }).Count -gt 0) {
        throw 'The delegated Graph context lacks required setup scopes. Authenticate interactively with the five displayed scopes before retrying; no provider request or write was attempted.'
    }
    $connected = $true
    Set-MgRequestContext -ClientTimeout 60 -MaxRetry 3 | Out-Null
    $requests = [Collections.Generic.List[object]]::new()

    function Invoke-PolicyGraph {
        param([string] $Path, [string] $Method = 'GET', [hashtable] $Body)
        $uri = if ($Path.StartsWith('https://', [StringComparison]::Ordinal)) {
            [Uri] $Path
        } else {
            [Uri] "https://graph.microsoft.com/v1.0/$Path"
        }
        if ($uri.Host -ne 'graph.microsoft.com' -or $uri.Scheme -ne 'https' -or $uri.Port -ne 443) {
            throw 'Policy requests must stay on the approved Microsoft Graph endpoint.'
        }
        if ($UserFlowOnly -and $Method -ne 'GET' -and (
            $Method -ne 'POST' -or (
                $uri.AbsolutePath -ne '/v1.0/identity/authenticationEventsFlows' -and
                $uri.AbsolutePath -notmatch '^/v1\.0/identity/authenticationEventsFlows/[0-9a-f-]{36}/conditions/applications/includeApplications$'
            )
        )) {
            throw 'Identity-only mode permits only creating the PoC user flow and linking its sign-in applications.'
        }
        $request = @{ Uri = $uri.AbsoluteUri; Method = $Method; OutputType = 'PSObject' }
        if ($Body) {
            $request.ContentType = 'application/json'
            $request.Body = ConvertTo-Json -InputObject $Body -Depth 20 -Compress
        }
        $requests.Add([pscustomobject]@{ method = $Method; path = $uri.AbsolutePath })
        Invoke-MgGraphRequest @request -ErrorAction Stop
    }

    function Get-PolicyCollection([string] $Path) {
        do {
            $page = Invoke-PolicyGraph $Path
            foreach ($item in $page.value) { $item }
            $next = $page.PSObject.Properties['@odata.nextLink']
            $Path = if ($next) { [string] $next.Value } else { '' }
        } while ($Path)
    }

    function Save-PolicyState {
        $state.updatedAt = [DateTimeOffset]::UtcNow.ToString('O')
        $state | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path -Encoding utf8
    }

    function Assert-OwnedUserFlow($Flow) {
        if (-not [Guid]::TryParse($Flow.id, [ref]([Guid]::Empty)) -or
            $Flow.displayName -ne $flowName -or $Flow.description -ne $flowDescription -or
            $Flow.onInteractiveAuthFlowStart.isSignUpAllowed -ne $true) {
            throw 'Recorded user flow no longer matches the managed password-based signup/sign-in flow.'
        }
        $providers = @(Get-PolicyCollection "identity/authenticationEventsFlows/$($Flow.id)/microsoft.graph.externalUsersSelfServiceSignUpEventsFlow/onAuthenticationMethodLoadStart/microsoft.graph.onAuthenticationMethodLoadStartExternalUsersSelfServiceSignUp/identityProviders")
        if ($providers.Count -ne 1 -or $providers[0].id -ne 'EmailPassword-OAUTH') {
            throw 'Recorded user flow no longer matches the managed password-based signup/sign-in flow.'
        }
    }

    function Assert-OwnedMfaPolicy($Policy) {
        if ($Policy.displayName -ne $policyName -or $Policy.state -ne 'enabled' -or
            (Compare-Object @($Policy.conditions.applications.includeApplications | Sort-Object) @($protectedApps | Sort-Object)) -or
            @($Policy.conditions.clientAppTypes).Count -ne 1 -or $Policy.conditions.clientAppTypes[0] -ne 'all' -or
            @($Policy.conditions.users.includeUsers).Count -ne 1 -or $Policy.conditions.users.includeUsers[0] -ne 'All' -or
            $Policy.grantControls.operator -ne 'AND' -or @($Policy.grantControls.builtInControls).Count -ne 1 -or
            $Policy.grantControls.builtInControls[0] -ne 'mfa') {
            throw 'Managed MFA policy no longer requires MFA for all users/clients on exactly the two PoC resources.'
        }
        foreach ($property in $Policy.conditions.PSObject.Properties) {
            if ($property.Name -notin @('applications', 'users', 'clientAppTypes') -and
                $null -ne $property.Value -and @($property.Value).Count -gt 0) {
                throw 'Managed MFA policy acquired an unexpected condition; no policy was overwritten.'
            }
        }
        foreach ($section in @(
            @{ value = $Policy.conditions.applications; allowed = @('includeApplications') }
            @{ value = $Policy.conditions.users; allowed = @('includeUsers') }
            @{ value = $Policy.grantControls; allowed = @('operator', 'builtInControls') }
        )) {
            foreach ($property in $section.value.PSObject.Properties) {
                if ($property.Name -notin $section.allowed -and $null -ne $property.Value -and @($property.Value).Count -gt 0) {
                    throw 'Managed MFA policy acquired an exclusion or another grant condition; inspect it before continuing.'
                }
            }
        }
    }

    $operator = Invoke-PolicyGraph 'me?$select=id'
    if ($operator.id -ne $expectedOperator) {
        throw 'The authenticated administrator is not the recorded approved operator. No policy was changed.'
    }
    $defaults = Invoke-PolicyGraph 'policies/identitySecurityDefaultsEnforcementPolicy'
    $email = Invoke-PolicyGraph 'policies/authenticationMethodsPolicy/authenticationMethodConfigurations/Email'
    $policies = @(Get-PolicyCollection 'identity/conditionalAccess/policies')
    $flows = @(Get-PolicyCollection 'identity/authenticationEventsFlows')
    @{
        tenantId = $tenant
        principalId = $operator.id
        clientId = $context.ClientId
        scopes = $context.Scopes
        securityDefaults = $defaults
        emailMethod = $email
        conditionalAccess = $policies
        authenticationFlows = $flows
        verifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 25 | Set-Content (Join-Path $directory 'identity-preflight.json') -Encoding utf8
    if ($PreflightOnly) {
        Write-Host "Read-only provider preflight completed. Security Defaults enabled: $($defaults.isEnabled)."
        Write-Host 'No provider settings were changed. This does not verify application MFA or real browser/native-client authentication.'
        return
    }
    if ($UserFlowOnly -and $defaults.isEnabled -ne $true) {
        throw 'Identity-only setup requires Security Defaults to remain enabled. No provider setting was changed.'
    }
    if (-not $UserFlowOnly -and $defaults.isEnabled) {
        throw 'Existing tenant security defaults are enabled. They will not be disabled to create a competing Conditional Access policy; an explicit security-preserving policy decision is required.'
    }
    if (-not $UserFlowOnly -and @($email.excludeTargets).Count -gt 0) {
        throw 'Existing email-method exclusions require review; this script will not clear them to force MFA compatibility.'
    }
    if (-not $state.ContainsKey('policy')) { $state.policy = @{} }
    $state.policy.runtimeSignInVerified = $false
    $preservedConfiguration = [System.Text.Json.Nodes.JsonNode]::Parse((ConvertTo-Json -Depth 25 -InputObject @{
        securityDefaults = $defaults
        emailMethod = $email
        conditionalAccess = @($policies | Sort-Object id)
    }))

    if (-not $UserFlowOnly) {
        if ($email.state -ne 'enabled' -or $email.allowExternalIdToUseEmailOtp -ne 'enabled') {
            Invoke-PolicyGraph -Path 'policies/authenticationMethodsPolicy/authenticationMethodConfigurations/Email' -Method PATCH -Body @{
                '@odata.type' = '#microsoft.graph.emailAuthenticationMethodConfiguration'
                state = 'enabled'
                allowExternalIdToUseEmailOtp = 'enabled'
            } | Out-Null
        }

        $policyBody = @{
            displayName = $policyName
            state = 'enabled'
            conditions = @{
                clientAppTypes = @('all')
                applications = @{ includeApplications = $protectedApps }
                users = @{ includeUsers = @('All') }
            }
            grantControls = @{ operator = 'AND'; builtInControls = @('mfa') }
        }
        if ($state.policy.ContainsKey('conditionalAccessId')) {
            $policy = Invoke-PolicyGraph "identity/conditionalAccess/policies/$($state.policy.conditionalAccessId)"
            Assert-OwnedMfaPolicy $policy
        } else {
            if (@($policies | Where-Object { $_.displayName -eq $policyName }).Count -gt 0) {
                throw 'A same-named MFA policy exists without a managed ID; it will not be adopted or overwritten.'
            }
            $policy = Invoke-PolicyGraph -Path 'identity/conditionalAccess/policies' -Method POST -Body $policyBody
            $state.policy.conditionalAccessId = $policy.id
            Save-PolicyState
        }
    }

    if ($state.policy.ContainsKey('flowId')) {
        $flow = Invoke-PolicyGraph "identity/authenticationEventsFlows/$($state.policy.flowId)"
    } else {
        if (@($flows | Where-Object { $_.displayName -eq $flowName }).Count -gt 0) {
            throw 'A same-named user flow exists without a managed ID; inspect it before continuing.'
        }
        $flow = Invoke-PolicyGraph -Path 'identity/authenticationEventsFlows' -Method POST -Body @{
            '@odata.type' = '#microsoft.graph.externalUsersSelfServiceSignUpEventsFlow'
            displayName = $flowName
            description = $flowDescription
            onAuthenticationMethodLoadStart = @{
                '@odata.type' = '#microsoft.graph.onAuthenticationMethodLoadStartExternalUsersSelfServiceSignUp'
                identityProviders = @(@{ id = 'EmailPassword-OAUTH' })
            }
            onInteractiveAuthFlowStart = @{
                '@odata.type' = '#microsoft.graph.onInteractiveAuthFlowStartExternalUsersSelfServiceSignUp'
                isSignUpAllowed = $true
            }
        }
        $state.policy.flowId = $flow.id
        Save-PolicyState
    }
    Assert-OwnedUserFlow $flow
    $applicationPath = "identity/authenticationEventsFlows/$($flow.id)/conditions/applications/includeApplications"
    $linked = @(Get-PolicyCollection $applicationPath)
    $linkedIds = @($linked | ForEach-Object { $_.appId })
    if (@($linkedIds | Where-Object { $signInApps -notcontains $_ }).Count -gt 0) {
        throw 'The managed user flow contains an unapproved application; no new application was linked.'
    }
    foreach ($appId in $signInApps) {
        if ($linkedIds -notcontains $appId) {
            Invoke-PolicyGraph -Path $applicationPath -Method POST -Body @{
                '@odata.type' = '#microsoft.graph.authenticationConditionApplication'
                appId = $appId
            } | Out-Null
        }
    }

    $verifiedFlow = Invoke-PolicyGraph "identity/authenticationEventsFlows/$($flow.id)"
    Assert-OwnedUserFlow $verifiedFlow
    $verifiedEmail = Invoke-PolicyGraph 'policies/authenticationMethodsPolicy/authenticationMethodConfigurations/Email'
    $verifiedApps = @(Get-PolicyCollection $applicationPath)
    if (@($verifiedApps).Count -ne $signInApps.Count -or
        @($signInApps | Where-Object { $verifiedApps.appId -notcontains $_ }).Count -gt 0) {
        throw 'The managed user flow must be linked to exactly the four approved PoC sign-in applications.'
    }
    if ($UserFlowOnly) {
        $verifiedDefaults = Invoke-PolicyGraph 'policies/identitySecurityDefaultsEnforcementPolicy'
        $verifiedPolicies = @(Get-PolicyCollection 'identity/conditionalAccess/policies')
        $verifiedConfiguration = [System.Text.Json.Nodes.JsonNode]::Parse((ConvertTo-Json -Depth 25 -InputObject @{
            securityDefaults = $verifiedDefaults
            emailMethod = $verifiedEmail
            conditionalAccess = @($verifiedPolicies | Sort-Object id)
        }))
        if (-not [System.Text.Json.Nodes.JsonNode]::DeepEquals($preservedConfiguration, $verifiedConfiguration)) {
            throw 'Security Defaults, authentication-method settings, or Conditional Access changed during identity-only setup. No such change was requested by this script; inspect the provider before proceeding.'
        }
        $state.policy.configurationMode = 'identity-only-security-defaults'
        $state.policy.strictMfaAcceptanceVerified = $false
        $state.policy.userFlowConfigurationVerifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
        Save-PolicyState
        @{
            tenantId = $tenant
            principalId = $operator.id
            flowId = $flow.id
            signInApplicationIds = $signInApps
            preservedProviderConfiguration = @{
                securityDefaults = $verifiedDefaults
                emailMethod = $verifiedEmail
                conditionalAccess = $verifiedPolicies
            }
            providerRequests = @($requests.ToArray())
            securityDefaultsEnabled = $verifiedDefaults.isEnabled
            strictMfaAcceptanceVerified = $false
            runtimeSignInVerified = $false
            verifiedAt = $state.policy.userFlowConfigurationVerifiedAt
        } | ConvertTo-Json -Depth 25 | Set-Content (Join-Path $directory 'identity-user-flow-validation.json') -Encoding utf8
        Write-Host 'Identity-only signup/sign-in flow and four application links verified. Security Defaults, authentication methods, and Conditional Access are unchanged.'
        Write-Host 'Strict MFA acceptance and actual browser/native-client authentication remain unverified. This does not enable Kanban data or task access.'
        return
    }
    $verifiedPolicy = Invoke-PolicyGraph "identity/conditionalAccess/policies/$($policy.id)"
    Assert-OwnedMfaPolicy $verifiedPolicy
    if ($verifiedEmail.state -ne 'enabled' -or $verifiedEmail.allowExternalIdToUseEmailOtp -ne 'enabled' -or
        @($signInApps | Where-Object { $verifiedApps.appId -notcontains $_ }).Count -gt 0) {
        throw 'Actual provider settings do not match the intended password/email-OTP MFA flow.'
    }
    $state.policy.configurationMode = 'app-scoped-mfa'
    $state.policy.strictMfaAcceptanceVerified = $false
    $state.policy.configurationVerifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
    Save-PolicyState
    Write-Host 'Provider flow and app-scoped MFA configuration verified. Actual signup/MFA, tokens, and all three MCP clients still require live proof.'
} finally {
    if ($connected) { Disconnect-MgGraph -ErrorAction Continue | Out-Null }
}

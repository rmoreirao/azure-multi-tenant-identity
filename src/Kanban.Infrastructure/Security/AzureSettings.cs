using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace Kanban.Infrastructure.Security;

public sealed class AzureSettings
{
    public string HostingTenantId { get; init; } = "";
    public bool UseManagedIdentity { get; init; } = true;
    public string DataProtectionKeyUri { get; init; } = "";

    public static AzureSettings Load(IConfiguration configuration)
    {
        var settings = configuration.GetRequiredSection("Azure").Get<AzureSettings>()
            ?? throw new InvalidOperationException("Azure configuration is required.");
        if (!Guid.TryParse(settings.HostingTenantId, out _))
        {
            throw new InvalidOperationException("Azure:HostingTenantId must identify the hosting/SQL directory.");
        }

        return settings;
    }

    public TokenCredential CreateCredential(bool operatorCommand = false) =>
        UseManagedIdentity && !operatorCommand
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new AzureCliCredential(new AzureCliCredentialOptions { TenantId = HostingTenantId });
}

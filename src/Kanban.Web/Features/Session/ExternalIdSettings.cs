using System.ComponentModel.DataAnnotations;

namespace Kanban.Web.Features.Session;

public sealed class ExternalIdSettings
{
    [Required] public string TenantName { get; init; } = "";
    [Required] public string TenantId { get; init; } = "";
    [Required, Url] public string Authority { get; init; } = "";
    [Required, Url] public string Issuer { get; init; } = "";
    [Required] public string WebClientId { get; init; } = "";
    [Required] public string WebClientSecret { get; init; } = "";
    [Required] public string ApiClientId { get; init; } = "";
    [Required] public string ApiAudience { get; init; } = "";
    [Required] public string PublicClientIds { get; init; } = "";

    public static ExternalIdSettings Load(IConfiguration configuration)
    {
        var settings = configuration.GetRequiredSection("ExternalId").Get<ExternalIdSettings>()
            ?? throw new InvalidOperationException("External ID configuration is required.");
        Validator.ValidateObject(settings, new ValidationContext(settings), validateAllProperties: true);
        if (!Guid.TryParse(settings.TenantId, out _) || !Guid.TryParse(settings.WebClientId, out _) ||
            !Guid.TryParse(settings.ApiClientId, out _) || settings.ApiAudience != settings.ApiClientId)
        {
            throw new InvalidOperationException("Configure actual External ID tenant/client GUIDs and the verified v2 API audience.");
        }

        var clients = settings.PublicClientIds.Split(',', StringSplitOptions.TrimEntries);
        if (clients.Any(client => !Guid.TryParse(client, out var id) || id == Guid.Empty) ||
            clients.Select(Guid.Parse).Distinct().Count() != clients.Length ||
            clients.Any(client => Guid.Parse(client) == Guid.Parse(settings.WebClientId) ||
                Guid.Parse(client) == Guid.Parse(settings.ApiClientId)))
        {
            throw new InvalidOperationException(
                "ExternalId:PublicClientIds must contain distinct registered public-client GUIDs, excluding the browser and API.");
        }

        if (new Uri(settings.Authority).Scheme != Uri.UriSchemeHttps ||
            new Uri(settings.Issuer).Scheme != Uri.UriSchemeHttps ||
            settings.WebClientSecret.StartsWith("@Microsoft.KeyVault(", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("External ID requires HTTPS metadata and a successfully resolved browser credential.");
        }

        return settings;
    }
}

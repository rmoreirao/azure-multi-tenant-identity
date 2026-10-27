using System.Security.Claims;
using System.Text.Json.Serialization;

namespace Kanban.Web.Features.Session;

public sealed record SessionSummary(
    [property: JsonRequired] string DisplayName,
    [property: JsonRequired] string Stage)
{
    [JsonRequired] public string IdentityKey { get; init; } = "";

    public static SessionSummary FromPrincipal(ClaimsPrincipal principal, string stage)
    {
        var key = Kanban.Core.Rules.Hash($"{principal.FindFirstValue("tid")}:{principal.FindFirstValue("oid")}");
        foreach (var claim in new[] { "name", "email", "preferred_username" })
        {
            var value = principal.FindFirstValue(claim)?.Trim();
            if (!string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return new SessionSummary(value, stage) { IdentityKey = key };
            }
        }

        return new SessionSummary("Employee", stage) { IdentityKey = key };
    }
}

public sealed record AntiforgeryTokenResponse([property: JsonRequired] string Token);

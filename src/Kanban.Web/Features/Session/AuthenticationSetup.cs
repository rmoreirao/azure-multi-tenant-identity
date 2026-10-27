using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;

namespace Kanban.Web.Features.Session;

public static class AuthenticationSetup
{
    public const string BrowserScheme = "browser";
    public const string OidcScheme = "oidc";
    public const string ApplicationScheme = "application";

    public static void AddEmployeeAuthentication(this IServiceCollection services, ExternalIdSettings identity, Uri publicBaseUrl)
    {
        var publicClientIds = identity.PublicClientIds.Split(',', StringSplitOptions.TrimEntries)
            .Select(Guid.Parse).ToHashSet();
        var authentication = services.AddAuthentication(options =>
        {
            options.DefaultScheme = ApplicationScheme;
            options.DefaultChallengeScheme = ApplicationScheme;
        });
        authentication.AddPolicyScheme(ApplicationScheme, null, options =>
        {
            options.ForwardDefaultSelector = context =>
                context.Request.Path.StartsWithSegments("/mcp")
                    ? McpAuthenticationDefaults.AuthenticationScheme
                    : context.Request.Headers.ContainsKey("Authorization")
                        ? JwtBearerDefaults.AuthenticationScheme
                        : BrowserScheme;
        });
        authentication.AddMicrosoftIdentityWebApp(options =>
        {
            options.Instance = $"https://{identity.TenantName}.ciamlogin.com/";
            options.TenantId = identity.TenantId;
            options.Domain = $"{identity.TenantName}.onmicrosoft.com";
            options.ClientId = identity.WebClientId;
            options.ClientSecret = identity.WebClientSecret;
            options.Authority = identity.Authority;
            options.MetadataAddress = $"{identity.Authority}/.well-known/openid-configuration";
            options.CallbackPath = "/signin-oidc";
            options.SignedOutCallbackPath = "/signout-callback-oidc";
            options.ResponseType = "code";
            options.UsePkce = true;
            options.SaveTokens = false;
            options.MapInboundClaims = false;
            options.GetClaimsFromUserInfoEndpoint = false;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.TokenValidationParameters.ValidIssuer = identity.Issuer;
            options.TokenValidationParameters.NameClaimType = "name";
            options.Events.OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.RedirectUri = new Uri(publicBaseUrl, "/signin-oidc").AbsoluteUri;
                return Task.CompletedTask;
            };
            options.Events.OnTokenValidated = context =>
            {
                if (!HasEmployeeIdentity(context.Principal, identity.TenantId))
                {
                    context.Fail("A verified directory and immutable employee object ID are required.");
                }
                return Task.CompletedTask;
            };
        }, options =>
        {
            options.Cookie.Name = "__Host-Kanban";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        }, openIdConnectScheme: OidcScheme, cookieScheme: BrowserScheme);

        authentication.AddJwtBearer(options =>
        {
            options.Authority = identity.Authority;
            options.RequireHttpsMetadata = true;
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = identity.Issuer,
                ValidAudience = identity.ApiAudience,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                NameClaimType = "name",
                RoleClaimType = "roles"
            };
            options.Events.OnTokenValidated = context =>
            {
                if (!HasEmployeeIdentity(context.Principal, identity.TenantId) ||
                    !Guid.TryParse(context.Principal?.FindFirstValue("azp"), out var clientId) ||
                    !publicClientIds.Contains(clientId) ||
                    context.Principal?.FindFirstValue("idtyp") == "app" ||
                    !HasTaskScope(context.Principal))
                {
                    context.Fail("An employee-delegated token from an approved public client for the registered API is required.");
                }
                return Task.CompletedTask;
            };
        }).AddMcp(options =>
        {
            var resource = new Uri(publicBaseUrl, "/mcp").AbsoluteUri;
            options.ResourceMetadataUri = new Uri(publicBaseUrl, "/.well-known/oauth-protected-resource/mcp");
            options.ResourceMetadata = new()
            {
                Resource = resource,
                AuthorizationServers = { identity.Issuer },
                ScopesSupported =
                [
                    $"{resource}/tasks.read",
                    $"{resource}/tasks.write",
                    "offline_access"
                ]
            };
        });

        services.AddAuthorizationBuilder()
            .AddPolicy("delegated", policy => policy
                .AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => HasTaskScope(context.User)));
    }

    private static bool HasEmployeeIdentity(ClaimsPrincipal? principal, string tenantId) =>
        principal?.FindFirstValue("tid") == tenantId &&
        Guid.TryParse(principal.FindFirstValue("oid"), out var objectId) && objectId != Guid.Empty;

    private static bool HasTaskScope(ClaimsPrincipal? principal) =>
        principal?.FindFirstValue("scp")?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(scope => scope is "tasks.read" or "tasks.write") == true;
}

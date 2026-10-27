using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Kanban.Web.Features.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;

namespace Kanban.SafetyTests;

public sealed class IdentityBoundaryTests
{
    [Fact]
    public async Task DelegatedTokensRequireSignatureAudienceTenantEmployeeScopeAndRegisteredClient()
    {
        using var rsa = RSA.Create(2048);
        await using var factory = new CandidateFactory(rsa);
        using var client = factory.Client();
        var valid = factory.Token();
        var cases = new (string Name, string Token, HttpStatusCode Expected)[]
        {
            ("read", valid, HttpStatusCode.OK),
            ("write", factory.Token(scope: "tasks.write"), HttpStatusCode.OK),
            ("second registered client", factory.Token(clientId: CandidateFactory.SecondClient), HttpStatusCode.OK),
            ("third registered client", factory.Token(clientId: CandidateFactory.ThirdClient), HttpStatusCode.OK),
            ("wrong issuer", factory.Token(issuer: "https://untrusted.invalid/v2.0"), HttpStatusCode.Unauthorized),
            ("wrong audience", factory.Token(audience: CandidateFactory.WebClient), HttpStatusCode.Unauthorized),
            ("wrong tenant", factory.Token(tenant: Guid.NewGuid().ToString()), HttpStatusCode.Unauthorized),
            ("missing employee", factory.Token(employee: null), HttpStatusCode.Unauthorized),
            ("empty employee", factory.Token(employee: Guid.Empty.ToString()), HttpStatusCode.Unauthorized),
            ("missing client", factory.Token(clientId: null), HttpStatusCode.Unauthorized),
            ("unregistered client", factory.Token(clientId: Guid.NewGuid().ToString()), HttpStatusCode.Unauthorized),
            ("confidential browser client", factory.Token(clientId: CandidateFactory.WebClient), HttpStatusCode.Unauthorized),
            ("app-only", factory.Token(scope: null, identityType: "app"), HttpStatusCode.Unauthorized),
            ("app claiming scope", factory.Token(identityType: "app"), HttpStatusCode.Unauthorized),
            ("unrelated scope", factory.Token(scope: "tasks.read.extra"), HttpStatusCode.Unauthorized),
            ("expired", factory.Token(expired: true), HttpStatusCode.Unauthorized),
            ("tampered signature", valid[..^20] + new string('a', 20), HttpStatusCode.Unauthorized)
        };
        foreach (var scenario in cases)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", scenario.Token);
            using var session = await client.GetAsync("/api/session");
            Assert.True(session.StatusCode == scenario.Expected, $"Session boundary: {scenario.Name}.");
            using var mcp = await client.SendAsync(Initialize());
            Assert.True(mcp.StatusCode == scenario.Expected, $"MCP boundary: {scenario.Name}.");
        }
    }

    [Fact]
    public async Task BrowserCookiesRequireAntiforgeryAndCannotAuthenticateMcp()
    {
        using var rsa = RSA.Create(2048);
        await using var factory = new CandidateFactory(rsa);
        using var client = factory.Client();
        var displayCases = new (string? Name, string? Email, string? Username, string Expected)[]
        {
            ("Example Employee", "employee@example.test", "employee-alias@example.test", "Example Employee"),
            ("unknown", "employee@example.test", "employee-alias@example.test", "employee@example.test"),
            (" UNKNOWN ", null, "employee@example.test", "employee@example.test"),
            (null, "employee@example.test", null, "employee@example.test"),
            ("  ", " employee@example.test ", null, "employee@example.test"),
            (" Ren\u00E9e Example ", null, null, "Ren\u00E9e Example"),
            ("unknown", null, null, "Employee"),
            (null, null, null, "Employee")
        };
        foreach (var scenario in displayCases)
        {
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie",
                factory.BrowserCookie(scenario.Name, scenario.Email, scenario.Username));
            using var response = await client.GetAsync("/api/session");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var summary = await response.Content.ReadFromJsonAsync<SessionSummary>();
            Assert.Equal(scenario.Expected, Assert.IsType<SessionSummary>(summary).DisplayName);
        }
        client.DefaultRequestHeaders.Remove("Cookie");
        var cookie = factory.BrowserCookie();
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        using var session = await client.GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        using var mcp = await client.SendAsync(Initialize());
        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
        using var missing = await client.PostAsync("/auth/logout", null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        using var bearerBypass = await client.PostAsync("/auth/logout", null);
        Assert.Equal(HttpStatusCode.Forbidden, bearerBypass.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;

        using var antiforgery = await client.GetAsync("/api/antiforgery");
        Assert.Equal(HttpStatusCode.OK, antiforgery.StatusCode);
        var body = await antiforgery.Content.ReadFromJsonAsync<JsonElement>();
        var csrfCookie = Assert.Single(antiforgery.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("__Host-Kanban-CSRF=", StringComparison.Ordinal)).Split(';')[0];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{cookie}; {csrfCookie}");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid");
        using var invalid = await client.PostAsync("/auth/logout", null);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", body.GetProperty("token").GetString());
        using var valid = await client.PostAsync("/auth/logout", null);
        Assert.Equal(HttpStatusCode.Redirect, valid.StatusCode);
    }

    [Fact]
    public async Task AnonymousDiscoveryOriginAndReservedRoutesKeepTheirHttpBoundaries()
    {
        using var rsa = RSA.Create(2048);
        await using var factory = new CandidateFactory(rsa);
        using var client = factory.Client();
        foreach (var path in new[] { "/api/session", "/api/antiforgery" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        await using (var unhealthyFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.PostConfigure<HealthCheckServiceOptions>(options =>
                {
                    options.Registrations.Clear();
                    options.Registrations.Add(new HealthCheckRegistration(
                        "sql-and-session-keys", new FailingHealthCheck(), HealthStatus.Unhealthy, []));
                }))))
        {
            using var healthClient = unhealthyFactory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });
            using var health = await healthClient.GetAsync("/health");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
            Assert.Equal("Unhealthy", await health.Content.ReadAsStringAsync());
            var report = await unhealthyFactory.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
            Assert.IsType<InvalidOperationException>(report.Entries["sql-and-session-keys"].Exception);
        }
        using var login = await client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var location = Assert.IsType<Uri>(login.Headers.Location);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal(CandidateFactory.WebClient, query["client_id"].ToString());
        Assert.Equal("https://localhost/signin-oidc", query["redirect_uri"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.Matches("^[A-Za-z0-9_-]{43}$", query["code_challenge"].ToString());
        Assert.False(string.IsNullOrWhiteSpace(query["state"]));
        Assert.False(string.IsNullOrWhiteSpace(query["nonce"]));
        Assert.False(query.ContainsKey("client_secret"));
        using var anonymous = await client.SendAsync(Initialize());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains("oauth-protected-resource/mcp", anonymous.Headers.WwwAuthenticate.ToString());
        var metadata = await client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/mcp");
        Assert.Equal("https://localhost/mcp", metadata.GetProperty("resource").GetString());
        Assert.Equal(new[] { "https://localhost/mcp/tasks.read", "https://localhost/mcp/tasks.write", "offline_access" },
            metadata.GetProperty("scopes_supported").EnumerateArray().Select(scope => scope.GetString()).ToArray());
        foreach (var prefix in new[] { "/api", "/mcp", "/auth", "/bff", "/.well-known", "/health", "/openapi", "/contracts" })
        {
            using var response = await client.GetAsync($"{prefix}/not-implemented");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token());
        using var crossOrigin = Initialize();
        crossOrigin.Headers.Add("Origin", "https://untrusted.invalid");
        using var denied = await client.SendAsync(crossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var sameOrigin = Initialize();
        sameOrigin.Headers.Add("Origin", "https://localhost");
        using var allowed = await client.SendAsync(sameOrigin);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        var openApi = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var schemas = openApi.GetProperty("components").GetProperty("schemas");
        var summary = schemas.GetProperty("SessionSummary");
        Assert.Contains("displayName", summary.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("stage", summary.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("string", schemas.GetProperty("AntiforgeryTokenResponse").GetProperty("properties")
            .GetProperty("token").GetProperty("type").GetString());
        Assert.Equal("#/components/schemas/SessionSummary",
            openApi.GetProperty("paths").GetProperty("/api/session").GetProperty("get").GetProperty("responses")
                .GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema")
                .GetProperty("$ref").GetString());
        Assert.False(openApi.TryGetProperty("servers", out _));

        await using var transport = new HttpClientTransport(new()
        {
            Endpoint = new Uri("https://localhost/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        }, client);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var mcpClient = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await mcpClient.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(new[] { "create_task", "get_task", "identity_probe", "list_tasks", "list_teams", "move_task", "update_task" },
            tools.Select(t => t.Name).Order().ToArray());
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        var toolSchemas = JsonSerializer.SerializeToElement(new { tools = tools.Select(tool => tool.ProtocolTool) }, jsonOptions);
        Assert.Equal("object", toolSchemas.GetProperty("tools")[0].GetProperty("inputSchema").GetProperty("type").GetString());
        var probeSchema = toolSchemas.GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "identity_probe");
        Assert.Equal("boolean", probeSchema.GetProperty("outputSchema").GetProperty("properties")
            .GetProperty("authenticated").GetProperty("type").GetString());
        var probe = await mcpClient.CallToolAsync("identity_probe", cancellationToken: timeout.Token);
        var structured = Assert.IsType<JsonElement>(probe.StructuredContent);
        Assert.True(structured.GetProperty("authenticated").GetBoolean());
        Assert.Equal("kanban-product", structured.GetProperty("stage").GetString());
        Assert.True(structured.GetProperty("taskAuthorizationImplemented").GetBoolean());
        var export = Environment.GetEnvironmentVariable("KANBAN_EXPORT_CONTRACTS");
        if (export is not null)
        {
            Assert.Equal("1", export);
            var contentRoot = factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
            var directory = Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "artifacts", "contract-generation"));
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "openapi.json"), JsonSerializer.Serialize(openApi, jsonOptions));
            await File.WriteAllTextAsync(Path.Combine(directory, "mcp-tools.json"), JsonSerializer.Serialize(toolSchemas, jsonOptions));
        }
    }

    private static HttpRequestMessage Initialize()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18", capabilities = new { },
                    clientInfo = new { name = "local-safety-check", version = "1" }
                }
            })
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        return request;
    }

    internal sealed class CandidateFactory(RSA rsa, Action<IServiceCollection>? configureServices = null,
        IReadOnlyDictionary<string, string>? settings = null) : WebApplicationFactory<Program>
    {
        public const string Tenant = "11111111-1111-1111-1111-111111111111";
        public const string ApiClient = "22222222-2222-2222-2222-222222222222";
        public const string WebClient = "33333333-3333-3333-3333-333333333333";
        public const string PublicClient = "44444444-4444-4444-4444-444444444444";
        public const string SecondClient = "55555555-5555-5555-5555-555555555555";
        public const string ThirdClient = "66666666-6666-6666-6666-666666666666";
        private const string Employee = "77777777-7777-7777-7777-777777777777";
        private const string Issuer = $"https://{Tenant}.ciamlogin.com/{Tenant}/v2.0";
        private readonly RsaSecurityKey signingKey = new(rsa) { KeyId = "local-test-key" };

        public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false
        });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            foreach (var setting in new Dictionary<string, string>
            {
                ["Application:PublicBaseUrl"] = "https://localhost",
                ["ExternalId:TenantName"] = "test-directory",
                ["ExternalId:TenantId"] = Tenant,
                ["ExternalId:Authority"] = Issuer,
                ["ExternalId:Issuer"] = Issuer,
                ["ExternalId:WebClientId"] = WebClient,
                ["ExternalId:WebClientSecret"] = "local-test-placeholder-not-a-credential",
                ["ExternalId:ApiClientId"] = ApiClient,
                ["ExternalId:ApiAudience"] = ApiClient,
                ["ExternalId:PublicClientIds"] = $"{PublicClient},{SecondClient},{ThirdClient}",
                ["Azure:HostingTenantId"] = Tenant,
                ["Azure:UseManagedIdentity"] = "true",
                ["Azure:DataProtectionKeyUri"] = "https://local-test.vault.azure.net/keys/test",
                ["Database:ConnectionString"] = "Server=local-test.database.windows.net;Database=kanban;Encrypt=True;TrustServerCertificate=False",
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = ""
            })
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
            if (settings is not null)
                foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<KeyManagementOptions>(options =>
                {
                    options.XmlRepository = new MemoryKeyRepository();
                    options.XmlEncryptor = null;
                });
                var configuration = new OpenIdConnectConfiguration
                {
                    Issuer = Issuer,
                    AuthorizationEndpoint = $"{Issuer}/authorize",
                    TokenEndpoint = $"{Issuer}/token",
                    EndSessionEndpoint = $"{Issuer}/logout"
                };
                configuration.SigningKeys.Add(signingKey);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
                    options => options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration));
                services.PostConfigure<OpenIdConnectOptions>(AuthenticationSetup.OidcScheme,
                    options => options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration));
                configureServices?.Invoke(services);
            });
        }

        public string Token(string? tenant = Tenant, string? employee = Employee,
            string? clientId = PublicClient, string? scope = "tasks.read",
            string issuer = Issuer, string audience = ApiClient, string? identityType = null, bool expired = false)
        {
            var claims = new List<Claim>();
            foreach (var (name, value) in new[] { ("tid", tenant), ("oid", employee), ("azp", clientId), ("scp", scope), ("idtyp", identityType) })
            {
                if (value is not null) claims.Add(new Claim(name, value));
            }
            return new JwtSecurityTokenHandler().CreateEncodedJwt(new SecurityTokenDescriptor
            {
                Issuer = issuer, Audience = audience, Subject = new ClaimsIdentity(claims),
                IssuedAt = DateTime.UtcNow.AddHours(-1), NotBefore = DateTime.UtcNow.AddHours(-1),
                Expires = expired ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddMinutes(10),
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
            });
        }

        public string BrowserCookie(string? name = null, string? email = null, string? username = null, string? employee = null)
        {
            var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(AuthenticationSetup.BrowserScheme);
            var claims = new List<Claim> { new("tid", Tenant), new("oid", employee ?? Employee) };
            foreach (var (type, value) in new[] { ("name", name), ("email", email), ("preferred_username", username) })
            {
                if (value is not null) claims.Add(new Claim(type, value));
            }
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationSetup.BrowserScheme));
            var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10)
            }, AuthenticationSetup.BrowserScheme);
            return $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}";
        }
    }

    private sealed class MemoryKeyRepository : IXmlRepository
    {
        private readonly List<XElement> elements = [];
        public IReadOnlyCollection<XElement> GetAllElements() => elements.Select(element => new XElement(element)).ToArray();
        public void StoreElement(XElement element, string friendlyName) => elements.Add(new XElement(element));
    }

    private sealed class FailingHealthCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Private connection diagnostic must not appear in the HTTP response.");
    }
}

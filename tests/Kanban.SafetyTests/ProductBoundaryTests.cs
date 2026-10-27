using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Kanban.Core;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Kanban.SafetyTests;

public sealed class ProductBoundaryTests
{
    [Fact]
    public async Task BrowserManagementRejectsBearerCookieMixingUnknownFieldsAndMissingCsrf()
    {
        using var rsa = RSA.Create(2048);
        await using var factory = new IdentityBoundaryTests.CandidateFactory(rsa);
        using var client = factory.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token());
        using var bearer = await client.PostAsJsonAsync("/api/v1/companies", new { name = "Synthetic" });
        Assert.Equal(HttpStatusCode.Unauthorized, bearer.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", factory.BrowserCookie(email: "employee@example.test"));
        foreach (var header in new[] { factory.Token(), "invalid" })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", header);
            using var mixed = await client.PostAsJsonAsync("/api/v1/companies", new { name = "Synthetic" });
            Assert.Equal(HttpStatusCode.Forbidden, mixed.StatusCode);
        }
        client.DefaultRequestHeaders.Authorization = null;
        using var missing = await client.PostAsJsonAsync("/api/v1/companies", new { name = "Synthetic" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        await AddCsrf(client);
        using var unknown = await client.PostAsJsonAsync("/api/v1/companies", new { name = "Synthetic", administrator = true });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var returnRoute = await client.GetAsync("/auth/login?returnTo=https%3A%2F%2Funtrusted.invalid");
        Assert.Equal(HttpStatusCode.BadRequest, returnRoute.StatusCode);
    }

    [Fact]
    public async Task CompanyCreationThrottlingReturnsAnExplicitRetryDelay()
    {
        using var rsa = RSA.Create(2048);
        await using var factory = new IdentityBoundaryTests.CandidateFactory(rsa,
            settings: new Dictionary<string, string> { ["Application:CreationPermitLimit"] = "1" });
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add("Cookie", factory.BrowserCookie(email: "employee@example.test"));
        await AddCsrf(client);
        using var first = await client.PostAsJsonAsync("/api/v1/companies", new { name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        using var next = await client.PostAsJsonAsync("/api/v1/companies", new { name = "" });
        Assert.Equal(HttpStatusCode.TooManyRequests, next.StatusCode);
        Assert.True(next.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [SqlFact]
    public async Task BrowserCreationIsDurableAndDoesNotImplicitlyCreateTeamAccess()
    {
        await using var f = await SqlScenario.Create();
        using var rsa = RSA.Create(2048);
        await using var factory = new IdentityBoundaryTests.CandidateFactory(rsa, services => services.AddSingleton(f.Service));
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add("Cookie", factory.BrowserCookie(f.Owner.Name, f.Owner.Email, employee: f.Owner.ObjectId.ToString()));
        await AddCsrf(client);
        var key = Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        try
        {
            using var created = await client.PostAsJsonAsync("/api/v1/companies", new { name = f.Company.Name });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var company = (await created.Content.ReadFromJsonAsync<CompanyView>())!;
            Assert.True(company.Administrator);
            Assert.NotNull(created.Headers.ETag);
            using var again = await client.PostAsJsonAsync("/api/v1/companies", new { name = f.Company.Name });
            Assert.Equal(company.Id, (await again.Content.ReadFromJsonAsync<CompanyView>())!.Id);
            using var teams = await client.GetAsync($"/api/v1/companies/{company.Id}/teams");
            var page = await teams.Content.ReadFromJsonAsync<Page<TeamView>>();
            Assert.Empty(page!.Items);
            using var setup = await client.GetAsync($"/api/v1/companies/{company.Id}/setup");
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        }
        finally { await f.TrackCompanyKey(key); }
    }

    [SqlFact]
    public async Task McpAndRestShareTasksVersionsConnectionScopesAndRevocation()
    {
        await using var f = await SqlScenario.Create();
        await f.Service.ChangeApproval(f.Owner, f.Company.Id, f.ClientId, true, "*", default);
        var grant = await f.Service.ChangeConnection(f.Contributor, f.Company.Id, f.ClientId, new([f.Team.Id], true, true), "*", default);
        using var rsa = RSA.Create(2048);
        await using var factory = new IdentityBoundaryTests.CandidateFactory(rsa, services => services.AddSingleton(f.Service),
            new Dictionary<string, string> { ["ExternalId:PublicClientIds"] = f.ClientId.ToString() });
        using var client = factory.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            factory.Token(employee: f.Contributor.ObjectId.ToString(), clientId: f.ClientId.ToString(), scope: "tasks.write"));
        await using var transport = new HttpClientTransport(new()
        {
            Endpoint = new Uri("https://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp
        }, client);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var created = await mcp.CallToolAsync("create_task", new Dictionary<string, object?>
        {
            ["companyId"] = f.Company.Id, ["teamId"] = f.Team.Id, ["title"] = "MCP synthetic task",
            ["idempotencyKey"] = Guid.NewGuid().ToString("N")
        }, cancellationToken: timeout.Token);
        Assert.False(created.IsError is true);
        var content = Assert.IsType<JsonElement>(created.StructuredContent);
        var id = content.GetProperty("taskId").GetGuid();
        var path = $"/api/v1/companies/{f.Company.Id}/teams/{f.Team.Id}/tasks/{id}";
        using var read = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var task = (await read.Content.ReadFromJsonAsync<TaskView>())!;
        Assert.Equal("MCP synthetic task", task.Title);
        Assert.Equal($"\"{task.Version}\"", read.Headers.ETag?.ToString());
        var moved = await mcp.CallToolAsync("move_task", new Dictionary<string, object?>
        {
            ["companyId"] = f.Company.Id, ["teamId"] = f.Team.Id, ["taskId"] = id,
            ["expectedVersion"] = task.Version, ["targetColumn"] = "done"
        }, cancellationToken: timeout.Token);
        Assert.False(moved.IsError is true);
        var staleRequest = new HttpRequestMessage(HttpMethod.Post, $"{path}/moves") { Content = JsonContent.Create(new { targetColumn = "todo" }) };
        staleRequest.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{task.Version}\""));
        using var stale = await client.SendAsync(staleRequest);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        await f.Service.RevokeConnection(f.Contributor, f.Company.Id, f.ClientId, grant.Version, default);
        using var denied = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var mcpDenied = await mcp.CallToolAsync("get_task", new Dictionary<string, object?>
        {
            ["companyId"] = f.Company.Id, ["teamId"] = f.Team.Id, ["taskId"] = id
        }, cancellationToken: timeout.Token);
        Assert.True(mcpDenied.IsError);
        Assert.Contains("CONNECTION_REQUIRED", JsonSerializer.Serialize(mcpDenied.Content), StringComparison.Ordinal);
    }

    private static async Task AddCsrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/antiforgery");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<JsonElement>();
        var csrf = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-Kanban-CSRF=", StringComparison.Ordinal)).Split(';')[0];
        var cookie = string.Join("; ", client.DefaultRequestHeaders.GetValues("Cookie"));
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{cookie}; {csrf}");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.GetProperty("token").GetString());
    }
}

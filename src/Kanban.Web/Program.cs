using System.Security.Claims;
using Azure.Core;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Kanban.Infrastructure.Operations;
using Kanban.Infrastructure.Persistence;
using Kanban.Infrastructure.Security;
using Kanban.Web.Features.Mcp;
using Kanban.Web.Features.Session;
using Kanban.Web.Features.Directory;
using Kanban.Core;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.AspNetCore;
using OpenTelemetry.Instrumentation.AspNetCore;

var operation = args.FirstOrDefault();
var isMigration = operation == "migrate";
var isOperator = isMigration || operation is "create-company" or "appoint-administrator";
var builder = WebApplication.CreateBuilder(isOperator ? args[1..] : args);
if (isMigration)
{
    var clientId = builder.Configuration["runtime-client-id"];
    var principalName = builder.Configuration["runtime-principal-name"];
    if (!Guid.TryParse(clientId, out var id) || string.IsNullOrWhiteSpace(principalName))
    {
        throw new InvalidOperationException("Migration requires --runtime-client-id (the managed identity application ID) and --runtime-principal-name.");
    }
    await DatabaseOperator.MigrateAsync(builder.Configuration, id, principalName, CancellationToken.None);
    return;
}
if (isOperator && operation is not null)
{
    await ProductOperator.Run(operation, builder.Configuration, CancellationToken.None);
    return;
}

var identity = ExternalIdSettings.Load(builder.Configuration);
var azure = AzureSettings.Load(builder.Configuration);
var database = DatabaseSettings.Load(builder.Configuration);
var publicUrlText = builder.Configuration["Application:PublicBaseUrl"];
if (!Uri.TryCreate(publicUrlText, UriKind.Absolute, out var publicUrl) ||
    publicUrl.Scheme != Uri.UriSchemeHttps ||
    !string.IsNullOrEmpty(publicUrl.Query) ||
    !string.IsNullOrEmpty(publicUrl.Fragment) ||
    !string.IsNullOrEmpty(publicUrl.UserInfo) ||
    publicUrl.AbsolutePath != "/")
{
    throw new InvalidOperationException("Application:PublicBaseUrl must be the exact HTTPS application origin.");
}
if (!Uri.TryCreate(azure.DataProtectionKeyUri, UriKind.Absolute, out var keyUri) ||
    keyUri.Scheme != Uri.UriSchemeHttps ||
    !keyUri.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Configure the actual Azure Key Vault data-protection key URI.");
}
builder.Configuration["AllowedHosts"] = publicUrl.Host;
var credential = azure.CreateCredential();
builder.Services.AddSingleton<TokenCredential>(credential);
builder.Services.AddSingleton(azure);
builder.Services.AddSingleton(new SqlConnectionFactory(database, credential));
builder.Services.AddDbContext<KanbanDbContext>((services, options) =>
    options.UseSqlServer(services.GetRequiredService<SqlConnectionFactory>().CreateConnection(), contextOwnsConnection: true));
builder.Services.AddDataProtection()
    .SetApplicationName("kanban-poc")
    .PersistKeysToDbContext<KanbanDbContext>()
    .ProtectKeysWithAzureKeyVault(keyUri, credential);
builder.Services.AddEmployeeAuthentication(identity, publicUrl);
builder.Services.AddAuthorizationBuilder().AddPolicy("browser-operation", policy =>
    policy.AddAuthenticationSchemes(AuthenticationSetup.BrowserScheme).RequireAuthenticatedUser());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new ProductOptions(
    builder.Configuration.GetValue("Application:SelfServiceEnabled", false),
    identity.PublicClientIds.Split(',', StringSplitOptions.TrimEntries).Select(id =>
        new ClientRegistration { Id = Guid.Parse(id), Name = $"Configured public client {id}" }).ToArray()));
builder.Services.AddScoped<ICompanyStore, CompanyStore>();
builder.Services.AddSingleton<ICursorCodec, CursorCodec>();
builder.Services.AddScoped<KanbanService>();
builder.Services.AddProductTraffic(builder.Configuration);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65536);
builder.Services.AddHttpContextAccessor();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-Kanban-CSRF";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
});
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Kanban PoC";
    document.Info.Description = "Browser management and employee-delegated task operations. IDs select context, not permissions.";
    document.Servers = [];
    return Task.CompletedTask;
}));
builder.Services.AddHealthChecks().AddCheck<DependencyHealthCheck>("sql-and-session-keys");
builder.Services.AddMcpServer()
    .WithTools<IdentityProbeTools>()
    .WithTools<TaskTools>()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless);
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        options.Filter = context => !context.Request.Path.StartsWithSegments("/signin-oidc") &&
            !context.Request.Path.StartsWithSegments("/signout-callback-oidc") &&
            !context.Request.Path.StartsWithSegments("/invitations"));
    builder.Services.AddOpenTelemetry().UseAzureMonitor();
}

var app = builder.Build();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (DomainException ex)
    {
        app.Logger.LogWarning("Rejected product operation {Code} with status {Status}.", ex.Code, ex.Status);
        await Results.Problem(statusCode: ex.Status, title: ex.Message, extensions: new Dictionary<string, object?>
        {
            ["code"] = ex.Code, ["correlationId"] = context.TraceIdentifier
        }).ExecuteAsync(context);
    }
});
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self'; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/mcp") &&
        context.Request.Headers.TryGetValue("Origin", out var origin) &&
        origin.ToString().TrimEnd('/') != publicUrl.GetLeftPart(UriPartial.Authority))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});
app.UseDefaultFiles();
var staticFiles = new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = context.Context.Request.Path.StartsWithSegments("/assets")
            ? "public, max-age=31536000, immutable"
            : context.File.Name == "release.json" ? "no-store" : "no-cache";
    }
};
app.UseStaticFiles(staticFiles);
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.GetEndpoint()?.Metadata.GetMetadata<BrowserOperation>() is not null &&
        context.Request.Headers.ContainsKey("Authorization"))
    {
        await Results.Problem(statusCode: 403, title: "Use a browser session without a bearer header.").ExecuteAsync(context);
        return;
    }
    if (!HttpMethods.IsGet(context.Request.Method) &&
        !HttpMethods.IsHead(context.Request.Method) &&
        !HttpMethods.IsOptions(context.Request.Method) &&
        context.User.Identity?.IsAuthenticated == true &&
        !context.Request.Headers.ContainsKey("Authorization"))
    {
        try
        {
            await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            app.Logger.LogWarning("Rejected browser mutation with invalid antiforgery proof.");
            await Results.Problem(statusCode: 400, title: "Browser request verification failed. Refresh and retry.")
                .ExecuteAsync(context);
            return;
        }
    }
    await next();
});

app.MapHealthChecks("/health");
app.MapOpenApi("/openapi/v1.json");
app.MapGet("/auth/login", (string? returnTo) => Results.Challenge(
    new AuthenticationProperties { RedirectUri = returnTo switch
    {
        null => "/", "/invitations" => "/invitations",
        _ => throw new DomainException(400, "INVALID_RETURN_ROUTE", "The sign-in return route is not allowed.")
    } }, [AuthenticationSetup.OidcScheme]))
    .WithName("StartBrowserSignIn")
    .Produces(StatusCodes.Status302Found);
app.MapPost("/auth/logout", () => Results.SignOut(
    new AuthenticationProperties { RedirectUri = "/" },
    [AuthenticationSetup.BrowserScheme, AuthenticationSetup.OidcScheme]))
    .WithName("SignOutBrowser")
    .Produces(StatusCodes.Status302Found)
    .Produces(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .RequireAuthorization("browser-operation").WithMetadata(new BrowserOperation());
app.MapGet("/api/session", (ClaimsPrincipal user, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return TypedResults.Ok(SessionSummary.FromPrincipal(user, "identity-feasibility"));
}).WithName("GetBrowserSession").Produces(StatusCodes.Status401Unauthorized).RequireAuthorization();
app.MapGet("/api/antiforgery", (IAntiforgery antiforgery, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var token = antiforgery.GetAndStoreTokens(context).RequestToken
        ?? throw new InvalidOperationException("Antiforgery did not issue a browser request token.");
    return TypedResults.Ok(new AntiforgeryTokenResponse(token));
}).WithName("GetAntiforgeryToken").Produces(StatusCodes.Status401Unauthorized).RequireAuthorization();
app.MapMcp("/mcp").RequireAuthorization("delegated");
app.MapProduct();
foreach (var prefix in new[] { "/api", "/mcp", "/auth", "/bff", "/.well-known", "/health", "/openapi", "/contracts" })
{
    app.MapFallback($"{prefix}/{{**path}}", () => Results.Problem(statusCode: 404, title: "Endpoint not found."))
        .ExcludeFromDescription();
}
app.MapFallbackToFile("index.html", staticFiles);
app.Run();

public partial class Program;

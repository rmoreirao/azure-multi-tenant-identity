using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Kanban.Web.Features.Directory;

public static class ProductTraffic
{
    public static void AddProductTraffic(this IServiceCollection services, IConfiguration configuration)
    {
        int Setting(string name, int fallback, int maximum)
        {
            var value = configuration.GetValue($"Application:{name}", fallback);
            if (value < 1 || value > maximum) throw new InvalidOperationException($"Application:{name} is outside its supported range.");
            return value;
        }
        var requests = Setting("RequestPermitLimit", 120, 10000);
        var concurrency = Setting("RequestConcurrencyLimit", 4, 100);
        var creates = Setting("CreationPermitLimit", 10, 1000);
        var window = TimeSpan.FromSeconds(Setting("RateWindowSeconds", 60, 3600));
        static string Subject(HttpContext context) => $"{context.User.FindFirstValue("tid")}:{context.User.FindFirstValue("oid")}";
        static bool ProductRequest(HttpContext context) => context.Request.Path.StartsWithSegments("/api/v1") ||
            context.Request.Path.StartsWithSegments("/mcp");
        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context => ProductRequest(context)
                    ? RateLimitPartition.GetConcurrencyLimiter(Subject(context), _ => new() { PermitLimit = concurrency, QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("unlimited")),
                PartitionedRateLimiter.Create<HttpContext, string>(context => ProductRequest(context)
                    ? RateLimitPartition.GetFixedWindowLimiter(Subject(context), _ => new()
                    {
                        PermitLimit = requests, Window = window, QueueLimit = 0, AutoReplenishment = true
                    })
                    : RateLimitPartition.GetNoLimiter("unlimited")));
            options.AddPolicy("company-creation", context => RateLimitPartition.GetFixedWindowLimiter(Subject(context), _ => new()
            {
                PermitLimit = creates, Window = window, QueueLimit = 0, AutoReplenishment = true
            }));
            options.OnRejected = async (rejection, ct) =>
            {
                var seconds = rejection.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
                    ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)) : 1;
                rejection.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Results.Problem(statusCode: 429, title: "Too many concurrent or recent requests. Retry after the indicated delay.",
                    extensions: new Dictionary<string, object?> { ["code"] = "RATE_LIMITED" }).ExecuteAsync(rejection.HttpContext);
            };
        });
    }
}

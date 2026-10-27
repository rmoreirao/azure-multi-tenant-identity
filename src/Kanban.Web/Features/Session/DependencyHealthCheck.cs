using Kanban.Infrastructure.Persistence;
using Kanban.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Kanban.Web.Features.Session;

public sealed class DependencyHealthCheck(
    KanbanDbContext database, IDataProtectionProvider protection, AzureSettings azure) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await database.Database.ExecuteSqlRawAsync("SELECT 1;", cancellationToken);
        if ((await database.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            return HealthCheckResult.Unhealthy("The reviewed product schema has not been applied.");
        var tenantReads = string.Join(" UNION ALL ", BusinessSecurityV1.Tables.Select(table =>
            $"SELECT TOP(1) [CompanyId] AS [Value] FROM [app].[{table}]"));
        if (await database.Database.SqlQueryRaw<Guid>(tenantReads).AnyAsync(cancellationToken))
            return HealthCheckResult.Unhealthy("The SQL session has unexpected company visibility.");
        if (azure.UseManagedIdentity && await database.Database.SqlQueryRaw<int>("""
            SELECT CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CONTROL')=1
                OR HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CREATE TABLE')=1
                OR HAS_PERMS_BY_NAME('bootstrap.Employee','OBJECT','SELECT')=1
                OR HAS_PERMS_BY_NAME('bootstrap.CompanyCreationReceipt','OBJECT','SELECT')=1
                OR HAS_PERMS_BY_NAME('kanban_directory','USER','IMPERSONATE')=1
                THEN 1 ELSE 0 END AS [Value]
            """).SingleAsync(cancellationToken) != 0)
            return HealthCheckResult.Unhealthy("The hosted SQL identity has unexpected privileges.");

        var protector = protection.CreateProtector("kanban-dependency-health");
        if (protector.Unprotect(protector.Protect("dependency-check")) != "dependency-check")
        {
            return HealthCheckResult.Unhealthy("Persistent session protection is unavailable.");
        }

        return HealthCheckResult.Healthy();
    }
}

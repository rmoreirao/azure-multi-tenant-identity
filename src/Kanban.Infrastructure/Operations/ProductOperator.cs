using System.Text.Json;
using Azure.Core;
using Kanban.Core;
using Kanban.Infrastructure.Persistence;
using Kanban.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kanban.Infrastructure.Operations;

public static class ProductOperator
{
    private static Guid Id(IConfiguration config, string key) =>
        Guid.TryParse(config[key], out var value) && value != Guid.Empty ? value
            : throw new InvalidOperationException($"--{key} must be an immutable GUID.");

    public static async Task Run(string operation, IConfiguration configuration, CancellationToken ct)
    {
        var settings = DatabaseSettings.Load(configuration);
        var target = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(settings.ConnectionString);
        if (target.DataSource != "tcp:sql-kanban-poc-2rza5wd265s3g.database.windows.net,1433")
            throw new InvalidOperationException("The product operator command requires the approved SQL target.");
        var azure = AzureSettings.Load(configuration);
        var credential = azure.CreateCredential(operatorCommand: true);
        var access = await credential.GetTokenAsync(new TokenRequestContext(["https://database.windows.net/.default"]), ct);
        var payload = access.Token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
        var operatorId = claims.RootElement.GetProperty("oid").GetGuid();
        if (claims.RootElement.GetProperty("tid").GetString() != azure.HostingTenantId || operatorId == Guid.Empty)
            throw new InvalidOperationException("The operator credential belongs to another directory.");
        var factory = new SqlConnectionFactory(settings, credential);
        if (operation == "create-company")
        {
            var directoryId = Id(configuration, "directory-id");
            if (directoryId.ToString() != configuration["ExternalId:TenantId"])
                throw new InvalidOperationException("Use an existing employee from the accepted External ID directory.");
            var key = Rules.Key(configuration["idempotency-key"] ?? "");
            var caller = new Caller(directoryId, Id(configuration, "employee-object-id"),
                Rules.Text(configuration["employee-name"], 1, 256), Rules.Address(configuration["employee-email"]),
                null, true, true, $"operator:{operatorId}:{key[..Math.Min(key.Length, 36)]}");
            var name = Rules.Text(configuration["company-name"], 1, 100);
            var store = new CompanyStore(factory, NullLogger<CompanyStore>.Instance);
            var companyId = await store.CreateCompany(caller, name, key, Rules.Hash(name), true, ct);
            await OperatorEvent(factory, companyId, null, operatorId, "operator.company-created", ct);
            Console.WriteLine($"Company created or reconciled: {companyId}. Initial administrator explicitly appointed.");
        }
        else if (operation == "appoint-administrator")
        {
            var companyId = Id(configuration, "company-id");
            await OperatorEvent(factory, companyId, Id(configuration, "membership-id"), operatorId, "operator.administrator-appointed", ct);
            Console.WriteLine($"Administrator appointment completed for company {companyId}. No team-content access was granted.");
        }
        else throw new InvalidOperationException("Unknown product operator command.");
    }

    private static async Task OperatorEvent(SqlConnectionFactory factory, Guid companyId, Guid? membershipId,
        Guid operatorId, string action, CancellationToken ct)
    {
        await using var connection = factory.CreateConnection();
        await connection.OpenAsync(ct);
        await using var context = new KanbanDbContext(new DbContextOptionsBuilder<KanbanDbContext>().UseSqlServer(connection).Options);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        try
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_set_session_context @key=N'CompanyId',@value={companyId};", ct);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT [Id] FROM [app].[Company] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={companyId} AND [Active]=1;", ct);
            var data = new DataSession(context, companyId);
            Rules.Require(await data.First(data.Query<Company>().Where(c => c.Id == companyId && c.Active), ct) is not null,
                404, "NOT_FOUND", "The company is not available.");
            if (membershipId is Guid id)
            {
                var member = await data.First(data.Query<Membership>().Where(m => m.Id == id), ct)
                    ?? throw new DomainException(404, "NOT_FOUND", "The membership is not available.");
                if (!member.Active) member.Epoch = checked(member.Epoch + 1);
                member.Active = true;
                member.Administrator = true;
            }
            if (membershipId is not null || await data.First(data.Query<AuditEvent>().Where(a =>
                a.Action == action && a.ObjectId == companyId && a.OperatorObjectId == operatorId), ct) is null)
                data.Add(new AuditEvent
                {
                    CompanyId = companyId, ActorId = null, OperatorObjectId = operatorId,
                    ObjectId = membershipId ?? companyId, Action = action,
                    CorrelationId = $"operator:{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow
                });
            await data.Save(ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            await transaction.DisposeAsync();
            try
            {
                await using var reset = connection.CreateCommand();
                reset.CommandText = "EXEC sys.sp_set_session_context @key=N'CompanyId',@value=NULL;";
                await reset.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Microsoft.Data.SqlClient.SqlException)
            {
                Microsoft.Data.SqlClient.SqlConnection.ClearPool(connection);
                throw;
            }
        }
    }
}

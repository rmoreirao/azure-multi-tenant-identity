using Kanban.Core;
using Kanban.Infrastructure.Persistence;
using Kanban.Infrastructure.Security;
using Kanban.Web.Features.Session;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Kanban.SafetyTests;

public sealed class IsolationSafetyTests
{
    [SqlFact]
    public async Task TaskPagesEnforceOneHundredAcrossAllColumnsAndAdvanceWithoutDuplicates()
    {
        await using var f = await SqlScenario.Create();
        await f.Store.InCompany(f.Owner, f.Company.Id, true, async (data, member) =>
        {
            var board = await data.First(data.Query<Board>().Where(b => b.TeamId == f.Team.Id), default);
            for (var i = 0; i < 101; i++)
                data.Add(new WorkItem
                {
                    CompanyId = f.Company.Id, TeamId = f.Team.Id, BoardId = board!.Id,
                    CreatedBy = member.Id, Title = $"Bounded fixture {i}",
                    CreatedAt = f.Clock.Current.AddSeconds(-i), UpdatedAt = f.Clock.Current,
                    Status = new[] { "todo", "inProgress", "done" }[i % 3]
                });
            await data.Save(default);
            return true;
        }, default);
        var page = await f.Service.Tasks(f.Owner, f.Company.Id, f.Team.Id, new(), null, 100, default);
        Assert.Equal(100, page.Items.Count);
        Assert.Equal(3, page.Items.Select(t => t.Status).Distinct().Count());
        var next = await f.Service.Tasks(f.Owner, f.Company.Id, f.Team.Id, new(), page.NextCursor, 100, default);
        Assert.Single(next.Items);
        Assert.DoesNotContain(next.Items[0].Id, page.Items.Select(t => t.Id));
        Assert.Null(next.NextCursor);
        await SqlScenario.Denied(400, () => f.Service.Tasks(f.Owner, f.Company.Id, f.Team.Id, new(), null, 101, default));
    }

    [SqlFact]
    public async Task CompanyOwnershipAppliesToIdsAssignmentsAndCursors()
    {
        await using var f = await SqlScenario.Create();
        var other = await f.CreateCompany(f.Contributor);
        await SqlScenario.Denied(404, () => f.Service.Board(f.Owner, other.Id, f.Team.Id, default));
        await SqlScenario.Denied(404, () => f.Service.Board(f.Contributor, other.Id, f.Team.Id, default));
        var task = await f.Task();
        await SqlScenario.Denied(404, () => f.Service.GetTask(f.Contributor, other.Id, f.Team.Id, task.TaskId, default));
        await SqlScenario.Denied(400, () => f.Service.CreateTask(f.Contributor, f.Company.Id, f.Team.Id,
            new("Wrong company", "", other.MembershipId), Guid.NewGuid().ToString("N"), default));
        await f.Task();
        var first = await f.Service.Tasks(f.Owner, f.Company.Id, f.Team.Id, new(), null, 1, default);
        Assert.NotNull(first.NextCursor);
        await SqlScenario.Denied(400, () => f.Service.Tasks(f.Contributor, f.Company.Id, f.Team.Id, new(), first.NextCursor, 1, default));
        await SqlScenario.Denied(400, () => f.Service.Tasks(f.Owner, f.Company.Id, f.Team.Id, new("done"), first.NextCursor, 1, default));
    }

    [SqlFact]
    public async Task SqlRuntimePermissionsFilterReadsBlockWritesAndDenyGlobalDirectory()
    {
        await using var f = await SqlScenario.Create();
        var other = await f.CreateCompany(f.Owner);
        await using var connection = await f.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXECUTE AS USER = '{SqlScenario.Runtime}';";
        await command.ExecuteNonQueryAsync();
        try
        {
            command.CommandText = "SELECT COUNT(*) FROM app.Company;";
            Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
            await using var database = new KanbanDbContext(new DbContextOptionsBuilder<KanbanDbContext>()
                .UseSqlServer(connection).Options);
            var health = new DependencyHealthCheck(database, new EphemeralDataProtectionProvider(),
                new AzureSettings { UseManagedIdentity = true });
            Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new())).Status);
            command.CommandText = "SELECT COUNT(*) FROM bootstrap.Employee;";
            var denied = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
            Assert.Equal(229, denied.Number);
            command.CommandText = "EXEC sys.sp_set_session_context @key=N'CompanyId',@value=@id; SELECT COUNT(*) FROM app.Company;";
            command.Parameters.AddWithValue("@id", f.Company.Id);
            Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
            Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync(new())).Status);
            command.CommandText = "INSERT app.CompanyGroup (CompanyId,Id,Name,Revision) VALUES (@other,NEWID(),N'Blocked',0);";
            command.Parameters.AddWithValue("@other", other.Id);
            var blocked = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(33504, blocked.Number);
            command.CommandText = "EXECUTE AS USER = 'kanban_directory';";
            await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        }
        finally
        {
            command.CommandText = "REVERT; EXEC sys.sp_set_session_context @key=N'CompanyId',@value=NULL;";
            await command.ExecuteNonQueryAsync();
        }
    }

    [SqlFact]
    public async Task FailedAndCancelledOperationsDoNotLeakCompanyContext()
    {
        await using var f = await SqlScenario.Create();
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Store.InCompany<int>(f.Owner, f.Company.Id, true,
            (_, _) => throw new OperationCanceledException(), default));
        var unavailable = await SqlScenario.Denied(503, () => f.Store.InCompany(f.Owner, f.Company.Id, true,
            async (_, _) =>
            {
                await using var failure = await f.Open();
                await using var fault = failure.CreateCommand();
                fault.CommandText = "THROW 51003, 'Synthetic directory busy.', 1;";
                await fault.ExecuteNonQueryAsync();
                return 0;
            }, default));
        Assert.Equal("DEPENDENCY_UNAVAILABLE", unavailable.Code);
        await using var connection = await f.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SESSION_CONTEXT(N'CompanyId');";
        Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync());
        var other = await f.CreateCompany(f.Owner);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            f.Service.Company(f.Owner, i % 2 == 0 ? f.Company.Id : other.Id, default)));
        Assert.Equal(4, results.Count(c => c.Id == f.Company.Id));
        Assert.Equal(4, results.Count(c => c.Id == other.Id));
    }
}

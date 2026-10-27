using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Kanban.Core;
using Microsoft.Data.SqlClient;

namespace Kanban.SafetyTests;

internal static class HostedRuntimeProbe
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--cleanup-hosted-runtime-probe", var manifest])
        {
            await SqlScenario.CleanupHosted(manifest);
            Console.WriteLine("Scoped hosted fixture cleanup completed.");
            return 0;
        }
        if (args is not ["--hosted-runtime-probe", var run, var expectedRelease] ||
            !Guid.TryParseExact(run, "N", out var runId))
        {
            Console.Error.WriteLine("Use an explicit hosted probe run or scoped fixture cleanup command.");
            return 64;
        }

        var checks = new List<string>();
        var status = "running";
        string? failure = null;
        var report = Path.Combine(AppContext.BaseDirectory, "probe-results.json");
        async Task Evidence()
        {
            await File.WriteAllTextAsync(report + ".tmp", JsonSerializer.Serialize(new
            {
                runId = run, releaseId = expectedRelease, status, failure, checks,
                recordedAt = DateTimeOffset.UtcNow, runtime = RuntimeInformation.FrameworkDescription,
                credential = "system-assigned managed identity",
                credentialVerified = checks.Contains("actual-runtime-principal-restricted-permissions-and-unscoped-rls"),
                evidenceKind = "hosted-managed-identity-component",
                syntheticCallers = true, testClock = true, ephemeralCursorKeys = true,
                providerAuthenticationExercised = false, httpOrMcpTransportExercised = false,
                browserAccepted = false, nativeClientsAccepted = false, productAccepted = false
            }));
            File.Move(report + ".tmp", report, overwrite: true);
        }
        await Evidence();
        try
        {
            if (!OperatingSystem.IsLinux() ||
                Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") != SqlScenario.Runtime)
                throw new InvalidOperationException("This probe can run only inside the approved application container.");
            const string deployed = "/home/site/wwwroot";
            using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(deployed, "wwwroot", "release.json")));
            Assert.Equal(expectedRelease, marker.RootElement.GetProperty("releaseId").GetString());
            foreach (var file in new[] { "Kanban.Core.dll", "Kanban.Infrastructure.dll", "Kanban.Web.dll" })
            {
                var tested = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, file)));
                var mounted = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(deployed, file)));
                Assert.Equal(mounted, tested);
                checks.Add($"exact-mounted-assembly:{file}");
            }
            await Evidence();

            var f = await SqlScenario.CreateHosted(runId);
            checks.Add("company-profile-invitation-membership-team-bootstrap");
            await using (var sql = await f.Open())
            {
                await using var command = sql.CreateCommand();
                command.CommandText = """
                    SELECT CURRENT_USER,
                        HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CONTROL'),
                        HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CREATE TABLE'),
                        (SELECT COUNT(*) FROM app.Company),
                        (SELECT COUNT(*) FROM app.WorkItem);
                    """;
                await using (var reader = await command.ExecuteReaderAsync())
                {
                    Assert.True(await reader.ReadAsync());
                    Assert.Equal(SqlScenario.Runtime, reader.GetString(0));
                    for (var i = 1; i < 5; i++) Assert.Equal(0, reader.GetInt32(i));
                }
                command.CommandText = "SELECT COUNT(*) FROM bootstrap.Employee;";
                var denied = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
                Assert.Equal(229, denied.Number);
            }
            checks.Add("actual-runtime-principal-restricted-permissions-and-unscoped-rls");
            var setup = await f.Service.Setup(f.Owner, f.Company.Id, default);
            Assert.True(setup.HasTeams);
            Assert.True(setup.HasInvitations);
            Assert.False(setup.HasGroups);
            Assert.NotNull(await f.Service.Board(f.Owner, f.Company.Id, f.Team.Id, default));
            checks.Add("resumable-setup-and-default-board");

            var second = await f.CreateCompany(f.Owner);
            Assert.Equal(2, (await f.Service.Companies(f.Owner, null, 100, default)).Items.Count);
            await SqlScenario.Denied(404, () => f.Service.Board(f.Owner, second.Id, f.Team.Id, default));
            checks.Add("multiple-companies-and-cross-company-denial");
            var group = await f.Service.CreateGroup(f.Owner, f.Company.Id, "Synthetic contributors", default);
            await f.Service.ChangeGroupMembers(f.Owner, f.Company.Id, group.Id, [f.Reader.Id], group.Version, default);
            await f.Service.ChangeGrant(f.Owner, f.Company.Id, f.Team.Id, "group", group.Id, 2, "*", default);
            var effective = await f.Service.Effective(f.Owner, f.Company.Id, f.Team.Id, f.Reader.Id, default);
            Assert.Equal(2, effective.Role);
            Assert.Equal(2, effective.Sources.Count);
            checks.Add("group-membership-and-maximum-effective-role");

            var key = Guid.NewGuid().ToString("N");
            var task = await f.Task(f.Member.Id, key);
            Assert.Equal(task.TaskId, (await f.Task(f.Member.Id, key)).TaskId);
            await SqlScenario.Denied(409, () => f.Service.CreateTask(f.Contributor, f.Company.Id,
                f.Team.Id, new("Changed content"), key, default));
            var updated = await f.Service.UpdateTask(f.Contributor, f.Company.Id, f.Team.Id, task.TaskId,
                new() { Title = "Synthetic hosted edit" }, task.Version, default);
            await SqlScenario.Denied(412, () => f.Service.MoveTask(f.Contributor, f.Company.Id,
                f.Team.Id, task.TaskId, "done", task.Version, default));
            var moved = await f.Service.MoveTask(f.Contributor, f.Company.Id, f.Team.Id, task.TaskId,
                "done", updated.Version, default);
            Assert.Equal("done", moved.Status);
            Assert.Single((await f.Service.Tasks(f.Viewer, f.Company.Id, f.Team.Id,
                new(Status: "done", Assignee: f.Member.Id), null, 100, default)).Items);
            checks.Add("task-create-replay-content-conflict-edit-move-filter-and-etag");

            await SqlScenario.Denied(403, () => f.Service.GetTask(f.Delegated(f.Contributor),
                f.Company.Id, f.Team.Id, task.TaskId, default));
            await f.Service.ChangeApproval(f.Owner, f.Company.Id, f.ClientId, true, "*", default);
            var connection = await f.Service.ChangeConnection(f.Contributor, f.Company.Id, f.ClientId,
                new([f.Team.Id], true, true), "*", default);
            var delegated = f.Delegated(f.Contributor);
            var delegatedTask = await f.Service.CreateTask(delegated, f.Company.Id, f.Team.Id,
                new("Synthetic delegated task"), Guid.NewGuid().ToString("N"), default);
            Assert.Equal(delegatedTask.TaskId, (await f.Service.GetTask(delegated, f.Company.Id,
                f.Team.Id, delegatedTask.TaskId, default)).Id);
            await f.Service.RevokeConnection(f.Contributor, f.Company.Id, f.ClientId, connection.Version, default);
            await SqlScenario.Denied(403, () => f.Service.GetTask(delegated, f.Company.Id,
                f.Team.Id, delegatedTask.TaskId, default));
            checks.Add("company-client-approval-own-connection-delegated-access-and-revocation");

            var direct = (await f.Service.Grants(f.Owner, f.Company.Id, f.Team.Id, null, 100, default))
                .Items.Single(g => g.PrincipalId == f.Member.Id);
            await f.Service.ChangeGrant(f.Owner, f.Company.Id, f.Team.Id, "member", f.Member.Id,
                null, direct.Version, default);
            var inactive = await f.Service.GetTask(f.Owner, f.Company.Id, f.Team.Id, task.TaskId, default);
            Assert.Equal(f.Member.Id, inactive.AssigneeMembershipId);
            Assert.Equal("inactive", inactive.AssigneeState);
            await SqlScenario.Denied(404, () => f.Service.GetTask(f.Contributor, f.Company.Id,
                f.Team.Id, task.TaskId, default));
            checks.Add("grant-revocation-and-retained-inactive-assignment");

            var hidden = await f.Service.CreateTeam(f.Owner, f.Company.Id,
                new("Synthetic manager recovery", f.Reader.Id), default);
            await SqlScenario.Denied(404, () => f.Service.Board(f.Owner, f.Company.Id, hidden.Id, default));
            await f.Service.ChangeMembership(f.Owner, f.Company.Id, f.Reader.Id, false, f.Reader.Version, default);
            var recovery = await f.Service.RecoverManager(f.Owner, f.Company.Id, hidden.Id,
                f.Member.Id, hidden.Version, default);
            Assert.Equal(0, recovery.Role);
            await SqlScenario.Denied(404, () => f.Service.Board(f.Owner, f.Company.Id, hidden.Id, default));
            Assert.NotNull(await f.Service.Board(f.Contributor, f.Company.Id, hidden.Id, default));
            checks.Add("managerless-team-recovery-without-administrator-content-access");

            var audit = (await f.Service.AuditEvents(f.Owner, f.Company.Id, null, 100, default)).Items;
            Assert.Single(audit, a => a.Action == "task.created" && a.ObjectId == task.TaskId);
            Assert.Contains(audit, a => a.Action == "connection.revoked");
            Assert.DoesNotContain(audit, a => a.TeamId == hidden.Id);
            checks.Add("atomic-metadata-audits-filtered-by-current-team-access");
            status = "passed";
        }
        catch (Exception error)
        {
            status = "failed";
            failure = error is DomainException domain ? $"{error.GetType().Name}:{domain.Code}" :
                error is SqlException sql ? $"{error.GetType().Name}:{sql.Number}" :
                $"{error.GetType().Name}:{error.Message}";
            Console.Error.WriteLine(failure);
        }
        await Evidence();
        Console.WriteLine($"Hosted runtime component probe {status}; {checks.Count} checks. Provider/browser/client acceptance remains false.");
        return status == "passed" ? 0 : 1;
    }
}

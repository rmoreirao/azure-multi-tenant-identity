using Kanban.Core;
using Kanban.Infrastructure.Operations;
using Kanban.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Text.Json.Nodes;

namespace Kanban.SafetyTests;

public sealed class MutationSafetyTests
{
    [SqlFact]
    public async Task CompanyCreationRolloutSwitchPreservesCommittedRetriesAndExistingAccess()
    {
        await using var f = await SqlScenario.Create();
        var disabled = new KanbanService(f.Store, new CursorCodec(new EphemeralDataProtectionProvider()),
            new(false, [new ClientRegistration { Id = f.ClientId, Name = "Existing test connection" }]), f.Clock);
        var committedKey = Guid.NewGuid().ToString("N");
        var blockedKey = Guid.NewGuid().ToString("N");
        var request = new CreateCompanyRequest(f.Company.Name);
        await f.TrackCompanyKey(committedKey);
        await f.TrackCompanyKey(blockedKey);
        try
        {
            var rejected = await SqlScenario.Denied(403,
                () => disabled.CreateCompany(f.Owner, request, blockedKey, default));
            Assert.Equal("CREATION_UNAVAILABLE", rejected.Code);
            Assert.Single((await disabled.Companies(f.Owner, null, 100, default)).Items);
            Assert.NotNull(await disabled.Board(f.Owner, f.Company.Id, f.Team.Id, default));

            var committed = await f.Service.CreateCompany(f.Owner, request, committedKey, default);
            var replay = await disabled.CreateCompany(f.Owner, request, committedKey, default);
            Assert.Equal(committed.Id, replay.Id);
            Assert.True(replay.Administrator);
            await SqlScenario.Denied(409, () => disabled.CreateCompany(f.Owner,
                new("Changed rollout retry"), committedKey, default));
            Assert.Empty((await disabled.Teams(f.Owner, committed.Id, null, 100, default)).Items);

            var enabled = await f.Service.CreateCompany(f.Owner, request, blockedKey, default);
            Assert.NotEqual(committed.Id, enabled.Id);
            Assert.Equal(enabled.Id, (await disabled.CreateCompany(f.Owner, request, blockedKey, default)).Id);
            Assert.Equal(3, (await disabled.Companies(f.Owner, null, 100, default)).Items.Count);
            Assert.NotNull(await disabled.Board(f.Owner, f.Company.Id, f.Team.Id, default));
        }
        finally
        {
            await f.TrackCompanyKey(committedKey);
            await f.TrackCompanyKey(blockedKey);
        }
    }

    [SqlFact]
    public async Task HostedCleanupRecoversLostCompanyResponsesWithoutDeletingOtherFixtures()
    {
        await using var other = await SqlScenario.Create();
        var f = await SqlScenario.Create();
        var snapshot = Path.Combine(Path.GetDirectoryName(f.Manifest)!, $"{f.Owner.CorrelationId}-recovery.json");
        var cleaned = false;
        try
        {
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(f.Manifest))!;
            manifest["companies"] = new JsonArray();
            await File.WriteAllTextAsync(snapshot, manifest.ToJsonString());
            await SqlScenario.CleanupHosted(snapshot);
            cleaned = true;
            Assert.Equal(other.Company.Id, (await other.Service.Company(other.Owner, other.Company.Id, default)).Id);
            await using var connection = await f.Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM bootstrap.Employee
                WHERE DirectoryId=@tenant AND ObjectId IN (@owner,@contributor,@viewer);
                """;
            command.Parameters.AddWithValue("@tenant", f.Owner.DirectoryId);
            command.Parameters.AddWithValue("@owner", f.Owner.ObjectId);
            command.Parameters.AddWithValue("@contributor", f.Contributor.ObjectId);
            command.Parameters.AddWithValue("@viewer", f.Viewer.ObjectId);
            Assert.Equal(0, await command.ExecuteScalarAsync());
        }
        finally
        {
            if (!cleaned) await f.DisposeAsync();
            File.Delete(snapshot);
        }
    }

    [SqlFact]
    public async Task ControlledOperatorCreationAndAdministratorRecoveryAreAuditedWithoutContentGrants()
    {
        await using var f = await SqlScenario.Create();
        var hidden = await f.Service.CreateTeam(f.Owner, f.Company.Id, new("Operator recovery test", f.Member.Id), default);
        var settings = new Dictionary<string, string?>
        {
            ["company-id"] = f.Company.Id.ToString(), ["membership-id"] = f.Reader.Id.ToString(),
            ["directory-id"] = f.Owner.DirectoryId.ToString(), ["ExternalId:TenantId"] = f.Owner.DirectoryId.ToString(),
            ["employee-object-id"] = f.Owner.ObjectId.ToString(), ["employee-name"] = f.Owner.Name,
            ["employee-email"] = f.Owner.Email, ["company-name"] = f.Company.Name,
            ["idempotency-key"] = Guid.NewGuid().ToString("N")
        };
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddInMemoryCollection(settings).Build();
        await ProductOperator.Run("appoint-administrator", configuration, default);
        Assert.True((await f.Service.Company(f.Viewer, f.Company.Id, default)).Administrator);
        await SqlScenario.Denied(404, () => f.Service.Board(f.Viewer, f.Company.Id, hidden.Id, default));
        Assert.Contains((await f.Service.AuditEvents(f.Viewer, f.Company.Id, null, 100, default)).Items,
            a => a.Action == "operator.administrator-appointed" && a.ActorId is null && a.OperatorObjectId is not null);
        try { await ProductOperator.Run("create-company", configuration, default); }
        finally { await f.TrackCompanyKey(settings["idempotency-key"]!); }
    }

    [SqlFact]
    public async Task CompanyAndTaskRetriesAreAtomicAndContentBound()
    {
        await using var f = await SqlScenario.Create();
        var key = Guid.NewGuid().ToString("N");
        CompanyView[] companies;
        try
        {
            companies = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
                f.Service.CreateCompany(f.Owner, new(f.Company.Name), key, default)));
        }
        finally { await f.TrackCompanyKey(key); }
        Assert.Single(companies.Select(c => c.Id).Distinct());
        var company = companies[0];
        Assert.Single((await f.Service.Members(f.Owner, company.Id, null, 100, default)).Items);
        Assert.Empty((await f.Service.Teams(f.Owner, company.Id, null, 100, default)).Items);
        var same = await f.Service.CreateCompany(f.Owner, new(company.Name), key, default);
        Assert.Equal(company.Id, same.Id);
        await SqlScenario.Denied(409, () => f.Service.CreateCompany(f.Owner, new("Different"), key, default));
        var replay = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Task(key: key)));
        Assert.Single(replay.Select(r => r.TaskId).Distinct());
        await SqlScenario.Denied(409, () => f.Service.CreateTask(f.Contributor, f.Company.Id, f.Team.Id,
            new("Different"), key, default));
        var audit = await f.Service.AuditEvents(f.Owner, f.Company.Id, null, 100, default);
        Assert.Single(audit.Items, a => a.Action == "task.created" && a.ObjectId == replay[0].TaskId);
        var owner = (await f.Service.Members(f.Owner, f.Company.Id, null, 100, default)).Items.Single(m => m.Id == f.Company.MembershipId);
        await SqlScenario.Denied(409, () => f.Service.ChangeMembership(f.Owner, f.Company.Id, f.Company.MembershipId, false, owner.Version, default));
    }

    [SqlFact]
    public async Task CompanyBootstrapAuditFailureRollsBackCompanyMembershipAndReceipt()
    {
        await using var f = await SqlScenario.Create();
        var key = Guid.NewGuid().ToString("N");
        await f.TrackCompanyKey(key);
        await using var connection = await f.Open();

        async Task<(int Companies, int Members, int Receipts, bool ContextCleared)> State()
        {
            await using var query = connection.CreateCommand();
            query.CommandText = """
                DECLARE @receipts int =
                    (SELECT COUNT(*) FROM bootstrap.CompanyCreationReceipt r
                     JOIN bootstrap.Employee u ON u.Id=r.UserId
                     WHERE u.DirectoryId=@tenant AND u.ObjectId=@owner AND r.[Key]=@key);
                DECLARE @cleared bit = CASE WHEN SESSION_CONTEXT(N'CompanyId') IS NULL THEN 1 ELSE 0 END;
                EXECUTE AS USER = N'kanban_directory';
                BEGIN TRY
                    SELECT
                        (SELECT COUNT(*) FROM app.Company WHERE Name=@name),
                        (SELECT COUNT(*) FROM app.Membership m JOIN app.Company c ON c.CompanyId=m.CompanyId WHERE c.Name=@name),
                        @receipts, @cleared;
                    REVERT;
                END TRY
                BEGIN CATCH
                    REVERT;
                    THROW;
                END CATCH;
                """;
            query.Parameters.AddWithValue("@name", f.Company.Name);
            query.Parameters.AddWithValue("@tenant", f.Owner.DirectoryId);
            query.Parameters.AddWithValue("@owner", f.Owner.ObjectId);
            query.Parameters.AddWithValue("@key", key);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var state = (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetBoolean(3));
            Assert.False(await reader.NextResultAsync());
            return state;
        }

        var before = await State();
        Assert.Equal(1, before.Companies);
        Assert.Equal(0, before.Receipts);
        Assert.True(before.ContextCleared);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            EXECUTE AS USER = N'{SqlScenario.Runtime}';
            BEGIN TRY
                EXEC bootstrap.CreateCompany @DirectoryId=@tenant, @ObjectId=@owner,
                    @CompanyName=@name, @Key=@key, @Hash=@hash, @Enabled=1, @CorrelationId=NULL;
                REVERT;
            END TRY
            BEGIN CATCH
                REVERT;
                THROW;
            END CATCH;
            """;
        command.Parameters.AddWithValue("@tenant", f.Owner.DirectoryId);
        command.Parameters.AddWithValue("@owner", f.Owner.ObjectId);
        command.Parameters.AddWithValue("@name", f.Company.Name);
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@hash", Rules.Hash(f.Company.Name));
        try
        {
            // Null audit metadata fails the final insert without altering shared schema or permissions.
            var failure = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
            Assert.Equal(515, failure.Number);
            Assert.Contains("CorrelationId", failure.Message, StringComparison.Ordinal);
            Assert.Equal(before, await State());
        }
        finally { await f.TrackCompanyKey(key); }

        var created = await f.CreateCompany(f.Owner, key);
        var replay = await f.Service.CreateCompany(f.Owner, new(f.Company.Name), key, default);
        Assert.NotEqual(f.Company.Id, created.Id);
        Assert.Equal(created.Id, replay.Id);
        Assert.Single((await f.Service.Members(f.Owner, created.Id, null, 100, default)).Items);
        Assert.Empty((await f.Service.Teams(f.Owner, created.Id, null, 100, default)).Items);
        var audit = (await f.Service.AuditEvents(f.Owner, created.Id, null, 100, default)).Items;
        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, a => a.Action == "company.created" && a.ObjectId == created.Id);
        Assert.Contains(audit, a => a.Action == "company.initial-administrator" && a.ObjectId == created.MembershipId);
        Assert.All(audit, a => Assert.Equal(created.MembershipId, a.ActorId));
    }

    [SqlFact]
    public async Task CompanyBootstrapContentionReturnsDependencyErrorAndPreservesRetry()
    {
        await using var f = await SqlScenario.Create();
        var key = Guid.NewGuid().ToString("N");
        await f.TrackCompanyKey(key);
        await using var connection = await f.Open();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @resource nvarchar(255) =
                (SELECT N'company-create:' + CONVERT(nvarchar(36),Id) FROM bootstrap.Employee
                 WHERE DirectoryId=@tenant AND ObjectId=@owner);
            IF @resource IS NULL THROW 51003, 'The fixture employee is unavailable.', 1;
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',
                @LockOwner='Transaction',@LockTimeout=0;
            SELECT @result;
            """;
        command.Parameters.AddWithValue("@tenant", f.Owner.DirectoryId);
        command.Parameters.AddWithValue("@owner", f.Owner.ObjectId);
        Assert.InRange(Assert.IsType<int>(await command.ExecuteScalarAsync()), 0, int.MaxValue);
        var error = await SqlScenario.Denied(503, () =>
            f.Service.CreateCompany(f.Owner, new(f.Company.Name), key, default));
        Assert.Equal("DEPENDENCY_UNAVAILABLE", error.Code);
        Assert.Equal(f.Company.Id, (await f.Service.Company(f.Owner, f.Company.Id, default)).Id);
        await using var receipt = connection.CreateCommand();
        receipt.Transaction = transaction;
        receipt.CommandText = """
            SELECT COUNT(*) FROM bootstrap.CompanyCreationReceipt r
            JOIN bootstrap.Employee u ON u.Id=r.UserId
            WHERE u.DirectoryId=@tenant AND u.ObjectId=@owner AND r.[Key]=@key;
            """;
        receipt.Parameters.AddWithValue("@tenant", f.Owner.DirectoryId);
        receipt.Parameters.AddWithValue("@owner", f.Owner.ObjectId);
        receipt.Parameters.AddWithValue("@key", key);
        Assert.Equal(0, await receipt.ExecuteScalarAsync());
        await transaction.RollbackAsync();

        var created = await f.CreateCompany(f.Owner, key);
        var replay = await f.Service.CreateCompany(f.Owner, new(f.Company.Name), key, default);
        Assert.Equal(created.Id, replay.Id);
        Assert.Single((await f.Service.Members(f.Owner, created.Id, null, 100, default)).Items);
        Assert.Equal(2, (await f.Service.AuditEvents(f.Owner, created.Id, null, 100, default)).Items.Count);
    }

    [SqlFact]
    public async Task InvitationExpiryRecipientRevocationAndConsumptionAreEnforced()
    {
        await using var f = await SqlScenario.Create();
        var invitation = await f.Service.Invite(f.Owner, f.Company.Id, f.Contributor.Email!, default);
        Assert.Equal(f.Clock.Current.AddDays(7), invitation.ExpiresAt);
        await SqlScenario.Denied(400, () => f.Service.AcceptInvitation(f.Viewer, invitation.Token!, default));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { await f.Service.AcceptInvitation(f.Contributor, invitation.Token!, default); return true; }
            catch (DomainException ex) when (ex.Code == "INVALID_INVITATION") { return false; }
        }));
        Assert.Single(attempts, v => v);
        invitation = await f.Service.Invite(f.Owner, f.Company.Id, f.Contributor.Email!, default);
        await f.Service.RevokeInvitation(f.Owner, f.Company.Id, invitation.Id, invitation.Version, default);
        await SqlScenario.Denied(400, () => f.Service.AcceptInvitation(f.Contributor, invitation.Token!, default));
        invitation = await f.Service.Invite(f.Owner, f.Company.Id, f.Contributor.Email!, default);
        f.Clock.Current = f.Clock.Current.AddDays(7);
        await SqlScenario.Denied(400, () => f.Service.AcceptInvitation(f.Contributor, invitation.Token!, default));
    }

    [SqlFact]
    public async Task AnErrorAfterSaveRollsBackMutationAndAuditTogether()
    {
        await using var f = await SqlScenario.Create();
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.InCompany<int>(f.Owner, f.Company.Id, true, async (d, m) =>
        {
            d.Add(new CompanyGroup { Id = id, CompanyId = f.Company.Id, Name = "Rollback" });
            d.Add(new AuditEvent { CompanyId = f.Company.Id, ActorId = m.Id, ObjectId = id,
                Action = "test.rollback", CorrelationId = "synthetic", CreatedAt = f.Clock.Current });
            await d.Save(default);
            throw new InvalidOperationException("Deliberate fixture rollback.");
        }, default));
        Assert.DoesNotContain((await f.Service.Groups(f.Owner, f.Company.Id, null, 100, default)).Items, g => g.Id == id);
        Assert.DoesNotContain((await f.Service.AuditEvents(f.Owner, f.Company.Id, null, 100, default)).Items, a => a.ObjectId == id);
    }
}

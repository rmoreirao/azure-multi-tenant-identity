using System.Text.Json;
using Azure.Identity;
using Kanban.Core;
using Kanban.Infrastructure.Persistence;
using Kanban.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kanban.SafetyTests;

public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("KANBAN_SQL_TESTS") != "1")
            Skip = "Real Azure SQL checks require explicit KANBAN_SQL_TESTS=1 and the approved target.";
    }
}

internal sealed class SqlScenario : IAsyncDisposable
{
    public const string Server = "tcp:sql-kanban-poc-2rza5wd265s3g.database.windows.net,1433";
    public const string Runtime = "app-kanban-poc-2rza5wd265s3g";
    public const string ProviderTenant = "0f6188cc-b440-43f2-87a5-35d9811f8c7c";
    private readonly List<Guid> companies = [];
    private readonly List<Caller> employees = [];
    private readonly List<(Guid EmployeeObjectId, string Key)> creationKeys = [];
    private readonly string runId;
    private readonly bool hosted;
    public string Manifest => hosted ? Path.Combine(AppContext.BaseDirectory, "fixture.json") :
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "artifacts", "sql-safety", $"{runId}.json"));
    public SqlConnectionFactory Connections { get; }
    public CompanyStore Store { get; }
    public KanbanService Service { get; }
    public Caller Owner { get; }
    public Caller Contributor { get; }
    public Caller Viewer { get; }
    public Guid ClientId { get; }
    public CompanyView Company { get; private set; } = null!;
    public TeamView Team { get; private set; } = null!;
    public MemberView Member { get; private set; } = null!;
    public MemberView Reader { get; private set; } = null!;
    public TestClock Clock { get; } = new();

    private SqlScenario(Guid? fixedRunId = null, bool hosted = false, IReadOnlyList<Guid>? objectIds = null,
        Guid? callerDirectoryId = null)
    {
        this.hosted = hosted;
        runId = (fixedRunId ?? Guid.NewGuid()).ToString("N");
        if (!hosted && (Environment.GetEnvironmentVariable("KANBAN_SQL_TESTS") != "1" ||
            Environment.GetEnvironmentVariable("KANBAN_SQL_TARGET") != Server))
            throw new InvalidOperationException("Explicit SQL test opt-in and exact target are required.");
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var settings = DatabaseSettings.Load(config);
        if (new SqlConnectionStringBuilder(settings.ConnectionString).DataSource != Server)
            throw new InvalidOperationException("SQL tests cannot run against another server.");
        var azure = AzureSettings.Load(config);
        if (hosted && (!OperatingSystem.IsLinux() ||
            Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") != Runtime || !azure.UseManagedIdentity ||
            config["ExternalId:TenantId"] != ProviderTenant ||
            azure.HostingTenantId != "f1640304-f816-412c-9e6f-4b6b818cf492"))
            throw new InvalidOperationException("The private probe requires the approved application's actual managed-identity runtime.");
        Connections = new(settings, hosted ? azure.CreateCredential() :
            new AzureCliCredential(new AzureCliCredentialOptions
            {
                TenantId = azure.HostingTenantId,
                ProcessTimeout = TimeSpan.FromSeconds(60)
            }));
        Store = new(Connections, NullLogger<CompanyStore>.Instance);
        ClientId = Guid.Parse((config["ExternalId:PublicClientIds"] ?? "").Split(',')[0].Trim());
        Service = new(Store, new CursorCodec(new EphemeralDataProtectionProvider()),
            new(true, [new ClientRegistration { Id = ClientId, Name = "Existing test connection" }]), Clock);
        Caller Employee(string name)
        {
            var user = new Caller(callerDirectoryId ??
                Guid.Parse(hosted ? ProviderTenant : IdentityBoundaryTests.CandidateFactory.Tenant),
                objectIds?[employees.Count] ?? Guid.NewGuid(),
                $"{name} {runId}", $"{name}.{runId}@example.test", null, true, true, runId);
            employees.Add(user);
            return user;
        }
        Owner = Employee("owner");
        Contributor = Employee("contributor");
        Viewer = Employee("viewer");
    }

    public static Task<SqlScenario> Create() => Prepare(new());

    public static Task<SqlScenario> CreateHosted(Guid runId) => Prepare(new(runId, hosted: true));

    private static async Task<SqlScenario> Prepare(SqlScenario fixture)
    {
        await fixture.Evidence(false);
        try
        {
            fixture.Company = await fixture.CreateCompany(fixture.Owner);
            fixture.Member = await fixture.Join(fixture.Contributor);
            fixture.Reader = await fixture.Join(fixture.Viewer);
            fixture.Team = await fixture.Service.CreateTeam(fixture.Owner, fixture.Company.Id,
                new("Synthetic team", fixture.Company.MembershipId), default);
            await fixture.Service.ChangeGrant(fixture.Owner, fixture.Company.Id, fixture.Team.Id,
                "member", fixture.Member.Id, 2, "*", default);
            await fixture.Service.ChangeGrant(fixture.Owner, fixture.Company.Id, fixture.Team.Id,
                "member", fixture.Reader.Id, 1, "*", default);
            return fixture;
        }
        catch
        {
            if (!fixture.hosted) await fixture.DisposeAsync();
            throw;
        }
    }

    public async Task<CompanyView> CreateCompany(Caller caller, string? key = null)
    {
        // Record the committed bootstrap result before any follow-up read can fail.
        var name = $"SQL safety {runId}";
        key ??= Guid.NewGuid().ToString("N");
        creationKeys.Add((caller.ObjectId, key));
        await Evidence(false);
        var id = await Store.CreateCompany(caller, name, key, Rules.Hash(name), true, default);
        if (!companies.Contains(id)) companies.Add(id);
        await Evidence(false);
        return await Service.Company(caller, id, default);
    }

    public async Task TrackCompanyKey(string key, Guid? employeeObjectId = null)
    {
        var employee = employeeObjectId ?? Owner.ObjectId;
        if (!creationKeys.Contains((employee, key))) creationKeys.Add((employee, key));
        await Evidence(false);
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.CompanyId FROM bootstrap.CompanyCreationReceipt r
            JOIN bootstrap.Employee u ON u.Id=r.UserId
            WHERE u.DirectoryId=@tenant AND u.ObjectId=@employee AND r.[Key]=@key;
            """;
        command.Parameters.AddWithValue("@tenant", Owner.DirectoryId);
        command.Parameters.AddWithValue("@employee", employee);
        command.Parameters.AddWithValue("@key", key);
        if (await command.ExecuteScalarAsync() is Guid id && !companies.Contains(id)) companies.Add(id);
        await Evidence(false);
    }

    public async Task<MemberView> Join(Caller caller)
    {
        var invitation = await Service.Invite(Owner, Company.Id, caller.Email!, default);
        var joined = await Service.AcceptInvitation(caller, invitation.Token!, default);
        return (await Service.Members(Owner, Company.Id, null, 100, default)).Items.Single(m => m.Id == joined.MembershipId);
    }

    public Task<CreateTaskResult> Task(Guid? assignee = null, string? key = null) =>
        Service.CreateTask(Contributor, Company.Id, Team.Id, new("Synthetic task", "", assignee),
            key ?? Guid.NewGuid().ToString("N"), default);

    public Caller Delegated(Caller caller, bool write = true) => caller with
        { ClientId = ClientId, CanRead = true, CanWrite = write, Email = null };

    public static async Task<DomainException> Denied(int status, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<DomainException>(action);
        Assert.Equal(status, error.Status);
        return error;
    }

    public async Task<SqlConnection> Open()
    {
        var connection = Connections.CreateConnection();
        await connection.OpenAsync();
        return connection;
    }

    private Task Evidence(bool cleaned) =>
        WriteEvidence(new
        {
            runId, server = Server, database = "kanban", directoryId = Owner.DirectoryId, companies,
            employeeObjects = employees.Select(e => e.ObjectId),
            creationKeys = creationKeys.Select(k => new { employeeObjectId = k.EmployeeObjectId, key = k.Key }),
            cleaned
        });

    private async Task WriteEvidence<T>(T evidence)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Manifest)!);
        await File.WriteAllTextAsync(Manifest + ".tmp", JsonSerializer.Serialize(evidence));
        File.Move(Manifest + ".tmp", Manifest, overwrite: true);
    }

    public static async Task CleanupHosted(string manifestPath)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var manifest = document.RootElement;
        if (manifest.GetProperty("server").GetString() != Server ||
            manifest.GetProperty("database").GetString() != "kanban" ||
            !Guid.TryParseExact(manifest.GetProperty("runId").GetString(), "N", out var id))
            throw new InvalidOperationException("Cleanup requires an exact-target synthetic fixture manifest.");
        var objects = manifest.GetProperty("employeeObjects").EnumerateArray().Select(e => e.GetGuid()).ToArray();
        var ownedCompanies = manifest.GetProperty("companies").EnumerateArray().Select(e => e.GetGuid()).Distinct().ToArray();
        var keys = manifest.GetProperty("creationKeys").EnumerateArray().ToArray();
        var directory = manifest.GetProperty("directoryId").GetGuid();
        if (directory != Guid.Parse(ProviderTenant) && directory != Guid.Parse(IdentityBoundaryTests.CandidateFactory.Tenant) ||
            objects.Length != 3 || objects.Distinct().Count() != 3 || objects.Contains(Guid.Empty) ||
            ownedCompanies.Length > 10 || ownedCompanies.Contains(Guid.Empty) || keys.Length > 10 ||
            keys.Any(k => !objects.Contains(k.GetProperty("employeeObjectId").GetGuid()) ||
                !Guid.TryParseExact(k.GetProperty("key").GetString(), "N", out _)))
            throw new InvalidOperationException("Cleanup rejected an invalid or unbounded fixture manifest.");
        var fixture = new SqlScenario(id, objectIds: objects, callerDirectoryId: directory);
        fixture.companies.AddRange(ownedCompanies);
        foreach (var key in keys)
            await fixture.TrackCompanyKey(key.GetProperty("key").GetString()!,
                key.GetProperty("employeeObjectId").GetGuid());
        await fixture.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (hosted) throw new InvalidOperationException("Hosted fixtures require the separate, scoped operator cleanup.");
        foreach (var creation in creationKeys.ToArray())
            await TrackCompanyKey(creation.Key, creation.EmployeeObjectId);
        await using var connection = await Open();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            foreach (var id in companies)
            {
                await using var check = connection.CreateCommand();
                check.Transaction = transaction;
                check.CommandText = "EXEC sys.sp_set_session_context @key=N'CompanyId',@value=@id; SELECT [Name] FROM [app].[Company] WHERE [Id]=@id;";
                check.Parameters.AddWithValue("@id", id);
                var name = await check.ExecuteScalarAsync();
                if (name is not string actual || !string.Equals(actual, $"SQL safety {runId}", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Cleanup rejected unowned company {id}.");
                foreach (var table in new[]
                {
                    "app.AuditEvent", "app.ConnectionTeam", "app.MutationReceipt", "app.WorkItem",
                    "app.MemberGrant", "app.GroupGrant", "app.GroupMember", "app.Invitation",
                    "app.ClientApproval", "app.ConnectionGrant", "app.Board", "app.Team",
                    "app.CompanyGroup", "bootstrap.CompanyCreationReceipt", "app.Membership", "app.Company"
                })
                {
                    await using var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = $"DELETE FROM {table} WHERE [CompanyId]=@id;";
                    delete.Parameters.AddWithValue("@id", id);
                    await delete.ExecuteNonQueryAsync();
                }

            }
            foreach (var employee in employees)
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM bootstrap.Employee WHERE DirectoryId=@tenant AND ObjectId=@object;";
                delete.Parameters.AddWithValue("@tenant", employee.DirectoryId);
                delete.Parameters.AddWithValue("@object", employee.ObjectId);
                await delete.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
            await Evidence(true);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            await using var reset = connection.CreateCommand();
            reset.CommandText = "EXEC sys.sp_set_session_context @key=N'CompanyId',@value=NULL;";
            await reset.ExecuteNonQueryAsync();
        }
    }
}

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Current { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Current;
}

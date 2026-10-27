using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Kanban.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kanban.Infrastructure.Persistence;

public sealed class CursorCodec(IDataProtectionProvider provider) : ICursorCodec
{
    private readonly IDataProtector protector = provider.CreateProtector("kanban.pagination.v1");
    public string Encode(CursorState cursor) => protector.Protect(JsonSerializer.Serialize(cursor));
    public CursorState Decode(string text)
    {
        Rules.Require(text.Length <= 4096, 400, "INVALID_CURSOR", "The cursor is too long.");
        try
        {
            return JsonSerializer.Deserialize<CursorState>(protector.Unprotect(text))
                ?? throw new DomainException(400, "INVALID_CURSOR", "The cursor is invalid.");
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            throw new DomainException(400, "INVALID_CURSOR", "The cursor is invalid or no longer available.");
        }
    }
}

public sealed class CompanyStore(SqlConnectionFactory connections, ILogger<CompanyStore> logger) : ICompanyStore
{
    private static bool Transient(SqlException error) =>
        error.IsTransient || error.Number is -2 or 1205 or 1222 or 51003;

    private DomainException Unavailable(SqlException error)
    {
        logger.LogWarning("SQL operation unavailable with error {Number}.", error.Number);
        return new DomainException(503, "DEPENDENCY_UNAVAILABLE",
            "The database operation is unavailable. Keep the same creation key when retrying; reload records before editing.");
    }

    private async Task<T> WithDatabaseErrors<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (SqlException error) when (error.Number == 51014)
        {
            throw new DomainException(403, "PROFILE_REQUIRED", "Open the browser application to initialize your profile.");
        }
        catch (SqlException error) when (Transient(error)) { throw Unavailable(error); }
        catch (DbUpdateException error)
        {
            if (error.InnerException is SqlException sql && Transient(sql)) throw Unavailable(sql);
            throw;
        }
    }

    private static KanbanDbContext Context(SqlConnection connection) =>
        new(new DbContextOptionsBuilder<KanbanDbContext>().UseSqlServer(connection).Options);

    private static SqlCommand Command(SqlConnection connection, string name, Caller? caller = null)
    {
        var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = $"bootstrap.{name}";
        command.CommandTimeout = 30;
        if (caller is not null)
        {
            command.Parameters.AddWithValue("@DirectoryId", caller.DirectoryId);
            command.Parameters.AddWithValue("@ObjectId", caller.ObjectId);
            command.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 512) { Value = caller.Name });
            command.Parameters.Add(new SqlParameter("@Email", SqlDbType.NVarChar, 320)
                { Value = caller.Email is null ? DBNull.Value : Rules.Address(caller.Email) });
        }
        return command;
    }

    private static async Task<Employee> Resolve(SqlConnection connection, Caller caller, CancellationToken ct)
    {
        await using var command = Command(connection, "ResolveEmployee", caller);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new DomainException(403, "PROFILE_REQUIRED", "Open the browser application to initialize your profile.");
        return new Employee
        {
            Id = reader.GetGuid(0), DirectoryId = caller.DirectoryId, ObjectId = caller.ObjectId,
            Name = reader.GetString(1), Email = reader.GetString(2)
        };
    }

    private async Task Reset(SqlConnection connection, bool opening = false)
    {
        if (connection.State != ConnectionState.Open)
        {
            SqlConnection.ClearPool(connection);
            logger.LogWarning("Discarded SQL pool because the session was unavailable during company-context reset.");
            if (opening) throw new InvalidOperationException("The SQL session is unavailable.");
            return;
        }
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 10;
            command.CommandText = "EXEC sys.sp_set_session_context @key=N'CompanyId', @value=NULL;";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (SqlException ex)
        {
            SqlConnection.ClearPool(connection);
            logger.LogWarning("Discarded SQL pool after company-context reset failed with SQL error {Number}.", ex.Number);
            if (opening) throw;
        }
    }

    private async Task<T> Execute<T>(Caller caller, Guid companyId, bool write, bool requireMembership,
        Func<IDataSession, Employee, Membership?, Task<T>> action, CancellationToken ct)
    {
        await using var connection = connections.CreateConnection();
        await connection.OpenAsync(ct);
        try
        {
            await Reset(connection, opening: true);
            var employee = await Resolve(connection, caller, ct);
            await using var context = Context(connection);
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"EXEC sys.sp_set_session_context @key=N'CompanyId', @value={companyId};", ct);
            if (write)
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT [Id] FROM [app].[Company] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={companyId} AND [Active]=1;", ct);
            var data = new DataSession(context, companyId);
            var company = await context.Set<Company>().FirstOrDefaultAsync(c => c.Id == companyId && c.Active, ct);
            Rules.Require(company is not null, 404, "NOT_FOUND", "The company is not available.");
            var member = await context.Set<Membership>().FirstOrDefaultAsync(m =>
                m.CompanyId == companyId && m.UserId == employee.Id && m.Active, ct);
            Rules.Require(!requireMembership || member is not null, 404, "NOT_FOUND", "The company is not available.");
            var result = await action(data, employee, member);
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException(412, "VERSION_CONFLICT", "The record changed. Load its current version.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new DomainException(409, "CONCURRENT_CREATE", "A competing request created this record. Refresh and retry.");
        }
        finally { await Reset(connection); }
    }

    public Task<T> InCompany<T>(Caller caller, Guid companyId, bool write,
        Func<IDataSession, Membership, Task<T>> action, CancellationToken cancellationToken) =>
        WithDatabaseErrors(() => Execute(caller, companyId, write, true, (data, _, member) => action(data, member!), cancellationToken));

    public Task<T> AcceptInvitation<T>(Caller caller, Guid companyId,
        Func<IDataSession, Employee, Task<T>> action, CancellationToken cancellationToken) =>
        WithDatabaseErrors(() => Execute(caller, companyId, true, false, (data, employee, _) => action(data, employee), cancellationToken));

    public Task<Page<CompanyView>> Companies(Caller caller, int offset, int limit, CancellationToken cancellationToken) =>
        WithDatabaseErrors(() => ReadCompanies(caller, offset, limit, cancellationToken));

    private async Task<Page<CompanyView>> ReadCompanies(Caller caller, int offset, int limit, CancellationToken cancellationToken)
    {
        await using var connection = connections.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await Reset(connection, opening: true);
            await Resolve(connection, caller, cancellationToken);
            await using var command = Command(connection, "ListCompanies");
            command.Parameters.AddWithValue("@DirectoryId", caller.DirectoryId);
            command.Parameters.AddWithValue("@ObjectId", caller.ObjectId);
            command.Parameters.AddWithValue("@Offset", offset);
            command.Parameters.AddWithValue("@Limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<CompanyView>();
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetBoolean(3),
                    Convert.ToBase64String((byte[])reader[4])));
            return new(rows.Take(limit).ToArray(), rows.Count > limit ? "more" : null);
        }
        finally { await Reset(connection); }
    }

    public Task<Guid> CreateCompany(Caller caller, string name, string key, string hash, bool enabled, CancellationToken cancellationToken) =>
        WithDatabaseErrors(() => CreateCompanyResult(caller, name, key, hash, enabled, cancellationToken));

    private async Task<Guid> CreateCompanyResult(Caller caller, string name, string key, string hash, bool enabled, CancellationToken cancellationToken)
    {
        await using var connection = connections.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await Reset(connection, opening: true);
            await Resolve(connection, caller, cancellationToken);
            await using var command = Command(connection, "CreateCompany");
            command.Parameters.AddWithValue("@DirectoryId", caller.DirectoryId);
            command.Parameters.AddWithValue("@ObjectId", caller.ObjectId);
            command.Parameters.Add(new SqlParameter("@CompanyName", SqlDbType.NVarChar, 200) { Value = name });
            command.Parameters.AddWithValue("@Key", key);
            command.Parameters.AddWithValue("@Hash", hash);
            command.Parameters.AddWithValue("@Enabled", enabled);
            command.Parameters.AddWithValue("@CorrelationId", caller.CorrelationId);
            return (Guid)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Company creation returned no result."));
        }
        catch (SqlException ex) when (ex.Number is 51009 or 51013)
        {
            throw new DomainException(ex.Number == 51009 ? 409 : 403,
                ex.Number == 51009 ? "IDEMPOTENCY_CONFLICT" : "CREATION_UNAVAILABLE",
                ex.Number == 51009 ? "The key was already used for different content." : "Company creation or access is unavailable.");
        }
        finally { await Reset(connection); }
    }

    public Task<Guid> InvitationCompany(string tokenHash, CancellationToken cancellationToken) =>
        WithDatabaseErrors(() => FindInvitationCompany(tokenHash, cancellationToken));

    private async Task<Guid> FindInvitationCompany(string tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = connections.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await Reset(connection, opening: true);
            await using var command = Command(connection, "FindInvitation");
            command.Parameters.AddWithValue("@Hash", tokenHash);
            return await command.ExecuteScalarAsync(cancellationToken) is Guid id ? id
                : throw new DomainException(400, "INVALID_INVITATION", "The invitation is unavailable.");
        }
        finally { await Reset(connection); }
    }
}

internal sealed class DataSession(KanbanDbContext context, Guid companyId) : IDataSession
{
    public IQueryable<T> Query<T>() where T : class =>
        context.Set<T>().Where(row => EF.Property<Guid>(row, "CompanyId") == companyId);
    public Task<List<T>> List<T>(IQueryable<T> query, CancellationToken cancellationToken) => query.ToListAsync(cancellationToken);
    public Task<T?> First<T>(IQueryable<T> query, CancellationToken cancellationToken) => query.FirstOrDefaultAsync(cancellationToken);
    public void Add<T>(T row) where T : class => context.Add(row);
    public void Remove<T>(T row) where T : class => context.Remove(row);
    public async Task Save(CancellationToken cancellationToken) => await context.SaveChangesAsync(cancellationToken);
}

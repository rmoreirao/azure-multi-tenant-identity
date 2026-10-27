using Kanban.Infrastructure.Persistence;
using Kanban.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Kanban.Infrastructure.Operations;

public static class DatabaseOperator
{
    public static async Task MigrateAsync(
        IConfiguration configuration, Guid runtimeClientId, string runtimePrincipalName,
        CancellationToken cancellationToken)
    {
        if (runtimeClientId == Guid.Empty || string.IsNullOrWhiteSpace(runtimePrincipalName) ||
            runtimePrincipalName.Length > 128)
        {
            throw new ArgumentException("The verified managed identity application/client ID and SQL principal name are required.");
        }

        var settings = DatabaseSettings.Load(configuration);
        var target = new SqlConnectionStringBuilder(settings.ConnectionString);
        Console.WriteLine($"Migration target: {target.DataSource}, database {target.InitialCatalog}.");
        var factory = new SqlConnectionFactory(settings, AzureSettings.Load(configuration).CreateCredential(operatorCommand: true));
        await using var connection = factory.CreateConnection();
        await using var context = new KanbanDbContext(
            new DbContextOptionsBuilder<KanbanDbContext>().UseSqlServer(connection).Options);
        await context.Database.MigrateAsync(cancellationToken);
        await connection.OpenAsync(cancellationToken);

        // SQL maps an Entra service principal by application ID, not its directory object ID.
        var sid = runtimeClientId.ToByteArray();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @name AND sid <> @sid)
                THROW 51000, 'Runtime principal name belongs to a different identity.', 1;
            IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @name)
            BEGIN
                DECLARE @create nvarchar(max) = N'CREATE USER ' + QUOTENAME(@name)
                    + N' WITH SID = ' + CONVERT(nvarchar(max), @sid, 1) + N', TYPE = E;';
                EXEC sys.sp_executesql @create;
            END;
            DECLARE @grant nvarchar(max) = N'GRANT SELECT, INSERT ON OBJECT::[infrastructure].[DataProtectionKeys] TO '
                + QUOTENAME(@name) + N';';
            EXEC sys.sp_executesql @grant;
            """;
        command.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 128) { Value = runtimePrincipalName });
        command.Parameters.Add(new SqlParameter("@sid", System.Data.SqlDbType.VarBinary, 16) { Value = sid });
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var product = connection.CreateCommand();
        product.CommandText = """
            DECLARE @grant nvarchar(max) = N'GRANT EXECUTE ON OBJECT::[bootstrap].[ResolveEmployee] TO ' + QUOTENAME(@name)
                + N'; GRANT EXECUTE ON OBJECT::[bootstrap].[ListCompanies] TO ' + QUOTENAME(@name)
                + N'; GRANT EXECUTE ON OBJECT::[bootstrap].[CreateCompany] TO ' + QUOTENAME(@name)
                + N'; GRANT EXECUTE ON OBJECT::[bootstrap].[FindInvitation] TO ' + QUOTENAME(@name)
                + N'; GRANT SELECT ON OBJECT::[dbo].[__EFMigrationsHistory] TO ' + QUOTENAME(@name)
                + N'; GRANT SELECT ON OBJECT::[app].[Company] TO ' + QUOTENAME(@name) + N';';
            EXEC sys.sp_executesql @grant;
            """;
        product.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 128) { Value = runtimePrincipalName });
        await product.ExecuteNonQueryAsync(cancellationToken);
        foreach (var table in BusinessSecurityV1.Tables.Where(t => t != "Company"))
        {
            await using var grant = connection.CreateCommand();
            grant.CommandText = "DECLARE @sql nvarchar(max) = N'GRANT " +
                (table == "AuditEvent" ? "SELECT, INSERT" : "SELECT, INSERT, UPDATE, DELETE") +
                $" ON OBJECT::[app].[{table}] TO ' + QUOTENAME(@name) + N';'; EXEC sys.sp_executesql @sql;";
            grant.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 128) { Value = runtimePrincipalName });
            await grant.ExecuteNonQueryAsync(cancellationToken);
        }
        var clients = configuration["ExternalId:PublicClientIds"]?.Split(',', StringSplitOptions.TrimEntries)
            ?? throw new InvalidOperationException("The existing public client registrations must be configured.");
        foreach (var client in clients)
        {
            if (!Guid.TryParse(client, out var clientId)) throw new InvalidOperationException("Invalid configured public client.");
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                IF NOT EXISTS (SELECT 1 FROM [bootstrap].[ClientRegistration] WHERE [Id]=@id)
                    INSERT [bootstrap].[ClientRegistration] ([Id],[Name]) VALUES (@id,@name);
                """;
            seed.Parameters.AddWithValue("@id", clientId);
            seed.Parameters.AddWithValue("@name", $"Configured public client {clientId}");
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }
        Console.WriteLine("Reviewed migrations applied; restricted runtime session and product permissions configured.");
    }
}

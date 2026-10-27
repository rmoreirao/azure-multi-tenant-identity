using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Kanban.Infrastructure.Persistence;

public sealed class DatabaseSettings
{
    public string ConnectionString { get; init; } = "";

    public static DatabaseSettings Load(IConfiguration configuration)
    {
        var settings = configuration.GetRequiredSection("Database").Get<DatabaseSettings>()
            ?? throw new InvalidOperationException("Database configuration is required.");
        var connection = new SqlConnectionStringBuilder(settings.ConnectionString);
        var host = connection.DataSource.Replace("tcp:", "", StringComparison.OrdinalIgnoreCase).Split(',')[0];
        if (!host.EndsWith(".database.windows.net", StringComparison.OrdinalIgnoreCase) ||
            connection.InitialCatalog != "kanban" ||
            connection.TrustServerCertificate ||
            connection.Encrypt == SqlConnectionEncryptOption.Optional ||
            connection.IntegratedSecurity ||
            connection.Authentication != SqlAuthenticationMethod.NotSpecified ||
            !string.IsNullOrEmpty(connection.UserID) ||
            !string.IsNullOrEmpty(connection.Password))
        {
            throw new InvalidOperationException(
                "Use the approved Azure SQL kanban database with certificate-validated encryption and the explicit Entra credential provider.");
        }

        return settings;
    }
}

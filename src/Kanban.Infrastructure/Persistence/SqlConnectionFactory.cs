using Azure.Core;
using Microsoft.Data.SqlClient;

namespace Kanban.Infrastructure.Persistence;

public sealed class SqlConnectionFactory
{
    private readonly string _connectionString;
    private readonly Func<SqlAuthenticationParameters, CancellationToken, Task<SqlAuthenticationToken>> _tokenCallback;

    public SqlConnectionFactory(DatabaseSettings settings, TokenCredential credential)
    {
        _connectionString = settings.ConnectionString;
        _tokenCallback = async (_, cancellationToken) =>
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(["https://database.windows.net/.default"]), cancellationToken);
            return new SqlAuthenticationToken(token.Token, token.ExpiresOn);
        };
    }

    public SqlConnection CreateConnection() => new(_connectionString)
    {
        AccessTokenCallback = _tokenCallback
    };
}

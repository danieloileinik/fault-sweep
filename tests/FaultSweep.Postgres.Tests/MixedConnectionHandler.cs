using System.Data.Common;
using Npgsql;

namespace FaultSweep.Postgres.Tests;

public sealed class MixedConnectionHandler(DbDataSource source, TestDatabase database) : IMessageHandler<PostgresMessage>
{
    public async ValueTask HandleAsync(PostgresMessage message, CancellationToken cancellationToken)
    {
        await using (DbCommand command = source.CreateCommand("SELECT 1"))
        {
            await command.ExecuteScalarAsync(cancellationToken);
        }

        // These writes have no fault boundaries, even though another command was intercepted.
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await AdoWorkload.RunAsync(connection, message, cancellationToken);
    }
}

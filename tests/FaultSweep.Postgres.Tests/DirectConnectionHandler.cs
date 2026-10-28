using Npgsql;

namespace FaultSweep.Postgres.Tests;

public sealed class DirectConnectionHandler(TestDatabase database) : IMessageHandler<PostgresMessage>
{
    public async ValueTask HandleAsync(PostgresMessage message, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await AdoWorkload.RunAsync(connection, message, cancellationToken);
    }
}

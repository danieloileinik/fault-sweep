using Npgsql;

namespace FaultSweep.Postgres.Tests;

public sealed class PreCreatedConnectionHandler(NpgsqlConnection connection) : IMessageHandler<PostgresMessage>
{
    public async ValueTask HandleAsync(PostgresMessage message, CancellationToken cancellationToken)
    {
        // Ownership stays with the caller; the connection was opened before FaultSweep was configured.
        await using var command = new NpgsqlCommand("UPDATE counters SET value = value + 10 WHERE id = 1", connection);
        if (message.Async)
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            command.ExecuteNonQuery();
        }
    }
}

using Npgsql;

namespace FaultSweep.Postgres.Tests;

public sealed class BatchHandler(TestDatabase database) : IMessageHandler<PostgresMessage>
{
    public async ValueTask HandleAsync(PostgresMessage message, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        if (message.Async)
        {
            await connection.OpenAsync(cancellationToken);
        }
        else
        {
            connection.Open();
        }

        await using var batch = new NpgsqlBatch(connection);
        batch.BatchCommands.Add(new NpgsqlBatchCommand("UPDATE counters SET value = value + 10 WHERE id = 1"));
        batch.BatchCommands.Add(new NpgsqlBatchCommand("UPDATE counters SET value = value + 10 WHERE id = 2"));
        if (message.Async)
        {
            await batch.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            batch.ExecuteNonQuery();
        }
    }
}

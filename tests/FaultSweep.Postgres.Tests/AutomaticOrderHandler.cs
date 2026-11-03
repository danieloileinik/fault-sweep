using Npgsql;

namespace FaultSweep.Postgres.Tests;

// Deliberately has no FaultSweep interfaces or dependencies and is not registered in DI.
public sealed class AutomaticOrderHandler(TestDatabase database)
{
    public async Task HandleAsync(AutomaticOrder message, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(token);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token);
        await using (
            var command = new NpgsqlCommand(
                message.Idempotent
                    ? "UPDATE counters SET value = 15 WHERE id = 1"
                    : "UPDATE counters SET value = value + 10 WHERE id = 1",
                connection,
                transaction
            )
        )
        {
            await command.ExecuteNonQueryAsync(token);
        }

        await using (
            var command = new NpgsqlCommand(
                """
                INSERT INTO outbox(message_id, created_at, payload)
                VALUES (42, clock_timestamp(), '{"value":15}')
                ON CONFLICT (message_id) DO NOTHING
                """,
                connection,
                transaction
            )
        )
        {
            await command.ExecuteNonQueryAsync(token);
        }

        await transaction.CommitAsync(token);
        if (message.Fail)
        {
            throw new InvalidOperationException("Handler failed after commit.");
        }
    }
}

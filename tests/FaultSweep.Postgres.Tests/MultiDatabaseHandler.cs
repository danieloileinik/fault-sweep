using Npgsql;

namespace FaultSweep.Postgres.Tests;

public sealed class MultiDatabaseHandler(IReadOnlyCollection<TestDatabase> databases)
{
    public async Task HandleAsync(PostgresMessage message, CancellationToken token)
    {
        foreach (TestDatabase database in databases)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = new NpgsqlCommand(
                "UPDATE counters SET value = 10 WHERE id = 1",
                connection
            );
            await command.ExecuteNonQueryAsync(token);
        }
    }
}

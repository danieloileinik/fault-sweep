using Dapper;
using Npgsql;

namespace FaultSweep.Postgres.Tests;

public sealed class TwentyEffectHandler(TestDatabase database) : IMessageHandler<int>
{
    public async ValueTask HandleAsync(int effects, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        for (int id = 1; id <= effects; id++)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO counters(id, value) VALUES (@id, 10) ON CONFLICT(id) DO UPDATE SET value = 10",
                new { id }, cancellationToken: cancellationToken));
        }
    }
}

using System.Data.Common;

namespace FaultSweep.Postgres.Tests;

public sealed class SourceCommandHandler(DbDataSource source) : IMessageHandler<PostgresMessage>
{
    public async ValueTask HandleAsync(PostgresMessage message, CancellationToken cancellationToken)
    {
        await using DbCommand command = source.CreateCommand("UPDATE counters SET value = value + 10 WHERE id = 1 RETURNING value");
        if (message.Async)
        {
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
        }
        else
        {
            using DbDataReader reader = command.ExecuteReader();
            reader.Read();
        }
    }
}

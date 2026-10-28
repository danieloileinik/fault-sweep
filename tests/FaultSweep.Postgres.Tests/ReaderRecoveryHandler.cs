using System.Data.Common;

namespace FaultSweep.Postgres.Tests;

public sealed class ReaderRecoveryHandler(Func<DbConnection> connections) : IMessageHandler<PostgresMessage>
{
    public async ValueTask HandleAsync(PostgresMessage message, CancellationToken cancellationToken)
    {
        await using DbConnection connection = connections();
        if (message.Async)
        {
            await connection.OpenAsync(cancellationToken);
        }
        else
        {
            connection.Open();
        }

        await using DbCommand write = connection.CreateCommand();
        write.CommandText = "UPDATE counters SET value = 10 WHERE id = 1 RETURNING value";
        try
        {
            if (message.Async)
            {
                await using DbDataReader reader = await write.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
            }
            else
            {
                using DbDataReader reader = write.ExecuteReader();
                reader.Read();
            }
        }
        catch (TimeoutException)
        {
            await using DbCommand verify = connection.CreateCommand();
            verify.CommandText = "SELECT value FROM counters WHERE id = 1";
            object? value = message.Async
                ? await verify.ExecuteScalarAsync(cancellationToken)
                : verify.ExecuteScalar();
            if (value is not 10)
            {
                throw new InvalidOperationException("The write did not complete before losing its acknowledgement.");
            }
        }

        await using DbCommand finish = connection.CreateCommand();
        finish.CommandText = "UPDATE counters SET value = 10 WHERE id = 2";
        if (message.Async)
        {
            await finish.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            finish.ExecuteNonQuery();
        }
    }
}

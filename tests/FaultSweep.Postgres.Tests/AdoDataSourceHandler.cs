using System.Data.Common;

namespace FaultSweep.Postgres.Tests;

public sealed class AdoDataSourceHandler(DbDataSource source) : IMessageHandler<PostgresMessage>
{
    public async ValueTask HandleAsync(PostgresMessage message, CancellationToken cancellationToken)
    {
        await using DbConnection connection = source.CreateConnection();
        await AdoWorkload.RunAsync(connection, message, cancellationToken);
    }
}

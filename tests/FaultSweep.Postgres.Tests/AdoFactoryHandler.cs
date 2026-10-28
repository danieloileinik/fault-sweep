using System.Data.Common;

namespace FaultSweep.Postgres.Tests;

public sealed class AdoFactoryHandler(Func<DbConnection> connections) : IMessageHandler<PostgresMessage>
{
    public async ValueTask HandleAsync(PostgresMessage message, CancellationToken cancellationToken)
    {
        await using DbConnection connection = connections();
        await AdoWorkload.RunAsync(connection, message, cancellationToken);
    }
}

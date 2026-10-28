using System.Data.Common;

namespace FaultSweep.Postgres.Tests;

public sealed class DapperFactoryHandler(Func<DbConnection> connections) : IMessageHandler<PostgresMessage>
{
    public async ValueTask HandleAsync(PostgresMessage message, CancellationToken cancellationToken)
    {
        await using DbConnection connection = connections();
        await DapperWorkload.RunAsync(connection, message, cancellationToken);
    }
}

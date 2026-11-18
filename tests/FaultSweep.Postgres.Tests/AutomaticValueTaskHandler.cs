namespace FaultSweep.Postgres.Tests;

public sealed class AutomaticValueTaskHandler : IAsyncDisposable
{
    private readonly TestDatabase database;
    private readonly AutomaticHandlerLifetime lifetime;

    public AutomaticValueTaskHandler(TestDatabase database, AutomaticHandlerLifetime lifetime)
    {
        this.database = database;
        this.lifetime = lifetime;
        lifetime.RecordCreation();
    }

    public async ValueTask HandleAsync(AutomaticOrder message)
    {
        var handler = new AutomaticOrderHandler(database);
        await handler.HandleAsync(message, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.RecordDisposal();
        await database.ExecuteAsync("UPDATE counters SET value = 11 WHERE id = 2");
    }
}

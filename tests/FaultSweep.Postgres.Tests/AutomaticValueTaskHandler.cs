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

    public ValueTask DisposeAsync()
    {
        lifetime.RecordDisposal();
        return ValueTask.CompletedTask;
    }
}

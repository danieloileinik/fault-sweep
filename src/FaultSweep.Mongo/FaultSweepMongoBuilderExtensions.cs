using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FaultSweep.Mongo;

public static class FaultSweepMongoBuilderExtensions
{
    public static FaultSweepBuilder<THandler, TMessage> TrackMongo<THandler, TMessage>(
        this FaultSweepBuilder<THandler, TMessage> builder,
        string[] collections,
        string[]? ignore = null,
        Snapshots.SnapshotComparisonOptions? comparison = null,
        string name = "mongo"
    )
        where THandler : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(collections);
        string[] selected = [.. collections];
        string[] ignored = ignore is null ? [] : [.. ignore];
        return builder
            .ValidateWith(services =>
            {
                if (services.GetService<IMongoDatabase>() is not MongoProxy)
                {
                    throw new InvalidOperationException(
                        "TrackMongo requires services.AddFaultSweepMongo(database). Resolve collections from that database or use InstrumentMongo."
                    );
                }

                _ = new MongoSnapshotProvider(
                    services.GetRequiredService<IMongoDatabase>(),
                    selected,
                    ignored
                );
            })
            .TrackState(
                name,
                (services, token) =>
                    new MongoSnapshotProvider(
                        services.GetRequiredService<IMongoDatabase>(),
                        selected,
                        ignored
                    ).CaptureAsync(token),
                comparison
            );
    }
}

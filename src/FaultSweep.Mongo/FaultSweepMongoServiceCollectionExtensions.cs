using FaultSweep.Effects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace FaultSweep.Mongo;

public static class FaultSweepMongoServiceCollectionExtensions
{
    public static IServiceCollection AddFaultSweepMongo(
        this IServiceCollection services,
        IMongoDatabase database
    )
    {
        ArgumentNullException.ThrowIfNull(database);
        services.AddFaultSweep();
        services.Replace(
            ServiceDescriptor.Singleton(provider =>
                (IMongoDatabase)
                    MongoProxy.Wrap(
                        typeof(IMongoDatabase),
                        database,
                        provider.GetRequiredService<IEffectInterceptor>()
                    )
            )
        );
        return services;
    }

    public static IMongoCollection<T> InstrumentMongo<T>(
        this IMongoCollection<T> collection,
        IEffectInterceptor interceptor
    )
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(interceptor);
        return (IMongoCollection<T>)
            MongoProxy.Wrap(typeof(IMongoCollection<T>), collection, interceptor);
    }
}

using FaultSweep.Effects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace FaultSweep.Redis;

public static class FaultSweepRedisServiceCollectionExtensions
{
    public static IServiceCollection AddFaultSweepRedis(this IServiceCollection services)
    {
        services.AddFaultSweep();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEffectInstrumentation, RedisInstrumentation>());
        return services;
    }

    public static IServiceCollection AddFaultSweepRedis(this IServiceCollection services, IConnectionMultiplexer connection)
    {
        services.AddFaultSweepRedis();
        services.AddSingleton(connection);
        return services;
    }
}

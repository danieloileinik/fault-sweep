using FaultSweep.Effects;
using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace FaultSweep.Redis;

public static class FaultSweepRedisBuilderExtensions
{
    public static FaultSweepBuilder<THandler, TMessage> TrackRedis<THandler, TMessage>(this FaultSweepBuilder<THandler, TMessage> builder,
        string prefix, int database = 0, SnapshotComparisonOptions? comparison = null, string name = "redis") where THandler : class
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentOutOfRangeException.ThrowIfNegative(database);
        return builder.ValidateWith(services =>
        {
            if (!services.GetServices<IEffectInstrumentation>().OfType<RedisInstrumentation>().Any())
            {
                throw new InvalidOperationException("TrackRedis requires services.AddFaultSweepRedis().");
            }

            _ = services.GetRequiredService<IConnectionMultiplexer>();
        }).TrackState(name, (services, token) => new RedisSnapshotProvider(services.GetRequiredService<IConnectionMultiplexer>(), prefix, database).CaptureAsync(token), comparison);
    }
}

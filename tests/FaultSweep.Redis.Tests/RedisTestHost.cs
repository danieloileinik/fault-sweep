using FaultSweep.Reporting;
using FaultSweep.Xunit;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace FaultSweep.Redis.Tests;

public sealed class RedisTestHost : IAsyncDisposable
{
    public ServiceProvider Services { get; }

    private readonly IDatabase database;

    public RedisTestHost(IConnectionMultiplexer connection, bool generate = false)
    {
        database = connection.GetDatabase();
        var services = new ServiceCollection();
        services.AddSingleton(connection);
        services.AddSingleton(database);
        services.AddFaultSweepRedis();
        services.AddScoped<RedisHandler>();
        if (generate)
        {
            services.AddFaultSweepXunit(
                new XunitRegressionOptions
                {
                    FixtureTypeName = "global::FaultSweep.Redis.Tests.RedisFixture",
                    CollectionName = RedisCollection.Name,
                    Namespace = "Generated",
                }
            );
        }

        Services = services.BuildServiceProvider();
    }

    public FaultSweepBuilder<RedisHandler, RedisRequest> CreateSweep(RedisRequest request)
    {
        return FaultSweep
            .For<RedisHandler>(Services)
            .WithMessage(request)
            .ResetWith(
                async (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    await database.KeyDeleteAsync(request.Prefix + "value");
                }
            )
            .TrackRedis(request.Prefix);
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}

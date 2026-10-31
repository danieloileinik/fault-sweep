using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Images;
using StackExchange.Redis;
using Testcontainers.Redis;
using Testcontainers.PostgreSql;

namespace FaultSweep.Redis.Tests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer redis = new RedisBuilder("redis:8.8.4@sha256:d1aca0ff27750117ecec32e6388fc48a6c15182a87de9349bde9cf312c0fe301").Build();
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.6@sha256:979bccfde35445db618c65107d27d08c6c21339d8dc3ad4c830c6633947be0e4").Build();
    public ConnectionMultiplexer Connection { get; private set; } = null!;
    public string RedisConfiguration => redis.GetConnectionString();
    public string PostgresConfiguration => postgres.GetConnectionString();
    private readonly List<RedisTestHost> regressionHosts = [];
    public bool RegressionFixed { get; set; }

    public FaultSweepBuilder<RedisHandler, RedisRequest> CreateSweep(RedisRequest message)
    {
        var host = new RedisTestHost(Connection);
        regressionHosts.Add(host);
        return host.CreateSweep(message).HandleWith(async (handler, request, token) =>
        {
            if (RegressionFixed && await Connection.GetDatabase().KeyExistsAsync(request.Prefix + "value"))
            {
                return;
            }

            await handler.HandleAsync(request, token);
        });
    }

    public async Task InitializeAsync()
    {
        TestcontainersSettings.ResourceReaperImage = new DockerImage("testcontainers/ryuk:0.14.0@sha256:7c1a8a9a47c780ed0f983770a662f80deb115d95cce3e2daa3d12115b8cd28f0");
        await Task.WhenAll(redis.StartAsync(), postgres.StartAsync());
        Connection = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        foreach (RedisTestHost host in regressionHosts)
        {
            await host.DisposeAsync();
        }

        if (Connection is not null)
        {
            await Connection.DisposeAsync();
        }

        await redis.DisposeAsync();
        await postgres.DisposeAsync();
    }
}

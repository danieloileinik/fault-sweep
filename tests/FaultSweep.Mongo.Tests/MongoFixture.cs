using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Images;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace FaultSweep.Mongo.Tests;

public sealed class MongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer container = new MongoDbBuilder(
        "mongo:9.0.4@sha256:452bdaf2127b8ff851e31410a267809b9829eded219979f2210e7c7efb85a058"
    ).Build();

    public IMongoClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        TestcontainersSettings.ResourceReaperImage = new DockerImage(
            "testcontainers/ryuk:0.14.0@sha256:7c1a8a9a47c780ed0f983770a662f80deb115d95cce3e2daa3d12115b8cd28f0"
        );
        await container.StartAsync();
        Client = new MongoClient(container.GetConnectionString());
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Images;
using Testcontainers.Kafka;

namespace FaultSweep.Kafka.Tests;

public sealed class KafkaBrokerFixture : IAsyncLifetime
{
    private readonly KafkaContainer container = new KafkaBuilder(
        "apache/kafka:4.4.0@sha256:833b867a9585a141da455dc2b57431c65cbccb27f11c91f694444cca9f777e0f"
    )
        .WithKRaft()
        .Build();

    public string BootstrapServers => container.GetBootstrapAddress();

    public async Task InitializeAsync()
    {
        TestcontainersSettings.ResourceReaperImage = new DockerImage(
            "testcontainers/ryuk:0.14.0@sha256:7c1a8a9a47c780ed0f983770a662f80deb115d95cce3e2daa3d12115b8cd28f0"
        );
        await container.StartAsync();
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

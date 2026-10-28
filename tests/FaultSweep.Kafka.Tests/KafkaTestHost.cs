using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Kafka.Tests;

public sealed class KafkaTestHost : IAsyncDisposable
{
    public KafkaTestHost()
    {
        var services = new ServiceCollection();
        services.AddFaultSweepKafka<string, EventPayload>();
        services.AddFaultSweepKafka<string, EventPayload>();
        services.AddScoped<PublishingHandler>();
        Services = services.BuildServiceProvider();
    }

    public ServiceProvider Services { get; }

    public FaultSweepBuilder<PublishingHandler, PublishRequest> Sweep(PublishRequest request,
        ArrayComparison comparison = ArrayComparison.Multiset)
    {
        return FaultSweep.For<PublishingHandler>(Services).WithMessage(request)
            .ResetWith((_, _) => ValueTask.CompletedTask)
            .TrackProduced(comparison);
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}

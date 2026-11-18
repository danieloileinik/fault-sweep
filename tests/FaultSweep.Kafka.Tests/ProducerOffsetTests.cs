using Confluent.Kafka;
using FaultSweep.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Kafka.Tests;

public sealed class ProducerOffsetTests
{
    [Fact]
    public async Task Assigns_offsets_per_topic_and_partition_across_retries_and_resets()
    {
        // Arrange
        await using var host = new KafkaTestHost();
        using IServiceScope scope = host.Services.CreateScope();
        var producer = scope.ServiceProvider.GetRequiredService<IProducer<string, EventPayload>>();
        var message = new Message<string, EventPayload> { Key = "id", Value = new EventPayload() };
        TopicPartition[] destinations =
        [
            new TopicPartition("orders", 1),
            new TopicPartition("billing", 0),
            new TopicPartition("orders", 0),
        ];
        List<List<long>> deliveries = [];
        var sweep = host.Sweep(new PublishRequest())
            .HandleWith(
                async (_, _, token) =>
                {
                    List<long> offsets = [];
                    deliveries.Add(offsets);
                    producer.Produce("orders", message, report => offsets.Add(report.Offset.Value));
                    foreach (TopicPartition destination in destinations)
                    {
                        var report = await producer.ProduceAsync(destination, message, token);
                        offsets.Add(report.Offset.Value);
                    }
                }
            )
            .ReplayScenario(new ReplayScenario(4, FaultKind.CrashBefore));

        // Act
        var result = await sweep.RunAsync();

        // Assert
        Assert.Equal(2, Assert.Single(result.Scenarios).Attempts.Count);
        Assert.Collection(
            deliveries,
            baseline => Assert.Equal([0L, 0L, 0L, 1L], baseline),
            faulted => Assert.Equal([0L, 0L, 0L], faulted),
            retried => Assert.Equal([1L, 1L, 1L, 2L], retried)
        );
    }
}

using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Kafka.Tests;

[Collection(KafkaBrokerCollection.Name)]
public sealed class KafkaBrokerTests(KafkaBrokerFixture fixture)
{
    [Theory]
    [InlineData(ArrayComparison.Set, false)]
    [InlineData(ArrayComparison.Multiset, true)]
    public async Task Real_broker_preserves_logical_events_and_exposes_physical_duplicates(
        ArrayComparison mode,
        bool violations
    )
    {
        // Arrange
        string topic = "sweep-" + Guid.NewGuid().ToString("N");
        using IAdminClient admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = fixture.BootstrapServers }
        ).Build();
        await admin.CreateTopicsAsync([
            new TopicSpecification
            {
                Name = topic,
                NumPartitions = 1,
                ReplicationFactor = 1,
            },
        ]);
        using IProducer<string, string> producer = new ProducerBuilder<string, string>(
            new ProducerConfig
            {
                BootstrapServers = fixture.BootstrapServers,
                EnableIdempotence = true,
                MessageTimeoutMs = 10000,
            }
        ).Build();
        var services = new ServiceCollection();
        services.AddFaultSweep();
        services.AddSingleton(producer);
        services.AddScoped<BrokerPublishingHandler>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        long startOffset = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        using IConsumer<string, string> consumer = new ConsumerBuilder<string, string>(
            new ConsumerConfig
            {
                BootstrapServers = fixture.BootstrapServers,
                GroupId = Guid.NewGuid().ToString("N"),
                EnableAutoCommit = false,
                AutoOffsetReset = AutoOffsetReset.Earliest,
            }
        ).Build();
        var partition = new TopicPartition(topic, 0);
        try
        {
            // Act
            FaultSweepResult result = await FaultSweep
                .For<BrokerPublishingHandler>(provider)
                .WithMessage(topic)
                .ResetWith(
                    (_, _) =>
                    {
                        startOffset = consumer
                            .QueryWatermarkOffsets(partition, TimeSpan.FromSeconds(10))
                            .High.Value;
                        return ValueTask.CompletedTask;
                    }
                )
                .TrackState(
                    "broker",
                    (_, token) =>
                    {
                        long end = consumer
                            .QueryWatermarkOffsets(partition, TimeSpan.FromSeconds(10))
                            .High.Value;
                        consumer.Assign(
                            new TopicPartitionOffset(partition, new Offset(startOffset))
                        );
                        List<object> messages = [];
                        while (consumer.Position(partition).Value < end)
                        {
                            ConsumeResult<string, string> consumed = consumer.Consume(token);
                            if (consumed.Offset.Value >= startOffset && consumed.Offset.Value < end)
                            {
                                messages.Add(new { consumed.Message.Key, consumed.Message.Value });
                            }
                        }
                        return ValueTask.FromResult(StateSnapshot.From(messages));
                    },
                    new SnapshotComparisonOptions { ArrayComparison = mode }
                )
                .WithDuplicateDeliveries()
                .RunAsync(timeout.Token);

            // Assert
            Assert.Equal(violations, result.HasViolations);
            Assert.All(
                result.Scenarios,
                scenario => Assert.True(scenario.Attempts[^1].Succeeded, result.ToString())
            );
            if (violations)
            {
                Assert.Contains(
                    result.Violations,
                    violation => violation.Scenario.Kind == FaultKind.AppliedButFailed
                );
                Assert.Equal(2, result.DuplicateViolations.Count);
            }
        }
        finally
        {
            await admin.DeleteTopicsAsync([topic]);
        }
    }
}

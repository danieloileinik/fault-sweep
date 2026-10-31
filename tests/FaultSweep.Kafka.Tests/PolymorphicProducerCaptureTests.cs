using System.Text.Json;
using Confluent.Kafka;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Kafka.Tests;

public sealed class PolymorphicProducerCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Captures_derived_members_and_detects_changed_payloads_after_a_timeout(
        bool useAsync
    )
    {
        // Arrange
        var key = new PaymentEvent("payment", 42);

        // Act
        FaultSweepResult[] results =
        [
            await RunAsync<PolymorphicEvent, IPolymorphicEvent>(
                key,
                amount => new PaymentEvent("payment", amount),
                useAsync
            ),
            await RunAsync<IPolymorphicEvent, PolymorphicEvent>(
                key,
                amount => new PaymentEvent("payment", amount),
                useAsync
            ),
        ];

        // Assert
        Assert.All(
            results,
            result =>
            {
                JsonElement baseline = result.BaselineState["produced"].Value[0];
                Assert.Equal(42, baseline.GetProperty("Key").GetProperty("Amount").GetInt32());
                Assert.Equal(10, baseline.GetProperty("Value").GetProperty("Amount").GetInt32());

                FaultScenarioResult violation = Assert.Single(result.Violations);
                Assert.Equal(FaultKind.AppliedButFailed, violation.Scenario.Kind);
                Assert.Equal(ViolationKind.StateMismatch, violation.Violations);
                Assert.Equal(
                    [10, 999],
                    violation
                        .State["produced"]
                        .Value.EnumerateArray()
                        .Select(message =>
                            message.GetProperty("Value").GetProperty("Amount").GetInt32()
                        )
                );
            }
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preserves_null_keys_and_tombstone_values(bool useAsync)
    {
        // Arrange
        PolymorphicEvent? key = null;

        // Act
        FaultSweepResult result = await RunAsync<PolymorphicEvent?, IPolymorphicEvent?>(
            key,
            _ => null,
            useAsync
        );

        // Assert
        result.ShouldHaveNoViolations();
        JsonElement captured = result.BaselineState["produced"].Value[0];
        Assert.Equal(JsonValueKind.Null, captured.GetProperty("Key").ValueKind);
        Assert.Equal(JsonValueKind.Null, captured.GetProperty("Value").ValueKind);
    }

    private static async Task<FaultSweepResult> RunAsync<TKey, TValue>(
        TKey key,
        Func<int, TValue> createValue,
        bool useAsync
    )
    {
        var services = new ServiceCollection();
        services.AddFaultSweepKafka<TKey, TValue>();
        await using ServiceProvider provider = services.BuildServiceProvider();

        return await FaultSweep
            .For<IProducer<TKey, TValue>>(provider)
            .WithMessage("payment")
            .ResetWith((_, _) => ValueTask.CompletedTask)
            .HandleWith(
                async (producer, id, token) =>
                {
                    async ValueTask ProduceAsync(int amount)
                    {
                        var message = new Message<TKey, TValue>
                        {
                            Key = key,
                            Value = createValue(amount),
                        };
                        if (useAsync)
                        {
                            await producer.ProduceAsync(id, message, token);
                        }
                        else
                        {
                            producer.Produce(id, message);
                        }
                    }

                    try
                    {
                        await ProduceAsync(10);
                    }
                    catch (TimeoutException)
                    {
                        await ProduceAsync(999);
                    }
                }
            )
            .TrackProduced(ArrayComparison.Set)
            .RunAsync();
    }
}

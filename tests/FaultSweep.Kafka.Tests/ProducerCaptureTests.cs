using System.Text.Json;
using Confluent.Kafka;
using FaultSweep.Effects;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Kafka.Tests;

public sealed class ProducerCaptureTests
{
    [Fact]
    public async Task Missing_capture_configuration_is_rejected_before_the_handler_or_reset_can_run()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddFaultSweep();
        services.AddScoped<PublishingHandler>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        bool reset = false;
        bool invoked = false;

        // Act
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FaultSweep.For<PublishingHandler>(provider).WithMessage(new PublishRequest())
                .ResetWith((_, _) =>
                {
                    reset = true;
                    return ValueTask.CompletedTask;
                })
                .HandleWith((_, _, _) =>
                {
                    invoked = true;
                    return ValueTask.CompletedTask;
                })
                .TrackProduced().RunAsync());

        // Assert
        Assert.False(reset);
        Assert.False(invoked);
        Assert.Contains("AddFaultSweepKafka", exception.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Captures_all_produce_overloads_and_models_fault_boundaries(bool useAsync, bool explicitPartition)
    {
        // Arrange
        await using var host = new KafkaTestHost();

        // Act
        FaultSweepResult result = await host.Sweep(new PublishRequest(useAsync, explicitPartition)).RunAsync();

        // Assert
        Assert.Equal("kafka", Assert.Single(result.BaselineTrace).Point.Effect.Source);
        Assert.Equal(3, result.Scenarios.Count);
        Assert.Equal(2, result.Violations.Count);
        Assert.All(result.Scenarios, scenario =>
        {
            int expected = scenario.Scenario.Kind == FaultKind.CrashBefore ? 1 : 2;
            Assert.Equal(expected, scenario.State["produced"].Value.GetArrayLength());
            Assert.True(scenario.Attempts[^1].Succeeded, result.ToString());
        });
        Assert.Equal(explicitPartition ? 2 : -1, result.BaselineState["produced"].Value[0].GetProperty("Partition").GetInt32());
    }

    [Fact]
    public async Task Clears_capture_per_scenario_and_between_repeated_sweeps()
    {
        // Arrange
        await using var host = new KafkaTestHost();

        // Act
        FaultSweepResult first = await host.Sweep(new PublishRequest()).RunAsync();
        FaultSweepResult second = await host.Sweep(new PublishRequest()).RunAsync();

        // Assert
        Assert.Equal(1, first.BaselineState["produced"].Value.GetArrayLength());
        Assert.Equal(1, second.BaselineState["produced"].Value.GetArrayLength());
        Assert.Equal(first.ToString(), second.ToString());
    }

    [Theory]
    [InlineData(ArrayComparison.Set, false)]
    [InlineData(ArrayComparison.Multiset, true)]
    public async Task Compares_events_as_a_set_or_multiset(ArrayComparison comparison, bool hasViolations)
    {
        // Arrange
        await using var host = new KafkaTestHost();

        // Act
        FaultSweepResult result = await host.Sweep(new PublishRequest(), comparison).RunAsync();

        // Assert
        Assert.Equal(hasViolations, result.HasViolations);
    }

    [Fact]
    public async Task Freezes_payloads_and_headers_at_the_time_of_production()
    {
        // Arrange
        await using var host = new KafkaTestHost();

        // Act
        FaultSweepResult result = await host.Sweep(new PublishRequest(Mutate: true)).RunAsync();

        // Assert
        JsonElement captured = result.BaselineState["produced"].Value[0];
        Assert.Equal(10, captured.GetProperty("Value").GetProperty("Amount").GetInt32());
        Assert.Equal("AQI=", captured.GetProperty("Headers")[0].GetProperty("Value").GetString());
    }

    [Fact]
    public async Task Keeps_independent_concurrent_sweeps_isolated()
    {
        // Arrange
        await using var first = new KafkaTestHost();
        await using var second = new KafkaTestHost();

        // Act
        FaultSweepResult[] results = await Task.WhenAll(
            first.Sweep(new PublishRequest()).RunAsync(), second.Sweep(new PublishRequest()).RunAsync());

        // Assert
        Assert.All(results, result =>
        {
            Assert.Equal(1, result.BaselineState["produced"].Value.GetArrayLength());
            Assert.Equal(2, result.Violations.Count);
        });
    }

    [Fact]
    public async Task Rejects_transactional_producers_even_when_the_handler_catches_the_exception()
    {
        // Arrange
        await using var host = new KafkaTestHost();
        bool caught = false;

        // Act
        FaultSweepExecutionException exception = await Assert.ThrowsAsync<FaultSweepExecutionException>(() =>
            host.Sweep(new PublishRequest()).HandleWith((_, _, _) =>
            {
                using IServiceScope scope = host.Services.CreateScope();
                try
                {
                    scope.ServiceProvider.GetRequiredService<IProducer<string, EventPayload>>().BeginTransaction();
                }
                catch (FaultSweepExecutionException)
                {
                    caught = true;
                }

                return ValueTask.CompletedTask;
            }).RunAsync());

        // Assert
        Assert.True(caught);
        Assert.Contains("Kafka transactions", exception.Message);
    }
}

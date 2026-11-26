using System.Text.Json;
using FaultSweep.Effects;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Redis.Tests;

public sealed class MinimizationTests
{
    [Fact]
    public async Task Shrinks_the_message_and_replays_the_smallest_fault()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddFaultSweep();
        var state = new MinimizeState();
        services.AddSingleton(state);
        services.AddScoped<MinimizeHandler>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        FaultSweepBuilder<MinimizeHandler, MinimizeRequest> Create(int amount) => FaultSweep.For<MinimizeHandler>(provider)
            .WithMessage(new MinimizeRequest(amount)).ResetWith((_, _) => { state.Value = 0; return ValueTask.CompletedTask; })
            .TrackState("counter", (_, _) => ValueTask.FromResult(StateSnapshot.From(state.Value)));

        // Act
        FaultSweepResult result = await Create(10).MinimizeMessageWith(
            message => Enumerable.Range(0, message.Amount).Select(value => new MinimizeRequest(value)), message => message.Amount).RunAsync();
        MinimizedScenario minimum = Assert.IsType<MinimizedScenario>(result.MinimalScenario);
        MinimizeRequest message = JsonSerializer.Deserialize<MinimizeRequest>(minimum.MessageJson!)!;
        FaultSweepResult replayed = await Create(message.Amount).ReplayScenario(new ReplayScenario(
            minimum.Result.Scenario.Point.Index, minimum.Result.Scenario.Kind, ExpectedEffect: minimum.Result.Scenario.Point.Effect)).RunAsync();

        // Assert
        Assert.Equal(1, message.Amount);
        Assert.Equal(FaultKind.CrashAfter, minimum.Result.Scenario.Kind);
        Assert.True(Assert.Single(replayed.Scenarios).HasViolation);
        Assert.False(minimum.BudgetExhausted);
        Assert.Contains("Reduced input:", result.ToString());
        string reducedReport = result.ToDetailedString().Split("Reduced input:")[1];
        Assert.Contains("1 in clean run, 2 observed", reducedReport);
        Assert.DoesNotContain("10 in clean run, 20 observed", reducedReport);
    }
    [Fact]
    public async Task Bounds_cyclic_shrinkers_and_rejects_moved_replay_points()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddFaultSweep();
        var state = new MinimizeState();
        services.AddSingleton(state);
        services.AddScoped<MinimizeHandler>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        FaultSweepBuilder<MinimizeHandler, MinimizeRequest> Create() => FaultSweep.For<MinimizeHandler>(provider).WithMessage(new MinimizeRequest(10))
            .ResetWith((_, _) => { state.Value = 0; return ValueTask.CompletedTask; })
            .TrackState("value", (_, _) => ValueTask.FromResult(StateSnapshot.From(state.Value)));

        // Act
        FaultSweepResult result = await Create().WithOptions(new FaultSweepOptions { MaxMinimizationRuns = 2 })
            .MinimizeMessageWith(message => Enumerable.Repeat(message, 10000), message => message.Amount).RunAsync();
        FaultSweepExecutionException error = await Assert.ThrowsAsync<FaultSweepExecutionException>(() => Create()
            .ReplayScenario(new ReplayScenario(1, FaultKind.CrashAfter, ExpectedEffect: new EffectDescriptor("other", "operation"))).RunAsync());

        // Assert
        Assert.True(result.MinimalScenario!.BudgetExhausted);
        Assert.Equal(2, result.MinimalScenario.EvaluatedCandidates);
        Assert.Contains("replay effect", error.Message);
    }
}

using FaultSweep.Effects;
using FaultSweep.Reporting;

namespace FaultSweep.Tests;

public sealed class SweepIsolationTests
{
    [Fact]
    public async Task Serializes_sweeps_that_share_an_environment()
    {
        // Arrange
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int deliveries = 0;
        var behavior = new TestBehavior(async (state, effects, token) =>
        {
            if (++deliveries == 1)
            {
                entered.SetResult(true);
                await release.Task.WaitAsync(token);
            }

            await SweepTestEnvironment.Apply(effects, "upsert", () => state.Counter = 10, token);
        });
        using var environment = new SweepTestEnvironment(behavior);

        // Act
        Task<FaultSweepResult> first = environment.CreateSweep().RunAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<FaultSweepResult> second = environment.CreateSweep().RunAsync();
        int resetsWhileFirstDeliveryBlocked = environment.State.Resets;
        bool secondWasBlocked = !second.IsCompleted;
        release.SetResult(true);
        FaultSweepResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(1, resetsWhileFirstDeliveryBlocked);
        Assert.True(secondWasBlocked);
        Assert.Equal(8, environment.State.Resets);
        Assert.All(results, result => Assert.False(result.HasViolations, result.ToString()));
    }

    [Fact]
    public async Task Rejects_overlapping_effects_without_resetting_over_running_operations()
    {
        // Arrange
        bool firstCompleted = false;
        var behavior = new TestBehavior(async (state, effects, token) =>
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task first = effects.ExecuteAsync(new EffectDescriptor("memory", "first"), async operationToken =>
            {
                await release.Task.WaitAsync(operationToken);
                firstCompleted = true;
            }, token).AsTask();
            try
            {
                await SweepTestEnvironment.Apply(effects, "second", () => state.Counter++, token);
            }
            finally
            {
                release.SetResult(true);
                await first;
            }
        });
        using var environment = new SweepTestEnvironment(behavior);

        // Act
        FaultSweepExecutionException exception = await Assert.ThrowsAsync<FaultSweepExecutionException>(
            () => environment.CreateSweep().RunAsync());

        // Assert
        Assert.True(firstCompleted);
        Assert.Contains("Overlapping effects", exception.Message);
        Assert.Equal(1, environment.State.Resets);
    }

    [Fact]
    public async Task Propagates_cancellation_while_waiting_for_the_shared_environment()
    {
        // Arrange
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int deliveries = 0;
        var behavior = new TestBehavior(async (state, effects, token) =>
        {
            if (++deliveries == 1)
            {
                entered.SetResult(true);
                await release.Task.WaitAsync(token);
            }

            await SweepTestEnvironment.Apply(effects, "upsert", () => state.Counter = 10, token);
        });
        using var environment = new SweepTestEnvironment(behavior);
        using var cancellation = new CancellationTokenSource();

        // Act
        Task<FaultSweepResult> first = environment.CreateSweep().RunAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<FaultSweepResult> waiting = environment.CreateSweep().RunAsync(cancellation.Token);
        cancellation.Cancel();
        Exception? exception = await Record.ExceptionAsync(() => waiting);
        release.SetResult(true);
        FaultSweepResult completed = await first.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(4, environment.State.Resets);
        Assert.False(completed.HasViolations, completed.ToString());
    }
}

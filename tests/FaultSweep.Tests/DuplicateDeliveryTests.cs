using System.Text.Json;
using FaultSweep.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Tests;

public sealed class DuplicateDeliveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checks_both_duplicate_modes_against_the_clean_run(bool idempotent)
    {
        // Arrange
        using var environment = new SweepTestEnvironment(
            new TestBehavior(
                (state, effects, token) =>
                    SweepTestEnvironment.Apply(
                        effects,
                        "debit",
                        () =>
                        {
                            lock (state)
                            {
                                state.Counter = idempotent ? 10 : state.Counter + 10;
                            }
                        },
                        token
                    )
            )
        );

        // Act
        FaultSweepResult result = await environment
            .CreateSweep()
            .WithDuplicateDeliveries()
            .RunAsync();

        // Assert
        Assert.Equal(!idempotent, result.HasViolations);
        Assert.Equal(2, result.DuplicateScenarios.Count);
        Assert.All(
            result.DuplicateScenarios,
            scenario =>
            {
                Assert.Equal(!idempotent, scenario.HasViolation);
                Assert.Equal([1, 2], scenario.Attempts.Select(attempt => attempt.Number));
                Assert.All(scenario.Attempts, attempt => Assert.True(attempt.Succeeded));
                Assert.Equal([1, 2], scenario.Trace.Select(entry => entry.Attempt));
            }
        );
        Assert.Equal(environment.State.HandlerInstances, environment.State.DisposedHandlers);
    }

    [Fact]
    public async Task Reproduces_an_inbox_race_and_keeps_parallel_deliveries_in_distinct_scopes()
    {
        // Arrange
        int entries = 0;
        int waiting = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var environment = new SweepTestEnvironment(
            new TestBehavior(
                async (state, effects, token) =>
                {
                    bool processed = state.Inbox;
                    if (Interlocked.Increment(ref entries) > 1)
                    {
                        if (Interlocked.Increment(ref waiting) == 2)
                        {
                            barrier.SetResult();
                        }
                        await barrier.Task.WaitAsync(token);
                    }
                    if (!processed)
                    {
                        await SweepTestEnvironment.Apply(
                            effects,
                            "commit",
                            () =>
                            {
                                lock (state)
                                {
                                    state.Counter++;
                                    state.Inbox = true;
                                }
                            },
                            token
                        );
                    }
                }
            )
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        FaultSweepResult result = await environment
            .CreateSweep()
            .ReplayDuplicates(DuplicateDeliveryMode.Parallel)
            .RunAsync(timeout.Token);

        // Assert
        Assert.Empty(result.Scenarios);
        DuplicateScenarioResult violation = Assert.Single(result.DuplicateViolations);
        Assert.Equal(2, violation.State["state"].Value.GetProperty("Counter").GetInt32());
        Assert.Equal(3, environment.State.HandlerInstances);
        Assert.Equal(3, environment.State.DeliveryInstances.Distinct().Count());
        Assert.NotNull(result.MinimalDuplicateScenario);
        Assert.Throws<FaultSweepAssertionException>(result.ShouldHaveNoViolations);
        Assert.Contains("Duplicate deliveries: Parallel", result.ToString());
    }

    [Theory]
    [InlineData(DuplicateDeliveryMode.Sequential)]
    [InlineData(DuplicateDeliveryMode.Parallel)]
    public async Task Supports_invariant_only_verification_and_message_minimization(
        DuplicateDeliveryMode mode
    )
    {
        // Arrange
        using var environment = new SweepTestEnvironment(
            ZooBehaviors.Create(ZooCase.NonIdempotentCounter)
        );
        var modes = new List<DuplicateDeliveryMode?>();

        // Act
        FaultSweepResult result = await environment
            .CreateSweep()
            .HandleWith(
                (_, message, token) =>
                    SweepTestEnvironment.Apply(
                        environment.Host.Services.GetRequiredService<Effects.IEffectInterceptor>(),
                        "debit",
                        () =>
                        {
                            lock (environment.State)
                            {
                                environment.State.Counter += message.Amount;
                            }
                        },
                        token
                    )
            )
            .WithOptions(new FaultSweepOptions { CompareWithBaseline = false })
            .ReplayDuplicates(mode)
            .CheckInvariant(
                "one debit",
                context =>
                {
                    modes.Add(context.Duplicates);
                    return context.GetState<TestState>("state").Counter == context.Message.Amount;
                }
            )
            .MinimizeMessageWith(
                message =>
                    Enumerable.Range(0, message.Amount).Select(amount => new TestMessage(amount)),
                message => message.Amount
            )
            .RunAsync();

        // Assert
        Assert.Null(result.MinimalScenario);
        MinimizedDuplicateScenario minimum = Assert.IsType<MinimizedDuplicateScenario>(
            result.MinimalDuplicateScenario
        );
        Assert.Equal(1, JsonSerializer.Deserialize<TestMessage>(minimum.MessageJson!)!.Amount);
        Assert.Equal(mode, minimum.Result.Mode);
        Assert.Equal(ViolationKind.InvariantFailed, minimum.Result.Violations);
        Assert.Contains(null, modes);
        Assert.Contains(mode, modes);
        Assert.Empty(minimum.Result.Verification.Differences);
    }

    [Theory]
    [InlineData(DuplicateDeliveryMode.Sequential)]
    [InlineData(DuplicateDeliveryMode.Parallel)]
    public async Task Retries_each_duplicate_delivery_independently(DuplicateDeliveryMode mode)
    {
        // Arrange
        int calls = 0;
        using var environment = new SweepTestEnvironment(
            new TestBehavior(
                (state, effects, token) =>
                {
                    if (Interlocked.Increment(ref calls) == 2)
                    {
                        throw new InvalidOperationException("Retry this delivery.");
                    }

                    return SweepTestEnvironment.Apply(
                        effects,
                        "upsert",
                        () => state.Counter = 10,
                        token
                    );
                }
            )
        );

        // Act
        FaultSweepResult result = await environment.CreateSweep().ReplayDuplicates(mode).RunAsync();

        // Assert
        result.ShouldHaveNoViolations();
        DuplicateScenarioResult scenario = Assert.Single(result.DuplicateScenarios);
        Assert.Equal(3, scenario.Attempts.Count);
        Assert.Single(scenario.Attempts, attempt => !attempt.Succeeded);
        Assert.Equal(4, calls);
        Assert.Equal(
            [1, 2],
            scenario.Attempts.Select(attempt => attempt.Identity.Delivery).Distinct().Order()
        );
        foreach (
            IGrouping<int, DeliveryAttempt> delivery in scenario.Attempts.GroupBy(attempt =>
                attempt.Identity.Delivery
            )
        )
        {
            Assert.Equal(
                Enumerable.Range(1, delivery.Count()),
                delivery.Select(attempt => attempt.Identity.Attempt)
            );
        }
        Assert.All(
            scenario.Trace,
            entry =>
                Assert.Equal(
                    scenario.Attempts.Single(attempt => attempt.Number == entry.Attempt).Identity,
                    entry.Identity
                )
        );
    }

    [Fact]
    public async Task Cancels_both_deliveries_before_releasing_the_environment()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int entered = 0;
        int active = 0;
        int waiting = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var environment = new SweepTestEnvironment(
            new TestBehavior(
                async (state, effects, token) =>
                {
                    if (Interlocked.Increment(ref entered) > 1)
                    {
                        Interlocked.Increment(ref active);
                        try
                        {
                            if (Interlocked.Increment(ref waiting) == 2)
                            {
                                barrier.TrySetResult();
                            }
                            await barrier.Task.WaitAsync(token);
                            cancellation.Cancel();
                            await Task.Delay(Timeout.Infinite, token);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref active);
                        }
                    }
                    await SweepTestEnvironment.Apply(
                        effects,
                        "upsert",
                        () => state.Counter = 10,
                        token
                    );
                }
            )
        );

        // Act
        Exception? error = await Record.ExceptionAsync(() =>
            environment
                .CreateSweep()
                .ReplayDuplicates(DuplicateDeliveryMode.Parallel)
                .RunAsync(cancellation.Token)
        );

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(0, active);
        Assert.Equal(environment.State.HandlerInstances, environment.State.DisposedHandlers);
    }
}

using FaultSweep.Effects;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Tests;

public sealed class SweepRunnerTests
{
    [Fact]
    public async Task Injects_each_fault_once_at_every_baseline_effect()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(
            ZooBehaviors.Create(ZooCase.IdempotentUpsert)
        );

        // Act
        FaultSweepResult result = await environment.CreateSweep().RunAsync();

        // Assert
        Assert.Equal(6, result.Scenarios.Count);
        Assert.Equal(7, environment.State.Resets);
        Assert.All(
            result.Scenarios,
            scenario =>
            {
                Assert.Equal(2, scenario.Attempts.Count);
                Assert.False(scenario.Attempts[0].Succeeded);
                Assert.True(scenario.Attempts[1].Succeeded);
                Assert.Single(
                    scenario.Trace,
                    entry =>
                        entry.Outcome
                            is EffectOutcome.CrashBefore
                                or EffectOutcome.CrashAfter
                                or EffectOutcome.AppliedButFailed
                );
            }
        );
    }

    [Theory]
    [InlineData(FaultKind.CrashBefore, 1)]
    [InlineData(FaultKind.CrashAfter, 2)]
    [InlineData(FaultKind.AppliedButFailed, 2)]
    public async Task Applies_operation_on_the_correct_side_of_the_fault(
        FaultKind kind,
        int expected
    )
    {
        // Arrange
        using var environment = new SweepTestEnvironment(
            ZooBehaviors.Create(ZooCase.NonIdempotentCounter)
        );

        // Act
        FaultSweepResult result = await environment
            .CreateSweep()
            .WithOptions(new FaultSweepOptions { FaultKinds = [kind] })
            .RunAsync();

        // Assert
        FaultScenarioResult scenario = Assert.Single(result.Scenarios);
        Assert.Equal(expected, scenario.State["state"].Value.GetProperty("Counter").GetInt32());
        Assert.Equal(expected != 1, scenario.HasViolation);
    }

    [Fact]
    public async Task Uses_and_disposes_a_new_handler_scope_per_delivery()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(
            ZooBehaviors.Create(ZooCase.IdempotentUpsert)
        );

        // Act
        FaultSweepResult result = await environment.CreateSweep().RunAsync();

        // Assert
        int expected = 1 + result.Scenarios.Sum(scenario => scenario.Attempts.Count);
        Assert.Equal(expected, environment.State.HandlerInstances);
        Assert.Equal(expected, environment.State.DisposedHandlers);
        Assert.Equal(expected, environment.State.DeliveryInstances.Distinct().Count());
    }

    [Fact]
    public async Task Allows_handler_to_recover_from_lost_acknowledgement_without_redelivery()
    {
        // Arrange
        var behavior = new TestBehavior(
            async (state, effects, token) =>
            {
                try
                {
                    await SweepTestEnvironment.Apply(
                        effects,
                        "upsert",
                        () => state.Counter = 10,
                        token
                    );
                }
                catch (TimeoutException)
                {
                    Assert.Equal(10, state.Counter);
                }
            }
        );
        using var environment = new SweepTestEnvironment(behavior);

        // Act
        FaultSweepResult result = await environment.CreateSweep().RunAsync();

        // Assert
        Assert.False(result.HasViolations, result.ToString());
        FaultScenarioResult timeout = Assert.Single(
            result.Scenarios,
            scenario => scenario.Scenario.Kind == FaultKind.AppliedButFailed
        );
        Assert.True(Assert.Single(timeout.Attempts).Succeeded);
    }

    [Fact]
    public async Task Keeps_crash_latched_when_handler_catches_it()
    {
        // Arrange
        var behavior = new TestBehavior(
            async (state, effects, token) =>
            {
                try
                {
                    await SweepTestEnvironment.Apply(
                        effects,
                        "upsert",
                        () => state.Counter = 10,
                        token
                    );
                }
                catch (SimulatedCrashException) { }

                await SweepTestEnvironment.Apply(effects, "cache", () => state.Cache = true, token);
            }
        );
        using var environment = new SweepTestEnvironment(behavior);

        // Act
        FaultSweepResult result = await environment.CreateSweep().RunAsync();

        // Assert
        Assert.False(result.HasViolations, result.ToString());
        Assert.All(result.Scenarios, scenario => Assert.Equal(2, scenario.Attempts.Count));
        FaultScenarioResult first = result.Scenarios[0];
        Assert.Single(first.Trace, entry => entry.Attempt == 1);
    }

    [Fact]
    public async Task Reports_retry_exhaustion_even_when_state_matches()
    {
        // Arrange
        var behavior = new TestBehavior(
            async (state, effects, token) =>
            {
                await SweepTestEnvironment.Apply(
                    effects,
                    "upsert",
                    () => state.Counter = 10,
                    token
                );
                if (state.Resets > 1)
                {
                    throw new InvalidOperationException("Cannot acknowledge delivery.");
                }
            }
        );
        using var environment = new SweepTestEnvironment(behavior);

        // Act
        FaultSweepResult result = await environment
            .CreateSweep()
            .WithOptions(new FaultSweepOptions { MaxRedeliveryAttempts = 2 })
            .RunAsync();

        // Assert
        Assert.All(
            result.Scenarios,
            scenario =>
            {
                Assert.Equal(ViolationKind.RedeliveryLimitExceeded, scenario.Violations);
                Assert.Empty(scenario.Differences);
                Assert.Equal(3, scenario.Attempts.Count);
            }
        );
    }

    [Fact]
    public async Task Reports_unreached_fault_instead_of_returning_a_false_pass()
    {
        // Arrange
        var behavior = new TestBehavior(
            async (state, effects, token) =>
            {
                if (state.Resets == 1)
                {
                    await SweepTestEnvironment.Apply(
                        effects,
                        "upsert",
                        () => state.Counter = 10,
                        token
                    );
                }
                else
                {
                    state.Counter = 10;
                }
            }
        );
        using var environment = new SweepTestEnvironment(behavior);

        // Act
        FaultSweepResult result = await environment.CreateSweep().RunAsync();

        // Assert
        Assert.All(
            result.Scenarios,
            scenario => Assert.Equal(ViolationKind.FaultNotReached, scenario.Violations)
        );
    }

    [Fact]
    public async Task Rejects_a_diverging_initial_effect_trace()
    {
        // Arrange
        var behavior = new TestBehavior(
            (state, effects, token) =>
                SweepTestEnvironment.Apply(
                    effects,
                    state.Resets == 1 ? "first" : "different",
                    () => state.Counter = 1,
                    token
                )
        );
        using var environment = new SweepTestEnvironment(behavior);

        // Act
        FaultSweepExecutionException exception =
            await Assert.ThrowsAsync<FaultSweepExecutionException>(() =>
                environment.CreateSweep().RunAsync()
            );

        // Assert
        Assert.Contains("diverged", exception.Message);
    }

    [Fact]
    public async Task Rejects_effect_limit_overflow_even_when_handler_catches_it()
    {
        // Arrange
        var behavior = new TestBehavior(
            async (state, effects, token) =>
            {
                try
                {
                    await SweepTestEnvironment.Apply(
                        effects,
                        "first",
                        () => state.Counter = 1,
                        token
                    );
                    await SweepTestEnvironment.Apply(
                        effects,
                        "second",
                        () => state.Counter = 2,
                        token
                    );
                }
                catch (FaultSweepExecutionException) { }
            }
        );
        using var environment = new SweepTestEnvironment(behavior);

        // Act
        FaultSweepExecutionException exception =
            await Assert.ThrowsAsync<FaultSweepExecutionException>(() =>
                environment
                    .CreateSweep()
                    .WithOptions(new FaultSweepOptions { MaxEffects = 1 })
                    .RunAsync()
            );

        // Assert
        Assert.Contains("limit of 1", exception.Message);
    }

    [Fact]
    public async Task Stops_on_baseline_failure_and_preserves_original_exception()
    {
        // Arrange
        var failure = new InvalidOperationException("Baseline failure.");
        using var environment = new SweepTestEnvironment(
            new TestBehavior((_, _, _) => throw failure)
        );

        // Act
        FaultSweepExecutionException exception =
            await Assert.ThrowsAsync<FaultSweepExecutionException>(() =>
                environment.CreateSweep().RunAsync()
            );

        // Assert
        Assert.Same(failure, exception.InnerException);
        Assert.Equal(1, environment.State.Resets);
    }

    [Fact]
    public async Task Propagates_cancellation_and_releases_environment_lock()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var behavior = new TestBehavior(
            async (state, effects, token) =>
            {
                if (state.Resets == 2)
                {
                    cancellation.Cancel();
                }

                await SweepTestEnvironment.Apply(
                    effects,
                    "upsert",
                    () => state.Counter = 10,
                    token
                );
            }
        );
        using var environment = new SweepTestEnvironment(behavior);

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            environment.CreateSweep().RunAsync(cancellation.Token)
        );
        FaultSweepResult next = await environment.CreateSweep().RunAsync();

        // Assert
        Assert.False(next.HasViolations, next.ToString());
    }

    [Fact]
    public async Task Leaves_interceptor_in_pass_through_mode_after_a_sweep()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(
            ZooBehaviors.Create(ZooCase.IdempotentUpsert)
        );
        IEffectInterceptor effects =
            environment.Host.Services.GetRequiredService<IEffectInterceptor>();

        // Act
        await environment.CreateSweep().RunAsync();
        int value = await effects.ExecuteAsync(
            new EffectDescriptor("outside", "read"),
            _ => ValueTask.FromResult(42)
        );

        // Assert
        Assert.Equal(42, value);
    }

    [Fact]
    public async Task Produces_a_repeatable_report_with_fault_marker_and_diff()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(
            ZooBehaviors.Create(ZooCase.NonIdempotentCounter)
        );

        // Act
        FaultSweepResult first = await environment.CreateSweep().RunAsync();
        FaultSweepResult second = await environment.CreateSweep().RunAsync();

        // Assert
        Assert.Equal(first.ToString(), second.ToString());
        Assert.Contains("! #1 memory:increment: CrashAfter", first.ToDetailedString());
        Assert.Contains("state $.Counter: ValueMismatch", first.ToString());
        Assert.Contains("expected: 1", first.ToString());
        Assert.Contains("actual:   2", first.ToString());
    }

    [Fact]
    public async Task Allows_a_recovery_path_to_differ_from_the_baseline_after_a_timeout()
    {
        // Arrange
        var behavior = new TestBehavior(
            async (state, effects, token) =>
            {
                try
                {
                    await SweepTestEnvironment.Apply(
                        effects,
                        "upsert",
                        () => state.Counter = 10,
                        token
                    );
                }
                catch (TimeoutException)
                {
                    await SweepTestEnvironment.Apply(
                        effects,
                        "verify-write",
                        () => state.Counter = 10,
                        token
                    );
                }

                await SweepTestEnvironment.Apply(effects, "cache", () => state.Cache = true, token);
            }
        );
        using var environment = new SweepTestEnvironment(behavior);

        // Act
        FaultSweepResult result = await environment.CreateSweep().RunAsync();

        // Assert
        Assert.False(result.HasViolations, result.ToString());
        FaultScenarioResult recovered = Assert.Single(
            result.Scenarios,
            scenario => scenario.Scenario is { Point.Index: 1, Kind: FaultKind.AppliedButFailed }
        );
        Assert.True(Assert.Single(recovered.Attempts).Succeeded);
        Assert.Equal(3, recovered.Trace.Count);
    }

    [Fact]
    public async Task Retries_a_natural_failure_after_the_injected_failure()
    {
        // Arrange
        int deliveries = 0;
        var behavior = new TestBehavior(
            async (state, effects, token) =>
            {
                deliveries++;
                await SweepTestEnvironment.Apply(
                    effects,
                    "upsert",
                    () => state.Counter = 10,
                    token
                );
                if (deliveries == 3)
                {
                    throw new InvalidOperationException("Transient failure during redelivery.");
                }
            }
        );
        using var environment = new SweepTestEnvironment(behavior);

        // Act
        FaultSweepResult result = await environment
            .CreateSweep()
            .WithOptions(new FaultSweepOptions { FaultKinds = [FaultKind.CrashBefore] })
            .RunAsync();

        // Assert
        Assert.False(result.HasViolations, result.ToString());
        Assert.Equal(3, Assert.Single(result.Scenarios).Attempts.Count);
    }

    [Fact]
    public async Task Distinguishes_repeated_calls_to_the_same_effect()
    {
        // Arrange
        var behavior = new TestBehavior(
            async (state, effects, token) =>
            {
                await SweepTestEnvironment.Apply(
                    effects,
                    "increment",
                    () => state.Counter++,
                    token
                );
                await SweepTestEnvironment.Apply(
                    effects,
                    "increment",
                    () => state.Counter++,
                    token
                );
            }
        );
        using var environment = new SweepTestEnvironment(behavior);

        // Act
        FaultSweepResult result = await environment.CreateSweep().RunAsync();

        // Assert
        Assert.Equal([1, 2], result.BaselineTrace.Select(entry => entry.Point.Index));
        FaultScenarioResult secondBefore = Assert.Single(
            result.Scenarios,
            scenario => scenario.Scenario is { Point.Index: 2, Kind: FaultKind.CrashBefore }
        );
        Assert.Equal(3, secondBefore.State["state"].Value.GetProperty("Counter").GetInt32());
    }

    [Fact]
    public async Task Rejects_a_silent_no_op_baseline_unless_explicitly_allowed()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(
            new TestBehavior((_, _, _) => ValueTask.CompletedTask)
        );

        // Act
        FaultSweepExecutionException exception =
            await Assert.ThrowsAsync<FaultSweepExecutionException>(() =>
                environment.CreateSweep().RunAsync()
            );
        FaultSweepResult allowed = await environment
            .CreateSweep()
            .WithOptions(new FaultSweepOptions { AllowEmptyTrace = true })
            .RunAsync();

        // Assert
        Assert.Contains("no effects", exception.Message);
        Assert.Empty(allowed.Scenarios);
        Assert.False(allowed.HasViolations);
    }

    [Fact]
    public async Task Supports_explicit_handler_invocation_and_multiple_snapshot_sources()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(
            ZooBehaviors.Create(ZooCase.AtomicInboxOutbox)
        );
        bool invoked = false;

        // Act
        FaultSweepResult result = await environment
            .CreateSweep()
            .HandleWith(
                (handler, message, token) =>
                {
                    invoked = true;
                    return handler.HandleAsync(message, token);
                }
            )
            .TrackState(
                "events",
                (services, _) =>
                    ValueTask.FromResult(
                        StateSnapshot.From(services.GetRequiredService<TestState>().Outbox)
                    ),
                new SnapshotComparisonOptions { ArrayComparison = ArrayComparison.Multiset }
            )
            .RunAsync();

        // Assert
        Assert.True(invoked);
        Assert.False(result.HasViolations, result.ToString());
        Assert.Equal(["events", "state"], result.BaselineState.Keys);
    }
}

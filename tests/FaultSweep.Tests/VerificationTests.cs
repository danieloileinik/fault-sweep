using System.Text.Json;
using FaultSweep.Effects;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Tests;

public sealed class VerificationTests
{
    [Fact]
    public async Task Checks_business_rules_after_the_clean_run_and_each_recovery()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(ZooBehaviors.Create(ZooCase.AtomicInboxOutbox));
        List<(bool Baseline, int Attempts)> contexts = [];

        // Act
        FaultSweepResult result = await environment.CreateSweep()
            .CheckInvariant("correct amount", context =>
            {
                contexts.Add((context.IsBaseline, context.Attempts.Count));
                return context.GetState<TestState>("state").Counter == context.Message.Amount;
            })
            .CheckInvariant("one logical event", context =>
                context.State["state"].Value.GetProperty("Outbox").EnumerateArray()
                    .Select(item => item.GetString()).Distinct().Count() == 1)
            .RunAsync();

        // Assert
        result.ShouldHaveNoViolations();
        Assert.Equal(result.Scenarios.Count + 1, contexts.Count);
        Assert.Equal((true, 1), contexts[0]);
        Assert.All(contexts.Skip(1), context => Assert.False(context.Baseline));
        Assert.All(result.Scenarios, scenario => Assert.Empty(scenario.InvariantViolations));
    }

    [Fact]
    public async Task Rejects_an_incorrect_clean_run_even_when_retries_match_it()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(new TestBehavior((state, effects, token) =>
            SweepTestEnvironment.Apply(effects, "wrong amount", () => state.Counter = 20, token)));

        // Act
        FaultSweepResult result = await environment.CreateSweep()
            .CheckInvariant("correct amount", context => context.GetState<TestState>("state").Counter == context.Message.Amount,
                "The balance must decrease by exactly the payment amount.")
            .RunAsync();

        // Assert
        Assert.True(result.BaselineVerification.HasViolations);
        Assert.Equal("correct amount", Assert.Single(result.BaselineVerification.InvariantViolations).Name);
        Assert.All(result.Scenarios, scenario =>
        {
            Assert.Empty(scenario.Differences);
            Assert.Equal(ViolationKind.InvariantFailed, scenario.Violations);
        });
        Assert.Null(result.MinimalScenario);
        Assert.NotNull(result.BaselineRegressionCase);
        Assert.Throws<FaultSweepAssertionException>(result.ShouldHaveNoViolations);
        Assert.Contains("Clean baseline verification failed", result.ToString());
        Assert.Contains("The balance must decrease by exactly the payment amount.", result.ToString());
    }

    [Fact]
    public async Task Runs_async_queries_in_a_separate_scope_without_tracking_snapshots()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(ZooBehaviors.Create(ZooCase.IdempotentUpsert));
        HashSet<TestHandler> scopes = [];
        FaultSweepBuilder<TestHandler, TestMessage> sweep = FaultSweep.For<TestHandler>(environment.Host)
            .WithMessage(new TestMessage())
            .ResetWith((_, _) =>
            {
                environment.State.Reset();
                return ValueTask.CompletedTask;
            })
            .WithOptions(new FaultSweepOptions { CompareWithBaseline = false });

        // Act
        FaultSweepResult result = await sweep.CheckInvariantAsync("correct amount", async (context, token) =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            Assert.False(context.Services.GetRequiredService<IEffectInterceptor>().IsActive);
            Assert.True(scopes.Add(context.Services.GetRequiredService<TestHandler>()));
            Assert.Empty(context.State);
            return context.Services.GetRequiredService<TestState>().Counter == context.Message.Amount;
        }).RunAsync();

        // Assert
        result.ShouldHaveNoViolations();
        Assert.Equal(result.Scenarios.Count + 1, scopes.Count);
        Assert.Equal(environment.State.HandlerInstances, environment.State.DisposedHandlers);
    }

    [Fact]
    public async Task Collects_assertion_exceptions_and_still_runs_other_rules()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(ZooBehaviors.Create(ZooCase.AtomicInboxOutbox));
        int secondRuleCalls = 0;
        var error = new InvalidOperationException("Expected one payment row.");

        // Act
        FaultSweepResult result = await environment.CreateSweep()
            .CheckInvariant("payment row", _ => throw error)
            .CheckInvariant("cache balance", _ =>
            {
                secondRuleCalls++;
                return false;
            })
            .RunAsync();

        // Assert
        Assert.Equal(result.Scenarios.Count + 1, secondRuleCalls);
        Assert.Equal(2, result.BaselineVerification.InvariantViolations.Count);
        Assert.Same(error, result.BaselineVerification.InvariantViolations[0].Error);
        Assert.All(result.Scenarios, scenario => Assert.Equal(2, scenario.InvariantViolations.Count));
        Assert.Contains("payment row' failed (InvalidOperationException)", result.ToString());
    }

    [Fact]
    public async Task Propagates_cancellation_from_verification_and_releases_the_sweep_lock()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(ZooBehaviors.Create(ZooCase.IdempotentUpsert));
        using var cancellation = new CancellationTokenSource();

        // Act
        Exception? failure = await Record.ExceptionAsync(() => environment.CreateSweep()
            .CheckInvariantAsync("cancel", (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(true);
            })
            .RunAsync(cancellation.Token));
        FaultSweepResult next = await environment.CreateSweep().RunAsync();

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        next.ShouldHaveNoViolations();
    }

    [Fact]
    public async Task Compares_explicit_expected_state_in_the_baseline_and_recovery()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(ZooBehaviors.Create(ZooCase.IdempotentUpsert));

        // Act
        FaultSweepResult result = await environment.CreateSweep()
            .TrackState("balance", (_, _) => ValueTask.FromResult(StateSnapshot.From(environment.State.Counter)))
            .ExpectState("balance", StateSnapshot.From(15))
            .RunAsync();

        // Assert
        Assert.True(result.HasViolations);
        SnapshotDifference baseline = Assert.Single(result.BaselineVerification.Differences);
        Assert.Equal("15", baseline.Expected);
        Assert.Equal("10", baseline.Actual);
        Assert.All(result.Scenarios, scenario => Assert.Contains(scenario.Differences,
            difference => difference is { Snapshot: "balance", Expected: "15" }));
    }

    [Fact]
    public async Task Uses_snapshot_normalization_for_explicit_expected_values()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(ZooBehaviors.Create(ZooCase.IdempotentUpsert));
        var comparison = new SnapshotComparisonOptions
        {
            IgnoredProperties = ["timestamp"],
            ArrayComparison = ArrayComparison.Set
        };

        // Act
        FaultSweepResult result = await environment.CreateSweep()
            .TrackState("events", (_, _) => ValueTask.FromResult(StateSnapshot.FromJson(
                """[{"id":"paid","timestamp":2},{"id":"paid","timestamp":3}]""")), comparison)
            .ExpectState("events", StateSnapshot.FromJson("""[{"id":"paid","timestamp":1}]"""))
            .RunAsync();

        // Assert
        result.ShouldHaveNoViolations();
    }

    [Fact]
    public async Task Invariant_only_mode_allows_physical_duplicates_of_one_logical_event()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(new TestBehavior((state, effects, token) =>
            SweepTestEnvironment.Apply(effects, "publish", () => state.Events.Add("payment-42"), token)));

        // Act
        FaultSweepResult result = await environment.CreateSweep()
            .WithOptions(new FaultSweepOptions { CompareWithBaseline = false })
            .CheckInvariant("event is present", context => context.GetState<TestState>("state").Counter == 0 &&
                context.State["state"].Value.GetProperty("Events").EnumerateArray()
                    .Select(item => item.GetString()).Distinct().SequenceEqual(["payment-42"]))
            .RunAsync();

        // Assert
        result.ShouldHaveNoViolations();
        Assert.Contains(result.Scenarios, scenario => scenario.State["state"].Value.GetProperty("Events").GetArrayLength() == 2);
        Assert.All(result.Scenarios, scenario => Assert.Empty(scenario.Differences));
    }

    [Fact]
    public async Task Checks_rules_even_when_redelivery_never_succeeds()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(new TestBehavior((state, effects, token) =>
        {
            if (state.Counter > 0)
            {
                throw new InvalidOperationException("Cannot recover.");
            }

            return SweepTestEnvironment.Apply(effects, "commit without event", () => state.Counter++, token);
        }));

        // Act
        FaultSweepResult result = await environment.CreateSweep()
            .CheckInvariant("event exists after recovery", context => context.IsBaseline || context.Attempts[^1].Succeeded)
            .RunAsync();

        // Assert
        Assert.False(result.BaselineVerification.HasViolations);
        Assert.Contains(result.Violations, scenario => scenario.Violations.HasFlag(ViolationKind.RedeliveryLimitExceeded)
            && scenario.Violations.HasFlag(ViolationKind.InvariantFailed));
    }

    [Fact]
    public async Task Minimizes_invariant_failures_without_accepting_an_invalid_clean_run()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(ZooBehaviors.Create(ZooCase.IdempotentUpsert));
        FaultSweepBuilder<TestHandler, TestMessage> Create(int amount) => FaultSweep.For<TestHandler>(environment.Host)
            .WithMessage(new TestMessage(amount))
            .ResetWith((_, _) => { environment.State.Reset(); return ValueTask.CompletedTask; })
            .HandleWith((_, message, token) => SweepTestEnvironment.Apply(
                environment.Host.Services.GetRequiredService<IEffectInterceptor>(), "debit", () =>
                {
                    environment.State.Counter += message.Amount == 1 ? 2 : message.Amount;
                }, token))
            .WithOptions(new FaultSweepOptions { CompareWithBaseline = false })
            .TrackState("balance", (_, _) => ValueTask.FromResult(StateSnapshot.From(environment.State.Counter)))
            .CheckInvariant("correct amount", context => context.GetState<int>("balance") == context.Message.Amount);

        // Act
        FaultSweepResult result = await Create(3)
            .MinimizeMessageWith(message => Enumerable.Range(1, message.Amount - 1).Select(value => new TestMessage(value)), message => message.Amount)
            .RunAsync();
        MinimizedScenario minimum = Assert.IsType<MinimizedScenario>(result.MinimalScenario);
        TestMessage message = JsonSerializer.Deserialize<TestMessage>(minimum.MessageJson!)!;
        FaultSweepResult replay = await Create(message.Amount).ReplayScenario(new ReplayScenario(
            minimum.Result.Scenario.Point.Index, minimum.Result.Scenario.Kind)).RunAsync();

        // Assert
        Assert.Equal(2, message.Amount);
        Assert.False(replay.BaselineVerification.HasViolations);
        Assert.Equal(ViolationKind.InvariantFailed, minimum.Result.Violations);
        Assert.Equal("correct amount", Assert.Single(Assert.Single(replay.Scenarios).InvariantViolations).Name);
    }

    [Fact]
    public async Task Recomputes_explicit_expectations_for_the_shrunken_message()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(ZooBehaviors.Create(ZooCase.NonIdempotentCounter));

        // Act
        FaultSweepResult result = await environment.CreateSweep()
            .HandleWith((_, message, token) => SweepTestEnvironment.Apply(
                environment.Host.Services.GetRequiredService<IEffectInterceptor>(), "debit", () => environment.State.Counter += message.Amount, token))
            .TrackState("balance", (_, _) => ValueTask.FromResult(StateSnapshot.From(environment.State.Counter)))
            .ExpectState("balance", message => StateSnapshot.From(message.Amount))
            .MinimizeMessageWith(message => Enumerable.Range(0, message.Amount).Select(amount => new TestMessage(amount)), message => message.Amount)
            .RunAsync();

        // Assert
        MinimizedScenario minimum = Assert.IsType<MinimizedScenario>(result.MinimalScenario);
        Assert.Equal(1, JsonSerializer.Deserialize<TestMessage>(minimum.MessageJson!)!.Amount);
        Assert.Contains(minimum.Result.Differences, difference => difference is { Snapshot: "balance", Expected: "1" });
    }

    [Fact]
    public async Task Reports_a_baseline_violation_even_when_the_trace_is_explicitly_empty()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(new TestBehavior((_, _, _) => ValueTask.CompletedTask));

        // Act
        FaultSweepResult result = await environment.CreateSweep()
            .WithOptions(new FaultSweepOptions { AllowEmptyTrace = true })
            .CheckInvariant("payment exists", _ => false)
            .RunAsync();

        // Assert
        Assert.True(result.HasViolations);
        Assert.Empty(result.Violations);
        Assert.NotNull(result.BaselineRegressionCase);
        Assert.Null(result.BaselineRegressionCase.Scenario);
        Assert.Throws<FaultSweepAssertionException>(result.ShouldHaveNoViolations);
        Assert.Contains("0 scenarios, 1 violations", result.ToString());
    }

    [Fact]
    public async Task Rejects_missing_or_duplicate_oracles_before_reset()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(ZooBehaviors.Create(ZooCase.IdempotentUpsert));

        // Act
        Exception? missingSnapshot = await Record.ExceptionAsync(() => environment.CreateSweep()
            .ExpectState("untracked", StateSnapshot.From(10)).RunAsync());
        Exception? noOracle = await Record.ExceptionAsync(() => environment.CreateSweep()
            .WithOptions(new FaultSweepOptions { CompareWithBaseline = false }).RunAsync());
        Exception? duplicateRule = Record.Exception(() => environment.CreateSweep()
            .CheckInvariant("one debit", _ => true).CheckInvariant("one debit", _ => true));
        Exception? duplicateExpected = Record.Exception(() => environment.CreateSweep()
            .ExpectState("state", StateSnapshot.From(1)).ExpectState("state", StateSnapshot.From(1)));

        // Assert
        Assert.IsType<InvalidOperationException>(missingSnapshot);
        Assert.IsType<InvalidOperationException>(noOracle);
        Assert.IsType<ArgumentException>(duplicateRule);
        Assert.IsType<ArgumentException>(duplicateExpected);
        Assert.Equal(0, environment.State.Resets);
    }
}

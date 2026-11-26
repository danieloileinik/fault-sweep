using FaultSweep.Effects;
using FaultSweep.Reporting;

namespace FaultSweep.Tests;

public sealed class ReportTests
{
    private const string Sql = "UPDATE accounts SET balance = balance - @amount WHERE id = @id";

    [Fact]
    public async Task Default_report_keeps_scenario_errors_attempt_count_and_diff_without_SQL_or_traces()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(CreateBehavior());

        // Act
        FaultSweepResult result = await environment.CreateSweep().RunAsync();
        string report = result.ToString();

        // Assert
        Assert.Contains("Crash after postgres:Command (#1)", report);
        Assert.Contains("Attempt 2: handler returned successfully", report);
        Assert.Contains("Attempt 1: simulated crash after", report);
        Assert.Contains("Lost acknowledgement after", report);
        Assert.Contains("state $.Counter:", report);
        Assert.Contains("1 in clean run", report);
        Assert.Contains("2 observed", report);
        Assert.DoesNotContain(Sql, report);
        Assert.DoesNotContain("Delivery 1:", report);
        Assert.DoesNotContain("Message:", report);
        Assert.Contains("ToDetailedString()", report);
    }

    [Fact]
    public async Task Detailed_report_contains_SQL_baseline_and_each_delivery()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(CreateBehavior());
        FaultSweepResult result = await environment.CreateSweep().RunAsync();
        int resets = environment.State.Resets;

        // Act
        string detailed = result.ToDetailedString();

        // Assert
        Assert.Contains(Sql, detailed);
        Assert.Contains("Clean run:", detailed);
        Assert.Contains("Attempt 1: simulated crash after", detailed);
        Assert.Contains("Attempt 2: handler returned successfully", detailed);
        Assert.Contains("#1 postgres:Command", detailed);
        Assert.Contains("Input message:", detailed);
        Assert.Equal(detailed, result.ToDetailedString());
        Assert.Equal(resets, environment.State.Resets);
    }

    [Fact]
    public async Task Assertion_exposes_the_detailed_report_without_rerunning_the_sweep()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(CreateBehavior());
        FaultSweepResult result = await environment.CreateSweep().RunAsync();
        int resets = environment.State.Resets;

        // Act
        FaultSweepAssertionException error = Assert.Throws<FaultSweepAssertionException>(
            result.ShouldHaveNoViolations
        );

        // Assert
        Assert.Same(result, error.Result);
        Assert.Equal(result.ToString(), error.Message);
        Assert.DoesNotContain(Sql, error.ToString());
        Assert.Equal(result.ToDetailedString(), error.ToDetailedString());
        Assert.Equal(resets, environment.State.Resets);
    }

    [Fact]
    public async Task Duplicate_failures_use_the_same_compact_summary()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(CreateBehavior());

        // Act
        FaultSweepResult result = await environment
            .CreateSweep()
            .WithDuplicateDeliveries()
            .RunAsync();
        string report = result.ToString();

        // Assert
        Assert.Equal(2, result.DuplicateViolations.Count);
        Assert.Single(report.Split(Environment.NewLine), line => line.Contains("state $.Counter"));
        Assert.Contains(
            "Replay: .ReplayScenario(new ReplayScenario(1, FaultKind.CrashAfter))",
            report
        );
        Assert.Contains("Sequential duplicate delivery", report);
        Assert.Contains("Parallel duplicate delivery", result.ToDetailedString());
        Assert.Contains("+1 more scenario", report);
        Assert.DoesNotContain(Sql, report);
        Assert.DoesNotContain("Delivery 1:", report);
        Assert.Contains("Delivery 2, attempt 1:", result.ToDetailedString());
    }

    [Fact]
    public async Task Repeated_application_errors_are_counted_without_repeating_their_SQL_messages()
    {
        // Arrange
        TestBehavior increment = CreateBehavior();
        var behavior = new TestBehavior(
            async (state, effects, token) =>
            {
                await increment.Action(state, effects, token);
                if (state.Counter > 1)
                {
                    throw new InvalidOperationException(
                        Sql,
                        new ArgumentException("Invalid currency")
                    );
                }
            }
        );
        using var environment = new SweepTestEnvironment(behavior);

        // Act
        FaultSweepResult result = await environment.CreateSweep().RunAsync();

        // Assert
        Assert.Contains("Handler still failing after the last retry", result.ToString());
        Assert.Contains("Attempt 4: ArgumentException: Invalid currency", result.ToString());
        Assert.DoesNotContain(Sql, result.ToString());
        Assert.Contains($"InvalidOperationException: {Sql}", result.ToDetailedString());
        Assert.Contains("ArgumentException: Invalid currency", result.ToDetailedString());
    }

    [Fact]
    public async Task Large_baseline_failures_show_preview_counts_and_keep_full_details()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(CreateBehavior());
        string payload = new string('x', 200) + "payload-end";
        var sweep = environment
            .CreateSweep()
            .TrackState(
                "payload",
                (_, _) =>
                    ValueTask.FromResult(
                        Snapshots.StateSnapshot.From(Enumerable.Repeat(payload, 5).ToArray())
                    )
            )
            .ExpectState("payload", Snapshots.StateSnapshot.From(Array.Empty<string>()));
        for (int i = 1; i <= 5; i++)
        {
            sweep.CheckInvariant($"rule {i}", _ => false);
        }

        // Act
        FaultSweepResult result = await sweep.RunAsync();
        string report = result.ToString();
        string detailed = result.ToDetailedString();

        // Assert
        Assert.Contains("+2 more differences", report);
        Assert.Contains("+2 more invariants", report);
        Assert.Contains("…", report);
        Assert.DoesNotContain("payload-end", report);
        Assert.DoesNotContain("rule 5", report);
        Assert.Contains("payload-end", detailed);
        Assert.Contains("rule 5", detailed);
    }

    [Fact]
    public async Task Groups_using_full_values_when_previews_are_identical()
    {
        // Arrange
        int updates = 0;
        string prefix = new string('x', 110);
        using var environment = new SweepTestEnvironment(
            new TestBehavior(
                async (_, effects, token) =>
                {
                    await SweepTestEnvironment.Apply(effects, "first", () => updates++, token);
                    await SweepTestEnvironment.Apply(effects, "second", () => updates++, token);
                }
            )
        );

        // Act
        var result = await environment
            .CreateSweep()
            .ResetWith(
                (_, _) =>
                {
                    updates = 0;
                    return ValueTask.CompletedTask;
                }
            )
            .WithOptions(new FaultSweepOptions { FaultKinds = [FaultKind.CrashAfter] })
            .TrackState(
                "value",
                (_, _) => ValueTask.FromResult(Snapshots.StateSnapshot.From(prefix + updates))
            )
            .RunAsync();

        // Assert
        Assert.Contains("Failure 2 (1 scenario)", result.ToString());
        Assert.Contains(prefix + "3", result.ToDetailedString());
        Assert.Contains(prefix + "4", result.ToDetailedString());
    }

    [Fact]
    public async Task Labels_a_retry_with_its_logical_duplicate_delivery()
    {
        // Arrange
        int calls = 0;
        using var environment = new SweepTestEnvironment(
            new TestBehavior(
                (state, effects, token) =>
                {
                    if (++calls == 2)
                    {
                        throw new InvalidOperationException("Retry this delivery");
                    }
                    return SweepTestEnvironment.Apply(
                        effects,
                        "debit",
                        () => state.Counter++,
                        token
                    );
                }
            )
        );

        // Act
        var result = await environment
            .CreateSweep()
            .ReplayDuplicates(DuplicateDeliveryMode.Sequential)
            .RunAsync();

        // Assert
        string report = result.ToDetailedString();
        Assert.Contains("Delivery 1, attempt 2: handler returned successfully", report);
        Assert.Contains("Delivery 2, attempt 1: handler returned successfully", report);
        Assert.DoesNotContain("Delivery 3", report);
        Assert.DoesNotContain("Shrink candidates", report);
    }

    [Fact]
    public void Existing_message_only_exception_constructor_remains_usable()
    {
        // Arrange
        const string message = "Custom assertion failure.";

        // Act
        var error = new FaultSweepAssertionException(message);

        // Assert
        Assert.Null(error.Result);
        Assert.Equal(message, error.ToDetailedString());
    }

    private static TestBehavior CreateBehavior() =>
        new TestBehavior(
            (state, effects, token) =>
                effects.ExecuteAsync(
                    new EffectDescriptor("postgres", "Command", Sql),
                    _ =>
                    {
                        lock (state)
                        {
                            state.Counter++;
                        }

                        return ValueTask.CompletedTask;
                    },
                    token
                )
        );
}

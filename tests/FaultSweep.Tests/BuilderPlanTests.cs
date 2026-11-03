using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Tests;

public sealed class BuilderPlanTests
{
    [Fact]
    public async Task Preserves_explicit_options_when_binding_a_message()
    {
        // Arrange
        var state = new TestState();
        using ServiceProvider services = CreateServices(state);
        var options = new FaultSweepOptions
        {
            FaultKinds = [FaultKind.CrashBefore],
            DuplicateDeliveries = [DuplicateDeliveryMode.Sequential],
        };
        FaultSweepBuilder<ConventionHandler, TestMessage> builder = FaultSweep
            .For<ConventionHandler>(services)
            .WithOptions(options)
            .WithMessage(new TestMessage())
            .ResetWith(
                (_, _) =>
                {
                    state.Reset();
                    return ValueTask.CompletedTask;
                }
            )
            .TrackState("state", (_, _) => ValueTask.FromResult(state.Snapshot()));

        // Act
        FaultSweepResult result = await builder.RunAsync();

        // Assert
        Assert.False(result.HasViolations);
        Assert.Equal(FaultKind.CrashBefore, Assert.Single(result.Scenarios).Scenario.Kind);
        Assert.Equal(
            DuplicateDeliveryMode.Sequential,
            Assert.Single(result.DuplicateScenarios).Mode
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Supports_convention_handlers_and_preserves_each_api_default(bool automatic)
    {
        // Arrange
        var state = new TestState();
        using ServiceProvider services = CreateServices(state);
        FaultSweepBuilder<ConventionHandler> builder = FaultSweep.For<ConventionHandler>(services);

        // Act
        FaultSweepResult result = automatic
            ? await builder
                .WithEnvironment(
                    "state",
                    new CallbackInstrumentation(_ => new CallbackDisposable(() => { })),
                    _ =>
                        ValueTask.FromResult<ISweepEnvironment>(
                            new CallbackSweepEnvironment(state.Snapshot, state.Reset)
                        )
                )
                .CheckIdempotencyAsync(new TestMessage())
            : await builder
                .WithMessage(new TestMessage())
                .ResetWith(
                    (_, _) =>
                    {
                        state.Reset();
                        return ValueTask.CompletedTask;
                    }
                )
                .TrackState("state", (_, _) => ValueTask.FromResult(state.Snapshot()))
                .RunAsync();

        // Assert
        Assert.False(result.HasViolations);
        Assert.Equal(3, result.Scenarios.Count);
        Assert.Equal(automatic ? 2 : 0, result.DuplicateScenarios.Count);
    }

    [Fact]
    public async Task Applies_comparison_options_to_automatic_environments()
    {
        // Arrange
        var state = new TestState();
        int captures = 0;
        using ServiceProvider services = CreateServices(state);
        var environment = new CallbackSweepEnvironment(
            () => StateSnapshot.From(new { state.Counter, CapturedAt = ++captures }),
            state.Reset
        );
        FaultSweepBuilder<ConventionHandler> builder = FaultSweep
            .For<ConventionHandler>(services)
            .WithOptions(new FaultSweepOptions { DuplicateDeliveries = [] })
            .WithEnvironment(
                "state",
                new CallbackInstrumentation(_ => new CallbackDisposable(() => { })),
                _ => ValueTask.FromResult<ISweepEnvironment>(environment),
                new SnapshotComparisonOptions { IgnoredProperties = ["CapturedAt"] }
            );

        // Act
        FaultSweepResult result = await builder.CheckIdempotencyAsync(new TestMessage());

        // Assert
        Assert.True(captures > 1);
        Assert.False(result.HasViolations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejects_missing_verification_before_touching_state(bool automatic)
    {
        // Arrange
        var state = new TestState();
        using ServiceProvider services = CreateServices(state);
        FaultSweepBuilder<ConventionHandler> builder = FaultSweep
            .For<ConventionHandler>(services)
            .WithOptions(new FaultSweepOptions { CompareWithBaseline = false });

        // Act
        Exception? error = await Record.ExceptionAsync(() =>
            automatic
                ? builder
                    .WithEnvironment(
                        "state",
                        new CallbackInstrumentation(_ => new CallbackDisposable(() => { })),
                        _ =>
                            ValueTask.FromResult<ISweepEnvironment>(
                                new CallbackSweepEnvironment(state.Snapshot, state.Reset)
                            )
                    )
                    .CheckIdempotencyAsync(new TestMessage())
                : builder
                    .WithMessage(new TestMessage())
                    .ResetWith(
                        (_, _) =>
                        {
                            state.Reset();
                            return ValueTask.CompletedTask;
                        }
                    )
                    .TrackState("state", (_, _) => ValueTask.FromResult(state.Snapshot()))
                    .RunAsync()
        );

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(0, state.Resets);
    }

    private static ServiceProvider CreateServices(TestState state) =>
        new ServiceCollection()
            .AddFaultSweep()
            .AddSingleton(state)
            .AddScoped<ConventionHandler>()
            .BuildServiceProvider();
}

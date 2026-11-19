using FaultSweep.Snapshots;

namespace FaultSweep.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public async Task Requires_explicit_reset_and_snapshot_configuration()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(
            ZooBehaviors.Create(ZooCase.IdempotentUpsert)
        );
        FaultSweepBuilder<TestHandler, TestMessage> builder = FaultSweep
            .For<TestHandler>(environment.Host)
            .WithMessage(new TestMessage());

        // Act
        Exception? missingReset = await Record.ExceptionAsync(() => builder.RunAsync());
        builder.ResetWith((_, _) => ValueTask.CompletedTask);
        Exception? missingSnapshot = await Record.ExceptionAsync(() => builder.RunAsync());

        // Assert
        Assert.IsType<InvalidOperationException>(missingReset);
        Assert.IsType<InvalidOperationException>(missingSnapshot);
        Assert.Equal(0, environment.State.Resets);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, int.MaxValue)]
    public void Validates_limits_before_touching_the_environment(int effects, int redeliveries)
    {
        // Arrange
        using var environment = new SweepTestEnvironment(
            ZooBehaviors.Create(ZooCase.IdempotentUpsert)
        );
        var options = new FaultSweepOptions
        {
            MaxEffects = effects,
            MaxRedeliveryAttempts = redeliveries,
        };

        // Act
        Exception? exception = Record.Exception(() =>
            environment.CreateSweep().WithOptions(options)
        );

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Equal(0, environment.State.Resets);
    }

    [Fact]
    public void Rejects_overflowing_retry_limit_for_replay()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(
            ZooBehaviors.Create(ZooCase.IdempotentUpsert)
        );
        var replay = new Reporting.ReplayScenario(1, FaultKind.CrashBefore, int.MaxValue);

        // Act
        Exception? error = Record.Exception(() => environment.CreateSweep().ReplayScenario(replay));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(error);
        Assert.Equal(0, environment.State.Resets);
    }

    [Fact]
    public void Rejects_duplicate_snapshot_names()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(
            ZooBehaviors.Create(ZooCase.IdempotentUpsert)
        );

        // Act
        Exception? exception = Record.Exception(() =>
            environment
                .CreateSweep()
                .TrackState("state", (_, _) => ValueTask.FromResult(StateSnapshot.From(1)))
        );

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }
}

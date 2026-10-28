using FaultSweep.Reporting;

namespace FaultSweep.Tests;

public sealed class ZooTests
{
    [Theory]
    [InlineData(ZooCase.NonIdempotentCounter)]
    [InlineData(ZooCase.DualWrite)]
    [InlineData(ZooCase.PublishBeforeCommit)]
    [InlineData(ZooCase.CacheBeforeCommit)]
    [InlineData(ZooCase.LostEvent)]
    [InlineData(ZooCase.InboxBeforeWork)]
    [InlineData(ZooCase.OutboxWithoutInbox)]
    public async Task Detects_broken_handlers(ZooCase example)
    {
        // Arrange
        using var environment = new SweepTestEnvironment(ZooBehaviors.Create(example));

        // Act
        FaultSweepResult result = await environment.CreateSweep().RunAsync();

        // Assert
        Assert.True(result.HasViolations);
        Assert.Contains(result.Violations, violation => violation.Violations.HasFlag(ViolationKind.StateMismatch));
        Assert.All(result.Scenarios, scenario => Assert.DoesNotContain(scenario.Attempts, attempt => attempt.Number > 2));
        Assert.Throws<FaultSweepAssertionException>(result.ShouldHaveNoViolations);
    }

    [Theory]
    [InlineData(ZooCase.AtomicInboxOutbox)]
    [InlineData(ZooCase.IdempotentUpsert)]
    public async Task Accepts_correct_handlers(ZooCase example)
    {
        // Arrange
        using var environment = new SweepTestEnvironment(ZooBehaviors.Create(example));

        // Act
        FaultSweepResult result = await environment.CreateSweep().RunAsync();

        // Assert
        Assert.False(result.HasViolations, result.ToString());
        Assert.Equal(result.BaselineTrace.Count * 3, result.Scenarios.Count);
        result.ShouldHaveNoViolations();
    }
}

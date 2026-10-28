using FaultSweep.Reporting;

namespace FaultSweep.Tests;

public sealed class PublicApiTests
{
    [Fact]
    public async Task Supports_a_consumer_outside_the_library_namespace()
    {
        // Arrange
        using var environment = new SweepTestEnvironment(ZooBehaviors.Create(ZooCase.AtomicInboxOutbox));

        // Act
        FaultSweepResult result = await FaultSweep.For<TestHandler>(environment.Host)
            .WithMessage(new TestMessage())
            .ResetWith((_, _) =>
            {
                environment.State.Reset();
                return ValueTask.CompletedTask;
            })
            .TrackState("state", (_, _) => ValueTask.FromResult(environment.State.Snapshot()))
            .RunAsync();

        // Assert
        result.ShouldHaveNoViolations();
    }
}

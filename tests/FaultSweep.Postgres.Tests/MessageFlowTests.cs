using FaultSweep.Reporting;
using Xunit.Abstractions;

namespace FaultSweep.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MessageFlowTests(PostgresFixture postgres, ITestOutputHelper output)
{
    [Theory]
    [InlineData(MessageFlow.DualWrite, true)]
    [InlineData(MessageFlow.PublishBeforeCommit, true)]
    [InlineData(MessageFlow.LostEvent, true)]
    [InlineData(MessageFlow.NonIdempotentCounter, true)]
    [InlineData(MessageFlow.InboxBeforeWork, true)]
    [InlineData(MessageFlow.OutboxWithoutInbox, true)]
    [InlineData(MessageFlow.CorrectInboxOutbox, false)]
    [InlineData(MessageFlow.IdempotentUpsert, false)]
    public async Task Finds_broken_message_flows_and_accepts_correct_flows(MessageFlow flow, bool expectedViolations)
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using var environment = new MessageFlowEnvironment(database);
        await environment.InitializeAsync();

        // Act
        FaultSweepResult result = await environment.Sweep(flow).RunAsync();

        // Assert
        Assert.True(expectedViolations == result.HasViolations, result.ToString());
        Assert.Equal(result.BaselineTrace.Count * 2, result.Scenarios.Count);
        Assert.All(result.Scenarios, scenario =>
        {
            Assert.False(scenario.Violations.HasFlag(ViolationKind.FaultNotReached), result.ToString());
            Assert.True(scenario.Attempts[^1].Succeeded, result.ToString());
        });
        if (expectedViolations)
        {
            Assert.Contains(result.Violations, violation => violation.Violations.HasFlag(ViolationKind.StateMismatch));
            Assert.Throws<FaultSweepAssertionException>(result.ShouldHaveNoViolations);
        }
        else
        {
            result.ShouldHaveNoViolations();
        }
    }

    [Fact]
    public async Task Reports_lost_publications_and_duplicate_publications_in_the_text_diff()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using var environment = new MessageFlowEnvironment(database);
        await environment.InitializeAsync();

        // Act
        FaultSweepResult lost = await environment.Sweep(MessageFlow.LostEvent).RunAsync();
        FaultSweepResult duplicated = await environment.Sweep(MessageFlow.PublishBeforeCommit).RunAsync();
        string lostReport = lost.ToString();
        output.WriteLine(lostReport);

        // Assert
        Assert.Contains(lost.Violations, violation =>
            violation.Scenario.Kind == FaultKind.CrashAfter &&
            violation.Scenario.Point.Effect.Operation == "Commit" &&
            violation.Differences.Any(difference =>
                difference is { Snapshot: "produced", Kind: Snapshots.DifferenceKind.Missing }));
        Assert.Contains(duplicated.Violations, violation => violation.Differences.Any(difference =>
            difference is { Snapshot: "produced", Kind: Snapshots.DifferenceKind.Unexpected }));
        Assert.Contains("produced $[0]: Missing", lostReport);
        Assert.Contains("CrashAfter", duplicated.ToString());
        Assert.Contains("kafka:Produce", duplicated.ToString());
    }
}

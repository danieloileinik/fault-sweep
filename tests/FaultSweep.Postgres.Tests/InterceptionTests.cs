using FaultSweep.Reporting;

namespace FaultSweep.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class InterceptionTests(PostgresFixture postgres)
{
    public static IEnumerable<object[]> ExecutionStyles()
    {
        bool[] executionModes = [false, true];
        foreach (AccessStyle style in Enum.GetValues<AccessStyle>())
        {
            foreach (bool useAsync in executionModes)
            {
                yield return [style, useAsync];
            }
        }
    }

    public static IEnumerable<object[]> CommandStyles()
    {
        foreach (object[] style in ExecutionStyles())
        {
            foreach (CommandApi api in Enum.GetValues<CommandApi>())
            {
                yield return [.. style, api];
            }
        }
    }

    [Theory]
    [MemberData(nameof(CommandStyles))]
    public async Task Intercepts_commands_and_observes_applied_writes_after_redelivery(
        AccessStyle style, bool useAsync, CommandApi api)
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using var host = new PostgresTestHost(database, style);
        var message = new PostgresMessage(useAsync, Transactional: false, Idempotent: false, api);

        // Act
        FaultSweepResult result = await host.Sweep(message).RunAsync();

        // Assert
        Assert.Equal(2, result.BaselineTrace.Count);
        Assert.Equal(6, result.Scenarios.Count);
        Assert.Equal(5, result.Violations.Count);
        Assert.All(result.BaselineTrace, entry =>
        {
            Assert.Equal("postgres", entry.Point.Effect.Source);
            Assert.Equal("Command", entry.Point.Effect.Operation);
        });
        Assert.All(result.Scenarios, scenario =>
        {
            Assert.Equal(2, scenario.Attempts.Count);
            Assert.True(scenario.Attempts[^1].Succeeded, result.ToString());
            int first = scenario.State["postgres"].Value[0].GetProperty("value").GetInt32();
            int second = scenario.State["postgres"].Value[1].GetProperty("value").GetInt32();
            Assert.Equal(scenario.Scenario is { Point.Index: 1, Kind: FaultKind.CrashBefore } ? 10 : 20, first);
            Assert.Equal(scenario.Scenario is { Point.Index: 2, Kind: not FaultKind.CrashBefore } ? 20 : 10, second);
        });
    }

    [Theory]
    [MemberData(nameof(CommandStyles))]
    public async Task Rolls_back_command_faults_and_intercepts_commit_separately(
        AccessStyle style, bool useAsync, CommandApi api)
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using var host = new PostgresTestHost(database, style);
        var message = new PostgresMessage(useAsync, Transactional: true, Idempotent: false, api);

        // Act
        FaultSweepResult result = await host.Sweep(message).RunAsync();

        // Assert
        Assert.Equal(3, result.BaselineTrace.Count);
        Assert.Equal("Commit", result.BaselineTrace[^1].Point.Effect.Operation);
        Assert.Equal(9, result.Scenarios.Count);
        Assert.Equal(2, result.Violations.Count);
        Assert.All(result.Violations, violation =>
        {
            Assert.Equal("Commit", violation.Scenario.Point.Effect.Operation);
            Assert.NotEqual(FaultKind.CrashBefore, violation.Scenario.Kind);
        });
        Assert.All(result.Scenarios, scenario =>
        {
            Assert.True(scenario.Attempts[^1].Succeeded, result.ToString());
            int value = scenario.HasViolation ? 20 : 10;
            Assert.Equal(value, scenario.State["postgres"].Value[0].GetProperty("value").GetInt32());
            Assert.Equal(value, scenario.State["postgres"].Value[1].GetProperty("value").GetInt32());
        });
    }

    [Theory]
    [MemberData(nameof(ExecutionStyles))]
    public async Task Does_not_report_violations_for_idempotent_transactions(AccessStyle style, bool useAsync)
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using var host = new PostgresTestHost(database, style);
        var message = new PostgresMessage(useAsync, Transactional: true, Idempotent: true);

        // Act
        FaultSweepResult result = await host.Sweep(message).RunAsync();

        // Assert
        Assert.Equal(9, result.Scenarios.Count);
        result.ShouldHaveNoViolations();
    }
}

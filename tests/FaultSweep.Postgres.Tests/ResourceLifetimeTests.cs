using FaultSweep.Effects;
using FaultSweep.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ResourceLifetimeTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Intercepts_commands_created_directly_by_a_data_source(bool useAsync)
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using var host = new PostgresTestHost(database, services =>
            services.AddScoped<IMessageHandler<PostgresMessage>, SourceCommandHandler>());

        // Act
        FaultSweepResult result = await host.Sweep(new PostgresMessage(useAsync, false, false)).RunAsync();

        // Assert
        Assert.Equal("Command", Assert.Single(result.BaselineTrace).Point.Effect.Operation);
        Assert.Equal(2, result.Violations.Count);
        Assert.All(result.Scenarios, scenario => Assert.True(scenario.Attempts[^1].Succeeded, result.ToString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Releases_a_reader_before_the_handler_recovers_on_the_same_connection(bool useAsync)
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using var host = new PostgresTestHost(database, services =>
            services.AddScoped<IMessageHandler<PostgresMessage>, ReaderRecoveryHandler>());

        // Act
        FaultSweepResult result = await host.Sweep(new PostgresMessage(useAsync, false, true)).RunAsync();

        // Assert
        result.ShouldHaveNoViolations();
        FaultScenarioResult recovered = Assert.Single(result.Scenarios, scenario =>
            scenario.Scenario is { Point.Index: 1, Kind: FaultKind.AppliedButFailed });
        Assert.True(Assert.Single(recovered.Attempts).Succeeded);
        Assert.Contains(recovered.Trace, entry => entry.Point.Effect.Target == "SELECT value FROM counters WHERE id = 1");
    }

    [Fact]
    public async Task Rejects_ambient_transactions_whose_commits_would_bypass_interception()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using var host = new PostgresTestHost(database, AccessStyle.AdoDataSource);

        // Act
        FaultSweepExecutionException exception = await Assert.ThrowsAsync<FaultSweepExecutionException>(() =>
            host.Sweep(new PostgresMessage(true, false, true)).HandleWith(async (handler, message, token) =>
            {
                using var transaction = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
                await handler.HandleAsync(message, token);
            }).RunAsync());

        // Assert
        Assert.Contains("explicit DbTransaction", exception.Message);
    }
}

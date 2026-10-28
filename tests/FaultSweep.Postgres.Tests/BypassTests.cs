using FaultSweep.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class BypassTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Direct_provider_construction_is_intercepted_without_replacing_connections()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using var host = new PostgresTestHost(database, services =>
            services.AddScoped<IMessageHandler<PostgresMessage>, DirectConnectionHandler>());
        var message = new PostgresMessage(Async: true, Transactional: false, Idempotent: false);

        // Act
        FaultSweepResult result = await host.Sweep(message).RunAsync();

        // Assert
        Assert.Equal(2, result.BaselineTrace.Count);
        Assert.Equal(5, result.Violations.Count);
    }

    [Fact]
    public async Task Mixed_creation_styles_are_all_intercepted()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using var host = new PostgresTestHost(database, services =>
            services.AddScoped<IMessageHandler<PostgresMessage>, MixedConnectionHandler>());
        var message = new PostgresMessage(Async: true, Transactional: false, Idempotent: false);

        // Act
        FaultSweepResult result = await host.Sweep(message).RunAsync();

        // Assert
        Assert.Equal(3, result.BaselineTrace.Count);
        Assert.Equal("SELECT 1", result.BaselineTrace[0].Point.Effect.Target);
        Assert.Equal(9, result.Scenarios.Count);
        Assert.Equal(5, result.Violations.Count);
    }
}

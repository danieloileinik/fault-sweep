using FaultSweep.Postgres;
using FaultSweep.Postgres.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace Orders.IntegrationTests;

using FaultSweep = global::FaultSweep.FaultSweep;

[Collection(PostgresCollection.Name)]
public sealed class ShortApiConsumerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Uses_the_short_API_from_an_application_namespace_with_an_interface_handler()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await database.ResetAsync(default);
        string before = (await database.CaptureAsync(default)).ToString();
        var services = new ServiceCollection();
        services.AddSingleton(database);
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        await FaultSweep
            .For<DirectConnectionHandler>(provider)
            .WithPostgres(database.ConnectionString)
            .AssertIdempotentAsync(
                new PostgresMessage(Async: true, Transactional: true, Idempotent: true)
            );

        // Assert
        Assert.Equal(before, (await database.CaptureAsync(default)).ToString());
    }
}

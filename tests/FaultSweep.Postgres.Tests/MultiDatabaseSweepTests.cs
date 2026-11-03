using FaultSweep.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MultiDatabaseSweepTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Captures_and_restores_multiple_named_postgres_stores()
    {
        // Arrange
        await using TestDatabase first = await postgres.CreateDatabaseAsync();
        await using TestDatabase second = await postgres.CreateDatabaseAsync();
        await first.ResetAsync(default);
        await second.ResetAsync(default);
        string firstSeed = (await first.CaptureAsync(default)).ToString();
        string secondSeed = (await second.CaptureAsync(default)).ToString();
        var services = new ServiceCollection();
        services.AddSingleton<IReadOnlyCollection<TestDatabase>>([first, second]);
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        FaultSweepResult result = await FaultSweep
            .For<MultiDatabaseHandler>(provider)
            .WithPostgres(first.ConnectionString, ignore: null, schema: "public", name: "first")
            .WithPostgres(second.ConnectionString, ignore: null, schema: "public", name: "second")
            .CheckIdempotencyAsync(
                new PostgresMessage(Async: true, Transactional: false, Idempotent: true)
            );

        // Assert
        Assert.False(result.HasViolations, result.ToString());
        Assert.Equal(["first", "second"], result.BaselineState.Keys);
        Assert.All(
            result.BaselineState.Values,
            state =>
                Assert.Equal(
                    10,
                    state.Value.GetProperty("counters")[0].GetProperty("value").GetInt32()
                )
        );
        Assert.Equal(firstSeed, (await first.CaptureAsync(default)).ToString());
        Assert.Equal(secondSeed, (await second.CaptureAsync(default)).ToString());
    }
}

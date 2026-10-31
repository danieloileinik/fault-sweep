using System.Diagnostics;
using FaultSweep.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class M1AcceptanceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Sweeps_twenty_real_database_effects_within_the_two_minute_budget()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        var services = new ServiceCollection();
        services.AddSingleton(database);
        services.AddScoped<TwentyEffectHandler>();
        services.AddFaultSweepPostgres(database.ConnectionString);
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        var watch = Stopwatch.StartNew();
        FaultSweepResult result = await FaultSweep
            .For<TwentyEffectHandler>(provider)
            .WithMessage(20)
            .ResetWith((_, token) => database.ResetAsync(token))
            .TrackPostgres(tables: ["counters"])
            .WithDuplicateDeliveries()
            .RunAsync();
        watch.Stop();

        // Assert
        result.ShouldHaveNoViolations();
        Assert.Equal(20, result.BaselineTrace.Count);
        Assert.Equal(60, result.Scenarios.Count);
        Assert.Equal(2, result.DuplicateScenarios.Count);
        Assert.True(
            watch.Elapsed < TimeSpan.FromMinutes(2),
            $"The 20-effect sweep took {watch.Elapsed}."
        );
    }

    [Fact]
    public async Task Missing_instrumentation_is_rejected_before_the_environment_is_reset()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddFaultSweep();
        services.AddScoped<TwentyEffectHandler>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        bool reset = false;

        // Act
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                FaultSweep
                    .For<TwentyEffectHandler>(provider)
                    .WithMessage(20)
                    .ResetWith(
                        (_, _) =>
                        {
                            reset = true;
                            return ValueTask.CompletedTask;
                        }
                    )
                    .TrackPostgres("Host=localhost;Database=unused", tables: ["counters"])
                    .RunAsync()
        );

        // Assert
        Assert.False(reset);
        Assert.Contains("AddFaultSweepPostgres", exception.Message);
    }
}

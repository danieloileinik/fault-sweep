using FaultSweep.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FaultSweep.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class EfCoreTests(PostgresFixture postgres)
{
    public static IEnumerable<object[]> Styles()
    {
        bool[] executionModes = [false, true];
        bool[] idempotencyModes = [false, true];
        foreach (EfConnectionStyle style in Enum.GetValues<EfConnectionStyle>())
        {
            foreach (bool useAsync in executionModes)
            {
                foreach (bool idempotent in idempotencyModes)
                {
                    yield return [style, useAsync, idempotent];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public async Task Intercepts_queries_batched_save_changes_and_implicit_commits(
        EfConnectionStyle style, bool useAsync, bool idempotent)
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(database.ConnectionString);
        await using var host = new PostgresTestHost(database, services =>
        {
            services.AddDbContext<CounterContext>(options =>
            {
                switch (style)
                {
                    case EfConnectionStyle.ConnectionString:
                        options.UseNpgsql(database.ConnectionString);
                        break;
                    case EfConnectionStyle.DirectConnection:
                        options.UseNpgsql(new NpgsqlConnection(database.ConnectionString), contextOwnsConnection: true);
                        break;
                    case EfConnectionStyle.DataSource:
                        options.UseNpgsql(source);
                        break;
                }
            });
            services.AddScoped<IMessageHandler<PostgresMessage>, EfHandler>();
        });

        // Act
        FaultSweepResult result = await host.Sweep(new PostgresMessage(useAsync, true, idempotent)).RunAsync();

        // Assert
        Assert.Equal(3, result.BaselineTrace.Count);
        Assert.Equal(9, result.Scenarios.Count);
        Assert.Equal("Commit", result.BaselineTrace[^1].Point.Effect.Operation);
        Assert.Contains("UPDATE", result.BaselineTrace[1].Point.Effect.Target!);
        Assert.Equal(idempotent ? 0 : 2, result.Violations.Count);
        Assert.All(result.Scenarios, scenario => Assert.True(scenario.Attempts[^1].Succeeded, result.ToString()));
        if (idempotent)
        {
            result.ShouldHaveNoViolations();
        }
        else
        {
            Assert.All(result.Violations, violation =>
                Assert.Equal("Commit", violation.Scenario.Point.Effect.Operation));
        }
    }
}

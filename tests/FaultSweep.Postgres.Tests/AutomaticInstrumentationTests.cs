using System.Data.Common;
using FaultSweep.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FaultSweep.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AutomaticInstrumentationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_single_registration_covers_new_connections_without_database_services_or_factories()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        var services = new ServiceCollection();
        services.AddSingleton(database);
        services.AddScoped<DirectDapperHandler>();
        services.AddFaultSweepPostgres();
        services.AddFaultSweepPostgres();
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        FaultSweepResult result = await FaultSweep.For<DirectDapperHandler>(provider)
            .WithMessage(new PostgresMessage(true, true, false))
            .ResetWith((_, token) => database.ResetAsync(token))
            .TrackState("postgres", (_, token) => database.CaptureAsync(token))
            .RunAsync();

        // Assert
        Assert.Null(provider.GetService<DbDataSource>());
        Assert.Null(provider.GetService<Func<DbConnection>>());
        Assert.Equal(3, result.BaselineTrace.Count);
        Assert.Equal(2, result.Violations.Count);
        Assert.All(result.Scenarios, scenario => Assert.True(scenario.Attempts[^1].Succeeded));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Covers_a_connection_and_compiled_commands_created_before_the_sweep(bool useAsync)
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var warmup = new NpgsqlCommand("SELECT 1", connection))
        {
            for (int index = 0; index < 100; index++)
            {
                warmup.ExecuteScalar();
                await warmup.ExecuteScalarAsync();
            }
        }

        await using var host = new PostgresTestHost(database, services =>
        {
            services.AddSingleton(connection);
            services.AddScoped<IMessageHandler<PostgresMessage>, PreCreatedConnectionHandler>();
        });

        // Act
        FaultSweepResult result = await host.Sweep(new PostgresMessage(useAsync, false, false)).RunAsync();

        // Assert
        Assert.Single(result.BaselineTrace);
        Assert.Equal(2, result.Violations.Count);
        Assert.All(result.Scenarios, scenario => Assert.True(scenario.Attempts[^1].Succeeded));
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Covers_native_npgsql_batches_as_single_execution_boundaries(bool useAsync)
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using var host = new PostgresTestHost(database, services =>
            services.AddScoped<IMessageHandler<PostgresMessage>, BatchHandler>());

        // Act
        FaultSweepResult result = await host.Sweep(new PostgresMessage(useAsync, false, false)).RunAsync();

        // Assert
        Assert.Equal("Batch", Assert.Single(result.BaselineTrace).Point.Effect.Operation);
        Assert.Contains("id = 1", result.BaselineTrace[0].Point.Effect.Target!);
        Assert.Contains("id = 2", result.BaselineTrace[0].Point.Effect.Target!);
        Assert.Equal(2, result.Violations.Count);
        Assert.All(result.Violations, violation =>
        {
            Assert.Equal(20, violation.State["postgres"].Value[0].GetProperty("value").GetInt32());
            Assert.Equal(20, violation.State["postgres"].Value[1].GetProperty("value").GetInt32());
        });
    }

    [Fact]
    public async Task Keeps_independent_sweep_contexts_separate_when_they_execute_concurrently()
    {
        // Arrange
        await using TestDatabase firstDatabase = await postgres.CreateDatabaseAsync();
        await using TestDatabase secondDatabase = await postgres.CreateDatabaseAsync();
        await using var first = new PostgresTestHost(firstDatabase, AccessStyle.AdoDirect);
        await using var second = new PostgresTestHost(secondDatabase, AccessStyle.DapperDirect);

        // Act
        FaultSweepResult[] results = await Task.WhenAll(
            first.Sweep(new PostgresMessage(true, true, false)).RunAsync(),
            second.Sweep(new PostgresMessage(true, true, true)).RunAsync());

        // Assert
        Assert.Equal(2, results[0].Violations.Count);
        results[1].ShouldHaveNoViolations();
        Assert.All(results, result => Assert.Equal(3, result.BaselineTrace.Count));
    }

    [Fact]
    public async Task Leaves_unrelated_queries_untouched_during_and_after_a_sweep()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using var host = new PostgresTestHost(database, AccessStyle.DapperDirect);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int deliveries = 0;

        // Act
        Task<FaultSweepResult> sweep = host.Sweep(new PostgresMessage(true, false, true))
            .HandleWith(async (handler, message, token) =>
            {
                if (Interlocked.Increment(ref deliveries) == 1)
                {
                    entered.SetResult(true);
                    await release.Task.WaitAsync(token);
                }

                await handler.HandleAsync(message, token);
            }).RunAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var outside = new NpgsqlConnection(database.ConnectionString);
        await outside.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT 42", outside);
        object? during = await command.ExecuteScalarAsync();
        release.SetResult(true);
        FaultSweepResult result = await sweep.WaitAsync(TimeSpan.FromSeconds(20));
        object? after = await command.ExecuteScalarAsync();

        // Assert
        Assert.Equal(42, during);
        Assert.Equal(42, after);
        result.ShouldHaveNoViolations();
        Assert.Equal(2, result.BaselineTrace.Count);
        Assert.DoesNotContain(result.BaselineTrace, entry => entry.Point.Effect.Target == "SELECT 42");
    }
}

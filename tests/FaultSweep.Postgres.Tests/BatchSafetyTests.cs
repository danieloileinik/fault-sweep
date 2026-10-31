using FaultSweep.Effects;
using FaultSweep.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FaultSweep.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class BatchSafetyTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false, -1)]
    [InlineData(true, -1)]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public async Task Rejects_error_barriers_before_any_write_even_when_the_handler_catches_the_error(
        bool useAsync,
        int barrierCommand
    )
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using var host = new PostgresTestHost(
            database,
            services => services.AddScoped<IMessageHandler<PostgresMessage>, BatchHandler>()
        );
        bool caught = false;

        // Act
        FaultSweepExecutionException exception =
            await Assert.ThrowsAsync<FaultSweepExecutionException>(() =>
                host.Sweep(new PostgresMessage(useAsync, false, false))
                    .HandleWith(
                        async (_, _, token) =>
                        {
                            await using var connection = new NpgsqlConnection(
                                database.ConnectionString
                            );
                            await connection.OpenAsync(token);
                            await using NpgsqlBatch batch = CreateBatch(connection);
                            batch.EnableErrorBarriers = barrierCommand == -1;
                            if (barrierCommand >= 0)
                            {
                                batch.BatchCommands[barrierCommand].AppendErrorBarrier = true;
                            }

                            try
                            {
                                if (useAsync)
                                {
                                    await batch.ExecuteNonQueryAsync(token);
                                }
                                else
                                {
                                    batch.ExecuteNonQuery();
                                }
                            }
                            catch (FaultSweepExecutionException)
                            {
                                caught = true;
                            }
                        }
                    )
                    .RunAsync()
            );
        var state = await database.CaptureAsync(default);

        // Assert
        Assert.True(caught);
        Assert.Contains("error barriers", exception.Message);
        Assert.All(
            state.Value.EnumerateArray(),
            row => Assert.Equal(0, row.GetProperty("value").GetInt32())
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Allows_explicitly_disabled_command_barriers_as_one_atomic_effect(
        bool useAsync
    )
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using var host = new PostgresTestHost(
            database,
            services => services.AddScoped<IMessageHandler<PostgresMessage>, BatchHandler>()
        );

        // Act
        FaultSweepResult result = await host.Sweep(new PostgresMessage(useAsync, false, false))
            .HandleWith(
                async (_, _, token) =>
                {
                    await using var connection = new NpgsqlConnection(database.ConnectionString);
                    await connection.OpenAsync(token);
                    await using NpgsqlBatch batch = CreateBatch(connection);
                    foreach (NpgsqlBatchCommand command in batch.BatchCommands)
                    {
                        command.AppendErrorBarrier = false;
                    }

                    if (useAsync)
                    {
                        await batch.ExecuteNonQueryAsync(token);
                    }
                    else
                    {
                        batch.ExecuteNonQuery();
                    }
                }
            )
            .RunAsync();

        // Assert
        Assert.Equal("Batch", Assert.Single(result.BaselineTrace).Point.Effect.Operation);
        Assert.Equal(3, result.Scenarios.Count);
        Assert.Equal(2, result.Violations.Count);
        Assert.All(result.Scenarios, scenario => Assert.True(scenario.Attempts[^1].Succeeded));
    }

    private static NpgsqlBatch CreateBatch(NpgsqlConnection connection)
    {
        var batch = new NpgsqlBatch(connection);
        batch.BatchCommands.Add(
            new NpgsqlBatchCommand("UPDATE counters SET value = value + 10 WHERE id = 1")
        );
        batch.BatchCommands.Add(
            new NpgsqlBatchCommand("UPDATE counters SET value = value + 10 WHERE id = 2")
        );
        return batch;
    }
}

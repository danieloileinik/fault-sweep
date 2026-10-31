using FaultSweep.Effects;
using FaultSweep.Reporting;
using StackExchange.Redis;

namespace FaultSweep.Redis.Tests;

[Collection(RedisCollection.Name)]
public sealed class RawCommandSafetyTests(RedisFixture fixture)
{
    [Theory]
    [InlineData("XREADGROUP", false)]
    [InlineData("XREADGROUP", true)]
    [InlineData("XGROUP", false)]
    [InlineData("XGROUP", true)]
    [InlineData("XACK", false)]
    [InlineData("XACK", true)]
    [InlineData("XAUTOCLAIM", true)]
    [InlineData("XCLAIM", true)]
    [InlineData("XACKDEL", true)]
    [InlineData("XNACK", true)]
    [InlineData("MULTI", true)]
    [InlineData("PUBLISH", true)]
    [InlineData("FLUSHDB", true)]
    [InlineData("ARSET", true)]
    [InlineData("VADD", true)]
    [InlineData("UNSUPPORTED_COMMAND", true)]
    public async Task Rejects_unsupported_raw_commands_before_execution_even_when_caught(
        string command,
        bool useAsync
    )
    {
        // Arrange
        await using var host = new RedisTestHost(fixture.Connection);
        var request = new RedisRequest("raw:" + Guid.NewGuid().ToString("N") + ":");
        IDatabase database = fixture.Connection.GetDatabase();
        string stream = request.Prefix + "stream";
        bool caught = false;
        object[] arguments = command switch
        {
            "XREADGROUP" => ["GROUP", "group", "consumer", "STREAMS", stream, "0"],
            "XGROUP" => ["SETID", stream, "group", "2-0"],
            "XACK" => [stream, "group", "1-0"],
            _ => [],
        };

        // Act
        FaultSweepExecutionException exception =
            await Assert.ThrowsAsync<FaultSweepExecutionException>(() =>
                host.CreateSweep(request)
                    .ResetWith(
                        async (_, _) =>
                        {
                            await database.KeyDeleteAsync([stream, request.Prefix + "value"]);
                            await database.StreamAddAsync(stream, "field", "value", "1-0");
                            await database.StreamAddAsync(stream, "field", "value", "2-0");
                            await database.StreamCreateConsumerGroupAsync(stream, "group", "0-0");
                            await database.StreamReadGroupAsync(
                                stream,
                                "group",
                                "consumer",
                                ">",
                                count: 1
                            );
                        }
                    )
                    .HandleWith(
                        async (_, _, _) =>
                        {
                            try
                            {
                                if (useAsync)
                                {
                                    await database.ExecuteAsync(command, arguments);
                                }
                                else
                                {
                                    database.Execute(command, arguments);
                                }
                            }
                            catch (FaultSweepExecutionException)
                            {
                                caught = true;
                            }

                            await database.StringSetAsync(request.Prefix + "value", 1);
                        }
                    )
                    .RunAsync()
            );
        StreamPendingMessageInfo[] pending = await database.StreamPendingMessagesAsync(
            stream,
            "group",
            1,
            "consumer"
        );
        StreamGroupInfo[] groups = await database.StreamGroupInfoAsync(stream);

        // Assert
        Assert.True(caught);
        Assert.Contains("raw Redis command", exception.Message);
        Assert.Equal(1, Assert.Single(pending).DeliveryCount);
        Assert.Equal("1-0", Assert.Single(groups).LastDeliveredId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tracks_sort_store_as_a_write_and_leaves_pure_raw_reads_uninstrumented(
        bool useAsync
    )
    {
        // Arrange
        await using var host = new RedisTestHost(fixture.Connection);
        var request = new RedisRequest("sort:" + Guid.NewGuid().ToString("N") + ":");
        IDatabase database = fixture.Connection.GetDatabase();
        string source = request.Prefix + "list";
        string destination = request.Prefix + "value";

        // Act
        FaultSweepResult result = await host.CreateSweep(request)
            .ResetWith(
                async (_, _) =>
                {
                    await database.KeyDeleteAsync([source, destination]);
                    await database.ListRightPushAsync(
                        source,
                        [(RedisValue)3, (RedisValue)1, (RedisValue)2]
                    );
                }
            )
            .HandleWith(
                async (_, _, _) =>
                {
                    if (useAsync)
                    {
                        await database.ExecuteAsync("exists", source);
                        await database.ExecuteAsync("sort", source, "STORE", destination);
                    }
                    else
                    {
                        database.Execute("exists", source);
                        database.Execute("sort", source, "STORE", destination);
                    }
                }
            )
            .RunAsync();

        // Assert
        result.ShouldHaveNoViolations();
        Assert.Equal("redis", Assert.Single(result.BaselineTrace).Point.Effect.Source);
        Assert.Equal(3, result.Scenarios.Count);
        Assert.All(result.Scenarios, scenario => Assert.True(scenario.Attempts[^1].Succeeded));
        Assert.Equal(
            ["1", "2", "3"],
            (await database.ListRangeAsync(destination)).Select(value => value.ToString())
        );
    }
}

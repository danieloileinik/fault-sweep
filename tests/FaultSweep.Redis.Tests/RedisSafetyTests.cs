using FaultSweep.Effects;
using StackExchange.Redis;

namespace FaultSweep.Redis.Tests;

[Collection(RedisCollection.Name)]
public sealed class RedisSafetyTests(RedisFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejects_queued_and_unacknowledged_writes_even_if_caught(bool fireAndForget)
    {
        // Arrange
        await using var host = new RedisTestHost(fixture.Connection);
        bool caught = false;

        // Act
        FaultSweepExecutionException exception = await Assert.ThrowsAsync<FaultSweepExecutionException>(() =>
            host.CreateSweep(new RedisRequest(Guid.NewGuid().ToString("N") + ":")).HandleWith((_, request, _) =>
            {
                try
                {
                    IDatabase database = fixture.Connection.GetDatabase();
                    if (fireAndForget)
                    {
                        database.StringSet(request.Prefix + "value", 1, flags: CommandFlags.FireAndForget);
                    }
                    else
                    {
                        database.CreateTransaction();
                    }
                }
                catch (FaultSweepExecutionException) { caught = true; }
                return ValueTask.CompletedTask;
            }).RunAsync());

        // Assert
        Assert.True(caught);
        Assert.Contains(fireAndForget ? "FireAndForget" : "transactions", exception.Message);
    }
}

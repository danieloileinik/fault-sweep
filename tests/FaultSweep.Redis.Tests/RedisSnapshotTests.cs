using FaultSweep.Snapshots;
using StackExchange.Redis;

namespace FaultSweep.Redis.Tests;

[Collection(RedisCollection.Name)]
public sealed class RedisSnapshotTests(RedisFixture fixture)
{
    [Fact]
    public async Task Snapshots_all_key_types_ignores_TTL_and_treats_the_prefix_literally()
    {
        // Arrange
        string prefix = Guid.NewGuid().ToString("N") + "[*]:";
        IDatabase database = fixture.Connection.GetDatabase();
        await database.StringSetAsync(prefix + "string", new byte[] { 0, 255 }, TimeSpan.FromHours(1));
        await database.HashSetAsync(prefix + "hash", [new HashEntry("b", 2), new HashEntry("a", 1)]);
        await database.ListRightPushAsync(prefix + "list", [(RedisValue)2, (RedisValue)1]);
        await database.SetAddAsync(prefix + "set", [(RedisValue)2, (RedisValue)1]);
        await database.SortedSetAddAsync(prefix + "sorted", [new SortedSetEntry("b", 2), new SortedSetEntry("a", 1)]);
        await database.StreamAddAsync(prefix + "stream", "field", "value", "1-0");
        await database.StringSetAsync(prefix.Replace("[*]", "X") + "other", 1);
        var snapshot = new RedisSnapshotProvider(fixture.Connection, prefix);

        // Act
        StateSnapshot first = await snapshot.CaptureAsync(CancellationToken.None);
        await database.KeyExpireAsync(prefix + "string", TimeSpan.FromHours(2));
        StateSnapshot second = await snapshot.CaptureAsync(CancellationToken.None);

        // Assert
        Assert.Equal(first.ToString(), second.ToString());
        Assert.Equal(6, first.Value.EnumerateObject().Count());
        Assert.DoesNotContain("TimeToLive", first.ToString());
    }
}

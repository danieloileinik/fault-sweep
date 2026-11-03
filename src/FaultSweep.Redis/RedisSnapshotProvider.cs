using System.Net;
using FaultSweep.Snapshots;
using StackExchange.Redis;

namespace FaultSweep.Redis;

/// <summary>Captures supported key values with binary-safe base64 encoding. TTL is excluded.</summary>
public sealed class RedisSnapshotProvider(IConnectionMultiplexer connection, string prefix, int database = 0) : ISnapshotProvider
{
    public async ValueTask<StateSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var keys = new SortedDictionary<string, RedisKey>(StringComparer.Ordinal);
        string escaped = string.Concat(prefix.Select(character => "*?[]\\".Contains(character) ? "\\" + character : character.ToString()));
        foreach (EndPoint endpoint in connection.GetEndPoints())
        {
            IServer server = connection.GetServer(endpoint);
            if (server.IsReplica)
            {
                continue;
            }

            await foreach (RedisKey key in server.KeysAsync(database, escaped + "*"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                keys[Convert.ToBase64String((byte[])key!)] = key;
            }
        }
        IDatabase db = connection.GetDatabase(database);
        var state = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, RedisKey> pair in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RedisType type = await db.KeyTypeAsync(pair.Value);
            object? value = type switch
            {
                RedisType.None => null,
                RedisType.String => Encode(await db.StringGetAsync(pair.Value)),
                RedisType.Hash => (await db.HashGetAllAsync(pair.Value)).OrderBy(entry => Encode(entry.Name), StringComparer.Ordinal).Select(entry => new { Name = Encode(entry.Name), Value = Encode(entry.Value) }).ToArray(),
                RedisType.List => (await db.ListRangeAsync(pair.Value)).Select(Encode).ToArray(),
                RedisType.Set => (await db.SetMembersAsync(pair.Value)).Select(Encode).Order(StringComparer.Ordinal).ToArray(),
                RedisType.SortedSet => (await db.SortedSetRangeByRankWithScoresAsync(pair.Value)).Select(entry => new { Element = Encode(entry.Element), Score = double.IsFinite(entry.Score) ? (object)entry.Score : entry.Score.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray(),
                RedisType.Stream => (await db.StreamRangeAsync(pair.Value)).Select(entry => new { Id = Encode(entry.Id), Values = entry.Values.Select(item => new { Name = Encode(item.Name), Value = Encode(item.Value) }).ToArray() }).ToArray(),
                _ => throw new NotSupportedException($"Redis snapshot type {type} is not supported.")
            };
            if (type != RedisType.None)
            {
                state.Add(pair.Key, new
                {
                    Type = type.ToString(),
                    Value = value
                });
            }
        }
        return StateSnapshot.From(state);
    }

    private static string? Encode(RedisValue value) => value.IsNull ? null : Convert.ToBase64String((byte[])value!);
}

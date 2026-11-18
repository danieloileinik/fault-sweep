using System.Text.Json;
using StackExchange.Redis;

namespace FaultSweep.Redis;

internal static class RedisRawCommandKeys
{
    private static readonly HashSet<string> SingleKeyWrites = new(
        """
        APPEND BITFIELD DECR DECRBY EXPIRE EXPIREAT GEOADD GETDEL GETEX GETSET HDEL HEXPIRE HEXPIREAT
        HGETDEL HGETEX HINCRBY HINCRBYFLOAT HMSET HPERSIST HPEXPIRE HPEXPIREAT HSET HSETEX HSETNX INCR
        INCRBY INCRBYFLOAT LINSERT LPOP LPUSH LPUSHX LREM LSET LTRIM PERSIST PEXPIRE PEXPIREAT PFADD
        PSETEX RESTORE RPOP RPUSH RPUSHX SADD SET SETBIT SETEX SETNX SETRANGE SPOP SREM XADD XDEL
        XTRIM ZADD ZINCRBY ZPOPMAX ZPOPMIN ZREM ZREMRANGEBYLEX ZREMRANGEBYRANK ZREMRANGEBYSCORE
        """.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal
    );

    public static string? Describe(string command, object? arguments)
    {
        IEnumerable<RedisValue>? keys;
        if (arguments is ReadOnlyMemory<RedisKeyOrValue> memory)
        {
            keys = Keys(
                command,
                memory.Length,
                index =>
                {
                    RedisKeyOrValue argument = memory.Span[index];
                    return argument.IsKey ? (byte[]?)argument.Key : argument.Value;
                }
            );
        }
        else
        {
            IReadOnlyList<object?> items =
                arguments as IReadOnlyList<object?>
                ?? (arguments as ICollection<object>)?.ToArray()
                ?? [];
            keys = Keys(
                command,
                items.Count,
                index =>
                    items[index] is RedisKey key ? (byte[]?)key : RedisValue.Unbox(items[index])
            );
        }

        // JSON encodes each key as base64, preserving binary keys and argument boundaries.
        return keys is null ? null : JsonSerializer.Serialize(keys.Select(key => (byte[]?)key));
    }

    private static IEnumerable<RedisValue>? Keys(
        string command,
        int length,
        Func<int, RedisValue> at
    )
    {
        IEnumerable<RedisValue> Range(int start, int count, int stride = 1)
        {
            for (int i = start; i < length && i - start < count; i += stride)
            {
                yield return at(i);
            }
        }

        IEnumerable<RedisValue> Counted(int countIndex) =>
            countIndex < length && at(countIndex).TryParse(out int count)
                ? Range(countIndex + 1, count)
                : [];

        return command switch
        {
            "DEL" or "UNLINK" or "PFMERGE" or "SDIFFSTORE" or "SINTERSTORE" or "SUNIONSTORE" =>
                Range(0, length),
            "BITOP" => Range(1, length),
            "MSET" or "MSETNX" => Range(0, length, 2),
            "BLPOP" or "BRPOP" or "BZPOPMAX" or "BZPOPMIN" => Range(0, length - 1),
            "COPY"
            or "SMOVE"
            or "RENAME"
            or "RENAMENX"
            or "LMOVE"
            or "BLMOVE"
            or "RPOPLPUSH"
            or "BRPOPLPUSH"
            or "GEOSEARCHSTORE"
            or "ZRANGESTORE" => Range(0, 2),
            "LMPOP" or "ZMPOP" => Counted(0),
            "BLMPOP" or "BZMPOP" or "EVAL" or "EVALSHA" or "FCALL" => Counted(1),
            "ZDIFFSTORE" or "ZINTERSTORE" or "ZUNIONSTORE" => Range(0, 1).Concat(Counted(1)),
            "SORT" => Range(0, 1).Concat(OptionKeys(1)),
            "GEORADIUS" => Range(0, 1).Concat(OptionKeys(5)),
            "GEORADIUSBYMEMBER" => Range(0, 1).Concat(OptionKeys(4)),
            _ when SingleKeyWrites.Contains(command) => Range(0, 1),
            _ => null,
        };

        IEnumerable<RedisValue> OptionKeys(int start)
        {
            for (int i = start; i < length; i++)
            {
                switch (at(i).ToString().ToUpperInvariant())
                {
                    case "STORE" or "STOREDIST" or "BY" or "GET" when i + 1 < length:
                        yield return at(++i);
                        break;
                    case "LIMIT":
                        i += 2;
                        break;
                    case "COUNT":
                        i++;
                        break;
                }
            }
        }
    }
}

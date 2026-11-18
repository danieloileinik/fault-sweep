using FaultSweep.Effects;
using StackExchange.Redis;

namespace FaultSweep.Redis;

internal static class RedisOperationClassifier
{
    // csharpier-ignore-start -- Keep consecutive fields together.
    private static readonly HashSet<string> Reads = new(StringComparer.Ordinal)
    {
        "StringGetBit",
        "StringGetRange",
        "StringDigest",
        "StringGet",
        "StringGetWithExpiry",
        "StringGetLease",
        "StringLength",
        "StringBitCount",
        "StringBitPosition",
        "StringLongestCommonSubsequence",
        "StringLongestCommonSubsequenceLength",
        "StringLongestCommonSubsequenceWithMatches",
        "HashGetLease",
        "HashRandomField",
        "HashRandomFields",
        "HashRandomFieldsWithValues",
        "HashStringLength",
        "HashGet",
        "HashGetAll",
        "HashExists",
        "HashLength",
        "HashKeys",
        "HashValues",
        "HashScan",
        "HashScanNoValues",
        "HashFieldGetExpireDateTime",
        "HashFieldGetTimeToLive",
        "KeyRandom",
        "LockQuery",
        "Sort",
        "SetCombineLength",
        "ScriptEvaluateReadOnly",
        "ScriptEvaluateReadOnlyResp",
        "KeyExists",
        "KeyType",
        "KeyTimeToLive",
        "KeyExpireTime",
        "KeyIdleTime",
        "KeyRefCount",
        "KeyDump",
        "KeyEncoding",
        "KeyFrequency",
        "ListGetByIndex",
        "ListLength",
        "ListRange",
        "ListPosition",
        "ListPositions",
        "SetContains",
        "SetLength",
        "SetMembers",
        "SetRandomMember",
        "SetRandomMembers",
        "SetScan",
        "SetCombine",
        "SetIntersectionLength",
        "SortedSetRangeByRank",
        "SortedSetRangeByRankWithScores",
        "SortedSetRangeByScore",
        "SortedSetRangeByScoreWithScores",
        "SortedSetRangeByValue",
        "SortedSetRank",
        "SortedSetScore",
        "SortedSetScores",
        "SortedSetLength",
        "SortedSetLengthByValue",
        "SortedSetScan",
        "SortedSetRandomMember",
        "SortedSetRandomMembers",
        "SortedSetRandomMembersWithScores",
        "SortedSetCombine",
        "SortedSetCombineWithScores",
        "SortedSetIntersectionLength",
        "StreamRead",
        "StreamReadGroup",
        "StreamRange",
        "StreamLength",
        "StreamInfo",
        "StreamConsumerInfo",
        "StreamGroupInfo",
        "StreamPending",
        "StreamPendingMessages",
        "HyperLogLogLength",
        "GeoDistance",
        "GeoHash",
        "GeoPosition",
        "GeoRadius",
        "GeoSearch",
        "Ping",
        "RandomKey",
        "IdentifyEndpoint",
        "DebugObject",
        "IsConnected",
        "Wait",
        "WaitAll",
        "TryWait",
    };
    private static readonly HashSet<string> RawReads = new(
        """
        BITCOUNT BITFIELD_RO BITPOS COMMAND DBSIZE DUMP ECHO EVAL_RO EVALSHA_RO EXISTS EXPIRETIME FCALL_RO
        GEODIST GEOHASH GEOPOS GEORADIUS_RO GEORADIUSBYMEMBER_RO GEOSEARCH GET GETBIT GETRANGE
        HEXISTS HEXPIRETIME HGET HGETALL HKEYS HLEN HMGET HPEXPIRETIME HPTTL HRANDFIELD HSCAN HSTRLEN HTTL HVALS
        INFO KEYS LASTSAVE LINDEX LLEN LPOS LRANGE MGET OBJECT PEXPIRETIME PFCOUNT PING PTTL PUBSUB RANDOMKEY
        SCAN SCARD SDIFF SINTER SINTERCARD SISMEMBER SMEMBERS SMISMEMBER SORT_RO SRANDMEMBER SSCAN STRLEN SUBSTR SUNION
        TIME TTL TYPE XINFO XLEN XPENDING XRANGE XREAD XREVRANGE
        ZCARD ZCOUNT ZDIFF ZINTER ZINTERCARD ZLEXCOUNT ZMSCORE ZRANDMEMBER ZRANGE ZRANGEBYLEX ZRANGEBYSCORE ZRANK
        ZREVRANGE ZREVRANGEBYLEX ZREVRANGEBYSCORE ZREVRANK ZSCAN ZSCORE ZUNION
        """.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
        StringComparer.OrdinalIgnoreCase
    );
    // Commands with optional writes, such as SORT STORE, must always expose a fault boundary.
    private static readonly HashSet<string> RawWrites = new(
        """
        APPEND BITFIELD BITOP BLMOVE BLMPOP BLPOP BRPOP BRPOPLPUSH BZMPOP BZPOPMAX BZPOPMIN COPY DECR DECRBY DEL
        EVAL EVALSHA EXPIRE EXPIREAT FCALL GEOADD GEORADIUS GEORADIUSBYMEMBER GEOSEARCHSTORE GETDEL GETEX GETSET
        HDEL HEXPIRE HEXPIREAT HGETDEL HGETEX HINCRBY HINCRBYFLOAT HMSET HPERSIST HPEXPIRE HPEXPIREAT HSET HSETEX HSETNX
        INCR INCRBY INCRBYFLOAT LINSERT LMOVE LMPOP LPOP LPUSH LPUSHX LREM LSET LTRIM MSET MSETNX
        PERSIST PEXPIRE PEXPIREAT PFADD PFMERGE PSETEX RENAME RENAMENX RESTORE RPOP RPOPLPUSH RPUSH RPUSHX
        SADD SDIFFSTORE SET SETBIT SETEX SETNX SETRANGE SINTERSTORE SMOVE SORT SPOP SREM SUNIONSTORE UNLINK
        XADD XDEL XTRIM ZADD ZDIFFSTORE ZINCRBY ZINTERSTORE ZMPOP ZPOPMAX ZPOPMIN ZRANGESTORE ZREM
        ZREMRANGEBYLEX ZREMRANGEBYRANK ZREMRANGEBYSCORE ZUNIONSTORE
        """.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
        StringComparer.OrdinalIgnoreCase
    );
    // csharpier-ignore-end

    public static bool IsRead(string method)
    {
        string name = method.EndsWith("Async", StringComparison.Ordinal) ? method[..^5] : method;
        // Group reads modify pending-entry state and are therefore effects.
        return name != "StreamReadGroup" && Reads.Contains(name);
    }

    public static RedisOperation Classify(string method, object?[] arguments)
    {
        string name = method.EndsWith("Async", StringComparison.Ordinal) ? method[..^5] : method;
        if (
            name is "CreateTransaction" or "CreateBatch"
            || arguments[0]?.GetType().Name is "RedisTransaction" or "RedisBatch"
        )
        {
            return new RedisOperation(
                Rejection: "Redis transactions and queued batches are not supported by fault injection."
            );
        }

        if (IsRead(method))
        {
            return new RedisOperation();
        }

        if (
            name == "Publish"
            || name.StartsWith("Array", StringComparison.Ordinal)
            || name.StartsWith("VectorSet", StringComparison.Ordinal)
            || name
                is "StreamReadGroup"
                    or "StreamAcknowledge"
                    or "StreamAcknowledgeAndDelete"
                    or "StreamAutoClaim"
                    or "StreamAutoClaimIdsOnly"
                    or "StreamClaim"
                    or "StreamClaimIdsOnly"
                    or "StreamCreateConsumerGroup"
                    or "StreamDeleteConsumerGroup"
                    or "StreamDeleteConsumer"
                    or "StreamConsumerGroupSetPosition"
                    or "StreamNegativeAcknowledge"
        )
        {
            return new RedisOperation(
                Rejection: "This Redis operation has state outside the supported key snapshot types."
            );
        }

        if (
            arguments.OfType<CommandFlags>().Any(flags => flags.HasFlag(CommandFlags.FireAndForget))
        )
        {
            return new RedisOperation(
                Rejection: "Redis FireAndForget cannot confirm whether an effect was applied."
            );
        }

        if (name is "Execute" or "ExecuteResp")
        {
            string command = arguments[1]?.ToString() ?? "";
            if (RawReads.Contains(command))
            {
                return new RedisOperation();
            }

            if (!RawWrites.Contains(command))
            {
                return new RedisOperation(
                    Rejection: $"The raw Redis command '{command}' cannot be safely instrumented. Use a supported key command."
                );
            }

            string verb = command.ToUpperInvariant();
            return new RedisOperation(
                new EffectDescriptor(
                    "redis",
                    verb,
                    RedisRawCommandKeys.Describe(verb, arguments[2])
                )
            );
        }

        string target =
            arguments.Skip(1).OfType<RedisKey>().Select(key => key.ToString()).FirstOrDefault()
            ?? "";
        return new RedisOperation(new EffectDescriptor("redis", name, target));
    }
}

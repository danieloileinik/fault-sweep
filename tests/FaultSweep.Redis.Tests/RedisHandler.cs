using StackExchange.Redis;

namespace FaultSweep.Redis.Tests;

public sealed class RedisHandler(IDatabase database) : IMessageHandler<RedisRequest>
{
    public async ValueTask HandleAsync(RedisRequest request, CancellationToken cancellationToken)
    {
        string key = request.Prefix + "value";
        if (request.Async)
        {
            _ = await database.KeyExistsAsync(key);
            _ = await database.StringGetAsync(request.Prefix + "read");
            switch (request.Operation)
            {
                case "string":
                    if (request.Idempotent)
                    {
                        await database.StringSetAsync(key, request.Amount);
                    }
                    else
                    {
                        await database.StringIncrementAsync(key, request.Amount);
                    }

                    break;
                case "hash":
                    await database.HashIncrementAsync(key, "amount", request.Amount);
                    break;
                case "list":
                    await database.ListRightPushAsync(key, request.Amount);
                    break;
                case "set":
                    await database.SetAddAsync(key, request.Amount);
                    break;
                case "sorted":
                    await database.SortedSetIncrementAsync(key, "member", request.Amount);
                    break;
                case "raw":
                    await database.ExecuteAsync("INCRBY", key, request.Amount);
                    break;
                case "script":
                    await database.ScriptEvaluateAsync("return redis.call('INCRBY', KEYS[1], ARGV[1])", [(RedisKey)key], [(RedisValue)request.Amount]);
                    break;
            }
        }
        else
        {
            _ = database.KeyExists(key);
            _ = database.StringGet(request.Prefix + "read");
            if (request.Idempotent)
            {
                database.StringSet(key, request.Amount);
            }
            else
            {
                database.StringIncrement(key, request.Amount);
            }
        }
    }
}

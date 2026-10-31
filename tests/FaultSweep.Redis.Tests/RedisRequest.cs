namespace FaultSweep.Redis.Tests;

public sealed record RedisRequest(string Prefix, bool Async = true, bool Idempotent = false, int Amount = 10, string Operation = "string");

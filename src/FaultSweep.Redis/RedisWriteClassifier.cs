using System.Reflection;
using StackExchange.Redis;

namespace FaultSweep.Redis;

internal static class RedisWriteClassifier
{
    private static readonly HashSet<string> Reads = new(StringComparer.Ordinal)
    {
        "StringGetBit", "StringGetRange", "StringDigest", "StringGet", "StringGetWithExpiry", "StringGetLease", "StringLength", "StringBitCount", "StringBitPosition", "StringLongestCommonSubsequence", "StringLongestCommonSubsequenceLength", "StringLongestCommonSubsequenceWithMatches",
        "HashGetLease", "HashRandomField", "HashRandomFields", "HashRandomFieldsWithValues", "HashStringLength", "HashGet", "HashGetAll", "HashExists", "HashLength", "HashKeys", "HashValues", "HashScan", "HashScanNoValues", "HashFieldGetExpireDateTime", "HashFieldGetTimeToLive",
        "KeyRandom", "LockQuery", "Sort", "SetCombineLength", "ScriptEvaluateReadOnly", "ScriptEvaluateReadOnlyResp", "KeyExists", "KeyType", "KeyTimeToLive", "KeyExpireTime", "KeyIdleTime", "KeyRefCount", "KeyDump", "KeyEncoding", "KeyFrequency",
        "ListGetByIndex", "ListLength", "ListRange", "ListPosition", "ListPositions",
        "SetContains", "SetLength", "SetMembers", "SetRandomMember", "SetRandomMembers", "SetScan", "SetCombine", "SetIntersectionLength",
        "SortedSetRangeByRank", "SortedSetRangeByRankWithScores", "SortedSetRangeByScore", "SortedSetRangeByScoreWithScores", "SortedSetRangeByValue", "SortedSetRank", "SortedSetScore", "SortedSetScores", "SortedSetLength", "SortedSetLengthByValue", "SortedSetScan", "SortedSetRandomMember", "SortedSetRandomMembers", "SortedSetRandomMembersWithScores", "SortedSetCombine", "SortedSetCombineWithScores", "SortedSetIntersectionLength",
        "StreamRead", "StreamReadGroup", "StreamRange", "StreamLength", "StreamInfo", "StreamConsumerInfo", "StreamGroupInfo", "StreamPending", "StreamPendingMessages",
        "HyperLogLogLength", "GeoDistance", "GeoHash", "GeoPosition", "GeoRadius", "GeoSearch",
        "Ping", "RandomKey", "IdentifyEndpoint", "DebugObject", "IsConnected", "Wait", "WaitAll", "TryWait"
    };
    public static bool IsRead(string method)
    {
        string name = method.EndsWith("Async", StringComparison.Ordinal) ? method[..^5] : method;
        // Group reads modify pending-entry state and are therefore effects.
        return name != "StreamReadGroup" && Reads.Contains(name);
    }

    public static bool RawCommandWrites(string command)
    {
        Assembly assembly = typeof(IDatabase).Assembly;
        Type enumType = assembly.GetType("StackExchange.Redis.RedisCommand", true)!;
        Type extensions = assembly.GetTypes().Single(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(method => method.Name == "IsPrimaryOnly" && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == enumType));
        object? value = null;
        if (command.ToUpperInvariant() is "MULTI" or "EXEC" or "DISCARD" or "WATCH" or "UNWATCH" or "PUBLISH" or "SPUBLISH" || !Enum.TryParse(enumType, command, true, out value))
        {
            RedisInstrumentation.Current.Value!.ReportInstrumentationFailure("This raw Redis command cannot be safely instrumented.");
        }
        MethodInfo classifier = extensions.GetMethod("IsPrimaryOnly", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        return (bool)classifier.Invoke(null, [value])!;
    }
}

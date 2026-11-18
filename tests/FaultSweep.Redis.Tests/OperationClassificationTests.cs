using StackExchange.Redis;

namespace FaultSweep.Redis.Tests;

public sealed class OperationClassificationTests
{
    [Theory]
    [InlineData("get", false, false)]
    [InlineData("SORT", true, false)]
    [InlineData("set", true, false)]
    [InlineData("XREADGROUP", false, true)]
    [InlineData("UNKNOWN_COMMAND", false, true)]
    public void Classifies_raw_commands_without_an_active_interceptor(
        string command,
        bool effect,
        bool rejected
    )
    {
        // Arrange
        object?[] arguments = [new object(), command, Array.Empty<object>()];

        // Act
        RedisOperation operation = RedisOperationClassifier.Classify("ExecuteAsync", arguments);

        // Assert
        Assert.Equal(effect, operation.Effect is not null);
        Assert.Equal(rejected, operation.Rejection is not null);
    }

    [Theory]
    [InlineData("StringGetAsync", false, false)]
    [InlineData("StringSetAsync", true, false)]
    [InlineData("StreamReadGroupAsync", false, true)]
    [InlineData("CreateTransaction", false, true)]
    public void Separates_reads_effects_and_unsupported_operations(
        string method,
        bool effect,
        bool rejected
    )
    {
        // Arrange
        object?[] arguments = [new object(), (RedisKey)"balance", CommandFlags.None];

        // Act
        RedisOperation operation = RedisOperationClassifier.Classify(method, arguments);

        // Assert
        Assert.Equal(effect, operation.Effect is not null);
        Assert.Equal(rejected, operation.Rejection is not null);
        if (effect)
        {
            Assert.Equal("balance", operation.Effect!.Target);
        }
    }

    [Fact]
    public void Raw_descriptors_preserve_ordered_binary_keys_and_ignore_payloads_across_overloads()
    {
        // Arrange
        byte[] first = [0xff];
        byte[] second = [0xfe];
        object[] pairs = [first, "one", (RedisKey)second, "two"];
        RedisKeyOrValue[] respPairs =
        [
            (RedisValue)first,
            (RedisValue)"changed",
            (RedisKey)second,
            (RedisValue)"again",
        ];
        object[] reversedPairs = [second, "one", first, "two"];

        // Act
        var expected = RedisOperationClassifier.Classify("Execute", [new object(), "mset", pairs]);
        var actual = RedisOperationClassifier.Classify(
            "ExecuteRespAsync",
            [new object(), "MSET", (ReadOnlyMemory<RedisKeyOrValue>)respPairs]
        );
        var reversed = RedisOperationClassifier.Classify(
            "ExecuteAsync",
            [new object(), "MSET", reversedPairs]
        );

        // Assert
        Assert.Equal("MSET", expected.Effect!.Operation);
        Assert.Equal(expected.Effect, actual.Effect);
        Assert.NotEqual(expected.Effect, reversed.Effect);
    }

    [Theory]
    [InlineData("BLMPOP", "0 2 first second LEFT COUNT 1", "first second")]
    [InlineData("ZINTERSTORE", "dest 2 first second WEIGHTS 2 3", "dest first second")]
    [InlineData("EVAL", "script 2 first second payload", "first second")]
    [InlineData("SORT", "first LIMIT 0 2 STORE dest", "first dest")]
    [InlineData("GEORADIUSBYMEMBER", "first STORE 2 km STOREDIST dest", "first dest")]
    public void Extracts_keys_from_counted_and_optional_arguments(
        string command,
        string args,
        string keys
    )
    {
        // Arrange
        object[] arguments = args.Split(' ');
        object[] expectedKeys = keys.Split(' ');

        // Act
        string? target = RedisRawCommandKeys.Describe(command, arguments);

        // Assert
        Assert.NotNull(target);
        Assert.Equal(RedisRawCommandKeys.Describe("DEL", expectedKeys), target);
    }

    [Fact]
    public void Rejects_unconfirmed_writes_before_producing_an_effect()
    {
        // Arrange
        object?[] arguments = [new object(), (RedisKey)"balance", CommandFlags.FireAndForget];

        // Act
        RedisOperation operation = RedisOperationClassifier.Classify("StringSet", arguments);

        // Assert
        Assert.Null(operation.Effect);
        Assert.Contains("FireAndForget", operation.Rejection);
    }
}

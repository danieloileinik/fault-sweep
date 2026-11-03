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

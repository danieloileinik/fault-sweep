using FaultSweep.Reporting;

namespace FaultSweep.Redis.Tests;

[Collection(RedisCollection.Name)]
public sealed class RedisInterceptionTests(RedisFixture fixture)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Intercepts_precreated_database_writes_and_skips_reads(bool useAsync, bool idempotent)
    {
        // Arrange
        await using var host = new RedisTestHost(fixture.Connection);
        var request = new RedisRequest(Guid.NewGuid().ToString("N") + ":", useAsync, idempotent);

        // Act
        FaultSweepResult result = await host.CreateSweep(request).RunAsync();

        // Assert
        Assert.Single(result.BaselineTrace);
        Assert.Equal(idempotent ? 0 : 2, result.Violations.Count);
        Assert.All(result.Scenarios, scenario => Assert.True(scenario.Attempts[^1].Succeeded, result.ToString()));
        Assert.Equal(1, result.MinimalScenario?.Result.Scenario.Point.Index ?? 1);
    }
    [Theory]
    [InlineData("hash", 2)]
    [InlineData("list", 2)]
    [InlineData("set", 0)]
    [InlineData("sorted", 2)]
    [InlineData("raw", 2)]
    [InlineData("script", 2)]
    public async Task Supports_multiple_write_types(string operation, int violations)
    {
        // Arrange
        await using var host = new RedisTestHost(fixture.Connection);
        var request = new RedisRequest(Guid.NewGuid().ToString("N") + ":", Operation: operation);

        // Act
        FaultSweepResult result = await host.CreateSweep(request).RunAsync();

        // Assert
        Assert.Single(result.BaselineTrace);
        Assert.Equal(violations, result.Violations.Count);
    }
}

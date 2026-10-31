using System.Text;
using Confluent.Kafka;
using FaultSweep.Kafka;
using FaultSweep.Postgres;
using FaultSweep.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FaultSweep.Redis.Tests;

[Collection(RedisCollection.Name)]
public sealed class MixedStoreTests(RedisFixture fixture)
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Finds_cache_before_commit_and_accepts_idempotent_outbox(bool correct, bool compareWithBaseline)
    {
        // Arrange
        string schema = "m2_" + Guid.NewGuid().ToString("N");
        string prefix = schema + ":";
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(fixture.PostgresConfiguration);
        await using (NpgsqlCommand setup = source.CreateCommand($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.orders(id int PRIMARY KEY, amount int); CREATE TABLE {schema}.outbox(id int PRIMARY KEY, payload text)"))
        {
            await setup.ExecuteNonQueryAsync();
        }

        var services = new ServiceCollection();
        services.AddSingleton(fixture);
        services.AddScoped<MixedHandler>();
        services.AddFaultSweepPostgres();
        services.AddFaultSweepRedis(fixture.Connection);
        services.AddFaultSweepKafka<int, string>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        try
        {

            // Act
            FaultSweepResult result = await FaultSweep.For<MixedHandler>(provider).WithMessage(new MixedRequest(schema, prefix, correct))
                .WithOptions(new FaultSweepOptions { CompareWithBaseline = compareWithBaseline })
                .ResetWith(async (_, token) =>
                {
                    await using NpgsqlCommand reset = source.CreateCommand($"TRUNCATE {schema}.orders, {schema}.outbox");
                    await reset.ExecuteNonQueryAsync(token);
                    await fixture.Connection.GetDatabase().KeyDeleteAsync(prefix + "cache");
                })
                .TrackPostgres(fixture.PostgresConfiguration, tables: ["orders", "outbox"], schema: schema)
                .TrackRedis(prefix)
                .TrackProduced()
                .CheckInvariant("one order", context => context.State["postgres"].Value.GetProperty("orders").GetArrayLength() == 1)
                .CheckInvariant("correct amount", context => context.State["postgres"].Value.GetProperty("orders")
                    .EnumerateArray().All(order => order.GetProperty("amount").GetInt32() == 10))
                .CheckInvariant("cache matches durable state", context =>
                {
                    var orders = context.State["postgres"].Value.GetProperty("orders");
                    string encodedKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(context.Message.Prefix + "cache"));
                    return orders.GetArrayLength() == 1
                        && context.State["redis"].Value.TryGetProperty(encodedKey, out var cache)
                        && int.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(cache.GetProperty("Value").GetString()!)), out int amount)
                        && amount == orders[0].GetProperty("amount").GetInt32();
                })
                .CheckInvariant("logical event exists", context =>
                    context.State["postgres"].Value.GetProperty("outbox").EnumerateArray().Any(row =>
                        row.GetProperty("id").GetInt32() == 1 && row.GetProperty("payload").GetString() == "placed")
                    || context.State["produced"].Value.EnumerateArray().Any(item =>
                        item.GetProperty("Key").GetInt32() == 1 && item.GetProperty("Value").GetString() == "placed"))
                .RunAsync();

            // Assert
            Assert.Equal(!correct, result.HasViolations);
            Assert.False(result.BaselineVerification.HasViolations, result.ToString());
            Assert.All(result.Scenarios, scenario => Assert.True(scenario.Attempts[^1].Succeeded, result.ToString()));
            if (!correct)
            {
                Assert.Equal("redis", result.MinimalScenario!.Result.Scenario.Point.Effect.Source);
                Assert.Contains(result.Violations, violation => violation.Scenario.Kind == FaultKind.AppliedButFailed);
                Assert.Contains(result.Violations, violation => violation.InvariantViolations.Any(invariant =>
                    invariant.Name == "cache matches durable state"));
                Assert.Contains(result.Violations, violation => violation.InvariantViolations.Any(invariant =>
                    invariant.Name == "logical event exists"));
            }
            if (!compareWithBaseline)
            {
                Assert.All(result.Scenarios, scenario => Assert.Empty(scenario.Differences));
            }
        }
        finally
        {
            await using NpgsqlCommand cleanup = source.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
            await fixture.Connection.GetDatabase().KeyDeleteAsync(prefix + "cache");
        }
    }
}

using FaultSweep.Effects;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FaultSweep.Mongo.Tests;

[Collection(MongoCollection.Name)]
public sealed class MongoTests(MongoFixture fixture)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true, "client-update")]
    [InlineData(true, true, "replace")]
    [InlineData(true, false, "replace")]
    [InlineData(true, true, "delete")]
    [InlineData(true, false, "delete")]
    [InlineData(true, true, "find-update")]
    [InlineData(true, false, "find-update")]
    [InlineData(true, true, "find-replace")]
    [InlineData(true, false, "find-replace")]
    [InlineData(true, true, "find-delete")]
    [InlineData(true, false, "find-delete")]
    public async Task Intercepts_sync_and_async_writes_and_duplicate_deliveries(
        bool correct,
        bool useAsync,
        string operation = "update"
    )
    {
        // Arrange
        IMongoDatabase database = fixture.Client.GetDatabase(
            "sweep_" + Guid.NewGuid().ToString("N")
        );
        var registrations = new ServiceCollection();
        registrations.AddFaultSweepMongo(database);
        registrations.AddScoped<MongoHandler>();
        await using ServiceProvider provider = registrations.BuildServiceProvider();
        try
        {
            // Act
            FaultSweepResult result = await FaultSweep
                .For<MongoHandler>(provider)
                .WithMessage(new MongoRequest(correct, useAsync, operation))
                .ResetWith(
                    async (_, token) =>
                    {
                        IMongoCollection<BsonDocument> collection =
                            database.GetCollection<BsonDocument>("payments");
                        await collection.DeleteManyAsync(
                            FilterDefinition<BsonDocument>.Empty,
                            token
                        );
                        if (operation.EndsWith("delete", StringComparison.Ordinal))
                        {
                            await collection.InsertOneAsync(
                                new BsonDocument { { "_id", 1 }, { "amount", 10 } },
                                cancellationToken: token
                            );
                        }
                    }
                )
                .TrackMongo(["payments"])
                .WithDuplicateDeliveries()
                .RunAsync();

            // Assert
            Assert.Equal(!correct, result.HasViolations);
            Assert.Equal("mongo", Assert.Single(result.BaselineTrace).Point.Effect.Source);
            Assert.Equal(3, result.Scenarios.Count);
            Assert.Equal(2, result.DuplicateScenarios.Count);
            if (!correct)
            {
                Assert.Contains(
                    result.Violations,
                    violation => violation.Scenario.Kind == FaultKind.AppliedButFailed
                );
            }
        }
        finally
        {
            await fixture.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Insert_is_idempotent_and_reads_are_not_effects(bool useAsync)
    {
        // Arrange
        IMongoDatabase database = fixture.Client.GetDatabase(
            "insert_" + Guid.NewGuid().ToString("N")
        );
        var registrations = new ServiceCollection();
        registrations.AddFaultSweepMongo(database);
        registrations.AddScoped<MongoHandler>();
        await using ServiceProvider provider = registrations.BuildServiceProvider();
        try
        {
            // Act
            FaultSweepResult result = await FaultSweep
                .For<MongoHandler>(provider)
                .WithMessage(new MongoRequest(true, useAsync, "insert"))
                .ResetWith(
                    async (_, token) =>
                        await database
                            .GetCollection<BsonDocument>("payments")
                            .DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, token)
                )
                .TrackMongo(["payments"])
                .RunAsync();

            // Assert
            result.ShouldHaveNoViolations();
            Assert.Equal("InsertOne", Assert.Single(result.BaselineTrace).Point.Effect.Operation);
        }
        finally
        {
            await fixture.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task Rejects_unsupported_bulk_writes_even_when_the_handler_catches_the_error()
    {
        // Arrange
        IMongoDatabase database = fixture.Client.GetDatabase(
            "reject_" + Guid.NewGuid().ToString("N")
        );
        var registrations = new ServiceCollection();
        registrations.AddFaultSweepMongo(database);
        registrations.AddScoped<MongoHandler>();
        await using ServiceProvider provider = registrations.BuildServiceProvider();

        // Act
        FaultSweepExecutionException error = await Assert.ThrowsAsync<FaultSweepExecutionException>(
            () =>
                FaultSweep
                    .For<MongoHandler>(provider)
                    .WithMessage(new MongoRequest(false, true, "bulk"))
                    .ResetWith((_, _) => ValueTask.CompletedTask)
                    .TrackMongo(["payments"])
                    .RunAsync()
        );

        // Assert
        Assert.Contains("InsertManyAsync", error.Message);
    }

    [Fact]
    public async Task Snapshots_order_by_id_preserve_bson_types_and_ignore_selected_fields()
    {
        // Arrange
        IMongoDatabase database = fixture.Client.GetDatabase(
            "snapshot_" + Guid.NewGuid().ToString("N")
        );
        IMongoCollection<BsonDocument> collection = database.GetCollection<BsonDocument>(
            "payments"
        );
        try
        {
            await collection.InsertManyAsync([
                new BsonDocument
                {
                    { "_id", 2 },
                    { "value", new BsonInt64(5) },
                    { "created_at", "a" },
                },
                new BsonDocument
                {
                    { "_id", 1 },
                    { "value", new BsonInt64(6) },
                    { "created_at", "b" },
                },
            ]);
            var snapshots = new MongoSnapshotProvider(
                database,
                ["payments", "empty"],
                ["created_at"]
            );

            // Act
            StateSnapshot before = await snapshots.CaptureAsync(default);
            await collection.UpdateManyAsync(
                FilterDefinition<BsonDocument>.Empty,
                Builders<BsonDocument>.Update.Set("created_at", "changed")
            );
            StateSnapshot after = await snapshots.CaptureAsync(default);

            // Assert
            Assert.Equal(before.ToString(), after.ToString());
            Assert.Equal(
                "1",
                before
                    .Value.GetProperty("payments")[0]
                    .GetProperty("_id")
                    .GetProperty("$numberInt")
                    .GetString()
            );
            Assert.True(
                before
                    .Value.GetProperty("payments")[0]
                    .GetProperty("value")
                    .TryGetProperty("$numberLong", out _)
            );
            Assert.Empty(before.Value.GetProperty("empty").EnumerateArray());
        }
        finally
        {
            await fixture.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }
}

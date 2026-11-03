using System.Reflection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FaultSweep.Mongo.Tests;

public sealed class OperationClassificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Describes_writes_and_rejects_unacknowledged_write_concern(bool acknowledged)
    {
        // Arrange
        IMongoCollection<BsonDocument> collection = new MongoClient("mongodb://127.0.0.1:1")
            .GetDatabase("classification")
            .GetCollection<BsonDocument>("payments")
            .WithWriteConcern(
                acknowledged ? WriteConcern.Acknowledged : WriteConcern.Unacknowledged
            );
        MethodInfo method = typeof(IMongoCollection<BsonDocument>)
            .GetMethods()
            .First(candidate => candidate.Name == "InsertOneAsync");

        // Act
        MongoOperation operation = MongoOperationClassifier.Classify(method, collection, []);

        // Assert
        Assert.Equal("InsertOne", operation.Effect!.Operation);
        Assert.Equal(collection.CollectionNamespace.ToString(), operation.Effect.Target);
        Assert.Equal(!acknowledged, operation.Rejection is not null);
    }

    [Theory]
    [InlineData("CountDocumentsAsync", false)]
    [InlineData("InsertManyAsync", true)]
    [InlineData("get_Indexes", true)]
    public void Separates_reads_and_unsupported_operations(string name, bool rejected)
    {
        // Arrange
        MethodInfo method = typeof(IMongoCollection<BsonDocument>)
            .GetMethods()
            .First(candidate => candidate.Name == name);

        // Act
        MongoOperation operation = MongoOperationClassifier.Classify(method, new object(), []);

        // Assert
        Assert.Null(operation.Effect);
        Assert.Equal(rejected, operation.Rejection is not null);
    }

    [Fact]
    public void Keeps_collections_wrapped_for_later_deliveries()
    {
        // Arrange
        MethodInfo method = typeof(IMongoDatabase)
            .GetMethods()
            .Single(candidate => candidate.Name == "GetCollection");

        // Act
        MongoOperation operation = MongoOperationClassifier.Classify(method, new object(), []);

        // Assert
        Assert.True(operation.WrapResult);
        Assert.Null(operation.Effect);
        Assert.Null(operation.Rejection);
    }
}

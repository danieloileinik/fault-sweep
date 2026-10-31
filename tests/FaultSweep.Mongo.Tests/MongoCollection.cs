namespace FaultSweep.Mongo.Tests;

[CollectionDefinition(Name)]
public sealed class MongoCollection : ICollectionFixture<MongoFixture>
{
    public const string Name = "MongoDB integration";
}

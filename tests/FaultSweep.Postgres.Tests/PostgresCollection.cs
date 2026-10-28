namespace FaultSweep.Postgres.Tests;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL integration";
}

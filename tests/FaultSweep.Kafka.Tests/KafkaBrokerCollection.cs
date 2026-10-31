namespace FaultSweep.Kafka.Tests;

[CollectionDefinition(Name)]
public sealed class KafkaBrokerCollection : ICollectionFixture<KafkaBrokerFixture>
{
    public const string Name = "Kafka broker integration";
}

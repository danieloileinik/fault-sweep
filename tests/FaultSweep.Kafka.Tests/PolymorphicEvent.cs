namespace FaultSweep.Kafka.Tests;

public abstract record PolymorphicEvent(string Id) : IPolymorphicEvent;

namespace FaultSweep.Kafka.Tests;

public sealed record PaymentEvent(string Id, int Amount) : PolymorphicEvent(Id);

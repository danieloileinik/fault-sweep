namespace FaultSweep.Kafka.Tests;

public sealed record PublishRequest(bool Async = true, bool ExplicitPartition = false, bool Mutate = false);

namespace FaultSweep.Kafka;

internal sealed record CapturedKafkaHeader(string Key, byte[]? Value);

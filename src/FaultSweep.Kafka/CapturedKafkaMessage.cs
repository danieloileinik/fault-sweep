using System.Text.Json;

namespace FaultSweep.Kafka;

internal sealed record CapturedKafkaMessage(
    string Topic, int Partition, JsonElement Key, JsonElement Value, IReadOnlyList<CapturedKafkaHeader> Headers);

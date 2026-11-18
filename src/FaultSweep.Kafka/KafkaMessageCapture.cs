using System.Text.Json;
using Confluent.Kafka;
using FaultSweep.Effects;
using FaultSweep.Snapshots;

namespace FaultSweep.Kafka;

internal sealed class KafkaMessageCapture
{
    private readonly AsyncLocal<KafkaCaptureSession?> current = new();

    public KafkaCaptureSession Session =>
        current.Value
        ?? throw new InvalidOperationException(
            "The capture producer can only be used inside a FaultSweep run. Register AddFaultSweepKafka<TKey, TValue>()."
        );

    public IDisposable Activate(IEffectInterceptor interceptor)
    {
        KafkaCaptureSession? previous = current.Value;
        current.Value = new KafkaCaptureSession(interceptor);
        return new KafkaCaptureScope(() => current.Value = previous);
    }

    public void Reset()
    {
        KafkaCaptureSession session = Session;
        lock (session.Gate)
        {
            session.Messages.Clear();
            session.NextOffsets.Clear();
        }
    }

    public long Add<TKey, TValue>(
        TopicPartition destination,
        Partition assignedPartition,
        Message<TKey, TValue> message
    )
    {
        var captured = new CapturedKafkaMessage(
            destination.Topic,
            destination.Partition.Value,
            JsonSerializer.SerializeToElement((object?)message.Key),
            JsonSerializer.SerializeToElement((object?)message.Value),
            message
                .Headers?.Select(header => new CapturedKafkaHeader(
                    header.Key,
                    header.GetValueBytes()?.ToArray()
                ))
                .ToArray()
                ?? []
        );
        KafkaCaptureSession session = Session;
        lock (session.Gate)
        {
            var key = (destination.Topic, assignedPartition.Value);
            long offset = session.NextOffsets.GetValueOrDefault(key);
            session.NextOffsets[key] = offset + 1;
            session.Messages.Add(captured);
            return offset;
        }
    }

    public StateSnapshot Snapshot()
    {
        KafkaCaptureSession session = Session;
        lock (session.Gate)
        {
            return StateSnapshot.From(session.Messages);
        }
    }
}

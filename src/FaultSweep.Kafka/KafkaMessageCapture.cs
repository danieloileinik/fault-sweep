using System.Text.Json;
using Confluent.Kafka;
using FaultSweep.Effects;
using FaultSweep.Snapshots;

namespace FaultSweep.Kafka;

internal sealed class KafkaMessageCapture
{
    private readonly AsyncLocal<KafkaCaptureSession?> current = new();

    public KafkaCaptureSession Session => current.Value ?? throw new InvalidOperationException(
        "The capture producer can only be used inside a FaultSweep run. Register AddFaultSweepKafka<TKey, TValue>().");

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
        }
    }

    public long Add<TKey, TValue>(TopicPartition destination, Message<TKey, TValue> message)
    {
        var captured = new CapturedKafkaMessage(destination.Topic, destination.Partition.Value,
            JsonSerializer.SerializeToElement(message.Key), JsonSerializer.SerializeToElement(message.Value),
            message.Headers?.Select(header => new CapturedKafkaHeader(header.Key, header.GetValueBytes()?.ToArray())).ToArray() ?? []);
        KafkaCaptureSession session = Session;
        lock (session.Gate)
        {
            long offset = session.Messages.Count;
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

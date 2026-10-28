using FaultSweep.Snapshots;

namespace FaultSweep.Kafka;

internal sealed class ProducedSnapshotProvider(KafkaMessageCapture capture) : ISnapshotProvider
{
    public ValueTask<StateSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(capture.Snapshot());
    }
}

namespace FaultSweep.Snapshots;

public interface ISnapshotProvider
{
    ValueTask<StateSnapshot> CaptureAsync(CancellationToken cancellationToken);
}

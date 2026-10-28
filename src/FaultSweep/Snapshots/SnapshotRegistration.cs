namespace FaultSweep.Snapshots;

internal sealed record SnapshotRegistration(
    string Name,
    Func<IServiceProvider, CancellationToken, ValueTask<StateSnapshot>> Capture,
    SnapshotComparer Comparer);

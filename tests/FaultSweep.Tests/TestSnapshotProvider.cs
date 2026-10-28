using FaultSweep.Snapshots;

namespace FaultSweep.Tests;

public sealed class TestSnapshotProvider(TestState state) : ISnapshotProvider
{
    public ValueTask<StateSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(state.Snapshot());
    }
}

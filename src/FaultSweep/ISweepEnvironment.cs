using FaultSweep.Snapshots;

namespace FaultSweep;

/// <summary>A prepared store whose initial state is restored between scenarios and on disposal.</summary>
public interface ISweepEnvironment : ISnapshotProvider, IAsyncDisposable
{
    ValueTask ResetAsync(CancellationToken cancellationToken);
}

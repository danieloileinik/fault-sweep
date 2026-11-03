using FaultSweep.Snapshots;

namespace FaultSweep.Tests;

internal sealed class CallbackSweepEnvironment(Func<StateSnapshot> capture, Action reset)
    : ISweepEnvironment
{
    public ValueTask<StateSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(capture());

    public ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

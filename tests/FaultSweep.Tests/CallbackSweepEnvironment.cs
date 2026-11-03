using FaultSweep.Snapshots;

namespace FaultSweep.Tests;

internal sealed class CallbackSweepEnvironment(
    Func<StateSnapshot> capture,
    Action reset,
    Action? dispose = null
) : ISweepEnvironment
{
    public ValueTask<StateSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(capture());

    public ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        dispose?.Invoke();
        return ValueTask.CompletedTask;
    }
}

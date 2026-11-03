namespace FaultSweep.Tests;

internal sealed class CallbackDisposable(Action dispose) : IDisposable
{
    public void Dispose() => dispose();
}

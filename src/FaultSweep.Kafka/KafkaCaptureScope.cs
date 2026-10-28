namespace FaultSweep.Kafka;

internal sealed class KafkaCaptureScope(Action restore) : IDisposable
{
    private bool disposed;

    public void Dispose()
    {
        if (!disposed)
        {
            restore();
            disposed = true;
        }
    }
}

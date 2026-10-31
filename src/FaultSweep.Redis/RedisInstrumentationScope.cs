namespace FaultSweep.Redis;

internal sealed class RedisInstrumentationScope(Action restore) : IDisposable
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

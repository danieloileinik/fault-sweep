using FaultSweep.Effects;

namespace FaultSweep.Postgres;

internal sealed class PostgresInstrumentationScope : IDisposable
{
    private readonly IEffectInterceptor? previous;
    private bool disposed;

    public PostgresInstrumentationScope(IEffectInterceptor interceptor)
    {
        previous = PostgresInstrumentation.Current.Value;
        PostgresInstrumentation.Current.Value = interceptor;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        PostgresInstrumentation.Current.Value = previous;
        disposed = true;
    }
}

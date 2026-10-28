using FaultSweep.Effects;

namespace FaultSweep.Postgres;

public sealed class PostgresInstrumentation : IEffectInstrumentation
{
    internal static AsyncLocal<IEffectInterceptor?> Current { get; } = new();

    public IDisposable Activate(IEffectInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        NpgsqlRuntimeHooks.EnsureInstalled();
        return new PostgresInstrumentationScope(interceptor);
    }
}

using FaultSweep.Effects;

namespace FaultSweep.Redis;

public sealed class RedisInstrumentation : IEffectInstrumentation
{
    internal static AsyncLocal<IEffectInterceptor?> Current { get; } = new();
    public IDisposable Activate(IEffectInterceptor interceptor)
    {
        RedisRuntimeHooks.Install();
        IEffectInterceptor? previous = Current.Value;
        Current.Value = interceptor;
        return new RedisInstrumentationScope(() => Current.Value = previous);
    }
}

using FaultSweep.Effects;

namespace FaultSweep.Tests;

internal sealed class CallbackInstrumentation(Func<IEffectInterceptor, IDisposable> activate)
    : IEffectInstrumentation
{
    public IDisposable Activate(IEffectInterceptor interceptor) => activate(interceptor);
}

namespace FaultSweep.Effects;

/// <summary>Activates provider interception for the current asynchronous sweep context.</summary>
public interface IEffectInstrumentation
{
    IDisposable Activate(IEffectInterceptor interceptor);
}

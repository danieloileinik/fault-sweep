using FaultSweep.Effects;

namespace FaultSweep.Execution;

internal sealed class EffectInstrumentationScope : IDisposable
{
    private readonly List<IDisposable> scopes = [];

    public EffectInstrumentationScope(IEnumerable<IEffectInstrumentation> instrumentations, IEffectInterceptor interceptor)
    {
        try
        {
            foreach (IEffectInstrumentation instrumentation in instrumentations)
            {
                scopes.Add(instrumentation.Activate(interceptor));
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        for (int index = scopes.Count - 1; index >= 0; index--)
        {
            scopes[index].Dispose();
        }

        scopes.Clear();
    }
}

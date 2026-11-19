using FaultSweep.Effects;

namespace FaultSweep.Execution;

internal sealed class EffectInstrumentationScope : IDisposable
{
    private readonly List<IDisposable> scopes = [];

    public EffectInstrumentationScope(
        IEnumerable<IEffectInstrumentation> instrumentations,
        IEffectInterceptor interceptor
    )
    {
        try
        {
            foreach (IEffectInstrumentation instrumentation in instrumentations)
            {
                scopes.Add(instrumentation.Activate(interceptor));
            }
        }
        catch (Exception exception)
        {
            Dispose(exception);
            throw;
        }
    }

    public void Dispose() => Dispose(null);

    private void Dispose(Exception? failure)
    {
        var active = scopes.ToArray();
        scopes.Clear();
        List<Exception> failures = [];
        for (int index = active.Length - 1; index >= 0; index--)
        {
            try
            {
                active[index].Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "Failed to release FaultSweep instrumentation scopes.",
                failure is null ? failures : [failure, .. failures]
            );
        }
    }
}

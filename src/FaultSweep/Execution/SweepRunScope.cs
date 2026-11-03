using FaultSweep.Effects;

namespace FaultSweep.Execution;

internal sealed class SweepRunScope : IAsyncDisposable
{
    private readonly EffectInterceptor interceptor;
    private readonly Func<ValueTask>? cleanup;

    private EffectInstrumentationScope? instrumentation;
    private Exception? failure;
    private bool disposed;

    private SweepRunScope(EffectInterceptor interceptor, Func<ValueTask>? cleanup)
    {
        this.interceptor = interceptor;
        this.cleanup = cleanup;
    }

    public static async ValueTask<SweepRunScope> AcquireAsync(
        EffectInterceptor interceptor,
        Func<ValueTask>? cleanup,
        CancellationToken token
    )
    {
        await interceptor.SweepLock.WaitAsync(token).ConfigureAwait(false);
        return new SweepRunScope(interceptor, cleanup);
    }

    // Activation must run in the caller's execution context so AsyncLocal changes reach deliveries.
    public void Activate(IEnumerable<IEffectInstrumentation> instrumentations) =>
        instrumentation = new EffectInstrumentationScope(instrumentations, interceptor);

    public void RecordFailure(Exception exception) => failure = exception;

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        List<Exception> failures = [];
        ReleaseInstrumentation(failures);
        try
        {
            if (cleanup is not null)
            {
                await cleanup().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            interceptor.SweepLock.Release();
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "Failed to release FaultSweep run resources.",
                failure is null ? failures : [failure, .. failures]
            );
        }
    }

    private void ReleaseInstrumentation(List<Exception> failures)
    {
        try
        {
            instrumentation?.Dispose();
        }
        catch (AggregateException exception)
        {
            failures.AddRange(exception.InnerExceptions);
        }
    }
}

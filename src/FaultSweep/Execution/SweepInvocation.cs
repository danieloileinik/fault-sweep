using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Execution;

internal static class SweepInvocation<THandler, TMessage>
    where THandler : class
{
    public static Func<IServiceProvider, TMessage, CancellationToken, ValueTask> Create(
        Func<THandler, TMessage, CancellationToken, ValueTask>? handle = null,
        bool createHandler = false
    )
    {
        if (handle is null && MediatRInvocation<THandler, TMessage>.Create() is { } dispatch)
        {
            return dispatch;
        }

        Func<THandler, TMessage, CancellationToken, ValueTask> invoke =
            handle ?? HandlerInvocation<THandler, TMessage>.Create();
        return async (provider, message, token) =>
        {
            THandler? handler = provider.GetService<THandler>();
            if (handler is not null || !createHandler)
            {
                await invoke(handler ?? provider.GetRequiredService<THandler>(), message, token)
                    .ConfigureAwait(false);
                return;
            }

            handler = ActivatorUtilities.CreateInstance<THandler>(provider);
            try
            {
                await invoke(handler, message, token).ConfigureAwait(false);
            }
            finally
            {
                if (handler is IAsyncDisposable asynchronous)
                {
                    await asynchronous.DisposeAsync().ConfigureAwait(false);
                }
                else if (handler is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        };
    }
}

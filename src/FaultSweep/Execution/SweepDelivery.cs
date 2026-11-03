using FaultSweep.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Execution;

internal sealed class SweepDelivery<TMessage>(
    IServiceProvider services,
    EffectInterceptor interceptor,
    Func<IServiceProvider, TMessage, CancellationToken, ValueTask> invoke
)
{
    public async ValueTask<DeliveryAttempt> DeliverAsync(
        EffectSession session,
        int attempt,
        TMessage runMessage,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Exception? error = null;
        try
        {
            // Disposal is part of a delivery: open transactions are rolled back before redelivery.
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await interceptor
                .RunAsync(
                    session,
                    () => invoke(scope.ServiceProvider, runMessage, cancellationToken)
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested
            )
        {
            error = exception;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (session.InfrastructureFailure is not null)
        {
            throw session.InfrastructureFailure;
        }

        error = session.Crash ?? error;
        return new DeliveryAttempt(attempt, error is null, error);
    }
}

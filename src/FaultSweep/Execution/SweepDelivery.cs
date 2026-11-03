using FaultSweep.Effects;
using FaultSweep.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Execution;

internal sealed class SweepDelivery<TMessage>(
    IServiceProvider services,
    EffectInterceptor interceptor,
    Func<IServiceProvider, TMessage, CancellationToken, ValueTask> invoke
)
{
    public async Task<DeliveryRun> DeliverWithRetriesAsync(
        TMessage message,
        DeliveryPlan plan,
        CancellationToken token
    )
    {
        List<DeliveryAttempt> attempts = [];
        List<EffectTraceEntry> trace = [];
        bool injected = false;
        for (int attempt = 1; attempt <= plan.MaxRedeliveryAttempts + 1; attempt++)
        {
            var identity = new DeliveryIdentity(plan.Delivery, attempt);
            int number = (attempt - 1) * plan.DeliveryCount + plan.Delivery;
            var session = new EffectSession(
                number,
                plan.MaxEffects,
                attempt == 1 ? plan.Fault : null,
                attempt == 1 ? plan.Expected : null
            );
            DeliveryAttempt result = await DeliverAsync(session, number, message, token)
                .ConfigureAwait(false);
            attempts.Add(result with { Identity = identity });
            trace.AddRange(session.Trace.Select(entry => entry with { Identity = identity }));
            injected |= session.FaultWasInjected;
            if (result.Succeeded)
            {
                break;
            }
        }

        return new DeliveryRun(attempts.AsReadOnly(), trace.AsReadOnly(), injected);
    }

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

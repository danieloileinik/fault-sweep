using FaultSweep.Effects;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using FaultSweep.Verification;

namespace FaultSweep.Execution;

internal sealed class DuplicateScenarioExecutor<TMessage>(
    SweepState store,
    SweepDelivery<TMessage> executor,
    SweepVerifier<TMessage> verifier,
    FaultSweepOptions options
)
{
    public async Task<DuplicateScenarioResult> RunAsync(
        DuplicateDeliveryMode mode,
        IReadOnlyDictionary<string, StateSnapshot> baseline,
        IReadOnlyDictionary<string, StateSnapshot> expected,
        TMessage runMessage,
        CancellationToken token
    )
    {
        await store.ResetAsync(token).ConfigureAwait(false);
        (List<DeliveryAttempt> Attempts, List<EffectTraceEntry> Trace)[] deliveries;
        if (mode == DuplicateDeliveryMode.Sequential)
        {
            deliveries =
            [
                await DeliverDuplicateAsync(0, runMessage, token).ConfigureAwait(false),
                await DeliverDuplicateAsync(1, runMessage, token).ConfigureAwait(false),
            ];
        }
        else
        {
            deliveries = await DeliverConcurrentlyAsync(runMessage, token).ConfigureAwait(false);
        }

        DeliveryAttempt[] attempts = deliveries
            .SelectMany(delivery => delivery.Attempts)
            .OrderBy(attempt => attempt.Number)
            .ToArray();
        EffectTraceEntry[] trace = deliveries
            .SelectMany(delivery => delivery.Trace)
            .OrderBy(entry => entry.Attempt)
            .ThenBy(entry => entry.Point.Index)
            .ToArray();
        IReadOnlyDictionary<string, StateSnapshot> state = await store
            .CaptureAsync(token)
            .ConfigureAwait(false);
        VerificationResult verification = await verifier
            .VerifyAsync(
                runMessage,
                state,
                expected,
                baseline,
                null,
                Array.AsReadOnly(attempts),
                token,
                mode
            )
            .ConfigureAwait(false);
        return new DuplicateScenarioResult(
            mode,
            attempts,
            trace,
            state,
            verification,
            deliveries.All(delivery => delivery.Attempts[^1].Succeeded)
        );
    }

    private async Task<(
        List<DeliveryAttempt> Attempts,
        List<EffectTraceEntry> Trace
    )[]> DeliverConcurrentlyAsync(TMessage runMessage, CancellationToken token)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int waiting = 0;
        Task<(List<DeliveryAttempt>, List<EffectTraceEntry>)> Launch(int deliveryIndex) =>
            Task.Run(
                async () =>
                {
                    if (Interlocked.Increment(ref waiting) == 2)
                    {
                        start.TrySetResult();
                    }

                    await start.Task.WaitAsync(token).ConfigureAwait(false);
                    return await DeliverDuplicateAsync(deliveryIndex, runMessage, token)
                        .ConfigureAwait(false);
                },
                token
            );

        Task<(List<DeliveryAttempt>, List<EffectTraceEntry>)>[] tasks = [Launch(0), Launch(1)];
        // Observe both deliveries before capture, reset, or propagating cancellation and infrastructure failures.
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<(
        List<DeliveryAttempt> Attempts,
        List<EffectTraceEntry> Trace
    )> DeliverDuplicateAsync(int delivery, TMessage runMessage, CancellationToken token)
    {
        List<DeliveryAttempt> attempts = [];
        List<EffectTraceEntry> trace = [];
        for (int retry = 0; retry <= options.MaxRedeliveryAttempts; retry++)
        {
            int number = retry * 2 + delivery + 1;
            var session = new EffectSession(number, options.MaxEffects);
            DeliveryAttempt attempt = await executor
                .DeliverAsync(session, number, runMessage, token)
                .ConfigureAwait(false);
            attempts.Add(attempt);
            trace.AddRange(session.Trace);
            if (attempt.Succeeded)
            {
                break;
            }
        }

        return (attempts, trace);
    }
}

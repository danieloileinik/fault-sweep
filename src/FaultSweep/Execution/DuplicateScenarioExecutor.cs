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
        int[] deliveryIds = [1, 2];
        Func<CancellationToken, Task<DeliveryRun>> CreateDelivery(int id) =>
            cancellation =>
                executor.DeliverWithRetriesAsync(
                    runMessage,
                    new DeliveryPlan(
                        options.MaxEffects,
                        options.MaxRedeliveryAttempts,
                        id,
                        deliveryIds.Length
                    ),
                    cancellation
                );
        DeliveryRun[] deliveries = await DeliveryScheduler
            .RunAsync(deliveryIds.Select(CreateDelivery).ToArray(), mode, token)
            .ConfigureAwait(false);

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
}

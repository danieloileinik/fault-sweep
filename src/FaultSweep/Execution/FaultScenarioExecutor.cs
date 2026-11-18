using FaultSweep.Effects;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using FaultSweep.Verification;

namespace FaultSweep.Execution;

internal sealed class FaultScenarioExecutor<TMessage>(
    SweepState store,
    SweepDelivery<TMessage> delivery,
    SweepVerifier<TMessage> verifier,
    FaultSweepOptions options,
    ReplayScenario? replay
)
{
    public async Task<FaultScenarioResult> RunAsync(
        FaultScenario scenario,
        IReadOnlyList<EffectPoint> baselinePoints,
        IReadOnlyDictionary<string, StateSnapshot> baselineState,
        IReadOnlyDictionary<string, StateSnapshot> expected,
        TMessage runMessage,
        CancellationToken cancellationToken
    )
    {
        await store.ResetAsync(cancellationToken).ConfigureAwait(false);
        DeliveryRun run = await delivery
            .DeliverWithRetriesAsync(
                runMessage,
                new DeliveryPlan(
                    options.MaxEffects,
                    replay?.MaxRedeliveryAttempts ?? options.MaxRedeliveryAttempts,
                    Fault: scenario,
                    Expected: baselinePoints
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        IReadOnlyDictionary<string, StateSnapshot> state = await store
            .CaptureAsync(cancellationToken)
            .ConfigureAwait(false);
        VerificationResult verification = await verifier
            .VerifyAsync(
                runMessage,
                state,
                expected,
                baselineState,
                scenario,
                run.Attempts,
                cancellationToken
            )
            .ConfigureAwait(false);

        ViolationKind violations = verification.GetScenarioViolations(run.Attempts[^1].Succeeded);
        if (!run.FaultWasInjected)
        {
            violations |= ViolationKind.FaultNotReached;
        }

        return new FaultScenarioResult(
            scenario,
            violations,
            run.Attempts,
            run.Trace,
            state,
            verification
        );
    }
}

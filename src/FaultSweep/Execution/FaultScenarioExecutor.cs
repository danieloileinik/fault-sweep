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
        var initialSession = new EffectSession(1, options.MaxEffects, scenario, baselinePoints);
        List<DeliveryAttempt> attempts =
        [
            await delivery
                .DeliverAsync(initialSession, 1, runMessage, cancellationToken)
                .ConfigureAwait(false),
        ];
        List<EffectTraceEntry> trace = [.. initialSession.Trace];

        for (
            int retry = 1;
            !attempts[^1].Succeeded
                && retry <= (replay?.MaxRedeliveryAttempts ?? options.MaxRedeliveryAttempts);
            retry++
        )
        {
            var session = new EffectSession(retry + 1, options.MaxEffects);
            attempts.Add(
                await delivery
                    .DeliverAsync(session, retry + 1, runMessage, cancellationToken)
                    .ConfigureAwait(false)
            );
            trace.AddRange(session.Trace);
        }

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
                attempts.AsReadOnly(),
                cancellationToken
            )
            .ConfigureAwait(false);

        ViolationKind violations = ViolationKind.None;
        if (verification.Differences.Count > 0)
        {
            violations |= ViolationKind.StateMismatch;
        }

        if (verification.InvariantViolations.Count > 0)
        {
            violations |= ViolationKind.InvariantFailed;
        }

        if (!attempts[^1].Succeeded)
        {
            violations |= ViolationKind.RedeliveryLimitExceeded;
        }

        if (!initialSession.FaultWasInjected)
        {
            violations |= ViolationKind.FaultNotReached;
        }

        return new FaultScenarioResult(scenario, violations, attempts, trace, state, verification);
    }
}

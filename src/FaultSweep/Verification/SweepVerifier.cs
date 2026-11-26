using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Verification;

internal sealed class SweepVerifier<TMessage>(
    IServiceProvider services,
    IReadOnlyList<SnapshotRegistration> snapshots,
    IReadOnlyList<InvariantRegistration<TMessage>> invariants,
    bool compareWithBaseline
)
{
    public async ValueTask<VerificationResult> VerifyAsync(
        TMessage runMessage,
        IReadOnlyDictionary<string, StateSnapshot> state,
        IReadOnlyDictionary<string, StateSnapshot> expected,
        IReadOnlyDictionary<string, StateSnapshot>? baselineState,
        FaultScenario? scenario,
        IReadOnlyList<DeliveryAttempt> attempts,
        CancellationToken cancellationToken,
        DuplicateDeliveryMode? duplicates = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<DifferenceEvidence> differences = [];
        foreach (SnapshotRegistration snapshot in snapshots)
        {
            StateSnapshot? reference =
                expected.GetValueOrDefault(snapshot.Name)
                ?? (compareWithBaseline ? baselineState?.GetValueOrDefault(snapshot.Name) : null);
            if (reference is not null)
            {
                differences.AddRange(
                    snapshot.Comparer.Compare(
                        snapshot.Name,
                        reference,
                        state[snapshot.Name],
                        !expected.ContainsKey(snapshot.Name)
                    )
                );
            }
        }

        List<InvariantViolation> failures = [];
        if (invariants.Count > 0)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            var context = new VerificationContext<TMessage>(
                runMessage,
                scope.ServiceProvider,
                state,
                scenario,
                attempts,
                duplicates
            );
            foreach (InvariantRegistration<TMessage> invariant in invariants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!await invariant.Check(context, cancellationToken).ConfigureAwait(false))
                    {
                        failures.Add(
                            new InvariantViolation(invariant.Name, invariant.FailureMessage)
                        );
                    }
                }
                catch (Exception exception)
                    when (exception is not OperationCanceledException
                        || !cancellationToken.IsCancellationRequested
                    )
                {
                    failures.Add(
                        new InvariantViolation(invariant.Name, exception.Message, exception)
                    );
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new VerificationResult(differences, failures);
    }
}

using FaultSweep.Effects;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using FaultSweep.Verification;

namespace FaultSweep.Execution;

internal sealed record SweepPlan<TMessage>(
    Func<IServiceProvider, TMessage, CancellationToken, ValueTask> Invoke,
    Func<IServiceProvider, CancellationToken, ValueTask> Reset,
    IReadOnlyList<SnapshotRegistration> Snapshots,
    IReadOnlyList<InvariantRegistration<TMessage>> Invariants,
    IReadOnlyDictionary<string, Func<TMessage, StateSnapshot>> ExpectedStates,
    FaultSweepOptions Options,
    ReplayScenario? Replay = null,
    MessageMinimizer<TMessage>? Minimizer = null,
    DuplicateDeliveryMode? DuplicateReplay = null,
    IReadOnlyCollection<IEffectInstrumentation>? Instrumentation = null
)
{
    public SweepPlan<TMessage> ValidateAndCopy()
    {
        FaultSweepOptions options = Options.ValidateAndCopy();
        if (Snapshots.Count == 0 && (options.CompareWithBaseline || Invariants.Count == 0))
        {
            throw new InvalidOperationException(
                "Track at least one state snapshot before running a sweep."
            );
        }

        if (!options.CompareWithBaseline && ExpectedStates.Count == 0 && Invariants.Count == 0)
        {
            throw new InvalidOperationException(
                "Configure expected state or an invariant when baseline comparison is disabled."
            );
        }

        foreach (string name in ExpectedStates.Keys)
        {
            if (
                !Snapshots.Any(snapshot =>
                    string.Equals(snapshot.Name, name, StringComparison.Ordinal)
                )
            )
            {
                throw new InvalidOperationException(
                    $"Expected state '{name}' requires a tracked snapshot with the same name."
                );
            }
        }

        return this with
        {
            Snapshots = Array.AsReadOnly(
                Snapshots.OrderBy(snapshot => snapshot.Name, StringComparer.Ordinal).ToArray()
            ),
            Invariants = Array.AsReadOnly(Invariants.ToArray()),
            ExpectedStates = new System.Collections.ObjectModel.ReadOnlyDictionary<
                string,
                Func<TMessage, StateSnapshot>
            >(
                new Dictionary<string, Func<TMessage, StateSnapshot>>(
                    ExpectedStates,
                    StringComparer.Ordinal
                )
            ),
            Options = options,
            Instrumentation = Array.AsReadOnly(Instrumentation?.ToArray() ?? []),
        };
    }
}

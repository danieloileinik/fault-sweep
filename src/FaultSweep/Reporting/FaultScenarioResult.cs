using FaultSweep.Effects;
using FaultSweep.Snapshots;

namespace FaultSweep.Reporting;

public sealed class FaultScenarioResult
{
    internal FaultScenarioResult(FaultScenario scenario, ViolationKind violations,
        IEnumerable<DeliveryAttempt> attempts, IEnumerable<EffectTraceEntry> trace,
        IReadOnlyDictionary<string, StateSnapshot> state, IEnumerable<SnapshotDifference> differences)
    {
        Scenario = scenario;
        Violations = violations;
        Attempts = Array.AsReadOnly(attempts.ToArray());
        Trace = Array.AsReadOnly(trace.ToArray());
        State = state;
        Differences = Array.AsReadOnly(differences.ToArray());
    }

    public FaultScenario Scenario { get; }

    public ViolationKind Violations { get; }

    public bool HasViolation => Violations != ViolationKind.None;

    public IReadOnlyList<DeliveryAttempt> Attempts { get; }

    public IReadOnlyList<EffectTraceEntry> Trace { get; }

    public IReadOnlyDictionary<string, StateSnapshot> State { get; }

    public IReadOnlyList<SnapshotDifference> Differences { get; }
}

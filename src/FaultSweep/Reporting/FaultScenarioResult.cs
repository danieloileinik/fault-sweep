using FaultSweep.Effects;
using FaultSweep.Snapshots;
using FaultSweep.Verification;

namespace FaultSweep.Reporting;

public sealed class FaultScenarioResult
{
    internal FaultScenarioResult(
        FaultScenario scenario,
        ViolationKind violations,
        IEnumerable<DeliveryAttempt> attempts,
        IEnumerable<EffectTraceEntry> trace,
        IReadOnlyDictionary<string, StateSnapshot> state,
        VerificationResult verification
    )
    {
        Scenario = scenario;
        Violations = violations;
        Attempts = Array.AsReadOnly(attempts.ToArray());
        Trace = Array.AsReadOnly(trace.ToArray());
        State = state;
        Verification = verification;
    }

    public FaultScenario Scenario { get; }
    public ViolationKind Violations { get; }
    public string? RegressionTest { get; internal set; }
    public bool HasViolation => Violations != ViolationKind.None;
    public IReadOnlyList<DeliveryAttempt> Attempts { get; }
    public IReadOnlyList<EffectTraceEntry> Trace { get; }
    public IReadOnlyDictionary<string, StateSnapshot> State { get; }
    public VerificationResult Verification { get; }
    public IReadOnlyList<SnapshotDifference> Differences => Verification.Differences;
    public IReadOnlyList<InvariantViolation> InvariantViolations =>
        Verification.InvariantViolations;
}

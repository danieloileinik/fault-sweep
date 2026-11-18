using FaultSweep.Effects;
using FaultSweep.Snapshots;
using FaultSweep.Verification;

namespace FaultSweep.Reporting;

public sealed class DuplicateScenarioResult
{
    internal DuplicateScenarioResult(
        DuplicateDeliveryMode mode,
        IEnumerable<DeliveryAttempt> attempts,
        IEnumerable<EffectTraceEntry> trace,
        IReadOnlyDictionary<string, StateSnapshot> state,
        VerificationResult verification,
        bool recovered
    )
    {
        Mode = mode;
        Attempts = Array.AsReadOnly(attempts.ToArray());
        Trace = Array.AsReadOnly(trace.ToArray());
        State = state;
        Verification = verification;
        Violations = verification.GetScenarioViolations(recovered);
    }

    public DuplicateDeliveryMode Mode { get; }
    public IReadOnlyList<DeliveryAttempt> Attempts { get; }
    public IReadOnlyList<EffectTraceEntry> Trace { get; }
    public IReadOnlyDictionary<string, StateSnapshot> State { get; }
    public VerificationResult Verification { get; }
    public ViolationKind Violations { get; }
    public bool HasViolation => Violations != ViolationKind.None;
    public string? RegressionTest { get; internal set; }
}

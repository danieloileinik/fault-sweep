using FaultSweep.Reporting;
using FaultSweep.Snapshots;

namespace FaultSweep.Verification;

public sealed class VerificationResult
{
    internal VerificationResult(
        IEnumerable<SnapshotDifference> differences,
        IEnumerable<InvariantViolation> invariantViolations
    )
    {
        Differences = Array.AsReadOnly(differences.ToArray());
        InvariantViolations = Array.AsReadOnly(invariantViolations.ToArray());
    }

    public IReadOnlyList<SnapshotDifference> Differences { get; }
    public IReadOnlyList<InvariantViolation> InvariantViolations { get; }
    public bool HasViolations => Differences.Count > 0 || InvariantViolations.Count > 0;

    internal ViolationKind GetScenarioViolations(bool recovered)
    {
        ViolationKind violations = recovered
            ? ViolationKind.None
            : ViolationKind.RedeliveryLimitExceeded;
        if (Differences.Count > 0)
        {
            violations |= ViolationKind.StateMismatch;
        }

        if (InvariantViolations.Count > 0)
        {
            violations |= ViolationKind.InvariantFailed;
        }

        return violations;
    }
}

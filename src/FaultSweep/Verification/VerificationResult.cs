using FaultSweep.Reporting;
using FaultSweep.Snapshots;

namespace FaultSweep.Verification;

public sealed class VerificationResult
{
    internal VerificationResult(
        IEnumerable<DifferenceEvidence> differences,
        IEnumerable<InvariantViolation> invariantViolations
    )
    {
        Evidence = Array.AsReadOnly(differences.ToArray());
        Differences = Array.AsReadOnly(Evidence.Select(item => item.Difference).ToArray());
        InvariantViolations = Array.AsReadOnly(invariantViolations.ToArray());
    }

    internal IReadOnlyList<DifferenceEvidence> Evidence { get; }
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

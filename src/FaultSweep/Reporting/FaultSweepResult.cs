using FaultSweep.Effects;
using FaultSweep.Snapshots;
using FaultSweep.Verification;

namespace FaultSweep.Reporting;

public sealed class FaultSweepResult
{
    internal FaultSweepResult(
        IReadOnlyDictionary<string, StateSnapshot> baselineState,
        IEnumerable<EffectTraceEntry> baselineTrace,
        VerificationResult baselineVerification,
        IEnumerable<FaultScenarioResult> scenarios,
        IEnumerable<DuplicateScenarioResult> duplicateScenarios
    )
    {
        BaselineState = baselineState;
        BaselineTrace = Array.AsReadOnly(baselineTrace.ToArray());
        BaselineVerification = baselineVerification;
        Scenarios = Array.AsReadOnly(scenarios.ToArray());
        Violations = Array.AsReadOnly(Scenarios.Where(scenario => scenario.HasViolation).ToArray());
        DuplicateScenarios = Array.AsReadOnly(duplicateScenarios.ToArray());
        DuplicateViolations = Array.AsReadOnly(
            DuplicateScenarios.Where(scenario => scenario.HasViolation).ToArray()
        );
    }

    internal string? InputJson { get; set; }
    public IReadOnlyDictionary<string, StateSnapshot> BaselineState { get; }
    public IReadOnlyList<EffectTraceEntry> BaselineTrace { get; }
    public VerificationResult BaselineVerification { get; }
    public RegressionTestCase? BaselineRegressionCase { get; internal set; }
    public string? BaselineRegressionTest { get; internal set; }
    public IReadOnlyList<FaultScenarioResult> Scenarios { get; }
    public IReadOnlyList<FaultScenarioResult> Violations { get; }
    public MinimizedScenario? MinimalScenario { get; internal set; }
    public IReadOnlyList<DuplicateScenarioResult> DuplicateScenarios { get; }
    public IReadOnlyList<DuplicateScenarioResult> DuplicateViolations { get; }
    public MinimizedDuplicateScenario? MinimalDuplicateScenario { get; internal set; }
    public bool HasViolations =>
        BaselineVerification.HasViolations || Violations.Count > 0 || DuplicateViolations.Count > 0;

    public void ShouldHaveNoViolations()
    {
        if (HasViolations)
        {
            throw new FaultSweepAssertionException(this);
        }
    }

    public override string ToString() => TextReport.Format(this);

    /// <summary>Includes SQL targets, individual delivery traces, message payloads and generated regression tests.</summary>
    public string ToDetailedString() => TextReport.Format(this, detailed: true);
}

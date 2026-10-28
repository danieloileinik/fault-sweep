using FaultSweep.Effects;
using FaultSweep.Snapshots;

namespace FaultSweep.Reporting;

public sealed class FaultSweepResult
{
    internal FaultSweepResult(IReadOnlyDictionary<string, StateSnapshot> baselineState,
        IEnumerable<EffectTraceEntry> baselineTrace, IEnumerable<FaultScenarioResult> scenarios)
    {
        BaselineState = baselineState;
        BaselineTrace = Array.AsReadOnly(baselineTrace.ToArray());
        Scenarios = Array.AsReadOnly(scenarios.ToArray());
        Violations = Array.AsReadOnly(Scenarios.Where(scenario => scenario.HasViolation).ToArray());
    }

    public IReadOnlyDictionary<string, StateSnapshot> BaselineState { get; }

    public IReadOnlyList<EffectTraceEntry> BaselineTrace { get; }

    public IReadOnlyList<FaultScenarioResult> Scenarios { get; }

    public IReadOnlyList<FaultScenarioResult> Violations { get; }

    public bool HasViolations => Violations.Count > 0;

    public void ShouldHaveNoViolations()
    {
        if (HasViolations)
        {
            throw new FaultSweepAssertionException(ToString());
        }
    }

    public override string ToString() => TextReport.Format(this);
}

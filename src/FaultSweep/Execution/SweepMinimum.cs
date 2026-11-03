using FaultSweep.Reporting;

namespace FaultSweep.Execution;

internal sealed record SweepMinimum<TMessage>(
    TMessage Message,
    FaultScenarioResult? Scenario,
    DuplicateScenarioResult? Duplicate,
    int EvaluatedCandidates,
    bool BudgetExhausted
);

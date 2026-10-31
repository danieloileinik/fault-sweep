namespace FaultSweep.Reporting;

public sealed record MinimizedDuplicateScenario(
    DuplicateScenarioResult Result,
    string? MessageJson,
    string MessageTypeName,
    int EvaluatedCandidates,
    bool BudgetExhausted
);

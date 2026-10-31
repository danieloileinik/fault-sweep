namespace FaultSweep.Reporting;
/// <summary>The smallest violating message and single-fault scenario found within the configured search budget.</summary>
public sealed record MinimizedScenario(FaultScenarioResult Result, string? MessageJson, string MessageTypeName,
    int EvaluatedCandidates, bool BudgetExhausted);

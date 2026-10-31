namespace FaultSweep.Reporting;

/// <summary>A null scenario reproduces a clean-run verification failure.</summary>
public sealed record RegressionTestCase(string? MessageJson, string MessageTypeName, ReplayScenario? Scenario);

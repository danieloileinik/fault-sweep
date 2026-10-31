using System.Text;
using FaultSweep.Effects;
using FaultSweep.Snapshots;
using FaultSweep.Verification;

namespace FaultSweep.Reporting;

internal static class TextReport
{
    public static string Format(FaultSweepResult result)
    {
        var report = new StringBuilder();
        int violations = result.Violations.Count + (result.BaselineVerification.HasViolations ? 1 : 0);
        report.AppendLine($"FaultSweep: {result.BaselineTrace.Count} effects, {result.Scenarios.Count} scenarios, {violations} violations.");
        if (result.BaselineVerification.HasViolations)
        {
            report.AppendLine();
            report.AppendLine("Clean baseline verification failed:");
            AppendVerification(report, result.BaselineVerification);
            if (result.BaselineRegressionTest is { } baselineTest)
            {
                report.AppendLine("  xUnit regression test:");
                report.AppendLine(baselineTest);
            }
        }
        if (result.MinimalScenario is { } minimal)
        {
            report.AppendLine($"Minimal scenario: effect #{minimal.Result.Scenario.Point.Index}, {minimal.Result.Scenario.Kind}, {minimal.Result.Attempts.Count} deliveries.");
            report.AppendLine($"  Message: {minimal.MessageJson ?? "<custom serialization required>"}");
            report.AppendLine($"  Candidates: {minimal.EvaluatedCandidates}; budget exhausted: {minimal.BudgetExhausted}");
        }
        foreach (FaultScenarioResult violation in result.Violations)
        {
            FaultScenario scenario = violation.Scenario;
            report.AppendLine();
            report.AppendLine($"Effect #{scenario.Point.Index} {scenario.Point.Effect}: {scenario.Kind}");
            report.AppendLine($"  Violations: {violation.Violations}");
            foreach (DeliveryAttempt attempt in violation.Attempts)
            {
                string status = attempt.Succeeded ? "success" : $"{attempt.Error?.GetType().Name}: {attempt.Error?.Message}";
                report.AppendLine($"  Delivery {attempt.Number}: {status}");
                foreach (EffectTraceEntry entry in violation.Trace.Where(entry => entry.Attempt == attempt.Number))
                {
                    string marker = entry.Outcome is EffectOutcome.CrashBefore or EffectOutcome.CrashAfter or EffectOutcome.AppliedButFailed
                        ? "!" : "-";
                    report.AppendLine($"    {marker} #{entry.Point.Index} {entry.Point.Effect}: {entry.Outcome}");
                }
            }

            AppendVerification(report, violation.Verification);
            if (violation.RegressionTest is { } source)
            {
                report.AppendLine("  xUnit regression test:");
                report.AppendLine(source);
            }
        }

        if (result.MinimalScenario?.Result.RegressionTest is { } regression)
        {
            report.AppendLine();
            report.AppendLine("Generated xUnit regression test:");
            report.AppendLine(regression);
        }
        return report.ToString();
    }

    private static void AppendVerification(StringBuilder report, VerificationResult verification)
    {
        foreach (SnapshotDifference difference in verification.Differences)
        {
            report.AppendLine($"  {difference.Snapshot} {difference.Path}: {difference.Kind}");
            report.AppendLine($"    expected: {difference.Expected ?? "<missing>"}");
            report.AppendLine($"    actual:   {difference.Actual ?? "<missing>"}");
        }

        foreach (InvariantViolation violation in verification.InvariantViolations)
        {
            string errorType = violation.Error is null ? string.Empty : $" ({violation.Error.GetType().Name})";
            report.AppendLine($"  Invariant '{violation.Name}' failed{errorType}: {violation.Message}");
        }
    }
}

using System.Text;
using FaultSweep.Effects;
using FaultSweep.Snapshots;

namespace FaultSweep.Reporting;

internal static class TextReport
{
    public static string Format(FaultSweepResult result)
    {
        var report = new StringBuilder();
        report.AppendLine($"FaultSweep: {result.BaselineTrace.Count} effects, {result.Scenarios.Count} scenarios, {result.Violations.Count} violations.");
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

            foreach (SnapshotDifference difference in violation.Differences)
            {
                report.AppendLine($"  {difference.Snapshot} {difference.Path}: {difference.Kind}");
                report.AppendLine($"    expected: {difference.Expected ?? "<missing>"}");
                report.AppendLine($"    actual:   {difference.Actual ?? "<missing>"}");
            }
        }

        return report.ToString();
    }
}

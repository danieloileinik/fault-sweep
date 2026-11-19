using System.Text;
using FaultSweep.Effects;
using FaultSweep.Snapshots;
using FaultSweep.Verification;

namespace FaultSweep.Reporting;

internal static class TextReport
{
    public static string Format(FaultSweepResult result, bool detailed = false)
    {
        var report = new StringBuilder();
        int violations =
            result.Violations.Count
            + result.DuplicateViolations.Count
            + (result.BaselineVerification.HasViolations ? 1 : 0);
        int scenarios = result.Scenarios.Count + result.DuplicateScenarios.Count;
        report.AppendLine(
            $"FaultSweep: {result.BaselineTrace.Count} effects, {scenarios} scenarios, {violations} violations."
        );
        if (!detailed)
        {
            CompactReport.Append(report, result);
            return report.ToString();
        }

        report.AppendLine("Clean baseline trace:");
        AppendTrace(report, result.BaselineTrace);

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
            report.AppendLine(
                $"Minimal scenario: effect #{minimal.Result.Scenario.Point.Index}, {minimal.Result.Scenario.Kind}, {minimal.Result.Attempts.Count} deliveries."
            );
            report.AppendLine(
                $"  Message: {minimal.MessageJson ?? "<custom serialization required>"}"
            );
            report.AppendLine(
                $"  Candidates: {minimal.EvaluatedCandidates}; budget exhausted: {minimal.BudgetExhausted}"
            );
        }
        foreach (FaultScenarioResult violation in result.Violations)
        {
            FaultScenario scenario = violation.Scenario;
            report.AppendLine();
            report.AppendLine(
                $"Effect #{scenario.Point.Index} {scenario.Point.Effect}: {scenario.Kind}"
            );
            report.AppendLine($"  Violations: {violation.Violations}");
            AppendAttempts(report, violation.Attempts, violation.Trace);

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
        foreach (DuplicateScenarioResult duplicate in result.DuplicateViolations)
        {
            report.AppendLine();
            report.AppendLine(
                $"Duplicate deliveries: {duplicate.Mode}; violations: {duplicate.Violations}"
            );
            AppendAttempts(report, duplicate.Attempts, duplicate.Trace);

            AppendVerification(report, duplicate.Verification);
            if (duplicate.RegressionTest is { } source)
            {
                report.AppendLine(source);
            }
        }

        if (result.MinimalDuplicateScenario is { } minimumDuplicate)
        {
            report.AppendLine($"Minimal duplicate scenario: {minimumDuplicate.Result.Mode}");
            report.AppendLine($"  Message: {minimumDuplicate.MessageJson}");
            report.AppendLine(
                $"  Candidates: {minimumDuplicate.EvaluatedCandidates}; budget exhausted: {minimumDuplicate.BudgetExhausted}"
            );
            if (minimumDuplicate.Result.RegressionTest is { } source)
            {
                report.AppendLine(source);
            }
        }

        return report.ToString();
    }

    private static void AppendAttempts(
        StringBuilder report,
        IReadOnlyList<DeliveryAttempt> attempts,
        IReadOnlyList<EffectTraceEntry> trace
    )
    {
        report.AppendLine(
            $"  Attempts: {attempts.Count}; succeeded: {attempts.Count(attempt => attempt.Succeeded)}; failed: {attempts.Count(attempt => !attempt.Succeeded)}"
        );
        foreach (DeliveryAttempt attempt in attempts)
        {
            string status = attempt.Succeeded
                ? "success"
                : $"{attempt.Error?.GetType().Name}: {attempt.Error?.Message}";
            report.AppendLine($"  Delivery {attempt.Number}: {status}");
            AppendTrace(report, trace.Where(entry => entry.Attempt == attempt.Number));
        }
    }

    private static void AppendTrace(StringBuilder report, IEnumerable<EffectTraceEntry> trace)
    {
        foreach (var entry in trace)
        {
            string marker = entry.Outcome
                is EffectOutcome.CrashBefore
                    or EffectOutcome.CrashAfter
                    or EffectOutcome.AppliedButFailed
                ? "!"
                : "-";
            report.AppendLine(
                $"    {marker} #{entry.Point.Index} {entry.Point.Effect}: {entry.Outcome}"
            );
        }
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
            string errorType = violation.Error is null
                ? string.Empty
                : $" ({violation.Error.GetType().Name})";
            report.AppendLine(
                $"  Invariant '{violation.Name}' failed{errorType}: {violation.Message}"
            );
        }
    }
}

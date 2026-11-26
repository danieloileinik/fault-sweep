using System.Text;
using FaultSweep.Effects;

namespace FaultSweep.Reporting;

internal static class TextReport
{
    public static string Format(FaultSweepResult result, bool detailed = false)
    {
        var report = new StringBuilder();
        var failures = result
            .Violations.Select(ReportScenario.From)
            .Concat(result.DuplicateViolations.Select(ReportScenario.From))
            .ToArray();
        int total = result.Scenarios.Count + result.DuplicateScenarios.Count;
        int operations = result.BaselineTrace.Count;
        report.AppendLine(
            $"FaultSweep: {failures.Length} of {total} scenario{(total == 1 ? "" : "s")} failed ({operations} operation{(operations == 1 ? "" : "s")} in clean run)."
        );

        if (result.BaselineVerification.HasViolations)
        {
            report.AppendLine("Clean baseline verification failed");
            ReportEvidence.Append(report, result.BaselineVerification, detailed);
        }

        var groups = failures.GroupBy(row => row, ReportScenario.ByEvidence).ToArray();
        AppendOverview(report, groups, detailed);
        if (detailed)
        {
            AppendScenarioTraces(report, result, groups);
        }

        if (result.MinimalScenario is { } fault)
        {
            AppendReplay(
                report,
                detailed,
                ReportScenario.From(fault.Result),
                !result.Violations.Contains(fault.Result),
                fault.MessageJson,
                fault.EvaluatedCandidates,
                fault.BudgetExhausted
            );
        }
        if (result.MinimalDuplicateScenario is { } duplicate)
        {
            AppendReplay(
                report,
                detailed,
                ReportScenario.From(duplicate.Result),
                !result.DuplicateViolations.Contains(duplicate.Result),
                duplicate.MessageJson,
                duplicate.EvaluatedCandidates,
                duplicate.BudgetExhausted
            );
        }
        if (detailed)
        {
            var sources = failures
                .Select(row => row.RegressionTest)
                .Append(result.BaselineRegressionTest)
                .Append(result.MinimalScenario?.Result.RegressionTest)
                .Append(result.MinimalDuplicateScenario?.Result.RegressionTest);
            foreach (string source in sources.OfType<string>().Distinct(StringComparer.Ordinal))
            {
                report.AppendLine("Generated xUnit regression test:");
                report.AppendLine(source);
            }
        }
        else if (result.HasViolations)
        {
            report.AppendLine("Full report: ToDetailedString()");
        }
        return report.ToString();
    }

    private static void AppendOverview(
        StringBuilder report,
        IGrouping<ReportScenario, ReportScenario>[] groups,
        bool detailed
    )
    {
        int shown = detailed ? groups.Length : Math.Min(groups.Length, 3);
        for (int index = 0; index < shown; index++)
        {
            var group = groups[index];
            report.AppendLine(
                $"Failure {index + 1} ({group.Count()} scenario{(group.Count() == 1 ? "" : "s")})"
            );
            ReportEvidence.Append(report, group.Key.Verification, detailed);
            if (group.Key.Violations.HasFlag(ViolationKind.FaultNotReached))
            {
                report.AppendLine(
                    "  The first attempt finished before the requested fault could be injected."
                );
            }
            if (group.Key.Violations.HasFlag(ViolationKind.RedeliveryLimitExceeded))
            {
                report.AppendLine("  Handler still failing after the last retry.");
            }
            if (!detailed)
            {
                AppendAttempts(report, group.Key, false, []);
                var others = group.Skip(1).Take(2).Select(row => row.Label).ToArray();
                if (others.Length > 0)
                {
                    report.AppendLine($"  Also seen with: {string.Join(", ", others)}");
                }
                if (group.Count() > 3)
                {
                    report.AppendLine(
                        $"  +{group.Count() - 3} more scenario{(group.Count() == 4 ? "" : "s")}"
                    );
                }
            }
        }
        if (groups.Length > shown)
        {
            report.AppendLine($"+{groups.Length - shown} more failure groups");
        }
    }

    private static void AppendScenarioTraces(
        StringBuilder report,
        FaultSweepResult result,
        IGrouping<ReportScenario, ReportScenario>[] groups
    )
    {
        if (result.InputJson is { } input)
        {
            report.AppendLine($"Input message: {input}");
        }
        report.AppendLine("Clean run:");
        foreach (var entry in result.BaselineTrace)
        {
            AppendEffect(report, entry);
        }
        for (int index = 0; index < groups.Length; index++)
        {
            foreach (var row in groups[index])
            {
                report.AppendLine($"Failure {index + 1}: {row.Label}");
                AppendAttempts(report, row, true, result.BaselineTrace);
            }
        }
    }

    private static void AppendReplay(
        StringBuilder report,
        bool detailed,
        ReportScenario row,
        bool reduced,
        string? message,
        int candidates,
        bool exhausted
    )
    {
        report.AppendLine($"Replay: {row.Replay}");
        if (reduced)
        {
            string input = message ?? "<custom serialization required>";
            report.AppendLine(
                $"  Reduced input: {(detailed ? input : ReportEvidence.Preview(input))}"
            );
            ReportEvidence.Append(report, row.Verification, detailed);
            if (detailed)
            {
                AppendAttempts(report, row, true, []);
            }
        }
        if (detailed && (candidates > 0 || exhausted))
        {
            report.AppendLine(
                $"  Shrink candidates evaluated: {candidates}{(exhausted ? " (limit reached)" : "")}"
            );
        }
    }

    private static void AppendAttempts(
        StringBuilder report,
        ReportScenario row,
        bool detailed,
        IReadOnlyList<EffectTraceEntry> baseline
    )
    {
        if (!detailed)
        {
            report.AppendLine($"  {row.Label}");
        }
        var traceByAttempt = detailed ? row.Trace.ToLookup(entry => entry.Attempt) : null;
        foreach (var delivery in row.Attempts.GroupBy(attempt => attempt.Identity.Delivery))
        {
            DeliveryAttempt[] attempts = delivery.ToArray();
            string? finalError = detailed ? attempts[^1].Error?.ToString() : null;
            IEnumerable<DeliveryAttempt> shown =
                detailed || attempts.Length <= 2 ? attempts : [attempts[0], attempts[^1]];
            foreach (DeliveryAttempt attempt in shown)
            {
                int number = attempt.Identity.Attempt;
                string label = row.Duplicate is null
                    ? "Attempt"
                    : $"Delivery {delivery.Key}, attempt";
                if (!detailed && number > 2)
                {
                    report.AppendLine(
                        $"    {number - 2} intervening {(number == 3 ? "retry" : "retries")} failed"
                    );
                }
                string status = Status(attempt, row, detailed);
                if (
                    detailed
                    && attempt.Error
                        is not (null or SimulatedCrashException or AppliedButFailedException)
                    && number < attempts.Length
                    && status == finalError
                )
                {
                    status = $"{attempt.Error.GetType().Name} (repeated on a later attempt)";
                }
                report.AppendLine($"    {label} {number}: {status}");
                if (traceByAttempt is not null)
                {
                    AppendTrace(report, traceByAttempt[attempt.Number].ToArray(), baseline);
                }
            }
        }
    }

    private static string Status(DeliveryAttempt attempt, ReportScenario row, bool detailed)
    {
        if (attempt.Error is null)
        {
            return "handler returned successfully";
        }
        if (attempt.Error is SimulatedCrashException crash)
        {
            string timing = crash.Kind == FaultKind.CrashAfter ? "after" : "before";
            return $"simulated crash {timing} {ReportScenario.Operation(crash.Point)} (#{crash.Point.Index})";
        }
        if (attempt.Error is AppliedButFailedException applied)
        {
            return $"operation #{applied.Point.Index} completed, then FaultSweep simulated a lost acknowledgement";
        }
        if (detailed)
        {
            return attempt.Error.ToString();
        }
        Exception cause = attempt.Error.GetBaseException();
        bool containsTarget = row.Trace.Any(entry =>
            entry.Point.Effect.Target is { Length: > 0 } target
            && cause.Message.Contains(target, StringComparison.Ordinal)
        );
        return containsTarget
            ? $"{cause.GetType().Name} (see full report)"
            : $"{cause.GetType().Name}: {ReportEvidence.Preview(cause.Message)}";
    }

    private static void AppendTrace(
        StringBuilder report,
        EffectTraceEntry[] trace,
        IReadOnlyList<EffectTraceEntry> baseline
    )
    {
        int matched = 0;
        while (
            matched < trace.Length
            && matched < baseline.Count
            && trace[matched].Point == baseline[matched].Point
            && trace[matched].Outcome == baseline[matched].Outcome
        )
        {
            matched++;
        }
        if (matched > 0)
        {
            report.AppendLine(
                matched == 1
                    ? "      Same as clean run step #1"
                    : $"      Same as clean run steps #1–#{matched}"
            );
        }
        foreach (var entry in trace.Skip(matched))
        {
            AppendEffect(report, entry);
        }
    }

    private static void AppendEffect(StringBuilder report, EffectTraceEntry entry) =>
        report.AppendLine($"      #{entry.Point.Index} {entry.Point.Effect}: {entry.Outcome}");
}

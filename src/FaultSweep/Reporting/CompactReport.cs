using System.Text;
using FaultSweep.Snapshots;
using FaultSweep.Verification;

namespace FaultSweep.Reporting;

internal static class CompactReport
{
    private const int PreviewCount = 3;
    private const int PreviewLength = 100;

    public static void Append(StringBuilder report, FaultSweepResult result)
    {
        if (!result.HasViolations)
        {
            return;
        }

        if (result.BaselineVerification.HasViolations)
        {
            report.AppendLine("Clean baseline verification failed");
            report.Append(VerificationSummary(result.BaselineVerification));
        }

        var faults = result.Violations.Select(fault =>
            (
                Label: $"{FaultLabel(fault)}{(fault == result.MinimalScenario?.Result ? " [minimal]" : "")}",
                EffectIndex: (int?)fault.Scenario.Point.Index,
                fault.Violations,
                fault.Attempts,
                fault.Verification
            )
        );
        var duplicates = result.DuplicateViolations.Select(duplicate =>
            (
                Label: $"Duplicate {duplicate.Mode}{(duplicate == result.MinimalDuplicateScenario?.Result ? " [minimal]" : "")}",
                EffectIndex: (int?)null,
                duplicate.Violations,
                duplicate.Attempts,
                duplicate.Verification
            )
        );

        foreach (
            var group in faults
                .Concat(duplicates)
                .GroupBy(row => (row.Violations, Evidence: VerificationSummary(row.Verification)))
        )
        {
            report.AppendLine(group.Key.Violations.ToString());
            report.Append(group.Key.Evidence);
            foreach (
                var attempts in group.GroupBy(row =>
                    (row.EffectIndex, Summary: AttemptSummary(row.Attempts))
                )
            )
            {
                report.AppendLine(
                    $"  {string.Join(", ", attempts.Select(row => row.Label))} — {attempts.Key.Summary}"
                );
            }
        }

        // A shrunken message can fail at a different effect from the original.
        if (result.MinimalScenario is { } minimal && !result.Violations.Contains(minimal.Result))
        {
            report.AppendLine(
                $"Minimal scenario: {FaultLabel(minimal.Result)} ({minimal.Result.Attempts.Count} attempts)"
            );
        }
        if (
            result.MinimalDuplicateScenario is { } duplicateMinimum
            && !result.DuplicateViolations.Contains(duplicateMinimum.Result)
        )
        {
            report.AppendLine($"Minimal duplicate scenario: {duplicateMinimum.Result.Mode}");
        }

        report.AppendLine("Full report: ToDetailedString()");
    }

    private static string FaultLabel(FaultScenarioResult fault)
    {
        var point = fault.Scenario.Point;
        return $"#{point.Index} {point.Effect.Source}:{point.Effect.Operation} {fault.Scenario.Kind}";
    }

    private static string AttemptSummary(IReadOnlyList<DeliveryAttempt> attempts)
    {
        string counts =
            $"{attempts.Count(attempt => attempt.Succeeded)}/{attempts.Count} attempts succeeded";
        var errors = attempts
            .Where(attempt => attempt.Error is not null)
            .GroupBy(attempt => attempt.Error!.GetType())
            .Select(group =>
                group.Count() == 1 ? group.Key.Name : $"{group.Key.Name} ×{group.Count()}"
            );
        string errorSummary = string.Join(", ", errors);
        return errorSummary.Length == 0 ? counts : $"{counts} ({errorSummary})";
    }

    private static string VerificationSummary(VerificationResult verification)
    {
        var summary = new StringBuilder();
        foreach (SnapshotDifference diff in verification.Differences.Take(PreviewCount))
        {
            summary.AppendLine(
                $"  {diff.Snapshot} {diff.Path}: {diff.Kind}, expected {Preview(diff.Expected)}, actual {Preview(diff.Actual)}"
            );
        }
        if (verification.Differences.Count > PreviewCount)
        {
            summary.AppendLine(
                $"  +{verification.Differences.Count - PreviewCount} more differences"
            );
        }

        foreach (var invariant in verification.InvariantViolations.Take(PreviewCount))
        {
            string type = invariant.Error is null ? "" : $" ({invariant.Error.GetType().Name})";
            summary.AppendLine(
                $"  Invariant '{invariant.Name}' failed{type}: {Preview(invariant.Message)}"
            );
        }
        if (verification.InvariantViolations.Count > PreviewCount)
        {
            summary.AppendLine(
                $"  +{verification.InvariantViolations.Count - PreviewCount} more invariants"
            );
        }
        return summary.ToString();
    }

    private static string Preview(string? value)
    {
        if (value is null)
        {
            return "<missing>";
        }
        string preview = value.Length <= PreviewLength ? value : value[..PreviewLength] + "…";
        return preview.ReplaceLineEndings(" ");
    }
}

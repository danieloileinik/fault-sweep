using System.Text;
using FaultSweep.Snapshots;
using FaultSweep.Verification;

namespace FaultSweep.Reporting;

internal static class ReportEvidence
{
    public static string Preview(string text) =>
        (text.Length <= 100 ? text : text[..100] + "…").ReplaceLineEndings(" ");

    public static void Append(StringBuilder report, VerificationResult verification, bool detailed)
    {
        int limit = detailed ? int.MaxValue : 3;
        foreach (var evidence in verification.Evidence.Take(limit))
        {
            var diff = evidence.Difference;
            string source = evidence.FromCleanRun ? "clean run" : "configured expectation";
            string location = $"{diff.Snapshot} {diff.Path}";
            string Value(string? value) =>
                detailed ? value ?? "<missing>" : Preview(value ?? "<missing>");

            if (evidence.ItemDescription is { } item)
            {
                string kind =
                    diff.Kind == DifferenceKind.Missing ? "Missing"
                    : evidence.ReferenceCount > 0 ? "Duplicate"
                    : "Unexpected";
                report.AppendLine($"  {kind} {(detailed ? item : Preview(item))}");
                if (evidence.ReferenceCount is { } count)
                {
                    report.AppendLine(
                        $"    Matching items: {count} in {source}, {evidence.ActualCount} observed"
                    );
                }
                if (detailed)
                {
                    report.AppendLine(
                        $"    {location} ({(diff.Kind == DifferenceKind.Missing ? source : "observed")}): {Value(diff.Expected ?? diff.Actual)}"
                    );
                }
            }
            else
            {
                string description = diff.Kind switch
                {
                    DifferenceKind.Missing => $"missing item from {source}: {Value(diff.Expected)}",
                    DifferenceKind.Unexpected => $"unexpected item: {Value(diff.Actual)}",
                    _ => $"{Value(diff.Expected)} in {source}, {Value(diff.Actual)} observed",
                };
                report.AppendLine($"  {location}: {description}");
            }
        }
        if (verification.Differences.Count > limit)
        {
            report.AppendLine($"  +{verification.Differences.Count - limit} more differences");
        }

        foreach (var invariant in verification.InvariantViolations.Take(limit))
        {
            string type = invariant.Error is null ? "" : $" ({invariant.Error.GetType().Name})";
            report.AppendLine(
                $"  Invariant '{invariant.Name}' failed{type}: {(detailed ? invariant.Message : Preview(invariant.Message))}"
            );
            if (detailed && invariant.Error?.InnerException is { } cause)
            {
                report.AppendLine($"    Caused by: {cause}");
            }
        }
        if (verification.InvariantViolations.Count > limit)
        {
            report.AppendLine(
                $"  +{verification.InvariantViolations.Count - limit} more invariants"
            );
        }
    }
}

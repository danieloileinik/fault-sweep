using FaultSweep.Effects;
using FaultSweep.Verification;

namespace FaultSweep.Reporting;

internal sealed record ReportScenario(
    FaultScenario? Fault,
    DuplicateDeliveryMode? Duplicate,
    ViolationKind Violations,
    VerificationResult Verification,
    IReadOnlyList<DeliveryAttempt> Attempts,
    IReadOnlyList<EffectTraceEntry> Trace,
    string? RegressionTest
)
{
    public static ReportScenario From(FaultScenarioResult result) =>
        new ReportScenario(
            result.Scenario,
            null,
            result.Violations,
            result.Verification,
            result.Attempts,
            result.Trace,
            result.RegressionTest
        );

    public static ReportScenario From(DuplicateScenarioResult result) =>
        new ReportScenario(
            null,
            result.Mode,
            result.Violations,
            result.Verification,
            result.Attempts,
            result.Trace,
            result.RegressionTest
        );

    public string Label =>
        Fault is { } fault
            ? $"{FaultAction(fault.Kind)} {Operation(fault.Point)} (#{fault.Point.Index})"
            : $"{Duplicate} duplicate delivery";

    internal static string Operation(EffectPoint point) =>
        point.Effect switch
        {
            { Source: "postgres", Operation: "Commit" } => "PostgreSQL COMMIT",
            { Source: "kafka", Operation: "Produce" } => "Kafka publish",
            _ => $"{point.Effect.Source}:{point.Effect.Operation}",
        };

    private static string FaultAction(FaultKind kind) =>
        kind switch
        {
            FaultKind.CrashBefore => "Crash before",
            FaultKind.CrashAfter => "Crash after",
            _ => "Lost acknowledgement after",
        };

    public string Replay =>
        Fault is { } fault
            ? $".ReplayScenario(new ReplayScenario({fault.Point.Index}, FaultKind.{fault.Kind}))"
            : $".ReplayDuplicates(DuplicateDeliveryMode.{Duplicate})";

    public static IEqualityComparer<ReportScenario> ByEvidence { get; } =
        EqualityComparer<ReportScenario>.Create(
            (a, b) =>
                a is not null
                && b is not null
                && a.Violations == b.Violations
                && a.Verification.Evidence.SequenceEqual(b.Verification.Evidence)
                && a.Verification.InvariantViolations.Select(InvariantKey)
                    .SequenceEqual(b.Verification.InvariantViolations.Select(InvariantKey))
                && a.TerminalErrors().SequenceEqual(b.TerminalErrors()),
            row =>
                HashCode.Combine(
                    row.Violations,
                    row.Verification.Evidence.FirstOrDefault(),
                    row.Verification.InvariantViolations.Count
                )
        );

    private static (string, string, string?) InvariantKey(InvariantViolation violation) =>
        (violation.Name, violation.Message, violation.Error?.ToString());

    private IEnumerable<string> TerminalErrors() =>
        Attempts
            .GroupBy(a => a.Identity.Delivery)
            .Select(group => group.Last().Error)
            .OfType<Exception>()
            .Select(error => error.ToString());
}

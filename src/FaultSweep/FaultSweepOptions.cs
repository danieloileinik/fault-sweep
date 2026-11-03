using FaultSweep.Reporting;

namespace FaultSweep;

public sealed record FaultSweepOptions
{
    // csharpier-ignore-start -- Keep consecutive properties together.
    public int MaxEffects { get; init; } = 20;
    /// <summary>The number of deliveries allowed after the initial faulted delivery.</summary>
    public int MaxRedeliveryAttempts { get; init; } = 3;
    public IReadOnlyCollection<FaultKind> FaultKinds { get; init; } =
    [FaultKind.CrashBefore, FaultKind.CrashAfter, FaultKind.AppliedButFailed];
    public bool AllowEmptyTrace { get; init; }
    /// <summary>Compares tracked snapshots with the clean run unless an explicit expected state replaces it.</summary>
    public bool CompareWithBaseline { get; init; } = true;
    public IReadOnlyCollection<DuplicateDeliveryMode> DuplicateDeliveries { get; init; } = [];
    /// <summary>Bounds examined shrink candidates, including invalid or cyclic candidates.</summary>
    public int MaxMinimizationRuns { get; init; } = 50;
    // csharpier-ignore-end

    internal FaultSweepOptions ValidateAndCopy()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEffects, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRedeliveryAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxMinimizationRuns);
        ArgumentNullException.ThrowIfNull(FaultKinds);
        ArgumentNullException.ThrowIfNull(DuplicateDeliveries);
        if (DuplicateDeliveries.Any(mode => !Enum.IsDefined(mode)))
        {
            throw new ArgumentException(
                "Unknown duplicate delivery mode.",
                nameof(DuplicateDeliveries)
            );
        }
        if (FaultKinds.Count == 0 || FaultKinds.Any(kind => !Enum.IsDefined(kind)))
        {
            throw new ArgumentException(
                "At least one valid fault kind is required.",
                nameof(FaultKinds)
            );
        }

        return this with
        {
            FaultKinds = Array.AsReadOnly(FaultKinds.Distinct().Order().ToArray()),
            DuplicateDeliveries = Array.AsReadOnly(
                DuplicateDeliveries.Distinct().Order().ToArray()
            ),
        };
    }
}

namespace FaultSweep;

public sealed record FaultSweepOptions
{
    public int MaxEffects { get; init; } = 20;

    /// <summary>The number of deliveries allowed after the initial faulted delivery.</summary>
    public int MaxRedeliveryAttempts { get; init; } = 3;

    public IReadOnlyCollection<FaultKind> FaultKinds { get; init; } =
        [FaultKind.CrashBefore, FaultKind.CrashAfter, FaultKind.AppliedButFailed];

    public bool AllowEmptyTrace { get; init; }

    internal FaultSweepOptions ValidateAndCopy()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEffects, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRedeliveryAttempts, 1);
        ArgumentNullException.ThrowIfNull(FaultKinds);
        if (FaultKinds.Count == 0 || FaultKinds.Any(kind => !Enum.IsDefined(kind)))
        {
            throw new ArgumentException("At least one valid fault kind is required.", nameof(FaultKinds));
        }

        return this with { FaultKinds = Array.AsReadOnly(FaultKinds.Distinct().Order().ToArray()) };
    }
}

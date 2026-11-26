namespace FaultSweep.Snapshots;

internal sealed record DifferenceEvidence(SnapshotDifference Difference, bool FromCleanRun)
{
    public string? ItemDescription { get; init; }
    public int? ReferenceCount { get; init; }
    public int? ActualCount { get; init; }
}

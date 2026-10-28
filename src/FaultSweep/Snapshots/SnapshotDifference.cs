namespace FaultSweep.Snapshots;

public sealed record SnapshotDifference(
    string Snapshot,
    string Path,
    DifferenceKind Kind,
    string? Expected,
    string? Actual);

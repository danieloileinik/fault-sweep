using System.Text.Json;

namespace FaultSweep.Snapshots;

internal readonly record struct JsonDifference(
    string Path,
    JsonElement Expected,
    JsonElement Actual
)
{
    public SnapshotDifference Format(string name)
    {
        bool missing = Actual.ValueKind == JsonValueKind.Undefined;
        bool unexpected = Expected.ValueKind == JsonValueKind.Undefined;
        return new SnapshotDifference(
            name,
            Path,
            missing ? DifferenceKind.Missing
                : unexpected ? DifferenceKind.Unexpected
                : DifferenceKind.ValueMismatch,
            unexpected ? null : StateSnapshot.Format(Expected),
            missing ? null : StateSnapshot.Format(Actual)
        );
    }
}

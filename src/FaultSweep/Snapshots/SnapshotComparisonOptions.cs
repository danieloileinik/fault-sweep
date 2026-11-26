using System.Text.Json;

namespace FaultSweep.Snapshots;

public sealed record SnapshotComparisonOptions
{
    // csharpier-ignore-start -- Keep consecutive properties together.
    /// <summary>Property names excluded at every nesting level, using ordinal comparison.</summary>
    public IReadOnlyCollection<string> IgnoredProperties { get; init; } = [];
    /// <summary>Controls equality of the root array, such as a produced event collection.</summary>
    public ArrayComparison ArrayComparison { get; init; } = ArrayComparison.Sequence;
    /// <summary>Describes unmatched root collection items in failure reports.</summary>
    public Func<JsonElement, string?>? DescribeItem { get; init; }
    /// <summary>Array modes for exact paths. Nested payload arrays remain sequences by default.</summary>
    public IReadOnlyDictionary<string, ArrayComparison> ArrayComparisons { get; init; } =
        new Dictionary<string, ArrayComparison>(StringComparer.Ordinal);
    /// <summary>Comparators keyed by exact JSON paths, for example $.balance or $[0].payload.</summary>
    public IReadOnlyDictionary<
        string,
        Func<JsonElement, JsonElement, bool>
    > Comparers { get; init; } =
        new Dictionary<string, Func<JsonElement, JsonElement, bool>>(StringComparer.Ordinal);
    // csharpier-ignore-end
}

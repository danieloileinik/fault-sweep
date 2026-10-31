using System.Text.Json;

namespace FaultSweep.Snapshots;

internal sealed class SnapshotComparer
{
    private readonly HashSet<string> ignoredProperties;
    private readonly Dictionary<string, Func<JsonElement, JsonElement, bool>> comparers;
    private readonly ArrayComparison arrayComparison;
    private readonly Dictionary<string, ArrayComparison> arrayComparisons;

    public SnapshotComparer(SnapshotComparisonOptions? options)
    {
        options ??= new SnapshotComparisonOptions();
        ArgumentNullException.ThrowIfNull(options.IgnoredProperties);
        ArgumentNullException.ThrowIfNull(options.Comparers);
        ArgumentNullException.ThrowIfNull(options.ArrayComparisons);
        if (!Enum.IsDefined(options.ArrayComparison))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Unknown array comparison mode."
            );
        }

        ignoredProperties = new HashSet<string>(options.IgnoredProperties, StringComparer.Ordinal);
        comparers = new Dictionary<string, Func<JsonElement, JsonElement, bool>>(
            options.Comparers,
            StringComparer.Ordinal
        );
        if (comparers.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
        {
            throw new ArgumentException(
                "Comparators require a nonempty path and a nonnull delegate.",
                nameof(options)
            );
        }

        arrayComparison = options.ArrayComparison;
        arrayComparisons = new Dictionary<string, ArrayComparison>(
            options.ArrayComparisons,
            StringComparer.Ordinal
        );
        if (
            arrayComparisons.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || !Enum.IsDefined(pair.Value)
            )
        )
        {
            throw new ArgumentException(
                "Array comparison modes require a nonempty path and a valid mode.",
                nameof(options)
            );
        }
    }

    public IReadOnlyList<SnapshotDifference> Compare(
        string name,
        StateSnapshot expected,
        StateSnapshot actual
    )
    {
        List<SnapshotDifference> differences = [];
        CompareValue(name, "$", expected.Value, actual.Value, differences);
        return differences.AsReadOnly();
    }

    private void CompareValue(
        string name,
        string path,
        JsonElement expected,
        JsonElement actual,
        List<SnapshotDifference> differences
    )
    {
        if (comparers.TryGetValue(path, out Func<JsonElement, JsonElement, bool>? comparer))
        {
            if (!comparer(expected, actual))
            {
                AddMismatch();
            }

            return;
        }

        if (expected.ValueKind != actual.ValueKind)
        {
            AddMismatch();
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                CompareObjects(name, path, expected, actual, differences);
                break;
            case JsonValueKind.Array:
                CompareArrays(name, path, expected, actual, differences);
                break;
            default:
                if (!JsonElement.DeepEquals(expected, actual))
                {
                    AddMismatch();
                }

                break;
        }

        void AddMismatch() =>
            differences.Add(
                new SnapshotDifference(
                    name,
                    path,
                    DifferenceKind.ValueMismatch,
                    StateSnapshot.Format(expected),
                    StateSnapshot.Format(actual)
                )
            );
    }

    private void CompareObjects(
        string name,
        string path,
        JsonElement expected,
        JsonElement actual,
        List<SnapshotDifference> differences
    )
    {
        string[] names = expected
            .EnumerateObject()
            .Select(property => property.Name)
            .Concat(actual.EnumerateObject().Select(property => property.Name))
            .Where(property => !ignoredProperties.Contains(property))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (string property in names)
        {
            string propertyPath = PropertyPath(path, property);
            bool hasExpected = expected.TryGetProperty(property, out JsonElement expectedValue);
            bool hasActual = actual.TryGetProperty(property, out JsonElement actualValue);
            if (!hasExpected)
            {
                differences.Add(
                    new SnapshotDifference(
                        name,
                        propertyPath,
                        DifferenceKind.Unexpected,
                        null,
                        StateSnapshot.Format(actualValue)
                    )
                );
            }
            else if (!hasActual)
            {
                differences.Add(
                    new SnapshotDifference(
                        name,
                        propertyPath,
                        DifferenceKind.Missing,
                        StateSnapshot.Format(expectedValue),
                        null
                    )
                );
            }
            else
            {
                CompareValue(name, propertyPath, expectedValue, actualValue, differences);
            }
        }
    }

    private void CompareArrays(
        string name,
        string path,
        JsonElement expected,
        JsonElement actual,
        List<SnapshotDifference> differences
    )
    {
        ArrayComparison mode = arrayComparisons.GetValueOrDefault(
            path,
            path == "$" ? arrayComparison : ArrayComparison.Sequence
        );
        JsonElement[] expectedItems = expected.EnumerateArray().ToArray();
        JsonElement[] actualItems = actual.EnumerateArray().ToArray();
        if (mode == ArrayComparison.Sequence)
        {
            for (int index = 0; index < Math.Max(expectedItems.Length, actualItems.Length); index++)
            {
                string itemPath = $"{path}[{index}]";
                if (index >= actualItems.Length)
                {
                    differences.Add(
                        new SnapshotDifference(
                            name,
                            itemPath,
                            DifferenceKind.Missing,
                            StateSnapshot.Format(expectedItems[index]),
                            null
                        )
                    );
                }
                else if (index >= expectedItems.Length)
                {
                    differences.Add(
                        new SnapshotDifference(
                            name,
                            itemPath,
                            DifferenceKind.Unexpected,
                            null,
                            StateSnapshot.Format(actualItems[index])
                        )
                    );
                }
                else
                {
                    CompareValue(
                        name,
                        itemPath,
                        expectedItems[index],
                        actualItems[index],
                        differences
                    );
                }
            }

            return;
        }

        if (mode == ArrayComparison.Multiset)
        {
            CompareMultiset(name, path, expectedItems, actualItems, differences);
            return;
        }

        for (int index = 0; index < expectedItems.Length; index++)
        {
            if (
                expectedItems
                    .Take(index)
                    .Any(item => Equal($"{path}[{index}]", item, expectedItems[index]))
            )
            {
                continue;
            }

            if (!actualItems.Any(item => Equal($"{path}[{index}]", expectedItems[index], item)))
            {
                differences.Add(
                    new SnapshotDifference(
                        name,
                        $"{path}[{index}]",
                        DifferenceKind.Missing,
                        StateSnapshot.Format(expectedItems[index]),
                        null
                    )
                );
            }
        }

        for (int index = 0; index < actualItems.Length; index++)
        {
            bool present = expectedItems.Any(item =>
                Equal($"{path}[{index}]", item, actualItems[index])
            );
            if (
                !present
                && !actualItems
                    .Take(index)
                    .Any(item => Equal($"{path}[{index}]", item, actualItems[index]))
            )
            {
                differences.Add(
                    new SnapshotDifference(
                        name,
                        $"{path}[+{index}]",
                        DifferenceKind.Unexpected,
                        null,
                        StateSnapshot.Format(actualItems[index])
                    )
                );
            }
        }
    }

    private void CompareMultiset(
        string name,
        string path,
        JsonElement[] expectedItems,
        JsonElement[] actualItems,
        List<SnapshotDifference> differences
    )
    {
        int[] actualByExpected = Enumerable.Repeat(-1, expectedItems.Length).ToArray();
        int[] expectedByActual = Enumerable.Repeat(-1, actualItems.Length).ToArray();
        for (int index = 0; index < expectedItems.Length; index++)
        {
            if (!TryMatch(index))
            {
                differences.Add(
                    new SnapshotDifference(
                        name,
                        $"{path}[{index}]",
                        DifferenceKind.Missing,
                        StateSnapshot.Format(expectedItems[index]),
                        null
                    )
                );
            }
        }

        for (int index = 0; index < actualItems.Length; index++)
        {
            if (expectedByActual[index] < 0)
            {
                differences.Add(
                    new SnapshotDifference(
                        name,
                        $"{path}[+{index}]",
                        DifferenceKind.Unexpected,
                        null,
                        StateSnapshot.Format(actualItems[index])
                    )
                );
            }
        }

        bool TryMatch(int expectedIndex)
        {
            // Prefer a free match before trying to reassign earlier pairs.
            for (int candidate = 0; candidate < actualItems.Length; candidate++)
            {
                if (
                    expectedByActual[candidate] < 0
                    && Equal(
                        $"{path}[{expectedIndex}]",
                        expectedItems[expectedIndex],
                        actualItems[candidate]
                    )
                )
                {
                    actualByExpected[expectedIndex] = candidate;
                    expectedByActual[candidate] = expectedIndex;
                    return true;
                }
            }

            int[] previousExpected = Enumerable.Repeat(-1, actualItems.Length).ToArray();
            var pending = new Queue<int>();
            pending.Enqueue(expectedIndex);
            while (pending.TryDequeue(out int current))
            {
                for (int candidate = 0; candidate < actualItems.Length; candidate++)
                {
                    if (
                        previousExpected[candidate] >= 0
                        || !Equal(
                            $"{path}[{current}]",
                            expectedItems[current],
                            actualItems[candidate]
                        )
                    )
                    {
                        continue;
                    }

                    previousExpected[candidate] = current;
                    if (expectedByActual[candidate] >= 0)
                    {
                        pending.Enqueue(expectedByActual[candidate]);
                        continue;
                    }

                    // Follow the augmenting path back to the unmatched expected item.
                    int actualIndex = candidate;
                    while (actualIndex >= 0)
                    {
                        int reassignedExpected = previousExpected[actualIndex];
                        int previousActual = actualByExpected[reassignedExpected];
                        actualByExpected[reassignedExpected] = actualIndex;
                        expectedByActual[actualIndex] = reassignedExpected;
                        actualIndex = previousActual;
                    }

                    return true;
                }
            }

            return false;
        }
    }

    private bool Equal(string path, JsonElement expected, JsonElement actual)
    {
        List<SnapshotDifference> differences = [];
        CompareValue(string.Empty, path, expected, actual, differences);
        return differences.Count == 0;
    }

    private static string PropertyPath(string path, string name)
    {
        return
            name.Length > 0
            && (char.IsAsciiLetter(name[0]) || name[0] == '_')
            && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            ? $"{path}.{name}"
            : $"{path}[{JsonSerializer.Serialize(name)}]";
    }
}

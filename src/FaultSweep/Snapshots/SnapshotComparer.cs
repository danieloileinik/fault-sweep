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
    ) =>
        Array.AsReadOnly(
            FindDifferences("$", expected.Value, actual.Value)
                .Select(difference => difference.Format(name))
                .ToArray()
        );

    private IEnumerable<JsonDifference> FindDifferences(
        string path,
        JsonElement expected,
        JsonElement actual
    )
    {
        if (comparers.TryGetValue(path, out var comparer))
        {
            return comparer(expected, actual) ? [] : [new JsonDifference(path, expected, actual)];
        }

        if (expected.ValueKind == actual.ValueKind)
        {
            switch (expected.ValueKind)
            {
                case JsonValueKind.Object:
                    return CompareObjects(path, expected, actual);
                case JsonValueKind.Array:
                    return CompareArrays(path, expected, actual);
                default:
                    if (JsonElement.DeepEquals(expected, actual))
                    {
                        return [];
                    }
                    break;
            }
        }

        return [new JsonDifference(path, expected, actual)];
    }

    private IEnumerable<JsonDifference> CompareObjects(
        string path,
        JsonElement expected,
        JsonElement actual
    )
    {
        var names = expected
            .EnumerateObject()
            .Select(property => property.Name)
            .Union(
                actual.EnumerateObject().Select(property => property.Name),
                StringComparer.Ordinal
            )
            .Where(property => !ignoredProperties.Contains(property))
            .Order(StringComparer.Ordinal);

        foreach (string property in names)
        {
            string propertyPath = PropertyPath(path, property);
            bool hasExpected = expected.TryGetProperty(property, out JsonElement left);
            bool hasActual = actual.TryGetProperty(property, out JsonElement right);
            if (!hasExpected || !hasActual)
            {
                yield return new JsonDifference(propertyPath, left, right);
                continue;
            }

            foreach (var difference in FindDifferences(propertyPath, left, right))
            {
                yield return difference;
            }
        }
    }

    private IEnumerable<JsonDifference> CompareArrays(
        string path,
        JsonElement expected,
        JsonElement actual
    )
    {
        var mode = arrayComparisons.GetValueOrDefault(
            path,
            path == "$" ? arrayComparison : ArrayComparison.Sequence
        );
        JsonElement[] left = [.. expected.EnumerateArray()];
        JsonElement[] right = [.. actual.EnumerateArray()];
        return mode switch
        {
            ArrayComparison.Set => CompareSet(path, left, right),
            ArrayComparison.Multiset => CompareMultiset(path, left, right),
            _ => CompareSequence(path, left, right),
        };
    }

    private IEnumerable<JsonDifference> CompareSequence(
        string path,
        JsonElement[] left,
        JsonElement[] right
    )
    {
        for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            string itemPath = $"{path}[{i}]";
            if (i >= right.Length)
            {
                yield return new JsonDifference(itemPath, left[i], default);
            }
            else if (i >= left.Length)
            {
                yield return new JsonDifference(itemPath, default, right[i]);
            }
            else
            {
                foreach (var difference in FindDifferences(itemPath, left[i], right[i]))
                {
                    yield return difference;
                }
            }
        }
    }

    private IEnumerable<JsonDifference> CompareSet(
        string path,
        JsonElement[] left,
        JsonElement[] right
    )
    {
        for (int i = 0; i < left.Length; i++)
        {
            string itemPath = $"{path}[{i}]";
            if (
                !left.Take(i).Any(item => Equal(itemPath, item, left[i]))
                && !right.Any(item => Equal(itemPath, left[i], item))
            )
            {
                yield return new JsonDifference(itemPath, left[i], default);
            }
        }

        for (int i = 0; i < right.Length; i++)
        {
            string itemPath = $"{path}[{i}]";
            if (
                !left.Any(item => Equal(itemPath, item, right[i]))
                && !right.Take(i).Any(item => Equal(itemPath, item, right[i]))
            )
            {
                yield return new JsonDifference($"{path}[+{i}]", default, right[i]);
            }
        }
    }

    private IEnumerable<JsonDifference> CompareMultiset(
        string path,
        JsonElement[] expectedItems,
        JsonElement[] actualItems
    )
    {
        int[] actualByExpected = Enumerable.Repeat(-1, expectedItems.Length).ToArray();
        int[] expectedByActual = Enumerable.Repeat(-1, actualItems.Length).ToArray();
        for (int index = 0; index < expectedItems.Length; index++)
        {
            if (!TryMatch(index))
            {
                yield return new JsonDifference($"{path}[{index}]", expectedItems[index], default);
            }
        }

        for (int index = 0; index < actualItems.Length; index++)
        {
            if (expectedByActual[index] < 0)
            {
                yield return new JsonDifference($"{path}[+{index}]", default, actualItems[index]);
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

    private bool Equal(string path, JsonElement expected, JsonElement actual) =>
        !FindDifferences(path, expected, actual).Any();

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

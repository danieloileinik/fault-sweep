using System.Text.Json;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;

namespace FaultSweep.Tests;

public sealed class MultisetMatchingTests
{
    [Theory]
    [InlineData("[1,0]", "[0,2]", false)]
    [InlineData("[1,0]", "[2,0]", false)]
    [InlineData("[0,1]", "[0,2]", false)]
    [InlineData("[0,1]", "[2,0]", false)]
    [InlineData("[1,2,0]", "[0,1,3]", false)]
    [InlineData("[1,0,0]", "[0,2,2]", true)]
    [InlineData("[0,0]", "[0]", true)]
    [InlineData("[0]", "[0,0]", true)]
    [InlineData("[]", "[]", false)]
    public async Task Finds_a_complete_matching_with_tolerance_independently_of_item_order(
        string expected,
        string actual,
        bool hasViolations
    )
    {
        // Arrange
        var reference = StateSnapshot.FromJson(expected);
        var observed = StateSnapshot.FromJson(actual);
        var options = new SnapshotComparisonOptions
        {
            ArrayComparison = ArrayComparison.Multiset,
            Comparers = Enumerable
                .Range(0, reference.Value.GetArrayLength())
                .ToDictionary(
                    index => $"$[{index}]",
                    _ => (Func<JsonElement, JsonElement, bool>)WithinTolerance
                ),
        };

        // Act
        FaultSweepResult result = await CompareAsync(reference, observed, options);

        // Assert
        Assert.Equal(hasViolations, result.HasViolations);
        Assert.All(
            result.Scenarios,
            scenario => Assert.Equal(hasViolations, scenario.HasViolation)
        );
    }

    [Fact]
    public async Task Reassigns_matches_using_nested_comparators_and_ignored_fields()
    {
        // Arrange
        var expected = StateSnapshot.FromJson(
            """{"events":[{"id":"a","amount":1},{"id":"b","amount":0}]}"""
        );
        var actual = StateSnapshot.FromJson(
            """{"events":[{"id":"x","amount":0},{"id":"y","amount":2}]}"""
        );
        var options = new SnapshotComparisonOptions
        {
            IgnoredProperties = ["id"],
            ArrayComparisons = new Dictionary<string, ArrayComparison>
            {
                ["$.events"] = ArrayComparison.Multiset,
            },
            Comparers = new Dictionary<string, Func<JsonElement, JsonElement, bool>>
            {
                ["$.events[0].amount"] = WithinTolerance,
                ["$.events[1].amount"] = WithinTolerance,
            },
        };

        // Act
        FaultSweepResult result = await CompareAsync(expected, actual, options);

        // Assert
        result.ShouldHaveNoViolations();
    }

    [Theory]
    [InlineData(ArrayComparison.Set)]
    [InlineData(ArrayComparison.Multiset)]
    public async Task Stops_comparing_candidate_fields_after_the_first_mismatch(
        ArrayComparison mode
    )
    {
        // Arrange
        var expected = StateSnapshot.FromJson("""[{"a":1,"b":0}]""");
        var actual = StateSnapshot.FromJson("""[{"a":2,"b":0}]""");
        int calls = 0;
        var options = new SnapshotComparisonOptions
        {
            ArrayComparison = mode,
            Comparers = new Dictionary<string, Func<JsonElement, JsonElement, bool>>
            {
                ["$[0].b"] = (_, _) =>
                {
                    calls++;
                    return true;
                },
            },
        };

        // Act
        FaultSweepResult result = await CompareAsync(expected, actual, options);

        // Assert
        Assert.True(result.HasViolations);
        Assert.Equal(0, calls);
    }

    private static bool WithinTolerance(JsonElement left, JsonElement right) =>
        Math.Abs(left.GetInt32() - right.GetInt32()) <= 1;

    private static async Task<FaultSweepResult> CompareAsync(
        StateSnapshot expected,
        StateSnapshot actual,
        SnapshotComparisonOptions options
    )
    {
        using var environment = new SweepTestEnvironment(
            ZooBehaviors.Create(ZooCase.AtomicInboxOutbox)
        );
        int captures = 0;

        return await environment
            .CreateSweep()
            .TrackState(
                "values",
                (_, _) => ValueTask.FromResult(captures++ == 0 ? expected : actual),
                options
            )
            .RunAsync();
    }
}

using System.Text.Json;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;

namespace FaultSweep.Tests;

public sealed class SnapshotTests
{
    [Theory]
    [InlineData("{\"a\":1,\"b\":2}", "{\"b\":2,\"a\":1}")]
    [InlineData("{\"number\":1}", "{\"number\":1.0}")]
    [InlineData("null", "null")]
    [InlineData("[1,2]", "[1,2]")]
    public async Task Compares_structural_values_without_object_property_order(string expected, string actual)
    {
        // Arrange
        StateSnapshot baseline = StateSnapshot.FromJson(expected);
        StateSnapshot scenario = StateSnapshot.FromJson(actual);

        // Act
        FaultSweepResult result = await CompareAsync(baseline, scenario);

        // Assert
        Assert.False(result.HasViolations, result.ToString());
    }

    [Fact]
    public async Task Ignores_configured_properties_at_every_level()
    {
        // Arrange
        StateSnapshot expected = StateSnapshot.FromJson("""{"id":1,"rows":[{"id":2,"created_at":"a","amount":10}]}""");
        StateSnapshot actual = StateSnapshot.FromJson("""{"id":9,"rows":[{"id":8,"created_at":"b","amount":10}]}""");
        var options = new SnapshotComparisonOptions { IgnoredProperties = ["id", "created_at"] };

        // Act
        FaultSweepResult result = await CompareAsync(expected, actual, options);

        // Assert
        Assert.False(result.HasViolations, result.ToString());
    }

    [Theory]
    [InlineData(ArrayComparison.Sequence, "[1,2]", "[2,1]", true)]
    [InlineData(ArrayComparison.Set, "[1,2]", "[2,1]", false)]
    [InlineData(ArrayComparison.Set, "[1,1,2]", "[2,1,2]", false)]
    [InlineData(ArrayComparison.Set, "[1]", "[1,2]", true)]
    [InlineData(ArrayComparison.Set, "[]", "[1,1]", true)]
    [InlineData(ArrayComparison.Multiset, "[1,1,2]", "[2,1,1]", false)]
    [InlineData(ArrayComparison.Multiset, "[1,2]", "[1,2,2]", true)]
    [InlineData(ArrayComparison.Multiset, "[1,1]", "[1]", true)]
    [InlineData(ArrayComparison.Multiset, "[]", "[]", false)]
    public async Task Compares_collections_using_the_selected_semantics(
        ArrayComparison mode, string expected, string actual, bool hasViolations)
    {
        // Arrange
        var options = new SnapshotComparisonOptions { ArrayComparison = mode };

        // Act
        FaultSweepResult result = await CompareAsync(StateSnapshot.FromJson(expected), StateSnapshot.FromJson(actual), options);

        // Assert
        Assert.Equal(hasViolations, result.HasViolations);
    }

    [Theory]
    [InlineData(ArrayComparison.Set)]
    [InlineData(ArrayComparison.Multiset)]
    public async Task Normalizes_collection_members_before_matching(ArrayComparison mode)
    {
        // Arrange
        StateSnapshot expected = StateSnapshot.FromJson("""[{"id":1,"name":"a"},{"id":2,"name":"b"}]""");
        StateSnapshot actual = StateSnapshot.FromJson("""[{"id":3,"name":"b"},{"id":4,"name":"a"}]""");
        var options = new SnapshotComparisonOptions { ArrayComparison = mode, IgnoredProperties = ["id"] };

        // Act
        FaultSweepResult result = await CompareAsync(expected, actual, options);

        // Assert
        Assert.False(result.HasViolations, result.ToString());
    }

    [Fact]
    public async Task Supports_custom_comparators_for_complex_fields()
    {
        // Arrange
        StateSnapshot expected = StateSnapshot.FromJson("""{"payload":{"amount":10.0,"unit":"USD"}}""");
        StateSnapshot actual = StateSnapshot.FromJson("""{"payload":{"amount":10.001,"unit":"USD"}}""");
        var options = new SnapshotComparisonOptions
        {
            Comparers = new Dictionary<string, Func<JsonElement, JsonElement, bool>>
            {
                ["$.payload"] = (left, right) =>
                    left.GetProperty("unit").GetString() == right.GetProperty("unit").GetString() &&
                    Math.Abs(left.GetProperty("amount").GetDecimal() - right.GetProperty("amount").GetDecimal()) < 0.01m
            }
        };

        // Act
        FaultSweepResult result = await CompareAsync(expected, actual, options);

        // Assert
        Assert.False(result.HasViolations, result.ToString());
    }

    [Fact]
    public async Task Reports_missing_unexpected_and_changed_values_with_stable_paths()
    {
        // Arrange
        StateSnapshot expected = StateSnapshot.FromJson("""{"missing":null,"changed":1,"rows":[1,2],"a.b":1}""");
        StateSnapshot actual = StateSnapshot.FromJson("""{"extra":null,"changed":2,"rows":[1],"a.b":2}""");

        // Act
        FaultSweepResult result = await CompareAsync(expected, actual);

        // Assert
        IReadOnlyList<SnapshotDifference> differences = result.Scenarios[0].Differences;
        Assert.Contains(differences, difference => difference is
        { Path: "$.missing", Kind: DifferenceKind.Missing, Expected: "null", Actual: null });
        Assert.Contains(differences, difference => difference is
        { Path: "$.extra", Kind: DifferenceKind.Unexpected, Expected: null, Actual: "null" });
        Assert.Contains(differences, difference => difference is
        { Path: "$.changed", Kind: DifferenceKind.ValueMismatch, Expected: "1", Actual: "2" });
        Assert.Contains(differences, difference => difference.Path == "$.rows[1]");
        Assert.Contains(differences, difference => difference.Path == "$[\"a.b\"]");
    }

    [Fact]
    public void Captures_values_instead_of_retaining_mutable_state()
    {
        // Arrange
        int[] values = [1, 2];
        StateSnapshot snapshot = StateSnapshot.From(values);

        // Act
        values[0] = 99;

        // Assert
        Assert.Equal("[1,2]", snapshot.ToString());
    }

    [Fact]
    public void Formats_object_properties_in_ordinal_order()
    {
        // Arrange
        StateSnapshot snapshot = StateSnapshot.FromJson("""{"z":{"b":2,"a":1},"a":0}""");

        // Act
        string json = snapshot.ToString();

        // Assert
        Assert.Equal("""{"a":0,"z":{"a":1,"b":2}}""", json);
    }

    private static async Task<FaultSweepResult> CompareAsync(StateSnapshot expected, StateSnapshot actual,
        SnapshotComparisonOptions? options = null)
    {
        using var environment = new SweepTestEnvironment(ZooBehaviors.Create(ZooCase.AtomicInboxOutbox));
        int captures = 0;
        return await environment.CreateSweep()
            .TrackState("values", (_, _) => ValueTask.FromResult(captures++ == 0 ? expected : actual), options)
            .RunAsync();
    }

    [Theory]
    [InlineData(ArrayComparison.Set)]
    [InlineData(ArrayComparison.Multiset)]
    public async Task Preserves_sequence_semantics_inside_event_payloads(ArrayComparison mode)
    {
        // Arrange
        StateSnapshot expected = StateSnapshot.FromJson("""[{"steps":["debit","credit"]}]""");
        StateSnapshot actual = StateSnapshot.FromJson("""[{"steps":["credit","debit"]}]""");
        var options = new SnapshotComparisonOptions { ArrayComparison = mode };

        // Act
        FaultSweepResult result = await CompareAsync(expected, actual, options);

        // Assert
        Assert.True(result.HasViolations);
    }

    [Fact]
    public async Task Supports_collection_modes_at_explicit_nested_paths()
    {
        // Arrange
        StateSnapshot expected = StateSnapshot.FromJson("""{"events":[1,2],"steps":[1,2]}""");
        StateSnapshot actual = StateSnapshot.FromJson("""{"events":[2,1],"steps":[1,2]}""");
        var options = new SnapshotComparisonOptions
        {
            ArrayComparisons = new Dictionary<string, ArrayComparison> { ["$.events"] = ArrayComparison.Multiset }
        };

        // Act
        FaultSweepResult result = await CompareAsync(expected, actual, options);

        // Assert
        Assert.False(result.HasViolations, result.ToString());
    }
}

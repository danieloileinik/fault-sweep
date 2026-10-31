using System.Text.Json;
using System.Text.RegularExpressions;
using FaultSweep.Reporting;

namespace FaultSweep.Xunit;

/// <summary>The existing collection fixture supplies CreateSweep(message), including reset, snapshots and normalizers.</summary>
public sealed class XunitRegressionGenerator : IRegressionTestGenerator
{
    private readonly XunitRegressionOptions options;

    public XunitRegressionGenerator(XunitRegressionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CollectionName);
        string[] identifiers = [options.Namespace, options.ClassName, options.FixtureTypeName];
        foreach (string identifier in identifiers)
        {
            if (
                !Regex.IsMatch(
                    identifier,
                    @"^(global::)?[A-Za-z_][A-Za-z_0-9]*(\.[A-Za-z_][A-Za-z_0-9]*)*$"
                )
            )
            {
                throw new ArgumentException(
                    "Regression names must be valid qualified C# identifiers.",
                    nameof(options)
                );
            }
        }

        this.options = options;
    }

    public string Generate(RegressionTestCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        if (testCase.MessageJson is null && options.MessageExpression is null)
        {
            throw new InvalidOperationException(
                "Supply MessageExpression for a message that cannot be serialized as JSON."
            );
        }

        string messageType = options.MessageTypeName ?? testCase.MessageTypeName;
        string expression =
            options.MessageExpression
            ?? $"global::System.Text.Json.JsonSerializer.Deserialize<{messageType}>({Literal(testCase.MessageJson!)})!";
        ReplayScenario? scenario = testCase.Scenario;
        if (testCase.Duplicates is { } duplicates)
        {
            return GenerateBaseline(messageType, expression, duplicates);
        }
        if (scenario is null)
        {
            return GenerateBaseline(messageType, expression);
        }

        string expected = scenario.ExpectedEffect is null
            ? "null"
            : $"new global::FaultSweep.Effects.EffectDescriptor({Literal(scenario.ExpectedEffect.Source)}, {Literal(scenario.ExpectedEffect.Operation)}, {Literal(scenario.ExpectedEffect.Target)})";
        return $$"""
            namespace {{options.Namespace}};

            [global::Xunit.Collection({{Literal(options.CollectionName)}})]
            public sealed class {{options.ClassName}}_Effect{{scenario.EffectIndex}}_{{scenario.Kind}}({{options.FixtureTypeName}} fixture)
            {
                [global::Xunit.Fact]
                public async global::System.Threading.Tasks.Task ReproducesViolation()
                {
                    // Arrange
                    {{messageType}} message = {{expression}};
                    var scenario = new global::FaultSweep.Reporting.ReplayScenario(
                        {{scenario.EffectIndex}}, global::FaultSweep.FaultKind.{{scenario.Kind}}, {{scenario.MaxRedeliveryAttempts
                ?? 3}}, {{expected}});

                    // Act
                    global::FaultSweep.Reporting.FaultSweepResult result = await fixture.CreateSweep(message)
                        .ReplayScenario(scenario)
                        .RunAsync();

                    // Assert
                    result.ShouldHaveNoViolations();
                }
            }
            """;
    }

    private string GenerateBaseline(
        string messageType,
        string expression,
        DuplicateDeliveryMode? duplicates = null
    )
    {
        string suffix = duplicates is null ? "Baseline" : "Duplicates_" + duplicates;
        string run = duplicates is { } mode
            ? $"fixture.CreateSweep(message).ReplayDuplicates(global::FaultSweep.Reporting.DuplicateDeliveryMode.{mode}).RunAsync()"
            : "fixture.CreateSweep(message).RunAsync()";
        return $$"""
            namespace {{options.Namespace}};

            [global::Xunit.Collection({{Literal(options.CollectionName)}})]
            public sealed class {{options.ClassName}}_{{suffix}}({{options.FixtureTypeName}} fixture)
            {
                [global::Xunit.Fact]
                public async global::System.Threading.Tasks.Task ReproducesViolation()
                {
                    // Arrange
                    {{messageType}} message = {{expression}};

                    // Act
                    global::FaultSweep.Reporting.FaultSweepResult result = await {{run}};

                    // Assert
                    result.ShouldHaveNoViolations();
                }
            }
            """;
    }

    private static string Literal(string? value) =>
        value is null ? "null" : JsonSerializer.Serialize(value);
}

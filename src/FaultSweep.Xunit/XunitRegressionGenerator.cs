using System.Text.Json;
using System.Text.RegularExpressions;
using FaultSweep.Reporting;

namespace FaultSweep.Xunit;

/// <summary>The existing collection fixture supplies CreateSweep(message), including reset, snapshots and normalizers.</summary>
public sealed class XunitRegressionGenerator : IRegressionTestGenerator
{
    private static readonly HashSet<string> ReservedKeywords = new(
        """
        abstract as base bool break byte case catch char checked class const continue decimal default
        delegate do double else enum event explicit extern false finally fixed float for foreach goto
        if implicit in int interface internal is lock long namespace new null object operator out override
        params private protected public readonly ref return sbyte sealed short sizeof stackalloc static
        string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual
        void volatile while __arglist __makeref __reftype __refvalue
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal
    );

    private readonly XunitRegressionOptions options;

    public XunitRegressionGenerator(XunitRegressionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CollectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FixtureTypeName);
        if (!IsIdentifier(options.ClassName))
        {
            throw new ArgumentException(
                "ClassName must be a single C# identifier.",
                nameof(options)
            );
        }

        string fixture = options.FixtureTypeName;
        if (fixture.StartsWith("global::", StringComparison.Ordinal))
        {
            fixture = fixture["global::".Length..];
        }

        if (
            !options.Namespace.Split('.').All(IsIdentifier) || !fixture.Split('.').All(IsIdentifier)
        )
        {
            throw new ArgumentException("Invalid namespace or fixture type name.", nameof(options));
        }

        this.options = options;
    }

    private static bool IsIdentifier(string value) =>
        Regex.IsMatch(value, @"\A@?[A-Za-z_][A-Za-z_0-9]*\z") && !ReservedKeywords.Contains(value);

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
        string suffix = "Baseline";
        string scenarioDeclaration = string.Empty;
        string run = "fixture.CreateSweep(message).RunAsync()";
        if (testCase.Duplicates is { } duplicates)
        {
            suffix = $"Duplicates_{duplicates}";
            run =
                $"fixture.CreateSweep(message).ReplayDuplicates(global::FaultSweep.Reporting.DuplicateDeliveryMode.{duplicates}).RunAsync()";
        }
        else if (testCase.Scenario is { } scenario)
        {
            suffix = $"Effect{scenario.EffectIndex}_{scenario.Kind}";
            string expected = scenario.ExpectedEffect is null
                ? "null"
                : $"new global::FaultSweep.Effects.EffectDescriptor({Literal(scenario.ExpectedEffect.Source)}, {Literal(scenario.ExpectedEffect.Operation)}, {Literal(scenario.ExpectedEffect.Target)})";
            int retries = scenario.MaxRedeliveryAttempts ?? 3;
            scenarioDeclaration = $$"""

                        var scenario = new global::FaultSweep.Reporting.ReplayScenario(
                            {{scenario.EffectIndex}}, global::FaultSweep.FaultKind.{{scenario.Kind}}, {{retries}}, {{expected}});
                """;
            run = """
                fixture.CreateSweep(message)
                            .ReplayScenario(scenario)
                            .RunAsync()
                """;
        }

        return $$"""
            namespace {{options.Namespace}};

            [global::Xunit.Collection({{Literal(options.CollectionName)}})]
            public sealed class {{options.ClassName}}_{{suffix}}({{options.FixtureTypeName}} fixture)
            {
                [global::Xunit.Fact]
                public async global::System.Threading.Tasks.Task ReproducesViolation()
                {
                    // Arrange
                    {{messageType}} message = {{expression}};{{scenarioDeclaration}}

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

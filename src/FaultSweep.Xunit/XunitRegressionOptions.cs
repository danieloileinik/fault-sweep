namespace FaultSweep.Xunit;

public sealed record XunitRegressionOptions
{
    public required string FixtureTypeName { get; init; }

    public required string CollectionName { get; init; }

    public string Namespace { get; init; } = "FaultSweep.RegressionTests";
    public string ClassName { get; init; } = "MessageRegression";
    public string? MessageTypeName { get; init; }

    public string? MessageExpression { get; init; }
}

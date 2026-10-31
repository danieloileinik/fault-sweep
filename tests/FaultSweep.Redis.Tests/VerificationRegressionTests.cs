using System.Reflection;
using System.Runtime.Loader;
using FaultSweep.Reporting;
using FaultSweep.Xunit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FaultSweep.Redis.Tests;

public sealed class VerificationRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, DuplicateDeliveryMode.Sequential)]
    [InlineData(false, DuplicateDeliveryMode.Parallel)]
    public async Task Generated_test_reproduces_business_rules_including_clean_run_failures(
        bool baselineBroken,
        DuplicateDeliveryMode? duplicates = null
    )
    {
        // Arrange
        using var fixture = new VerificationRegressionFixture(baselineBroken);
        FaultSweepBuilder<MinimizeHandler, MinimizeRequest> sweep = fixture.CreateSweep(
            new MinimizeRequest(10)
        );
        if (duplicates is { } mode)
        {
            sweep.ReplayDuplicates(mode);
        }
        FaultSweepResult result = await sweep.RunAsync();
        string source = result.GenerateXunitRegressionTest(VerificationRegressionFixture.Options);
        IEnumerable<string> paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Concat(
                AppDomain
                    .CurrentDomain.GetAssemblies()
                    .Where(assembly =>
                        !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location)
                    )
                    .Select(assembly => assembly.Location)
            )
            .Distinct();
        var compilation = CSharpCompilation.Create(
            "Verification_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)],
            paths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        stream.Position = 0;
        Assembly generated = AssemblyLoadContext.Default.LoadFromStream(stream);
        Type type = generated
            .GetTypes()
            .Single(candidate =>
                candidate.IsPublic && candidate.GetMethod("ReproducesViolation") is not null
            );
        object test = Activator.CreateInstance(type, fixture)!;
        Task Invoke() => (Task)type.GetMethod("ReproducesViolation")!.Invoke(test, null)!;

        // Act
        Exception? broken = await Record.ExceptionAsync(Invoke);
        fixture.Fixed = true;
        Exception? corrected = await Record.ExceptionAsync(Invoke);

        // Assert
        Assert.IsType<FaultSweepAssertionException>(broken);
        Assert.Null(corrected);
        Assert.Equal(baselineBroken, result.BaselineVerification.HasViolations);
        Assert.Equal(
            source,
            baselineBroken ? result.BaselineRegressionTest
                : duplicates is not null ? result.MinimalDuplicateScenario!.Result.RegressionTest
                : result.MinimalScenario!.Result.RegressionTest
        );
        Assert.Equal(
            !baselineBroken && duplicates is null,
            source.Contains(".ReplayScenario(scenario)", StringComparison.Ordinal)
        );
        Assert.All(
            result.Violations,
            scenario => Assert.Equal(ViolationKind.InvariantFailed, scenario.Violations)
        );
    }
}

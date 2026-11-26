using System.Reflection;
using System.Runtime.Loader;
using FaultSweep.Reporting;
using FaultSweep.Xunit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace FaultSweep.Redis.Tests;

[Collection(RedisCollection.Name)]
public sealed class RegressionGenerationTests(RedisFixture fixture)
{
    [Theory]
    [InlineData("Generated", "Orders.Replay", "App.Fixture")]
    [InlineData("global::Generated", "Replay", "App.Fixture")]
    [InlineData("Generated", "Replay", "App.class")]
    public void Rejects_names_that_cannot_appear_in_generated_source(
        string ns,
        string name,
        string fixtureName
    )
    {
        // Arrange
        var options = new XunitRegressionOptions
        {
            Namespace = ns,
            ClassName = name,
            FixtureTypeName = fixtureName,
            CollectionName = "orders",
        };

        // Act
        Exception? error = Record.Exception(() => new XunitRegressionGenerator(options));

        // Assert
        Assert.IsType<ArgumentException>(error);
    }

    [Fact]
    public async Task Generated_test_compiles_fails_for_the_bug_and_passes_after_the_fix()
    {
        // Arrange
        await using var host = new RedisTestHost(fixture.Connection, generate: true);
        var request = new RedisRequest(Guid.NewGuid().ToString("N") + ":");
        FaultSweepResult result = await host.CreateSweep(request).RunAsync();
        string source = result.GenerateXunitRegressionTest(
            new XunitRegressionOptions
            {
                FixtureTypeName = "global::FaultSweep.Redis.Tests.RedisFixture",
                CollectionName = RedisCollection.Name,
                Namespace = "Generated",
            }
        );
        Assert.Equal(source, result.MinimalScenario!.Result.RegressionTest);
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
            "Regression_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)],
            paths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        using var stream = new MemoryStream();
        EmitResult emitted = compilation.Emit(stream);
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
        fixture.RegressionFixed = true;
        try
        {
            await Invoke();
        }
        finally
        {
            fixture.RegressionFixed = false;
        }

        // Assert
        Assert.IsType<FaultSweepAssertionException>(broken);
        Assert.All(
            result.Violations,
            violation => Assert.Contains("Xunit.Fact", violation.RegressionTest!)
        );
        Assert.Contains("Generated xUnit regression test:", result.ToDetailedString());
        Assert.Equal(2, result.ToDetailedString().Split(source, StringSplitOptions.None).Length);
        Assert.DoesNotContain(source, result.ToString());
    }
}

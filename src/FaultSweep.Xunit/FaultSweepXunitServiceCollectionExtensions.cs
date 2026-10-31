using FaultSweep.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FaultSweep.Xunit;

public static class FaultSweepXunitServiceCollectionExtensions
{
    public static IServiceCollection AddFaultSweepXunit(
        this IServiceCollection services,
        XunitRegressionOptions options
    )
    {
        services.AddFaultSweep();
        services.Replace(
            ServiceDescriptor.Singleton<IRegressionTestGenerator>(
                new XunitRegressionGenerator(options)
            )
        );
        return services;
    }

    public static string GenerateXunitRegressionTest(
        this FaultSweepResult result,
        XunitRegressionOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.BaselineRegressionCase is { } baselineCase)
        {
            return new XunitRegressionGenerator(options).Generate(baselineCase);
        }

        if (result.MinimalScenario is null && result.MinimalDuplicateScenario is { } duplicate)
        {
            return new XunitRegressionGenerator(options).Generate(
                new RegressionTestCase(
                    duplicate.MessageJson,
                    duplicate.MessageTypeName,
                    null,
                    duplicate.Result.Mode
                )
            );
        }

        MinimizedScenario minimum =
            result.MinimalScenario
            ?? throw new InvalidOperationException("There is no violating scenario to reproduce.");
        var fault = minimum.Result.Scenario;
        return new XunitRegressionGenerator(options).Generate(
            new RegressionTestCase(
                minimum.MessageJson,
                minimum.MessageTypeName,
                new ReplayScenario(
                    fault.Point.Index,
                    fault.Kind,
                    Math.Max(1, minimum.Result.Attempts.Count - 1),
                    fault.Point.Effect
                )
            )
        );
    }
}

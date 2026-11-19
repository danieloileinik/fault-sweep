using System.Text.Json;
using FaultSweep.Execution;

namespace FaultSweep.Reporting;

internal sealed class RegressionTestPreparer<TMessage>(IRegressionTestGenerator? generator)
{
    public void Attach(
        FaultSweepResult result,
        TMessage runMessage,
        SweepMinimum<TMessage>? minimum
    )
    {
        if (minimum is not null)
        {
            AttachMinimum(result, minimum);
        }

        if (result.BaselineVerification.HasViolations)
        {
            result.BaselineRegressionCase = new RegressionTestCase(
                SerializeMessage(runMessage),
                CSharpTypeName.Format(typeof(TMessage)),
                null
            );
        }

        if (generator is null)
        {
            return;
        }

        if (result.BaselineRegressionCase is { } baselineCase)
        {
            result.BaselineRegressionTest = generator.Generate(baselineCase);
        }

        foreach (FaultScenarioResult violation in result.Violations)
        {
            violation.RegressionTest ??= generator.Generate(CreateCase(runMessage, violation));
        }
        foreach (DuplicateScenarioResult violation in result.DuplicateViolations)
        {
            violation.RegressionTest ??= generator.Generate(
                new RegressionTestCase(
                    SerializeMessage(runMessage),
                    CSharpTypeName.Format(typeof(TMessage)),
                    null,
                    violation.Mode
                )
            );
        }
    }

    private static RegressionTestCase CreateCase(
        TMessage runMessage,
        FaultScenarioResult violation
    ) =>
        new(
            SerializeMessage(runMessage),
            CSharpTypeName.Format(typeof(TMessage)),
            new ReplayScenario(
                violation.Scenario.Point.Index,
                violation.Scenario.Kind,
                violation.Attempts.Count > 1 ? violation.Attempts.Count - 1 : 1,
                violation.Scenario.Point.Effect
            )
        );

    private static string? SerializeMessage(TMessage value)
    {
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void AttachMinimum(FaultSweepResult result, SweepMinimum<TMessage> minimum)
    {
        string? json = SerializeMessage(minimum.Message);
        string typeName = CSharpTypeName.Format(typeof(TMessage));
        if (minimum.Scenario is { } scenario)
        {
            result.MinimalScenario = new MinimizedScenario(
                scenario,
                json,
                typeName,
                minimum.EvaluatedCandidates,
                minimum.BudgetExhausted
            );
            if (generator is not null)
            {
                scenario.RegressionTest = generator.Generate(CreateCase(minimum.Message, scenario));
            }
        }

        if (minimum.Duplicate is { } duplicate)
        {
            result.MinimalDuplicateScenario = new MinimizedDuplicateScenario(
                duplicate,
                json,
                typeName,
                minimum.EvaluatedCandidates,
                minimum.BudgetExhausted
            );
            if (generator is not null)
            {
                duplicate.RegressionTest = generator.Generate(
                    new RegressionTestCase(json, typeName, null, duplicate.Mode)
                );
            }
        }
    }
}

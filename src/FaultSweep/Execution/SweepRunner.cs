using FaultSweep.Effects;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using FaultSweep.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Execution;

internal sealed class SweepRunner<TMessage>(
    IServiceProvider services,
    EffectInterceptor interceptor,
    TMessage message,
    SweepPlan<TMessage> plan
)
{
    private readonly SweepState state = new(services, plan.Reset, plan.Snapshots);
    private readonly SweepDelivery<TMessage> delivery = new(services, interceptor, plan.Invoke);
    private readonly SweepVerifier<TMessage> verifier = new(
        services,
        plan.Snapshots,
        plan.Invariants,
        plan.Options.CompareWithBaseline
    );
    private readonly SweepMinimizer<TMessage> minimizer = new(
        plan.Minimizer,
        plan.Options.MaxMinimizationRuns
    );

    public async Task<FaultSweepResult> RunAsync(
        CancellationToken cancellationToken,
        Func<ValueTask>? cleanup = null
    )
    {
        await using SweepRunScope scope = await SweepRunScope
            .AcquireAsync(interceptor, cleanup, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            scope.Activate(
                services.GetServices<IEffectInstrumentation>().Concat(plan.Instrumentation ?? [])
            );
            FaultSweepResult result = (
                await RunOnceAsync(message, false, cancellationToken).ConfigureAwait(false)
            )!;
            SweepMinimum<TMessage>? minimum = await minimizer
                .MinimizeAsync(
                    result,
                    message,
                    (candidate, token) => RunOnceAsync(candidate, true, token),
                    cancellationToken
                )
                .ConfigureAwait(false);
            var regressionTests = new RegressionTestPreparer<TMessage>(
                services.GetService<IRegressionTestGenerator>()
            );
            regressionTests.Attach(result, message, minimum);
            return result;
        }
        catch (Exception exception)
        {
            scope.RecordFailure(exception);
            throw;
        }
    }

    private async Task<FaultSweepResult?> RunOnceAsync(
        TMessage runMessage,
        bool candidate,
        CancellationToken cancellationToken
    )
    {
        FaultSweepOptions options = plan.Options;
        ReplayScenario? replay = plan.Replay;
        DuplicateDeliveryMode? duplicateReplay = plan.DuplicateReplay;
        var expected = plan.ExpectedStates.ToDictionary(
            pair => pair.Key,
            pair =>
                pair.Value(runMessage)
                ?? throw new FaultSweepExecutionException(
                    $"Expected state '{pair.Key}' returned null."
                ),
            StringComparer.Ordinal
        );
        await state.ResetAsync(cancellationToken).ConfigureAwait(false);
        var baselineSession = new EffectSession(1, options.MaxEffects);
        DeliveryAttempt baseline = await delivery
            .DeliverAsync(baselineSession, 1, runMessage, cancellationToken)
            .ConfigureAwait(false);
        if (!baseline.Succeeded)
        {
            if (candidate)
            {
                return null;
            }

            throw new FaultSweepExecutionException(
                "The clean baseline delivery failed.",
                baseline.Error!
            );
        }

        if (baselineSession.Trace.Count == 0 && !options.AllowEmptyTrace)
        {
            if (candidate)
            {
                return null;
            }

            throw new FaultSweepExecutionException(
                "The baseline recorded no effects. Instrument the handler or explicitly allow an empty trace. "
                    + "Enable the provider's FaultSweep instrumentation in the test configuration."
            );
        }

        IReadOnlyDictionary<string, StateSnapshot> baselineState = await state
            .CaptureAsync(cancellationToken)
            .ConfigureAwait(false);
        VerificationResult baselineVerification = await verifier
            .VerifyAsync(
                runMessage,
                baselineState,
                expected,
                null,
                null,
                [baseline],
                cancellationToken
            )
            .ConfigureAwait(false);
        if (candidate && baselineVerification.HasViolations)
        {
            return null;
        }

        EffectPoint[] points = baselineSession.Trace.Select(entry => entry.Point).ToArray();
        List<FaultScenarioResult> scenarios = [];
        var faults = new FaultScenarioExecutor<TMessage>(
            state,
            delivery,
            verifier,
            options,
            replay
        );
        if (
            replay is not null
            && (
                replay.EffectIndex > points.Length
                || replay.ExpectedEffect is not null
                    && points[replay.EffectIndex - 1].Effect != replay.ExpectedEffect
            )
        )
        {
            if (candidate)
            {
                return null;
            }

            throw new FaultSweepExecutionException(
                "The replay effect does not match the clean baseline trace."
            );
        }
        IReadOnlyCollection<FaultKind> faultKinds = replay is null
            ? options.FaultKinds
            : [replay.Kind];
        foreach (
            EffectPoint point in points.Where(point =>
                duplicateReplay is null && (replay is null || point.Index == replay.EffectIndex)
            )
        )
        {
            foreach (FaultKind kind in faultKinds)
            {
                var scenario = new FaultScenario(point, kind);
                scenarios.Add(
                    await faults
                        .RunAsync(
                            scenario,
                            points,
                            baselineState,
                            expected,
                            runMessage,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                );
            }
        }

        List<DuplicateScenarioResult> duplicates = [];
        var duplicateScenarios = new DuplicateScenarioExecutor<TMessage>(
            state,
            delivery,
            verifier,
            options
        );
        IReadOnlyCollection<DuplicateDeliveryMode> modes =
            duplicateReplay is { } mode ? [mode]
            : replay is null ? options.DuplicateDeliveries
            : [];
        foreach (DuplicateDeliveryMode duplicateMode in modes)
        {
            duplicates.Add(
                await duplicateScenarios
                    .RunAsync(duplicateMode, baselineState, expected, runMessage, cancellationToken)
                    .ConfigureAwait(false)
            );
        }

        return new FaultSweepResult(
            baselineState,
            baselineSession.Trace,
            baselineVerification,
            scenarios,
            duplicates
        );
    }
}

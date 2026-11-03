using System.Collections.ObjectModel;
using FaultSweep.Effects;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using FaultSweep.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Execution;

internal sealed partial class SweepRunner<TMessage>(
    IServiceProvider services,
    EffectInterceptor interceptor,
    TMessage message,
    SweepPlan<TMessage> plan
)
{
    private readonly Func<IServiceProvider, CancellationToken, ValueTask> reset = plan.Reset;
    private readonly IReadOnlyList<SnapshotRegistration> snapshots = plan.Snapshots;
    private readonly IReadOnlyDictionary<string, Func<TMessage, StateSnapshot>> expectedStates =
        plan.ExpectedStates;
    private readonly FaultSweepOptions options = plan.Options;
    private readonly ReplayScenario? replay = plan.Replay;
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
    private readonly DuplicateDeliveryMode? duplicateReplay = plan.DuplicateReplay;

    public async Task<FaultSweepResult> RunAsync(
        CancellationToken cancellationToken,
        Func<ValueTask>? cleanup = null
    )
    {
        await interceptor.SweepLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var instrumentation = new EffectInstrumentationScope(
                services.GetServices<IEffectInstrumentation>().Concat(plan.Instrumentation ?? []),
                interceptor
            );
            try
            {
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
                instrumentation.Dispose(exception);
                throw;
            }
            finally
            {
                instrumentation.Dispose();
            }
        }
        finally
        {
            try
            {
                if (cleanup is not null)
                {
                    await cleanup().ConfigureAwait(false);
                }
            }
            finally
            {
                interceptor.SweepLock.Release();
            }
        }
    }

    private async Task<FaultSweepResult?> RunOnceAsync(
        TMessage runMessage,
        bool candidate,
        CancellationToken cancellationToken
    )
    {
        var expected = expectedStates.ToDictionary(
            pair => pair.Key,
            pair =>
                pair.Value(runMessage)
                ?? throw new FaultSweepExecutionException(
                    $"Expected state '{pair.Key}' returned null."
                ),
            StringComparer.Ordinal
        );
        await ResetAsync(cancellationToken).ConfigureAwait(false);
        var baselineSession = new EffectSession(1, options.MaxEffects);
        DeliveryAttempt baseline = await DeliverAsync(
                baselineSession,
                1,
                runMessage,
                cancellationToken
            )
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

        IReadOnlyDictionary<string, StateSnapshot> baselineState = await CaptureAsync(
                cancellationToken
            )
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
                    await RunScenarioAsync(
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
        IReadOnlyCollection<DuplicateDeliveryMode> modes =
            duplicateReplay is { } mode ? [mode]
            : replay is null ? options.DuplicateDeliveries
            : [];
        foreach (DuplicateDeliveryMode duplicateMode in modes)
        {
            duplicates.Add(
                await RunDuplicatesAsync(
                        duplicateMode,
                        baselineState,
                        expected,
                        runMessage,
                        cancellationToken
                    )
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

    private async Task<FaultScenarioResult> RunScenarioAsync(
        FaultScenario scenario,
        IReadOnlyList<EffectPoint> baselinePoints,
        IReadOnlyDictionary<string, StateSnapshot> baselineState,
        IReadOnlyDictionary<string, StateSnapshot> expected,
        TMessage runMessage,
        CancellationToken cancellationToken
    )
    {
        await ResetAsync(cancellationToken).ConfigureAwait(false);
        var initialSession = new EffectSession(1, options.MaxEffects, scenario, baselinePoints);
        List<DeliveryAttempt> attempts =
        [
            await DeliverAsync(initialSession, 1, runMessage, cancellationToken)
                .ConfigureAwait(false),
        ];
        List<EffectTraceEntry> trace = [.. initialSession.Trace];

        for (
            int retry = 1;
            !attempts[^1].Succeeded
                && retry <= (replay?.MaxRedeliveryAttempts ?? options.MaxRedeliveryAttempts);
            retry++
        )
        {
            var session = new EffectSession(retry + 1, options.MaxEffects);
            attempts.Add(
                await DeliverAsync(session, retry + 1, runMessage, cancellationToken)
                    .ConfigureAwait(false)
            );
            trace.AddRange(session.Trace);
        }

        IReadOnlyDictionary<string, StateSnapshot> state = await CaptureAsync(cancellationToken)
            .ConfigureAwait(false);
        VerificationResult verification = await verifier
            .VerifyAsync(
                runMessage,
                state,
                expected,
                baselineState,
                scenario,
                attempts.AsReadOnly(),
                cancellationToken
            )
            .ConfigureAwait(false);

        ViolationKind violations = ViolationKind.None;
        if (verification.Differences.Count > 0)
        {
            violations |= ViolationKind.StateMismatch;
        }

        if (verification.InvariantViolations.Count > 0)
        {
            violations |= ViolationKind.InvariantFailed;
        }

        if (!attempts[^1].Succeeded)
        {
            violations |= ViolationKind.RedeliveryLimitExceeded;
        }

        if (!initialSession.FaultWasInjected)
        {
            violations |= ViolationKind.FaultNotReached;
        }

        return new FaultScenarioResult(scenario, violations, attempts, trace, state, verification);
    }

    private async ValueTask<DeliveryAttempt> DeliverAsync(
        EffectSession session,
        int attempt,
        TMessage runMessage,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Exception? error = null;
        try
        {
            // Disposal is part of a delivery: open transactions are rolled back before redelivery.
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await interceptor
                .RunAsync(
                    session,
                    () => plan.Invoke(scope.ServiceProvider, runMessage, cancellationToken)
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested
            )
        {
            error = exception;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (session.InfrastructureFailure is not null)
        {
            throw session.InfrastructureFailure;
        }

        error = session.Crash ?? error;
        return new DeliveryAttempt(attempt, error is null, error);
    }

    private async ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        foreach (
            ISweepParticipant participant in scope.ServiceProvider.GetServices<ISweepParticipant>()
        )
        {
            await participant.ResetAsync(cancellationToken).ConfigureAwait(false);
        }

        await reset(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyDictionary<string, StateSnapshot>> CaptureAsync(
        CancellationToken cancellationToken
    )
    {
        var state = new SortedDictionary<string, StateSnapshot>(StringComparer.Ordinal);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        foreach (
            SnapshotRegistration snapshot in snapshots.OrderBy(
                snapshot => snapshot.Name,
                StringComparer.Ordinal
            )
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            StateSnapshot captured = await snapshot
                .Capture(scope.ServiceProvider, cancellationToken)
                .ConfigureAwait(false);
            state.Add(
                snapshot.Name,
                captured
                    ?? throw new FaultSweepExecutionException(
                        $"Snapshot provider '{snapshot.Name}' returned null."
                    )
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ReadOnlyDictionary<string, StateSnapshot>(state);
    }
}

using System.Collections.ObjectModel;
using System.Text.Json;
using FaultSweep.Effects;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using FaultSweep.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Execution;

internal sealed partial class SweepRunner<THandler, TMessage>(
    IServiceProvider services,
    EffectInterceptor interceptor,
    TMessage message,
    Func<THandler, TMessage, CancellationToken, ValueTask> handle,
    Func<IServiceProvider, CancellationToken, ValueTask> reset,
    IReadOnlyList<SnapshotRegistration> snapshots,
    IReadOnlyList<InvariantRegistration<TMessage>> invariants,
    IReadOnlyDictionary<string, Func<TMessage, StateSnapshot>> expectedStates,
    FaultSweepOptions options,
    ReplayScenario? replay = null,
    MessageMinimizer<TMessage>? minimizer = null,
    DuplicateDeliveryMode? duplicateReplay = null,
    bool createHandler = false,
    IReadOnlyCollection<IEffectInstrumentation>? additionalInstrumentation = null
)
    where THandler : class
{
    public async Task<FaultSweepResult> RunAsync(
        CancellationToken cancellationToken,
        Func<ValueTask>? cleanup = null
    )
    {
        await interceptor.SweepLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var instrumentation = new EffectInstrumentationScope(
                services
                    .GetServices<IEffectInstrumentation>()
                    .Concat(additionalInstrumentation ?? []),
                interceptor
            );
            FaultSweepResult result = (
                await RunOnceAsync(message, false, cancellationToken).ConfigureAwait(false)
            )!;
            await MinimizeAsync(result, cancellationToken).ConfigureAwait(false);
            AttachRegressionTests(result, message);
            return result;
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
        VerificationResult baselineVerification = await VerifyAsync(
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
        VerificationResult verification = await VerifyAsync(
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

    private async ValueTask<VerificationResult> VerifyAsync(
        TMessage runMessage,
        IReadOnlyDictionary<string, StateSnapshot> state,
        IReadOnlyDictionary<string, StateSnapshot> expected,
        IReadOnlyDictionary<string, StateSnapshot>? baselineState,
        FaultScenario? scenario,
        IReadOnlyList<DeliveryAttempt> attempts,
        CancellationToken cancellationToken,
        DuplicateDeliveryMode? duplicates = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<SnapshotDifference> differences = [];
        foreach (
            SnapshotRegistration snapshot in snapshots.OrderBy(
                snapshot => snapshot.Name,
                StringComparer.Ordinal
            )
        )
        {
            StateSnapshot? reference =
                expected.GetValueOrDefault(snapshot.Name)
                ?? (
                    options.CompareWithBaseline
                        ? baselineState?.GetValueOrDefault(snapshot.Name)
                        : null
                );
            if (reference is not null)
            {
                differences.AddRange(
                    snapshot.Comparer.Compare(snapshot.Name, reference, state[snapshot.Name])
                );
            }
        }

        List<InvariantViolation> failures = [];
        if (invariants.Count > 0)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            var context = new VerificationContext<TMessage>(
                runMessage,
                scope.ServiceProvider,
                state,
                scenario,
                attempts,
                duplicates
            );
            foreach (InvariantRegistration<TMessage> invariant in invariants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!await invariant.Check(context, cancellationToken).ConfigureAwait(false))
                    {
                        failures.Add(
                            new InvariantViolation(invariant.Name, invariant.FailureMessage)
                        );
                    }
                }
                catch (Exception exception)
                    when (exception is not OperationCanceledException
                        || !cancellationToken.IsCancellationRequested
                    )
                {
                    failures.Add(
                        new InvariantViolation(invariant.Name, exception.Message, exception)
                    );
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new VerificationResult(differences, failures);
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
                    () => InvokeAsync(scope.ServiceProvider, runMessage, cancellationToken)
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

    private async ValueTask InvokeAsync(
        IServiceProvider provider,
        TMessage runMessage,
        CancellationToken token
    )
    {
        THandler? handler = provider.GetService<THandler>();
        if (handler is not null || !createHandler)
        {
            await handle(handler ?? provider.GetRequiredService<THandler>(), runMessage, token)
                .ConfigureAwait(false);
            return;
        }

        handler = ActivatorUtilities.CreateInstance<THandler>(provider);
        try
        {
            await handle(handler, runMessage, token).ConfigureAwait(false);
        }
        finally
        {
            if (handler is IAsyncDisposable asynchronous)
            {
                await asynchronous.DisposeAsync().ConfigureAwait(false);
            }
            else if (handler is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static FaultScenarioResult? Smallest(FaultSweepResult result) =>
        result
            .Violations.OrderBy(item => item.Scenario.Point.Index)
            .ThenBy(item => item.Attempts.Count)
            .ThenBy(item => item.Scenario.Kind)
            .FirstOrDefault();

    private async Task MinimizeAsync(FaultSweepResult original, CancellationToken token)
    {
        // A fault scenario is meaningful only when the original and shrunken clean runs satisfy the business rules.
        if (original.BaselineVerification.HasViolations)
        {
            return;
        }

        FaultScenarioResult? smallest = Smallest(original);
        DuplicateScenarioResult? smallestDuplicate = original.DuplicateViolations.FirstOrDefault();
        if (smallest is null && smallestDuplicate is null)
        {
            return;
        }

        TMessage bestMessage = message;
        int evaluated = 0;
        bool exhausted = false;
        if (minimizer is not null)
        {
            long score = minimizer.Complexity(bestMessage);
            ArgumentOutOfRangeException.ThrowIfNegative(score);
            while (evaluated < options.MaxMinimizationRuns)
            {
                bool improved = false;
                // Only a strictly smaller candidate may replace the current message; cycles cannot keep the search alive.
                foreach (TMessage candidate in minimizer.Shrink(bestMessage))
                {
                    token.ThrowIfCancellationRequested();
                    if (evaluated >= options.MaxMinimizationRuns)
                    {
                        exhausted = true;
                        break;
                    }
                    evaluated++;
                    long candidateScore = minimizer.Complexity(candidate);
                    if (candidateScore < 0 || candidateScore >= score)
                    {
                        continue;
                    }

                    FaultSweepResult? candidateResult = await RunOnceAsync(candidate, true, token)
                        .ConfigureAwait(false);
                    FaultScenarioResult? candidateViolation = candidateResult is null
                        ? null
                        : Smallest(candidateResult);
                    DuplicateScenarioResult? candidateDuplicate =
                        candidateResult?.DuplicateViolations.FirstOrDefault();
                    if (candidateViolation is null && candidateDuplicate is null)
                    {
                        continue;
                    }

                    bestMessage = candidate;
                    score = candidateScore;
                    smallest = candidateViolation;
                    smallestDuplicate = candidateDuplicate;
                    improved = true;
                }
                if (!improved)
                {
                    break;
                }
            }
            exhausted |= evaluated >= options.MaxMinimizationRuns;
        }
        if (smallest is not null)
        {
            original.MinimalScenario = new MinimizedScenario(
                smallest,
                SerializeMessage(bestMessage),
                CSharpTypeName.Format(typeof(TMessage)),
                evaluated,
                exhausted
            );
        }
        if (smallestDuplicate is not null)
        {
            original.MinimalDuplicateScenario = new MinimizedDuplicateScenario(
                smallestDuplicate,
                SerializeMessage(bestMessage),
                CSharpTypeName.Format(typeof(TMessage)),
                evaluated,
                exhausted
            );
        }
        IRegressionTestGenerator? generator = services.GetService<IRegressionTestGenerator>();
        if (generator is not null && smallest is not null)
        {
            smallest.RegressionTest = generator.Generate(CreateCase(bestMessage, smallest));
        }
        if (generator is not null && smallestDuplicate is not null)
        {
            smallestDuplicate.RegressionTest = generator.Generate(
                new RegressionTestCase(
                    SerializeMessage(bestMessage),
                    CSharpTypeName.Format(typeof(TMessage)),
                    null,
                    smallestDuplicate.Mode
                )
            );
        }
    }

    private void AttachRegressionTests(FaultSweepResult result, TMessage runMessage)
    {
        if (result.BaselineVerification.HasViolations)
        {
            result.BaselineRegressionCase = new RegressionTestCase(
                SerializeMessage(runMessage),
                CSharpTypeName.Format(typeof(TMessage)),
                null
            );
        }

        IRegressionTestGenerator? generator = services.GetService<IRegressionTestGenerator>();
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

    private RegressionTestCase CreateCase(TMessage runMessage, FaultScenarioResult violation) =>
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

using System.Collections.ObjectModel;
using FaultSweep.Effects;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Execution;

internal sealed class SweepRunner<THandler, TMessage>(
    IServiceProvider services,
    EffectInterceptor interceptor,
    TMessage message,
    Func<THandler, TMessage, CancellationToken, ValueTask> handle,
    Func<IServiceProvider, CancellationToken, ValueTask> reset,
    IReadOnlyList<SnapshotRegistration> snapshots,
    FaultSweepOptions options) where THandler : class
{
    public async Task<FaultSweepResult> RunAsync(CancellationToken cancellationToken)
    {
        await interceptor.SweepLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var instrumentation = new EffectInstrumentationScope(
                services.GetServices<IEffectInstrumentation>(), interceptor);
            await ResetAsync(cancellationToken).ConfigureAwait(false);
            var baselineSession = new EffectSession(1, options.MaxEffects);
            DeliveryAttempt baseline = await DeliverAsync(baselineSession, 1, cancellationToken).ConfigureAwait(false);
            if (!baseline.Succeeded)
            {
                throw new FaultSweepExecutionException("The clean baseline delivery failed.", baseline.Error!);
            }

            if (baselineSession.Trace.Count == 0 && !options.AllowEmptyTrace)
            {
                throw new FaultSweepExecutionException(
                    "The baseline recorded no effects. Instrument the handler or explicitly allow an empty trace. " +
                    "Enable the provider's FaultSweep instrumentation in the test configuration.");
            }

            IReadOnlyDictionary<string, StateSnapshot> baselineState = await CaptureAsync(cancellationToken).ConfigureAwait(false);
            EffectPoint[] points = baselineSession.Trace.Select(entry => entry.Point).ToArray();
            List<FaultScenarioResult> scenarios = [];
            foreach (EffectPoint point in points)
            {
                foreach (FaultKind kind in options.FaultKinds)
                {
                    var scenario = new FaultScenario(point, kind);
                    scenarios.Add(await RunScenarioAsync(scenario, points, baselineState, cancellationToken).ConfigureAwait(false));
                }
            }

            return new FaultSweepResult(baselineState, baselineSession.Trace, scenarios);
        }
        finally
        {
            interceptor.SweepLock.Release();
        }
    }

    private async Task<FaultScenarioResult> RunScenarioAsync(FaultScenario scenario,
        IReadOnlyList<EffectPoint> baselinePoints, IReadOnlyDictionary<string, StateSnapshot> baselineState,
        CancellationToken cancellationToken)
    {
        await ResetAsync(cancellationToken).ConfigureAwait(false);
        var initialSession = new EffectSession(1, options.MaxEffects, scenario, baselinePoints);
        List<DeliveryAttempt> attempts =
            [await DeliverAsync(initialSession, 1, cancellationToken).ConfigureAwait(false)];
        List<EffectTraceEntry> trace = [.. initialSession.Trace];

        for (int retry = 1; !attempts[^1].Succeeded && retry <= options.MaxRedeliveryAttempts; retry++)
        {
            var session = new EffectSession(retry + 1, options.MaxEffects);
            attempts.Add(await DeliverAsync(session, retry + 1, cancellationToken).ConfigureAwait(false));
            trace.AddRange(session.Trace);
        }

        IReadOnlyDictionary<string, StateSnapshot> state = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        List<SnapshotDifference> differences = [];
        foreach (SnapshotRegistration snapshot in snapshots.OrderBy(snapshot => snapshot.Name, StringComparer.Ordinal))
        {
            differences.AddRange(snapshot.Comparer.Compare(snapshot.Name, baselineState[snapshot.Name], state[snapshot.Name]));
        }

        ViolationKind violations = ViolationKind.None;
        if (differences.Count > 0)
        {
            violations |= ViolationKind.StateMismatch;
        }

        if (!attempts[^1].Succeeded)
        {
            violations |= ViolationKind.RedeliveryLimitExceeded;
        }

        if (!initialSession.FaultWasInjected)
        {
            violations |= ViolationKind.FaultNotReached;
        }

        return new FaultScenarioResult(scenario, violations, attempts, trace, state, differences);
    }

    private async ValueTask<DeliveryAttempt> DeliverAsync(EffectSession session, int attempt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Exception? error = null;
        try
        {
            // Disposal is part of a delivery: open transactions are rolled back before redelivery.
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await interceptor.RunAsync(session, () =>
                handle(scope.ServiceProvider.GetRequiredService<THandler>(), message, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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
        foreach (ISweepParticipant participant in scope.ServiceProvider.GetServices<ISweepParticipant>())
        {
            await participant.ResetAsync(cancellationToken).ConfigureAwait(false);
        }

        await reset(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyDictionary<string, StateSnapshot>> CaptureAsync(CancellationToken cancellationToken)
    {
        var state = new SortedDictionary<string, StateSnapshot>(StringComparer.Ordinal);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        foreach (SnapshotRegistration snapshot in snapshots.OrderBy(snapshot => snapshot.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            StateSnapshot captured = await snapshot.Capture(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            state.Add(snapshot.Name, captured ?? throw new FaultSweepExecutionException(
                $"Snapshot provider '{snapshot.Name}' returned null."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ReadOnlyDictionary<string, StateSnapshot>(state);
    }
}

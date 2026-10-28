using System.Diagnostics.CodeAnalysis;
using FaultSweep.Effects;

namespace FaultSweep.Execution;

internal sealed class EffectInterceptor : IEffectInterceptor, IDisposable
{
    private readonly AsyncLocal<EffectSession?> current = new AsyncLocal<EffectSession?>();

    // A shared environment cannot be reset by two sweeps at once.
    internal SemaphoreSlim SweepLock { get; } = new SemaphoreSlim(1, 1);

    public bool IsActive => current.Value is not null;

    [DoesNotReturn]
    public void ReportInstrumentationFailure(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        throw current.Value?.Reject(message) ?? new FaultSweepExecutionException(message);
    }

    public void Dispose() => SweepLock.Dispose();

    public void Execute(EffectDescriptor effect, Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Execute(effect, () =>
        {
            operation();
            return true;
        });
    }

    public TResult Execute<TResult>(EffectDescriptor effect, Func<TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(operation);
        EffectSession? session = current.Value;
        if (session is null)
        {
            return operation();
        }

        EffectPoint point = session.Begin(effect);
        var outcome = EffectOutcome.Failed;
        try
        {
            FaultKind? fault = session.FaultAt(point);
            if (fault == FaultKind.CrashBefore)
            {
                outcome = EffectOutcome.CrashBefore;
                throw session.Inject(point, fault.Value);
            }

            TResult result = operation();
            if (fault is FaultKind.CrashAfter or FaultKind.AppliedButFailed)
            {
                outcome = fault == FaultKind.CrashAfter ? EffectOutcome.CrashAfter : EffectOutcome.AppliedButFailed;
                throw session.Inject(point, fault.Value);
            }

            outcome = EffectOutcome.Succeeded;
            return result;
        }
        finally
        {
            session.Complete(point, outcome);
        }
    }

    public async ValueTask ExecuteAsync(EffectDescriptor effect, Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await ExecuteAsync(effect, async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TResult> ExecuteAsync<TResult>(EffectDescriptor effect,
        Func<CancellationToken, ValueTask<TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        EffectSession? session = current.Value;
        if (session is null)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        EffectPoint point = session.Begin(effect);
        var outcome = EffectOutcome.Failed;
        try
        {
            FaultKind? fault = session.FaultAt(point);
            if (fault == FaultKind.CrashBefore)
            {
                outcome = EffectOutcome.CrashBefore;
                throw session.Inject(point, fault.Value);
            }

            TResult result = await operation(cancellationToken).ConfigureAwait(false);
            if (fault is FaultKind.CrashAfter or FaultKind.AppliedButFailed)
            {
                outcome = fault == FaultKind.CrashAfter ? EffectOutcome.CrashAfter : EffectOutcome.AppliedButFailed;
                throw session.Inject(point, fault.Value);
            }

            outcome = EffectOutcome.Succeeded;
            return result;
        }
        finally
        {
            session.Complete(point, outcome);
        }
    }

    internal async ValueTask RunAsync(EffectSession session, Func<ValueTask> operation)
    {
        EffectSession? previous = current.Value;
        current.Value = session;
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            current.Value = previous;
            session.Close();
        }
    }
}

using System.Diagnostics.CodeAnalysis;

namespace FaultSweep.Effects;

/// <summary>Wraps an atomic effect. Outside a sweep the operation executes normally.</summary>
public interface IEffectInterceptor
{
    bool IsActive { get; }

    /// <summary>Aborts the sweep even if application code catches the instrumentation exception.</summary>
    [DoesNotReturn]
    void ReportInstrumentationFailure(string message);

    void Execute(EffectDescriptor effect, Action operation);

    TResult Execute<TResult>(EffectDescriptor effect, Func<TResult> operation);

    ValueTask ExecuteAsync(
        EffectDescriptor effect,
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default);

    ValueTask<TResult> ExecuteAsync<TResult>(
        EffectDescriptor effect,
        Func<CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken = default);
}

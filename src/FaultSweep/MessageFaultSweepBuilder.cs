using FaultSweep.Execution;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep;

public sealed class FaultSweepBuilder<THandler, TMessage> where THandler : class
{
    private readonly IServiceProvider services;
    private readonly TMessage message;
    private readonly List<SnapshotRegistration> snapshots = [];
    private readonly List<Action<IServiceProvider>> validations = [];
    private Func<IServiceProvider, CancellationToken, ValueTask>? reset;
    private Func<THandler, TMessage, CancellationToken, ValueTask>? handle;
    private FaultSweepOptions options = new FaultSweepOptions();

    internal FaultSweepBuilder(IServiceProvider services, TMessage message)
    {
        this.services = services;
        this.message = message;
    }

    /// <summary>Resets every tracked store and seeds the same initial data before each scenario.</summary>
    public FaultSweepBuilder<THandler, TMessage> ResetWith(
        Func<IServiceProvider, CancellationToken, ValueTask> resetAndSeed)
    {
        ArgumentNullException.ThrowIfNull(resetAndSeed);
        reset = resetAndSeed;
        return this;
    }

    public FaultSweepBuilder<THandler, TMessage> HandleWith(
        Func<THandler, TMessage, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        handle = handler;
        return this;
    }

    public FaultSweepBuilder<THandler, TMessage> WithOptions(FaultSweepOptions sweepOptions)
    {
        ArgumentNullException.ThrowIfNull(sweepOptions);
        options = sweepOptions.ValidateAndCopy();
        return this;
    }

    /// <summary>Validates adapter prerequisites before resetting state or invoking the handler.</summary>
    public FaultSweepBuilder<THandler, TMessage> ValidateWith(Action<IServiceProvider> validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        validations.Add(validation);
        return this;
    }

    public FaultSweepBuilder<THandler, TMessage> TrackState(
        string name,
        Func<IServiceProvider, CancellationToken, ValueTask<StateSnapshot>> capture,
        SnapshotComparisonOptions? comparison = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return AddSnapshot(name, capture, comparison);
    }

    /// <summary>Resolves the adapter in the snapshot's own dependency injection scope.</summary>
    public FaultSweepBuilder<THandler, TMessage> TrackSnapshot<TProvider>(
        string name, SnapshotComparisonOptions? comparison = null) where TProvider : class, ISnapshotProvider
    {
        return AddSnapshot(name,
            (provider, token) => provider.GetRequiredService<TProvider>().CaptureAsync(token), comparison);
    }

    public Task<FaultSweepResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (reset is null)
        {
            throw new InvalidOperationException("Configure ResetWith to reset and seed all tracked state.");
        }

        if (snapshots.Count == 0)
        {
            throw new InvalidOperationException("Track at least one state snapshot before running a sweep.");
        }

        Func<THandler, TMessage, CancellationToken, ValueTask> invoke = handle ?? InvokeHandlerAsync;
        if (handle is null && !typeof(IMessageHandler<TMessage>).IsAssignableFrom(typeof(THandler)))
        {
            throw new InvalidOperationException("Implement IMessageHandler<TMessage> or configure HandleWith.");
        }

        foreach (Action<IServiceProvider> validation in validations)
        {
            validation(services);
        }

        EffectInterceptor interceptor = services.GetService<EffectInterceptor>()
            ?? throw new InvalidOperationException("Register FaultSweep with services.AddFaultSweep().");
        var runner = new SweepRunner<THandler, TMessage>(
            services, interceptor, message, invoke, reset, [.. snapshots], options.ValidateAndCopy());
        return runner.RunAsync(cancellationToken);
    }

    private FaultSweepBuilder<THandler, TMessage> AddSnapshot(
        string name,
        Func<IServiceProvider, CancellationToken, ValueTask<StateSnapshot>> capture,
        SnapshotComparisonOptions? comparison)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (snapshots.Any(snapshot => string.Equals(snapshot.Name, name, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Snapshot '{name}' is already registered.", nameof(name));
        }

        snapshots.Add(new SnapshotRegistration(name, capture, new SnapshotComparer(comparison)));
        return this;
    }

    private static ValueTask InvokeHandlerAsync(THandler handler, TMessage value, CancellationToken token)
    {
        return ((IMessageHandler<TMessage>)handler).HandleAsync(value, token);
    }
}

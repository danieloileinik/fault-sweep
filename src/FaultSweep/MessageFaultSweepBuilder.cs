using FaultSweep.Execution;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using FaultSweep.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep;

public sealed class FaultSweepBuilder<THandler, TMessage>
    where THandler : class
{
    private readonly IServiceProvider services;
    private readonly TMessage message;
    private readonly List<SnapshotRegistration> snapshots = [];
    private readonly List<Action<IServiceProvider>> validations = [];
    private readonly List<InvariantRegistration<TMessage>> invariants = [];
    private readonly Dictionary<string, Func<TMessage, StateSnapshot>> expectedStates = new(
        StringComparer.Ordinal
    );
    private Func<IServiceProvider, CancellationToken, ValueTask>? reset;
    private Func<THandler, TMessage, CancellationToken, ValueTask>? handle;
    private ReplayScenario? replay;
    private DuplicateDeliveryMode? duplicateReplay;
    private MessageMinimizer<TMessage>? minimizer;
    private FaultSweepOptions options = new();

    internal FaultSweepBuilder(IServiceProvider services, TMessage message)
    {
        this.services = services;
        this.message = message;
    }

    /// <summary>Resets every tracked store and seeds the same initial data before each scenario.</summary>
    public FaultSweepBuilder<THandler, TMessage> ResetWith(
        Func<IServiceProvider, CancellationToken, ValueTask> resetAndSeed
    )
    {
        ArgumentNullException.ThrowIfNull(resetAndSeed);
        reset = resetAndSeed;
        return this;
    }

    public FaultSweepBuilder<THandler, TMessage> HandleWith(
        Func<THandler, TMessage, CancellationToken, ValueTask> handler
    )
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
        SnapshotComparisonOptions? comparison = null
    )
    {
        ArgumentNullException.ThrowIfNull(capture);
        return AddSnapshot(name, capture, comparison);
    }

    /// <summary>Resolves the adapter in the snapshot's own dependency injection scope.</summary>
    public FaultSweepBuilder<THandler, TMessage> TrackSnapshot<TProvider>(
        string name,
        SnapshotComparisonOptions? comparison = null
    )
        where TProvider : class, ISnapshotProvider
    {
        return AddSnapshot(
            name,
            (provider, token) => provider.GetRequiredService<TProvider>().CaptureAsync(token),
            comparison
        );
    }

    /// <summary>Replaces the clean-run oracle for a tracked snapshot. Also verifies the clean run itself.</summary>
    public FaultSweepBuilder<THandler, TMessage> ExpectState(string name, StateSnapshot expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return ExpectState(name, _ => expected);
    }

    /// <summary>Derives expected state from the current message, including during message minimization.</summary>
    public FaultSweepBuilder<THandler, TMessage> ExpectState(
        string name,
        Func<TMessage, StateSnapshot> expected
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(expected);
        if (!expectedStates.TryAdd(name, expected))
        {
            throw new ArgumentException(
                $"Expected state for '{name}' is already registered.",
                nameof(name)
            );
        }

        return this;
    }

    /// <summary>Checks a read-only business rule after the clean run and every fault scenario.</summary>
    public FaultSweepBuilder<THandler, TMessage> CheckInvariant(
        string name,
        Func<VerificationContext<TMessage>, bool> condition,
        string? failureMessage = null
    )
    {
        ArgumentNullException.ThrowIfNull(condition);
        return CheckInvariantAsync(
            name,
            (context, _) => ValueTask.FromResult(condition(context)),
            failureMessage
        );
    }

    /// <summary>Supports read-only asynchronous queries through a separate verification scope.</summary>
    public FaultSweepBuilder<THandler, TMessage> CheckInvariantAsync(
        string name,
        Func<VerificationContext<TMessage>, CancellationToken, ValueTask<bool>> condition,
        string? failureMessage = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(condition);
        if (
            invariants.Any(invariant =>
                string.Equals(invariant.Name, name, StringComparison.Ordinal)
            )
        )
        {
            throw new ArgumentException($"Invariant '{name}' is already registered.", nameof(name));
        }

        invariants.Add(
            new InvariantRegistration<TMessage>(
                name,
                condition,
                failureMessage ?? "The invariant returned false."
            )
        );
        return this;
    }

    public FaultSweepBuilder<THandler, TMessage> ReplayScenario(ReplayScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentOutOfRangeException.ThrowIfLessThan(scenario.EffectIndex, 1);
        if (!Enum.IsDefined(scenario.Kind) || scenario.MaxRedeliveryAttempts is < 1)
        {
            throw new ArgumentException("Invalid replay scenario.", nameof(scenario));
        }

        replay = scenario;
        duplicateReplay = null;
        return this;
    }

    public FaultSweepBuilder<THandler, TMessage> WithDuplicateDeliveries()
    {
        options = options with
        {
            DuplicateDeliveries =
            [
                DuplicateDeliveryMode.Sequential,
                DuplicateDeliveryMode.Parallel,
            ],
        };
        return this;
    }

    public FaultSweepBuilder<THandler, TMessage> ReplayDuplicates(DuplicateDeliveryMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        duplicateReplay = mode;
        replay = null;
        return this;
    }

    /// <summary>Explores strictly smaller messages according to a deterministic, nonnegative complexity measure.</summary>
    public FaultSweepBuilder<THandler, TMessage> MinimizeMessageWith(
        Func<TMessage, IEnumerable<TMessage>> shrink,
        Func<TMessage, long> complexity
    )
    {
        ArgumentNullException.ThrowIfNull(shrink);
        ArgumentNullException.ThrowIfNull(complexity);
        minimizer = new MessageMinimizer<TMessage>(shrink, complexity);
        return this;
    }

    public Task<FaultSweepResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (reset is null)
        {
            throw new InvalidOperationException(
                "Configure ResetWith to reset and seed all tracked state."
            );
        }

        if (snapshots.Count == 0 && (options.CompareWithBaseline || invariants.Count == 0))
        {
            throw new InvalidOperationException(
                "Track at least one state snapshot before running a sweep."
            );
        }

        if (!options.CompareWithBaseline && expectedStates.Count == 0 && invariants.Count == 0)
        {
            throw new InvalidOperationException(
                "Configure expected state or an invariant when baseline comparison is disabled."
            );
        }

        foreach (var name in expectedStates.Keys.Where(name => !snapshots.Any(snapshot =>
                     string.Equals(snapshot.Name, name, StringComparison.Ordinal)
                 )))
        {
            throw new InvalidOperationException(
                $"Expected state '{name}' requires a tracked snapshot with the same name."
            );
        }

        Func<THandler, TMessage, CancellationToken, ValueTask> invoke =
            handle ?? InvokeHandlerAsync;
        if (handle is null && !typeof(IMessageHandler<TMessage>).IsAssignableFrom(typeof(THandler)))
        {
            throw new InvalidOperationException(
                "Implement IMessageHandler<TMessage> or configure HandleWith."
            );
        }

        foreach (Action<IServiceProvider> validation in validations)
        {
            validation(services);
        }

        EffectInterceptor interceptor =
            services.GetService<EffectInterceptor>()
            ?? throw new InvalidOperationException(
                "Register FaultSweep with services.AddFaultSweep()."
            );
        var runner = new SweepRunner<THandler, TMessage>(
            services,
            interceptor,
            message,
            invoke,
            reset,
            [.. snapshots],
            [.. invariants],
            new Dictionary<string, Func<TMessage, StateSnapshot>>(
                expectedStates,
                StringComparer.Ordinal
            ),
            options.ValidateAndCopy(),
            replay,
            minimizer,
            duplicateReplay
        );
        return runner.RunAsync(cancellationToken);
    }

    private FaultSweepBuilder<THandler, TMessage> AddSnapshot(
        string name,
        Func<IServiceProvider, CancellationToken, ValueTask<StateSnapshot>> capture,
        SnapshotComparisonOptions? comparison
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (snapshots.Any(snapshot => string.Equals(snapshot.Name, name, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Snapshot '{name}' is already registered.", nameof(name));
        }

        snapshots.Add(new SnapshotRegistration(name, capture, new SnapshotComparer(comparison)));
        return this;
    }

    private static ValueTask InvokeHandlerAsync(
        THandler handler,
        TMessage value,
        CancellationToken token
    )
    {
        return ((IMessageHandler<TMessage>)handler).HandleAsync(value, token);
    }
}

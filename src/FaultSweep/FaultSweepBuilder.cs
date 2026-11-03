using FaultSweep.Effects;
using FaultSweep.Execution;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using FaultSweep.Verification;

namespace FaultSweep;

public sealed class FaultSweepBuilder<THandler>
    where THandler : class
{
    private readonly IServiceProvider services;
    private readonly List<EnvironmentRegistration> environments = [];
    private readonly List<(
        string Name,
        Func<VerificationContext, CancellationToken, ValueTask<bool>> Check,
        string Failure
    )> invariants = [];
    private FaultSweepOptions options = new()
    {
        DuplicateDeliveries = [DuplicateDeliveryMode.Sequential, DuplicateDeliveryMode.Parallel],
    };

    internal FaultSweepBuilder(IServiceProvider services)
    {
        this.services = services;
    }

    /// <summary>Reuses the same message on every delivery. Prefer an immutable message.</summary>
    public FaultSweepBuilder<THandler, TMessage> WithMessage<TMessage>(TMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (environments.Count > 0 || invariants.Count > 0)
        {
            throw new InvalidOperationException(
                "Use AssertIdempotentAsync(message) or CheckIdempotencyAsync(message) with automatic stores and invariants."
            );
        }

        return new FaultSweepBuilder<THandler, TMessage>(services, message);
    }

    /// <summary>Configures an automatically restored store without changing application registrations.</summary>
    public FaultSweepBuilder<THandler> WithEnvironment(
        string name,
        IEffectInstrumentation instrumentation,
        Func<CancellationToken, ValueTask<ISweepEnvironment>> create
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(instrumentation);
        ArgumentNullException.ThrowIfNull(create);
        if (environments.Any(environment => environment.Name == name))
        {
            throw new ArgumentException(
                $"Environment '{name}' is already registered.",
                nameof(name)
            );
        }

        environments.Add(new EnvironmentRegistration(name, instrumentation, create));
        return this;
    }

    public FaultSweepBuilder<THandler> WithOptions(FaultSweepOptions sweepOptions)
    {
        ArgumentNullException.ThrowIfNull(sweepOptions);
        options = sweepOptions.ValidateAndCopy();
        return this;
    }

    public FaultSweepBuilder<THandler> CheckInvariant(
        string name,
        Func<VerificationContext, bool> condition,
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

    public FaultSweepBuilder<THandler> CheckInvariantAsync(
        string name,
        Func<VerificationContext, CancellationToken, ValueTask<bool>> condition,
        string? failureMessage = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(condition);
        if (invariants.Any(invariant => invariant.Name == name))
        {
            throw new ArgumentException($"Invariant '{name}' is already registered.", nameof(name));
        }

        invariants.Add((name, condition, failureMessage ?? "The invariant returned false."));
        return this;
    }

    public async Task AssertIdempotentAsync<TMessage>(
        TMessage message,
        CancellationToken cancellationToken = default
    )
    {
        FaultSweepResult result = await CheckIdempotencyAsync(message, cancellationToken)
            .ConfigureAwait(false);
        result.ShouldHaveNoViolations();
    }

    /// <summary>Checks faults and duplicate deliveries, then restores the original state, including on failure.</summary>
    public Task<FaultSweepResult> CheckIdempotencyAsync<TMessage>(
        TMessage message,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(message);
        if (environments.Count == 0)
        {
            throw new InvalidOperationException(
                "Configure a store, for example WithPostgres(connectionString)."
            );
        }

        if (!options.CompareWithBaseline && invariants.Count == 0)
        {
            throw new InvalidOperationException(
                "Configure an invariant when baseline comparison is disabled."
            );
        }

        Func<IServiceProvider, TMessage, CancellationToken, ValueTask>? dispatch =
            MediatRInvocation<THandler, TMessage>.Create();
        Func<THandler, TMessage, CancellationToken, ValueTask>? invoke = dispatch is null
            ? HandlerInvocation<THandler, TMessage>.Create()
            : null;
        EnvironmentRegistration[] registrations = [.. environments];
        List<ISweepEnvironment> prepared = [];
        EffectInterceptor interceptor = SweepInterceptors.For(services);
        var runner = new SweepRunner<THandler, TMessage>(
            services,
            interceptor,
            message,
            invoke,
            async (_, token) =>
            {
                if (prepared.Count == 0)
                {
                    foreach (EnvironmentRegistration registration in registrations)
                    {
                        ISweepEnvironment environment = await registration
                            .Create(token)
                            .ConfigureAwait(false);
                        prepared.Add(environment);
                    }
                }

                foreach (ISweepEnvironment environment in prepared)
                {
                    await environment.ResetAsync(token).ConfigureAwait(false);
                }
            },
            registrations
                .Select(
                    (registration, index) =>
                        new SnapshotRegistration(
                            registration.Name,
                            (_, token) => prepared[index].CaptureAsync(token),
                            new SnapshotComparer(null)
                        )
                )
                .ToArray(),
            invariants
                .Select(invariant => new InvariantRegistration<TMessage>(
                    invariant.Name,
                    (context, token) => invariant.Check(context, token),
                    invariant.Failure
                ))
                .ToArray(),
            new Dictionary<string, Func<TMessage, StateSnapshot>>(StringComparer.Ordinal),
            options.ValidateAndCopy(),
            createHandler: true,
            additionalInstrumentation: registrations
                .Select(registration => registration.Instrumentation)
                .ToArray(),
            dispatch: dispatch
        );

        return runner.RunAsync(
            cancellationToken,
            async () =>
            {
                List<Exception> failures = [];
                foreach (ISweepEnvironment environment in prepared.AsEnumerable().Reverse())
                {
                    try
                    {
                        await environment.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        failures.Add(exception);
                    }
                }

                if (failures.Count > 0)
                {
                    throw new AggregateException(
                        "Failed to restore the initial store state.",
                        failures
                    );
                }
            }
        );
    }
}

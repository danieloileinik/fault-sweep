using FaultSweep.Effects;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Tests;

public sealed class InstrumentationLifetimeTests
{
    [Fact]
    public async Task Releases_all_scopes_once_in_reverse_order_when_disposal_fails()
    {
        // Arrange
        List<int> released = [];
        var firstFailure = new InvalidOperationException("second scope");
        var secondFailure = new InvalidOperationException("third scope");
        var state = new TestState();
        using ServiceProvider services = CreateServices(
            state,
            new CallbackInstrumentation(_ => new CallbackDisposable(() => released.Add(1))),
            new CallbackInstrumentation(_ => new CallbackDisposable(() =>
            {
                released.Add(2);
                throw firstFailure;
            })),
            new CallbackInstrumentation(_ => new CallbackDisposable(() =>
            {
                released.Add(3);
                throw secondFailure;
            }))
        );

        // Act
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() =>
            CreateSweep(services, state).RunAsync()
        );

        // Assert
        Assert.Equal([3, 2, 1], released);
        Assert.Equal([secondFailure, firstFailure], error.InnerExceptions);
    }

    [Fact]
    public async Task Preserves_activation_failure_and_releases_every_prepared_scope()
    {
        // Arrange
        List<int> released = [];
        var activationFailure = new InvalidOperationException("activation");
        var disposalFailure = new InvalidOperationException("disposal");
        var state = new TestState();
        using ServiceProvider services = CreateServices(
            state,
            new CallbackInstrumentation(_ => new CallbackDisposable(() => released.Add(1))),
            new CallbackInstrumentation(_ => new CallbackDisposable(() =>
            {
                released.Add(2);
                throw disposalFailure;
            })),
            new CallbackInstrumentation(_ => throw activationFailure),
            new CallbackInstrumentation(_ =>
                throw new InvalidOperationException("must not activate")
            )
        );

        // Act
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() =>
            CreateSweep(services, state).RunAsync()
        );

        // Assert
        Assert.Equal([2, 1], released);
        Assert.Equal([activationFailure, disposalFailure], error.InnerExceptions);
        Assert.Equal(0, state.Resets);
    }

    [Fact]
    public async Task Rethrows_original_activation_failure_when_cleanup_succeeds()
    {
        // Arrange
        int releases = 0;
        var failure = new InvalidOperationException("activation");
        var state = new TestState();
        using ServiceProvider services = CreateServices(
            state,
            new CallbackInstrumentation(_ => new CallbackDisposable(() => releases++)),
            new CallbackInstrumentation(_ => throw failure)
        );

        // Act
        Exception? error = await Record.ExceptionAsync(() =>
            CreateSweep(services, state).RunAsync()
        );

        // Assert
        Assert.Same(failure, error);
        Assert.Equal(1, releases);
        Assert.Equal(0, state.Resets);
    }

    [Fact]
    public async Task Preserves_execution_failure_when_disposal_also_fails()
    {
        // Arrange
        var executionFailure = new InvalidOperationException("handler");
        var disposalFailure = new InvalidOperationException("disposal");
        var state = new TestState();
        using ServiceProvider services = CreateServices(
            state,
            new CallbackInstrumentation(_ => new CallbackDisposable(() => throw disposalFailure))
        );
        FaultSweepBuilder<ConventionHandler, TestMessage> builder = CreateSweep(services, state)
            .HandleWith((_, _, _) => throw executionFailure);

        // Act
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() =>
            builder.RunAsync()
        );

        // Assert
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Same(
            executionFailure,
            Assert.IsType<FaultSweepExecutionException>(error.InnerExceptions[0]).InnerException
        );
        Assert.Same(disposalFailure, error.InnerExceptions[1]);
    }

    private static ServiceProvider CreateServices(
        TestState state,
        params IEffectInstrumentation[] instrumentations
    )
    {
        var services = new ServiceCollection();
        services.AddFaultSweep();
        services.AddSingleton(state);
        services.AddScoped<ConventionHandler>();
        foreach (IEffectInstrumentation instrumentation in instrumentations)
        {
            services.AddSingleton(instrumentation);
        }

        return services.BuildServiceProvider();
    }

    private static FaultSweepBuilder<ConventionHandler, TestMessage> CreateSweep(
        IServiceProvider services,
        TestState state
    ) =>
        FaultSweep
            .For<ConventionHandler>(services)
            .WithMessage(new TestMessage())
            .ResetWith(
                (_, _) =>
                {
                    state.Reset();
                    return ValueTask.CompletedTask;
                }
            )
            .TrackState("state", (_, _) => ValueTask.FromResult(state.Snapshot()));
}

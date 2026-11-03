using FaultSweep.Effects;
using FaultSweep.Reporting;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MediatRSweepTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Runs_validation_transaction_and_save_changes_for_every_delivery(
        bool idempotent
    )
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await database.ResetAsync(default);
        var probe = new MediatRPipelineProbe();
        await using ServiceProvider services = CreateServices(database, probe);
        using var cancellation = new CancellationTokenSource();

        // Act
        FaultSweepResult result = await FaultSweep
            .For<MediatRSweepHandler>(services)
            .WithPostgres(database.ConnectionString)
            .CheckIdempotencyAsync(new MediatRSweepCommand(idempotent), cancellation.Token);

        // Assert
        Assert.Equal(!idempotent, result.HasViolations);
        Assert.Contains(result.BaselineTrace, effect => effect.Point.Effect.Operation == "Commit");
        Assert.Equal(
            10,
            result
                .BaselineState["postgres"]
                .Value.GetProperty("counters")[0]
                .GetProperty("value")
                .GetInt32()
        );
        int deliveries =
            1
            + result.Scenarios.Sum(scenario => scenario.Attempts.Count)
            + result.DuplicateScenarios.Sum(scenario => scenario.Attempts.Count);
        Assert.Equal(deliveries, probe.Deliveries.Count);
        Assert.Contains(
            probe.Deliveries.Values,
            stages => stages.SequenceEqual(["validate", "transaction", "handler", "save", "commit"])
        );
        Assert.All(
            probe.Deliveries,
            delivery =>
            {
                Assert.Equal("validate", delivery.Value.First());
                Assert.Throws<ObjectDisposedException>(() => delivery.Key.Counters);
            }
        );
        Assert.All(probe.Tokens, token => Assert.Equal(cancellation.Token, token));
        Assert.Equal(
            0,
            (await database.CaptureAsync(default)).Value[0].GetProperty("value").GetInt32()
        );
    }

    [Fact]
    public async Task Supports_requests_without_response_and_invariants()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await database.ResetAsync(default);
        var probe = new MediatRPipelineProbe();
        await using ServiceProvider services = CreateServices(database, probe);

        // Act
        await FaultSweep
            .For<MediatRVoidHandler>(services)
            .WithPostgres(database.ConnectionString)
            .CheckInvariant(
                "saved through pipeline",
                context =>
                    context
                        .State["postgres"]
                        .Value.GetProperty("counters")[0]
                        .GetProperty("value")
                        .GetInt32() == 10
            )
            .AssertIdempotentAsync(new MediatRVoidCommand());

        // Assert
        Assert.Contains(probe.Deliveries.Values, stages => stages.Last() == "commit");
        Assert.Equal(
            0,
            (await database.CaptureAsync(default)).Value[0].GetProperty("value").GetInt32()
        );
    }

    [Fact]
    public async Task Validation_failure_prevents_handler_execution_and_preserves_seed()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await database.ResetAsync(default);
        var probe = new MediatRPipelineProbe();
        await using ServiceProvider services = CreateServices(database, probe);

        // Act
        FaultSweepExecutionException error = await Assert.ThrowsAsync<FaultSweepExecutionException>(
            () =>
                FaultSweep
                    .For<MediatRSweepHandler>(services)
                    .WithPostgres(database.ConnectionString)
                    .AssertIdempotentAsync(new MediatRSweepCommand(Amount: 0))
        );

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(error.InnerException);
        Assert.Equal(["validate"], Assert.Single(probe.Deliveries).Value.ToArray());
        Assert.Equal(
            0,
            (await database.CaptureAsync(default)).Value[0].GetProperty("value").GetInt32()
        );
    }

    [Fact]
    public async Task Detailed_builder_dispatches_replays_through_the_same_pipeline()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        var probe = new MediatRPipelineProbe();
        await using ServiceProvider services = CreateServices(database, probe, instrument: true);

        // Act
        var sweep = FaultSweep
            .For<IRequestHandler<MediatRSweepCommand, int>>(services)
            .WithMessage(new MediatRSweepCommand())
            .ResetWith((_, token) => database.ResetAsync(token))
            .TrackPostgres(database.ConnectionString, ["counters"]);
        FaultSweepResult discovery = await sweep.RunAsync();
        int commitIndex = discovery
            .BaselineTrace.Single(effect => effect.Point.Effect.Operation == "Commit")
            .Point.Index;
        FaultSweepResult result = await sweep
            .ReplayScenario(new ReplayScenario(commitIndex, FaultKind.CrashAfter))
            .RunAsync();

        // Assert
        result.ShouldHaveNoViolations();
        Assert.Single(result.Scenarios);
        Assert.Equal("Commit", result.Scenarios[0].Scenario.Point.Effect.Operation);
        Assert.All(probe.Deliveries.Values, stages => Assert.Equal("validate", stages.First()));
    }

    [Fact]
    public async Task Missing_sender_fails_without_falling_back_to_a_direct_handler_call()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await database.ResetAsync(default);
        var services = new ServiceCollection();
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        FaultSweepExecutionException error = await Assert.ThrowsAsync<FaultSweepExecutionException>(
            () =>
                FaultSweep
                    .For<MediatRSweepHandler>(provider)
                    .WithPostgres(database.ConnectionString)
                    .AssertIdempotentAsync(new MediatRSweepCommand())
        );

        // Assert
        Assert.Contains(
            "MediatR.ISender",
            Assert.IsType<InvalidOperationException>(error.InnerException).Message
        );
        Assert.Equal(
            0,
            (await database.CaptureAsync(default)).Value[0].GetProperty("value").GetInt32()
        );
    }

    private static ServiceProvider CreateServices(
        TestDatabase database,
        MediatRPipelineProbe probe,
        bool instrument = false
    )
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(probe);
        services.AddDbContext<CounterContext>(options =>
            options.UseNpgsql(database.ConnectionString)
        );
        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssemblyContaining<MediatRSweepHandler>();
            configuration.AddOpenBehavior(typeof(MediatRValidationBehavior<,>));
            configuration.AddOpenBehavior(typeof(MediatRTransactionBehavior<,>));
        });
        if (instrument)
        {
            services.AddFaultSweepPostgres();
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

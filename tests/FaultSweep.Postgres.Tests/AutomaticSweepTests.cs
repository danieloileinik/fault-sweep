using FaultSweep.Effects;
using FaultSweep.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FaultSweep.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AutomaticSweepTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Short_API_discovers_handler_and_tables_and_restores_seed_and_sequences(
        bool idempotent
    )
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await InitializeAsync(database);
        var services = new ServiceCollection();
        services.AddSingleton(database);
        await using ServiceProvider provider = services.BuildServiceProvider();
        int invariantCalls = 0;
        var sweep = FaultSweep
            .For<AutomaticOrderHandler>(provider)
            .WithPostgres(database.ConnectionString, ignore: ["outbox.id", "outbox.created_at"])
            .CheckInvariant(
                "seed survives",
                context =>
                {
                    invariantCalls++;
                    var counters = context.State["postgres"].Value.GetProperty("counters");
                    return counters.GetArrayLength() == 2
                        && counters[1].GetProperty("value").GetInt32() == 7;
                }
            );

        // Act
        FaultSweepResult result = await sweep.CheckIdempotencyAsync(new AutomaticOrder(idempotent));

        // Assert
        Assert.Equal(!idempotent, result.HasViolations);
        Assert.Equal(result.BaselineTrace.Count * 3, result.Scenarios.Count);
        Assert.Equal(2, result.DuplicateScenarios.Count);
        Assert.Equal(result.Scenarios.Count + result.DuplicateScenarios.Count + 1, invariantCalls);
        Assert.False(result.BaselineVerification.HasViolations, result.ToString());
        Assert.Equal(
            42,
            result
                .BaselineState["postgres"]
                .Value.GetProperty("outbox")[0]
                .GetProperty("message_id")
                .GetInt32()
        );
        await AssertRestoredAsync(database);
        if (idempotent)
        {
            result.ShouldHaveNoViolations();
        }
        else
        {
            Assert.Contains(
                result.Violations,
                scenario => scenario.Scenario.Kind == FaultKind.AppliedButFailed
            );
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Assertion_works_without_any_additional_library_setup(bool idempotent)
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await InitializeAsync(database);
        var services = new ServiceCollection();
        services.AddSingleton(database);
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        Exception? error = await Record.ExceptionAsync(() =>
            FaultSweep
                .For<AutomaticOrderHandler>(provider)
                .WithPostgres(database.ConnectionString, ignore: ["outbox.id", "outbox.created_at"])
                .AssertIdempotentAsync(new AutomaticOrder(idempotent))
        );

        // Assert
        if (idempotent)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.IsType<FaultSweepAssertionException>(error);
        }

        await AssertRestoredAsync(database);
    }

    [Fact]
    public async Task Optional_async_invariant_checks_the_baseline_and_failed_scenarios()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await InitializeAsync(database);
        var services = new ServiceCollection();
        services.AddSingleton(database);
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        FaultSweepAssertionException error = await Assert.ThrowsAsync<FaultSweepAssertionException>(
            () =>
                FaultSweep
                    .For<AutomaticOrderHandler>(provider)
                    .WithPostgres(
                        database.ConnectionString,
                        ignore: ["outbox.id", "outbox.created_at"]
                    )
                    .CheckInvariantAsync(
                        "business rule",
                        async (context, token) =>
                        {
                            await Task.Yield();
                            token.ThrowIfCancellationRequested();
                            Assert.Same(
                                database,
                                context.Services.GetRequiredService<TestDatabase>()
                            );
                            return false;
                        }
                    )
                    .AssertIdempotentAsync(new AutomaticOrder())
        );

        // Assert
        Assert.Contains("business rule", error.Message);
        await AssertRestoredAsync(database);
    }

    [Fact]
    public async Task Baseline_failure_and_cancellation_restore_original_data()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await InitializeAsync(database);
        var services = new ServiceCollection();
        services.AddSingleton(database);
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();

        // Act
        await Assert.ThrowsAsync<FaultSweepExecutionException>(() =>
            FaultSweep
                .For<AutomaticOrderHandler>(provider)
                .WithPostgres(database.ConnectionString, ignore: ["outbox.id", "outbox.created_at"])
                .AssertIdempotentAsync(new AutomaticOrder(Fail: true))
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FaultSweep
                .For<AutomaticOrderHandler>(provider)
                .WithPostgres(database.ConnectionString, ignore: ["outbox.id", "outbox.created_at"])
                .CheckInvariant(
                    "cancel",
                    _ =>
                    {
                        cancellation.Cancel();
                        return true;
                    }
                )
                .AssertIdempotentAsync(new AutomaticOrder(), cancellation.Token)
        );

        // Assert
        await AssertRestoredAsync(database);
    }

    [Fact]
    public async Task Ignore_is_scoped_to_one_table_and_does_not_hide_another_tables_values()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await InitializeAsync(database);
        await database.ExecuteAsync(
            "ALTER TABLE counters ADD COLUMN created_at timestamptz DEFAULT clock_timestamp()"
        );
        var services = new ServiceCollection();
        services.AddSingleton(database);
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        FaultSweepResult result = await FaultSweep
            .For<AutomaticOrderHandler>(provider)
            .WithPostgres(database.ConnectionString, ignore: ["outbox.id", "outbox.created_at"])
            .CheckIdempotencyAsync(new AutomaticOrder());

        // Assert
        result.ShouldHaveNoViolations();
        Assert.True(
            result
                .BaselineState["postgres"]
                .Value.GetProperty("counters")[0]
                .TryGetProperty("created_at", out _)
        );
        Assert.False(
            result
                .BaselineState["postgres"]
                .Value.GetProperty("outbox")[0]
                .TryGetProperty("created_at", out _)
        );
    }

    [Fact]
    public async Task Rejects_unknown_ignored_columns_before_running_the_handler()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await InitializeAsync(database);
        var services = new ServiceCollection();
        services.AddSingleton(database);
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            FaultSweep
                .For<AutomaticOrderHandler>(provider)
                .WithPostgres(database.ConnectionString, ignore: ["outbox.typo"])
                .AssertIdempotentAsync(new AutomaticOrder())
        );

        // Assert
        Assert.Contains("outbox.typo", error.Message);
        await AssertRestoredAsync(database);
    }

    [Theory]
    [InlineData("public", "counters")]
    [InlineData("other", "detail")]
    public async Task Rejects_table_inheritance_before_resetting_either_schema(
        string schema,
        string table
    )
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await database.ExecuteAsync(
            """
            CREATE SCHEMA other;
            CREATE TABLE other.detail (note text, PRIMARY KEY (id)) INHERITS (public.counters);
            INSERT INTO public.counters VALUES (1, 5);
            INSERT INTO other.detail VALUES (2, 7, 'original');
            """
        );
        var services = new ServiceCollection();
        services.AddSingleton(database);
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        var error = await Assert.ThrowsAsync<FaultSweepExecutionException>(() =>
            FaultSweep
                .For<AutomaticOrderHandler>(provider)
                .WithPostgres(database.ConnectionString, schema: schema)
                .AssertIdempotentAsync(new AutomaticOrder())
        );

        // Assert
        Assert.Contains("INHERITS", error.Message);
        Assert.Contains($"{schema}.{table}", error.Message);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM ONLY public.counters),
                (SELECT value FROM ONLY public.counters WHERE id = 1),
                (SELECT note FROM ONLY other.detail WHERE id = 2 AND value = 7)
            """,
            connection
        );
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(5, reader.GetInt32(1));
        Assert.Equal("original", reader.GetString(2));
    }

    [Fact]
    public async Task Restores_seed_rows_in_declarative_partitions()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await InitializeAsync(database);
        await database.ExecuteAsync(
            """
            CREATE TABLE archive (id int PRIMARY KEY) PARTITION BY LIST (id);
            CREATE TABLE archive_one PARTITION OF archive FOR VALUES IN (1);
            INSERT INTO archive VALUES (1);
            """
        );
        var services = new ServiceCollection();
        services.AddSingleton(database);
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        await FaultSweep
            .For<AutomaticOrderHandler>(provider)
            .WithPostgres(database.ConnectionString, ignore: ["outbox.id", "outbox.created_at"])
            .AssertIdempotentAsync(new AutomaticOrder());

        // Assert
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id FROM ONLY archive_one", connection);
        Assert.Equal(1, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Supports_ValueTask_without_token_and_disposes_registered_and_created_handlers(
        bool registered
    )
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await InitializeAsync(database);
        var lifetime = new AutomaticHandlerLifetime();
        var services = new ServiceCollection();
        services.AddSingleton(database);
        services.AddSingleton(lifetime);
        if (registered)
        {
            services.AddScoped<AutomaticValueTaskHandler>();
        }

        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        FaultSweepResult result = await FaultSweep
            .For<AutomaticValueTaskHandler>(provider)
            .WithPostgres(database.ConnectionString, ignore: ["outbox.id", "outbox.created_at"])
            .CheckIdempotencyAsync(new AutomaticOrder());

        // Assert
        result.ShouldHaveNoViolations();
        EffectPoint disposal = result.BaselineTrace[^1].Point;
        Assert.Equal("UPDATE counters SET value = 11 WHERE id = 2", disposal.Effect.Target);
        Assert.Equal(
            11,
            result
                .BaselineState["postgres"]
                .Value.GetProperty("counters")[1]
                .GetProperty("value")
                .GetInt32()
        );
        Assert.Equal(3, result.Scenarios.Count(scenario => scenario.Scenario.Point == disposal));
        Assert.True(lifetime.Created > 1);
        Assert.Equal(lifetime.Created, lifetime.Disposed);
        await AssertRestoredAsync(database);
    }

    [Fact]
    public async Task Restores_ignored_seed_values_without_firing_application_triggers()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await InitializeAsync(database);
        await database.ExecuteAsync(
            """
            INSERT INTO outbox(message_id, created_at, payload) VALUES (99, '2020-01-01T00:00:00Z', '{"seed":true}');
            CREATE FUNCTION bump_seed() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN NEW.value := NEW.value + 100; RETURN NEW; END; $$;
            CREATE TRIGGER bump_seed BEFORE INSERT ON counters FOR EACH ROW EXECUTE FUNCTION bump_seed();
            """
        );
        var snapshots = new PostgresSnapshotProvider(
            () => new NpgsqlConnection(database.ConnectionString),
            new PostgresSnapshotOptions { Tables = ["counters", "a_child", "outbox"] }
        );
        string before = (await snapshots.CaptureAsync(default)).ToString();
        var services = new ServiceCollection();
        services.AddSingleton(database);
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        await FaultSweep
            .For<AutomaticOrderHandler>(provider)
            .WithPostgres(database.ConnectionString, ignore: ["outbox.id", "outbox.created_at"])
            .AssertIdempotentAsync(new AutomaticOrder());

        // Assert
        Assert.Equal(before, (await snapshots.CaptureAsync(default)).ToString());
        await database.ExecuteAsync("INSERT INTO counters VALUES (3, 0)");
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT value FROM counters WHERE id = 3",
            connection
        );
        Assert.Equal(100, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Serializes_automatic_sweeps_including_backup_and_cleanup()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await InitializeAsync(database);
        var services = new ServiceCollection();
        services.AddSingleton(database);
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    FaultSweep
                        .For<AutomaticOrderHandler>(provider)
                        .WithPostgres(
                            database.ConnectionString,
                            ignore: ["outbox.id", "outbox.created_at"]
                        )
                        .AssertIdempotentAsync(new AutomaticOrder())
                )
        );

        // Assert
        await AssertRestoredAsync(database);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handles_deferrable_foreign_key_cycles_and_rejects_nondeferrable_cycles(
        bool deferrable
    )
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await InitializeAsync(database);
        string constraint = deferrable ? "DEFERRABLE INITIALLY DEFERRED" : "NOT DEFERRABLE";
        await database.ExecuteAsync(
            $"""
            CREATE TABLE cycle_a(id int PRIMARY KEY, parent_id int);
            CREATE TABLE cycle_b(id int PRIMARY KEY, parent_id int REFERENCES cycle_a(id) {constraint});
            ALTER TABLE cycle_a ADD FOREIGN KEY(parent_id) REFERENCES cycle_b(id) {constraint};
            INSERT INTO cycle_a VALUES(1, NULL);
            INSERT INTO cycle_b VALUES(1, 1);
            UPDATE cycle_a SET parent_id = 1;
            """
        );
        var services = new ServiceCollection();
        services.AddSingleton(database);
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        Exception? error = await Record.ExceptionAsync(() =>
            FaultSweep
                .For<AutomaticOrderHandler>(provider)
                .WithPostgres(database.ConnectionString, ignore: ["outbox.id", "outbox.created_at"])
                .AssertIdempotentAsync(new AutomaticOrder())
        );

        // Assert
        if (deferrable)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.Contains(
                "DEFERRABLE",
                Assert.IsType<FaultSweepExecutionException>(error).Message
            );
        }

        await AssertRestoredAsync(database);
    }

    private static Task InitializeAsync(TestDatabase database) =>
        database.ExecuteAsync(
            """
            INSERT INTO counters VALUES (1, 5), (2, 7);
            CREATE TABLE outbox (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                message_id integer UNIQUE NOT NULL,
                created_at timestamptz NOT NULL,
                payload jsonb NOT NULL);
            CREATE TABLE a_child (id integer PRIMARY KEY, counter_id integer REFERENCES counters(id), value integer GENERATED ALWAYS AS (counter_id * 2) STORED);
            INSERT INTO a_child(id, counter_id) VALUES (1, 1);
            """
        );

    private static async Task AssertRestoredAsync(TestDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT value FROM counters WHERE id = 1),
                (SELECT count(*) FROM outbox), (SELECT value FROM a_child WHERE id = 1),
                (SELECT last_value FROM outbox_id_seq), (SELECT is_called FROM outbox_id_seq)
            """,
            connection
        );
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(5, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.Equal(1, reader.GetInt64(3));
        Assert.False(reader.GetBoolean(4));
    }
}

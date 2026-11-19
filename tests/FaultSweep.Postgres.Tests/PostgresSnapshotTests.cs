using System.Text.Json;
using FaultSweep.Effects;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using Npgsql;

namespace FaultSweep.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresSnapshotTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Captures_amount_changes_when_a_column_matches_the_row_alias()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await database.ExecuteAsync("""
            CREATE TABLE payments (id int PRIMARY KEY, captured_row jsonb, amount int);
            INSERT INTO payments VALUES (1, '{"tag":1}', 10);
            """);
        var provider = new PostgresSnapshotProvider(
            () => new NpgsqlConnection(database.ConnectionString),
            new PostgresSnapshotOptions { Tables = ["payments"] }
        );

        // Act
        StateSnapshot before = await provider.CaptureAsync(default);
        await database.ExecuteAsync("UPDATE payments SET amount = 20 WHERE id = 1");
        StateSnapshot after = await provider.CaptureAsync(default);

        // Assert
        JsonElement row = after.Value.GetProperty("payments")[0];
        Assert.Equal(10, before.Value.GetProperty("payments")[0].GetProperty("amount").GetInt32());
        Assert.Equal(20, row.GetProperty("amount").GetInt32());
        Assert.Equal(1, row.GetProperty("captured_row").GetProperty("tag").GetInt32());
    }

    [Fact]
    public async Task Orders_rows_by_composite_primary_key_and_removes_only_selected_columns()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await database.ExecuteAsync("""
            CREATE TABLE records (part integer, id integer, created_at timestamptz, payload jsonb, PRIMARY KEY(part, id));
            INSERT INTO records VALUES (2, 1, now(), '{"id":123}'), (1, 2, now(), '{"id":456}'), (1, 1, now(), '{"id":789}');
            """);
        var provider = new PostgresSnapshotProvider(() => new NpgsqlConnection(database.ConnectionString),
            new PostgresSnapshotOptions { Tables = ["records"], IgnoredColumns = ["created_at"] });

        // Act
        StateSnapshot snapshot = await provider.CaptureAsync(CancellationToken.None);

        // Assert
        JsonElement rows = snapshot.Value.GetProperty("records");
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal(1, rows[0].GetProperty("part").GetInt32());
        Assert.Equal(1, rows[0].GetProperty("id").GetInt32());
        Assert.Equal(2, rows[1].GetProperty("id").GetInt32());
        Assert.Equal(2, rows[2].GetProperty("part").GetInt32());
        Assert.False(rows[0].TryGetProperty("created_at", out _));
        Assert.Equal(789, rows[0].GetProperty("payload").GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Quotes_schema_table_and_column_identifiers_without_executing_them_as_sql()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await database.ExecuteAsync("""
            CREATE SCHEMA "odd""schema";
            CREATE TABLE "odd""schema"."table;drop" ("key""name" integer PRIMARY KEY, "ignore me" text);
            INSERT INTO "odd""schema"."table;drop" VALUES (2, 'b'), (1, 'a');
            """);
        var provider = new PostgresSnapshotProvider(() => new NpgsqlConnection(database.ConnectionString),
            new PostgresSnapshotOptions { Schema = "odd\"schema", Tables = ["table;drop"], IgnoredColumns = ["ignore me"] });

        // Act
        StateSnapshot snapshot = await provider.CaptureAsync(CancellationToken.None);

        // Assert
        JsonElement rows = snapshot.Value.GetProperty("table;drop");
        Assert.Equal(1, rows[0].GetProperty("key\"name").GetInt32());
        Assert.False(rows[0].TryGetProperty("ignore me", out _));
    }

    [Fact]
    public async Task Rejects_tables_without_a_primary_key()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await database.ExecuteAsync("CREATE TABLE no_key (value integer)");
        var provider = new PostgresSnapshotProvider(() => new NpgsqlConnection(database.ConnectionString),
            new PostgresSnapshotOptions { Tables = ["no_key"] });

        // Act
        FaultSweepExecutionException exception = await Assert.ThrowsAsync<FaultSweepExecutionException>(
            () => provider.CaptureAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Contains("primary key", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_tracking_supports_DI_and_explicit_snapshot_connections(bool explicitConnection)
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await using var host = new PostgresTestHost(database, AccessStyle.AdoDirect);
        FaultSweepBuilder<IMessageHandler<PostgresMessage>, PostgresMessage> builder = host.Sweep(new PostgresMessage(true, true, true));
        builder = explicitConnection
            ? builder.TrackPostgres(database.ConnectionString, tables: ["counters"], name: "tracked")
            : builder.TrackPostgres(tables: ["counters"], name: "tracked");

        // Act
        FaultSweepResult result = await builder.RunAsync();

        // Assert
        result.ShouldHaveNoViolations();
        Assert.Equal(3, result.BaselineTrace.Count);
        Assert.Equal(10, result.BaselineState["tracked"].Value.GetProperty("counters")[0].GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Ignored_primary_keys_still_determine_row_order_and_empty_tables_are_preserved()
    {
        // Arrange
        await using TestDatabase database = await postgres.CreateDatabaseAsync();
        await database.ExecuteAsync("""
            CREATE TABLE items (id integer PRIMARY KEY, value text);
            INSERT INTO items VALUES (2, 'second'), (1, 'first');
            """);
        var provider = new PostgresSnapshotProvider(() => new NpgsqlConnection(database.ConnectionString),
            new PostgresSnapshotOptions { Tables = ["items", "counters"], IgnoredColumns = ["id"] });

        // Act
        StateSnapshot snapshot = await provider.CaptureAsync(CancellationToken.None);

        // Assert
        Assert.Equal("first", snapshot.Value.GetProperty("items")[0].GetProperty("value").GetString());
        Assert.False(snapshot.Value.GetProperty("items")[0].TryGetProperty("id", out _));
        Assert.Equal(0, snapshot.Value.GetProperty("counters").GetArrayLength());
    }
}

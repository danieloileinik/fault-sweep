using System.Data;
using FaultSweep.Snapshots;
using Npgsql;

namespace FaultSweep.Postgres;

internal sealed class PostgresSweepEnvironment : ISweepEnvironment
{
    private readonly NpgsqlConnection connection;

    private PostgresBackupStore? backup;
    private PostgresSnapshotProvider snapshot = null!;

    private PostgresSweepEnvironment(string connectionString)
    {
        // A dedicated session owns temporary copies without occupying the application's connection pool.
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Enlist = false,
        };
        connection = new NpgsqlConnection(settings.ConnectionString);
    }

    public static async ValueTask<ISweepEnvironment> CreateAsync(
        string connectionString,
        string schema,
        string[] ignore,
        CancellationToken token
    )
    {
        var environment = new PostgresSweepEnvironment(connectionString);
        try
        {
            await environment
                .InitializeAsync(connectionString, schema, ignore, token)
                .ConfigureAwait(false);
            return environment;
        }
        catch
        {
            await environment.connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(
        string connectionString,
        string schema,
        string[] ignore,
        CancellationToken token
    )
    {
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, token)
            .ConfigureAwait(false);
        var reader = new PostgresSchemaReader(connection, schema);
        PostgresRestorePlan plan = await reader
            .ReadAsync(transaction, ignore, token)
            .ConfigureAwait(false);
        var store = new PostgresBackupStore(connection, plan);
        await store.CreateAsync(transaction, token).ConfigureAwait(false);
        snapshot = new PostgresSnapshotProvider(
            () => new NpgsqlConnection(connectionString),
            new PostgresSnapshotOptions
            {
                Schema = plan.Schema,
                Tables = plan.Tables.Select(table => table.Name).ToArray(),
            },
            plan.IgnoredColumns.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.Ordinal
            )
        );
        await transaction.CommitAsync(token).ConfigureAwait(false);
        backup = store;
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken) =>
        backup!.RestoreAsync(cancellationToken);

    public ValueTask<StateSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
        snapshot.CaptureAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (backup is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await backup.RestoreAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

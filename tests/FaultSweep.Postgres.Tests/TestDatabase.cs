using FaultSweep.Snapshots;
using Npgsql;

namespace FaultSweep.Postgres.Tests;

public sealed class TestDatabase(string connectionString, Func<ValueTask> drop) : IAsyncDisposable
{
    private readonly NpgsqlDataSource admin = NpgsqlDataSource.Create(connectionString);

    public string ConnectionString => connectionString;

    public async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = admin.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync()
    {
        await using NpgsqlCommand command = admin.CreateCommand(
            "CREATE TABLE counters (id integer PRIMARY KEY, value integer NOT NULL)"
        );
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask ResetAsync(CancellationToken token)
    {
        await using NpgsqlCommand command = admin.CreateCommand(
            "TRUNCATE counters; INSERT INTO counters VALUES (1, 0), (2, 0)"
        );
        await command.ExecuteNonQueryAsync(token);
    }

    public async ValueTask<StateSnapshot> CaptureAsync(CancellationToken token)
    {
        await using NpgsqlCommand command = admin.CreateCommand(
            "SELECT json_agg(row_to_json(c) ORDER BY id)::text FROM counters c"
        );
        string json = (string)(await command.ExecuteScalarAsync(token))!;
        return StateSnapshot.FromJson(json);
    }

    public async ValueTask DisposeAsync()
    {
        await admin.DisposeAsync();
        await drop();
    }
}

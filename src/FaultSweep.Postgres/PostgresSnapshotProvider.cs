using System.Data;
using System.Data.Common;
using System.Text.Json;
using FaultSweep.Effects;
using FaultSweep.Snapshots;

namespace FaultSweep.Postgres;

/// <summary>Captures selected tables in one repeatable-read transaction, ordered by their primary keys.</summary>
public sealed class PostgresSnapshotProvider : ISnapshotProvider
{
    private readonly Func<DbConnection> connections;
    private readonly PostgresSnapshotOptions options;

    public PostgresSnapshotProvider(DbDataSource source, PostgresSnapshotOptions options)
        : this((source ?? throw new ArgumentNullException(nameof(source))).CreateConnection, options)
    {
    }

    /// <summary>The provider owns and disposes each connection returned by this factory.</summary>
    public PostgresSnapshotProvider(Func<DbConnection> connections, PostgresSnapshotOptions options)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        this.connections = connections;
        this.options = options.ValidateAndCopy();
    }

    public async ValueTask<StateSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        await using DbConnection connection = connections();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using DbTransaction transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        var tables = new SortedDictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (string table in options.Tables)
        {
            string[] primaryKey = await ReadPrimaryKeyAsync(connection, transaction, table, cancellationToken).ConfigureAwait(false);
            if (primaryKey.Length == 0)
            {
                throw new FaultSweepExecutionException(
                    $"Tracked table '{options.Schema}.{table}' must exist and have a primary key for deterministic snapshots.");
            }

            await using DbCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT (to_jsonb(captured_row) - @ignored)::text FROM {Quote(options.Schema)}.{Quote(table)} AS captured_row ORDER BY {string.Join(", ", primaryKey.Select(Quote))}";
            AddParameter(command, "ignored", options.IgnoredColumns.ToArray());
            List<JsonElement> rows = [];
            await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    using JsonDocument document = JsonDocument.Parse(reader.GetString(0));
                    rows.Add(document.RootElement.Clone());
                }
            }

            tables.Add(table, rows);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return StateSnapshot.From(tables);
    }

    private async Task<string[]> ReadPrimaryKeyAsync(DbConnection connection, DbTransaction transaction, string table,
        CancellationToken token)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
              SELECT a.attname
              FROM pg_catalog.pg_class c
              JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
              JOIN pg_catalog.pg_index i ON i.indrelid = c.oid AND i.indisprimary
              CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS k(attnum, position)
              JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum
              WHERE n.nspname = @schema AND c.relname = @table AND k.position <= i.indnkeyatts
              ORDER BY k.position
              """;
        AddParameter(command, "schema", options.Schema);
        AddParameter(command, "table", table);
        List<string> columns = [];
        await using DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(0));
        }

        return [.. columns];
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}

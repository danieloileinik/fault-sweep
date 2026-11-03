using System.Data;
using FaultSweep.Effects;
using FaultSweep.Snapshots;
using Npgsql;

namespace FaultSweep.Postgres;

internal sealed class PostgresSweepEnvironment : ISweepEnvironment
{
    private readonly NpgsqlConnection connection;
    private readonly string schema;
    private readonly List<PostgresTableBackup> tables = [];
    private readonly List<PostgresSequenceBackup> sequences = [];
    private readonly List<PostgresTriggerBackup> triggers = [];
    private PostgresSnapshotProvider snapshot = null!;
    private bool initialized;

    private PostgresSweepEnvironment(string connectionString, string schema)
    {
        // A dedicated session owns temporary copies without occupying the application's connection pool.
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Enlist = false,
        };
        connection = new NpgsqlConnection(settings.ConnectionString);
        this.schema = schema;
    }

    public static async ValueTask<ISweepEnvironment> CreateAsync(
        string connectionString,
        string schema,
        string[] ignore,
        CancellationToken token
    )
    {
        var environment = new PostgresSweepEnvironment(connectionString, schema);
        try
        {
            await environment
                .InitializeAsync(connectionString, ignore, token)
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
        string[] ignore,
        CancellationToken token
    )
    {
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, token)
            .ConfigureAwait(false);
        await using (
            var command = new NpgsqlCommand(
                """
                SELECT c.relname,
                    ARRAY(SELECT a.attname::text FROM pg_catalog.pg_attribute a
                        WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                            AND a.attgenerated = '' ORDER BY a.attnum),
                    EXISTS(SELECT 1 FROM pg_catalog.pg_index i WHERE i.indrelid = c.oid AND i.indisprimary),
                    c.relrowsecurity
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relkind IN ('r', 'p') AND NOT c.relispartition
                ORDER BY c.relname
                """,
                connection,
                transaction
            )
        )
        {
            command.Parameters.AddWithValue("schema", schema);
            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(token)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                string table = reader.GetString(0);
                if (!reader.GetBoolean(2) || reader.GetBoolean(3))
                {
                    throw new FaultSweepExecutionException(
                        $"Automatic PostgreSQL sweeps require primary keys and no row-level security: '{schema}.{table}'."
                    );
                }

                tables.Add(
                    new PostgresTableBackup(
                        table,
                        $"faultsweep_seed_{tables.Count}",
                        reader.GetFieldValue<string[]>(1)
                    )
                );
            }
        }

        if (tables.Count == 0)
        {
            throw new FaultSweepExecutionException(
                $"No tables found in PostgreSQL schema '{schema}'. Apply migrations before the sweep."
            );
        }

        await ExecuteAsync(
                $"LOCK TABLE {string.Join(", ", tables.Select(table => Qualified(table.Name)))} IN ACCESS EXCLUSIVE MODE",
                transaction,
                token
            )
            .ConfigureAwait(false);
        await OrderTablesAsync(transaction, token).ConfigureAwait(false);
        await ReadTriggersAsync(transaction, token).ConfigureAwait(false);

        var unmatched = new HashSet<string>(ignore, StringComparer.Ordinal);
        var ignoredColumns = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (PostgresTableBackup table in tables)
        {
            // Temporary copies preserve generated values without defaults, constraints or triggers.
            await ExecuteAsync(
                    $"CREATE TEMP TABLE {Quote(table.TemporaryName)} ON COMMIT PRESERVE ROWS AS TABLE {Qualified(table.Name)}",
                    transaction,
                    token
                )
                .ConfigureAwait(false);
            await using var columns = new NpgsqlCommand(
                """
                SELECT a.attname FROM pg_catalog.pg_attribute a
                WHERE a.attrelid = @table::regclass AND a.attnum > 0 AND NOT a.attisdropped
                """,
                connection,
                transaction
            );
            columns.Parameters.AddWithValue("table", Qualified(table.Name));
            List<string> ignored = [];
            await using (
                NpgsqlDataReader reader = await columns
                    .ExecuteReaderAsync(token)
                    .ConfigureAwait(false)
            )
            {
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    string column = reader.GetString(0);
                    foreach (
                        string candidate in ignore.Where(candidate =>
                            candidate == column || candidate == $"{table.Name}.{column}"
                        )
                    )
                    {
                        ignored.Add(column);
                        unmatched.Remove(candidate);
                    }
                }
            }

            ignoredColumns.Add(table.Name, [.. ignored]);
        }

        if (unmatched.Count > 0)
        {
            throw new ArgumentException(
                $"Unknown ignored PostgreSQL columns: {string.Join(", ", unmatched)}.",
                nameof(ignore)
            );
        }

        snapshot = new PostgresSnapshotProvider(
            () => new NpgsqlConnection(connectionString),
            new PostgresSnapshotOptions
            {
                Schema = schema,
                Tables = tables.Select(table => table.Name).ToArray(),
            },
            ignoredColumns
        );

        List<string> sequenceNames = [];
        await using (
            var command = new NpgsqlCommand(
                """
                SELECT c.relname FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relkind = 'S' ORDER BY c.relname
                """,
                connection,
                transaction
            )
        )
        {
            command.Parameters.AddWithValue("schema", schema);
            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(token)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                sequenceNames.Add(reader.GetString(0));
            }
        }

        foreach (string name in sequenceNames)
        {
            await using var command = new NpgsqlCommand(
                $"SELECT last_value, is_called FROM {Qualified(name)}",
                connection,
                transaction
            );
            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(token)
                .ConfigureAwait(false);
            await reader.ReadAsync(token).ConfigureAwait(false);
            sequences.Add(
                new PostgresSequenceBackup(name, reader.GetInt64(0), reader.GetBoolean(1))
            );
        }

        await transaction.CommitAsync(token).ConfigureAwait(false);
        initialized = true;
    }

    private async Task OrderTablesAsync(NpgsqlTransaction transaction, CancellationToken token)
    {
        var dependencies = tables.ToDictionary(
            table => table.Name,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal
        );
        await using (
            var command = new NpgsqlCommand(
                """
                SELECT child.relname, parent.relname, cn.nspname, pn.nspname, k.condeferrable
                FROM pg_catalog.pg_constraint k
                JOIN pg_catalog.pg_class child ON child.oid = k.conrelid
                JOIN pg_catalog.pg_namespace cn ON cn.oid = child.relnamespace
                JOIN pg_catalog.pg_class parent ON parent.oid = k.confrelid
                JOIN pg_catalog.pg_namespace pn ON pn.oid = parent.relnamespace
                WHERE k.contype = 'f' AND k.conparentid = 0 AND (cn.nspname = @schema OR pn.nspname = @schema)
                """,
                connection,
                transaction
            )
        )
        {
            command.Parameters.AddWithValue("schema", schema);
            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(token)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                string child = reader.GetString(0);
                string parent = reader.GetString(1);
                if (reader.GetString(2) != schema || reader.GetString(3) != schema)
                {
                    throw new FaultSweepExecutionException(
                        "Automatic PostgreSQL restoration requires foreign keys to stay within the selected schema."
                    );
                }

                if (child != parent && !reader.GetBoolean(4))
                {
                    dependencies[child].Add(parent);
                }
            }
        }

        List<PostgresTableBackup> ordered = [];
        while (ordered.Count < tables.Count)
        {
            PostgresTableBackup? next = tables.FirstOrDefault(table =>
                !ordered.Contains(table)
                && dependencies[table.Name]
                    .All(parent => ordered.Any(table => table.Name == parent))
            );
            if (next is null)
            {
                throw new FaultSweepExecutionException(
                    "Automatic PostgreSQL restoration requires cyclic foreign keys to be DEFERRABLE."
                );
            }

            ordered.Add(next);
        }

        tables.Clear();
        tables.AddRange(ordered);
    }

    private async Task ReadTriggersAsync(NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname, t.tgname, t.tgenabled::text FROM pg_catalog.pg_trigger t
            JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND NOT t.tgisinternal AND t.tgenabled <> 'D'
            """,
            connection,
            transaction
        );
        command.Parameters.AddWithValue("schema", schema);
        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(token)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            triggers.Add(
                new PostgresTriggerBackup(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2)
                )
            );
        }
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (PostgresTriggerBackup trigger in triggers)
        {
            await ExecuteAsync(
                    $"ALTER TABLE {Qualified(trigger.Table)} DISABLE TRIGGER {Quote(trigger.Name)}",
                    transaction,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        await ExecuteAsync("SET CONSTRAINTS ALL DEFERRED", transaction, cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                $"TRUNCATE {string.Join(", ", tables.Select(table => Qualified(table.Name)))}",
                transaction,
                cancellationToken
            )
            .ConfigureAwait(false);
        foreach (PostgresTableBackup table in tables)
        {
            string columns = string.Join(", ", table.Columns.Select(Quote));
            await ExecuteAsync(
                    $"INSERT INTO {Qualified(table.Name)} ({columns}) OVERRIDING SYSTEM VALUE SELECT {columns} FROM {Quote(table.TemporaryName)}",
                    transaction,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        foreach (PostgresSequenceBackup sequence in sequences)
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_catalog.setval(@name::regclass, @value, @called)",
                connection,
                transaction
            );
            command.Parameters.AddWithValue("name", Qualified(sequence.Name));
            command.Parameters.AddWithValue("value", sequence.Value);
            command.Parameters.AddWithValue("called", sequence.IsCalled);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (PostgresTriggerBackup trigger in triggers)
        {
            string mode = trigger.Mode switch
            {
                "A" => "ENABLE ALWAYS",
                "R" => "ENABLE REPLICA",
                _ => "ENABLE",
            };
            await ExecuteAsync(
                    $"ALTER TABLE {Qualified(trigger.Table)} {mode} TRIGGER {Quote(trigger.Name)}",
                    transaction,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<StateSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
        snapshot.CaptureAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (initialized)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await ResetAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ExecuteAsync(
        string sql,
        NpgsqlTransaction transaction,
        CancellationToken token
    )
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private string Qualified(string name) => $"{Quote(schema)}.{Quote(name)}";

    private static string Quote(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
}

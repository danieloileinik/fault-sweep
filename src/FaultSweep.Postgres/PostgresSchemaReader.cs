using System.Collections.Immutable;
using FaultSweep.Effects;
using Npgsql;
using static FaultSweep.Postgres.PostgresSql;

namespace FaultSweep.Postgres;

internal sealed class PostgresSchemaReader(NpgsqlConnection connection, string schema)
{
    public async Task<PostgresRestorePlan> ReadAsync(
        NpgsqlTransaction transaction,
        string[] ignore,
        CancellationToken token
    )
    {
        ImmutableArray<PostgresTableBackup> tables = await ReadTablesAsync(transaction, token)
            .ConfigureAwait(false);
        await using (
            var command = new NpgsqlCommand(
                $"LOCK TABLE {string.Join(", ", tables.Select(table => Qualified(table.Name)))} IN ACCESS EXCLUSIVE MODE",
                connection,
                transaction
            )
        )
        {
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        IReadOnlyDictionary<string, HashSet<string>> dependencies = await ReadDependenciesAsync(
                tables,
                transaction,
                token
            )
            .ConfigureAwait(false);
        ImmutableArray<PostgresTableBackup> ordered = PostgresTableOrder.Sort(tables, dependencies);
        ImmutableArray<PostgresTriggerBackup> triggers = await ReadTriggersAsync(transaction, token)
            .ConfigureAwait(false);
        ImmutableDictionary<string, ImmutableArray<string>> ignored = await ReadIgnoredColumnsAsync(
                ordered,
                ignore,
                transaction,
                token
            )
            .ConfigureAwait(false);
        ImmutableArray<PostgresSequenceBackup> sequences = await ReadSequencesAsync(
                transaction,
                token
            )
            .ConfigureAwait(false);
        return new PostgresRestorePlan(schema, ordered, sequences, triggers, ignored);
    }

    private async Task<ImmutableArray<PostgresTableBackup>> ReadTablesAsync(
        NpgsqlTransaction transaction,
        CancellationToken token
    )
    {
        List<PostgresTableBackup> tables = [];
        await using (
            var command = new NpgsqlCommand(
                """
                SELECT c.relname,
                    ARRAY(SELECT a.attname::text FROM pg_catalog.pg_attribute a
                        WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                            AND a.attgenerated = '' ORDER BY a.attnum),
                    EXISTS(SELECT 1 FROM pg_catalog.pg_index i WHERE i.indrelid = c.oid AND i.indisprimary),
                    c.relrowsecurity,
                    c.relkind = 'r' AND EXISTS (
                        SELECT 1 FROM pg_catalog.pg_inherits h
                        WHERE h.inhparent = c.oid OR h.inhrelid = c.oid)
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
                if (reader.GetBoolean(4))
                {
                    throw new FaultSweepExecutionException(
                        $"Automatic PostgreSQL restoration does not support INHERITS tables: '{schema}.{table}'."
                    );
                }

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
                        reader.GetFieldValue<string[]>(1).ToImmutableArray()
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

        return tables.ToImmutableArray();
    }

    private async Task<IReadOnlyDictionary<string, HashSet<string>>> ReadDependenciesAsync(
        ImmutableArray<PostgresTableBackup> tables,
        NpgsqlTransaction transaction,
        CancellationToken token
    )
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

        return dependencies;
    }

    private async Task<ImmutableArray<PostgresTriggerBackup>> ReadTriggersAsync(
        NpgsqlTransaction transaction,
        CancellationToken token
    )
    {
        List<PostgresTriggerBackup> triggers = [];
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
        return triggers.ToImmutableArray();
    }

    private async Task<ImmutableDictionary<string, ImmutableArray<string>>> ReadIgnoredColumnsAsync(
        ImmutableArray<PostgresTableBackup> tables,
        string[] ignore,
        NpgsqlTransaction transaction,
        CancellationToken token
    )
    {
        var unmatched = new HashSet<string>(ignore, StringComparer.Ordinal);
        var ignoredColumns = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(
            StringComparer.Ordinal
        );
        foreach (PostgresTableBackup table in tables)
        {
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

            ignoredColumns.Add(table.Name, ignored.ToImmutableArray());
        }

        if (unmatched.Count > 0)
        {
            throw new ArgumentException(
                $"Unknown ignored PostgreSQL columns: {string.Join(", ", unmatched)}.",
                nameof(ignore)
            );
        }

        return ignoredColumns.ToImmutable();
    }

    private async Task<ImmutableArray<PostgresSequenceBackup>> ReadSequencesAsync(
        NpgsqlTransaction transaction,
        CancellationToken token
    )
    {
        List<PostgresSequenceBackup> sequences = [];
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

        return sequences.ToImmutableArray();
    }

    private string Qualified(string name) => PostgresSql.Qualified(schema, name);
}

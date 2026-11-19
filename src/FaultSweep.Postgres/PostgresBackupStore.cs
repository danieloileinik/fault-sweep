using Npgsql;
using static FaultSweep.Postgres.PostgresSql;

namespace FaultSweep.Postgres;

internal sealed class PostgresBackupStore(NpgsqlConnection connection, PostgresRestorePlan plan)
{
    public async Task CreateAsync(NpgsqlTransaction transaction, CancellationToken token)
    {
        foreach (PostgresTableBackup table in plan.Tables)
        {
            // Temporary copies preserve generated values without defaults, constraints or triggers.
            await ExecuteAsync(
                    $"CREATE TEMP TABLE {Quote(table.TemporaryName)} ON COMMIT PRESERVE ROWS AS TABLE {Qualified(table.Name)}",
                    transaction,
                    token
                )
                .ConfigureAwait(false);
        }
    }

    public async ValueTask RestoreAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (PostgresTriggerBackup trigger in plan.Triggers)
        {
            await ExecuteAsync(
                    $"ALTER TABLE ONLY {PostgresSql.Qualified(trigger.Schema, trigger.Table)} DISABLE TRIGGER {Quote(trigger.Name)}",
                    transaction,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        await ExecuteAsync("SET CONSTRAINTS ALL DEFERRED", transaction, cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                $"TRUNCATE {string.Join(", ", plan.Tables.Select(table => Qualified(table.Name)))}",
                transaction,
                cancellationToken
            )
            .ConfigureAwait(false);
        foreach (PostgresTableBackup table in plan.Tables)
        {
            string columns = string.Join(", ", table.Columns.Select(Quote));
            await ExecuteAsync(
                    $"INSERT INTO {Qualified(table.Name)} ({columns}) OVERRIDING SYSTEM VALUE SELECT {columns} FROM {Quote(table.TemporaryName)}",
                    transaction,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        await ExecuteAsync("SET CONSTRAINTS ALL IMMEDIATE", transaction, cancellationToken)
            .ConfigureAwait(false);

        foreach (PostgresSequenceBackup sequence in plan.Sequences)
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

        foreach (PostgresTriggerBackup trigger in plan.Triggers)
        {
            string mode = trigger.Mode switch
            {
                "A" => "ENABLE ALWAYS",
                "R" => "ENABLE REPLICA",
                _ => "ENABLE",
            };
            await ExecuteAsync(
                    $"ALTER TABLE ONLY {PostgresSql.Qualified(trigger.Schema, trigger.Table)} {mode} TRIGGER {Quote(trigger.Name)}",
                    transaction,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

    private string Qualified(string name) => PostgresSql.Qualified(plan.Schema, name);
}

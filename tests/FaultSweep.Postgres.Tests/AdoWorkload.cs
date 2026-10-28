using System.Data.Common;

namespace FaultSweep.Postgres.Tests;

public static class AdoWorkload
{
    public static async ValueTask RunAsync(DbConnection connection, PostgresMessage message, CancellationToken token)
    {
        if (message.Async)
        {
            await connection.OpenAsync(token);
            await using DbTransaction? transaction = message.Transactional ? await connection.BeginTransactionAsync(token) : null;
            for (int id = 1; id <= 2; id++)
            {
                await using DbCommand command = CreateCommand(connection, transaction, message, id);
                switch (message.Api)
                {
                    case CommandApi.NonQuery:
                        await command.ExecuteNonQueryAsync(token);
                        break;
                    case CommandApi.Scalar:
                        await command.ExecuteScalarAsync(token);
                        break;
                    case CommandApi.Reader:
                        await using (DbDataReader reader = await command.ExecuteReaderAsync(token))
                        {
                            while (await reader.ReadAsync(token))
                            {
                                _ = reader.GetInt32(0);
                            }
                        }

                        break;
                }
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(token);
            }
        }
        else
        {
            connection.Open();
            using DbTransaction? transaction = message.Transactional ? connection.BeginTransaction() : null;
            for (int id = 1; id <= 2; id++)
            {
                using DbCommand command = CreateCommand(connection, transaction, message, id);
                switch (message.Api)
                {
                    case CommandApi.NonQuery:
                        command.ExecuteNonQuery();
                        break;
                    case CommandApi.Scalar:
                        command.ExecuteScalar();
                        break;
                    case CommandApi.Reader:
                        using (DbDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                _ = reader.GetInt32(0);
                            }
                        }

                        break;
                }
            }

            transaction?.Commit();
        }
    }

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, PostgresMessage message, int id)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = WorkloadSql.Update(message);
        command.Transaction = transaction;
        DbParameter idParameter = command.CreateParameter();
        idParameter.ParameterName = "id";
        idParameter.Value = id;
        DbParameter amountParameter = command.CreateParameter();
        amountParameter.ParameterName = "amount";
        amountParameter.Value = 10;
        DbParameter[] parameters = [idParameter, amountParameter];
        command.Parameters.AddRange(parameters);
        return command;
    }
}

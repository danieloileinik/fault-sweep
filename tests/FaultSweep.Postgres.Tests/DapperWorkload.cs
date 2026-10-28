using System.Data.Common;
using Dapper;

namespace FaultSweep.Postgres.Tests;

public static class DapperWorkload
{
    public static async ValueTask RunAsync(DbConnection connection, PostgresMessage message, CancellationToken token)
    {
        if (message.Async)
        {
            // Without a transaction, Dapper opens and closes the initially closed connection itself.
            if (message.Transactional)
            {
                await connection.OpenAsync(token);
            }

            await using DbTransaction? transaction = message.Transactional ? await connection.BeginTransactionAsync(token) : null;
            for (int id = 1; id <= 2; id++)
            {
                var command = new CommandDefinition(WorkloadSql.Update(message), new { id, amount = 10 }, transaction,
                    cancellationToken: token);
                switch (message.Api)
                {
                    case CommandApi.NonQuery:
                        await connection.ExecuteAsync(command);
                        break;
                    case CommandApi.Scalar:
                        await connection.ExecuteScalarAsync<int>(command);
                        break;
                    case CommandApi.Reader:
                        await connection.QuerySingleAsync<int>(command);
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
            if (message.Transactional)
            {
                connection.Open();
            }

            using DbTransaction? transaction = message.Transactional ? connection.BeginTransaction() : null;
            for (int id = 1; id <= 2; id++)
            {
                var parameters = new { id, amount = 10 };
                switch (message.Api)
                {
                    case CommandApi.NonQuery:
                        connection.Execute(WorkloadSql.Update(message), parameters, transaction);
                        break;
                    case CommandApi.Scalar:
                        connection.ExecuteScalar<int>(WorkloadSql.Update(message), parameters, transaction);
                        break;
                    case CommandApi.Reader:
                        connection.QuerySingle<int>(WorkloadSql.Update(message), parameters, transaction);
                        break;
                }
            }

            transaction?.Commit();
        }
    }
}

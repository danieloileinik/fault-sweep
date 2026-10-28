using Confluent.Kafka;
using Dapper;
using Npgsql;

namespace FaultSweep.Postgres.Tests;

public sealed class PostgresKafkaHandler(TestDatabase database, IProducer<int, OrderPlacedEvent> producer)
    : IMessageHandler<MessageFlowRequest>
{
    public async ValueTask HandleAsync(MessageFlowRequest request, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        bool guardedByInbox = request.Flow is MessageFlow.LostEvent or MessageFlow.InboxBeforeWork or MessageFlow.CorrectInboxOutbox;
        if (guardedByInbox && await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM inbox WHERE message_id = @OrderId)", request, cancellationToken: cancellationToken)))
        {
            return;
        }

        if (request.Flow is MessageFlow.DualWrite or MessageFlow.PublishBeforeCommit &&
            await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM orders WHERE id = @OrderId)", request, cancellationToken: cancellationToken)))
        {
            return;
        }

        switch (request.Flow)
        {
            case MessageFlow.DualWrite:
                await ExecuteAsync("INSERT INTO orders VALUES (@OrderId, @Amount)");
                await PublishAsync();
                break;
            case MessageFlow.PublishBeforeCommit:
                await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken))
                {
                    await ExecuteAsync("INSERT INTO orders VALUES (@OrderId, @Amount)", transaction);
                    await PublishAsync();
                    await transaction.CommitAsync(cancellationToken);
                }

                break;
            case MessageFlow.LostEvent:
                await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken))
                {
                    await ExecuteAsync("INSERT INTO orders VALUES (@OrderId, @Amount)", transaction);
                    await ExecuteAsync("INSERT INTO inbox VALUES (@OrderId)", transaction);
                    await transaction.CommitAsync(cancellationToken);
                }

                await PublishAsync();
                break;
            case MessageFlow.NonIdempotentCounter:
                await ExecuteAsync("UPDATE balances SET value = value + @Amount WHERE id = 1");
                await PublishAsync();
                break;
            case MessageFlow.InboxBeforeWork:
                await ExecuteAsync("INSERT INTO inbox VALUES (@OrderId)");
                await ExecuteAsync("INSERT INTO orders VALUES (@OrderId, @Amount)");
                await PublishAsync();
                break;
            case MessageFlow.OutboxWithoutInbox:
            case MessageFlow.CorrectInboxOutbox:
                await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken))
                {
                    await ExecuteAsync("UPDATE balances SET value = value + @Amount WHERE id = 1", transaction);
                    await ExecuteAsync("INSERT INTO outbox(event_key, payload) VALUES (@OrderId, jsonb_build_object('orderId', @OrderId, 'amount', @Amount))", transaction);
                    if (request.Flow == MessageFlow.CorrectInboxOutbox)
                    {
                        await ExecuteAsync("INSERT INTO orders VALUES (@OrderId, @Amount)", transaction);
                        await ExecuteAsync("INSERT INTO inbox VALUES (@OrderId)", transaction);
                    }

                    await transaction.CommitAsync(cancellationToken);
                }

                break;
            case MessageFlow.IdempotentUpsert:
                await ExecuteAsync("INSERT INTO orders VALUES (@OrderId, @Amount) ON CONFLICT(id) DO UPDATE SET amount = EXCLUDED.amount");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }

        Task<int> ExecuteAsync(string sql, NpgsqlTransaction? transaction = null)
            => connection.ExecuteAsync(new CommandDefinition(sql, request, transaction, cancellationToken: cancellationToken));

        Task<DeliveryResult<int, OrderPlacedEvent>> PublishAsync()
        {
            return producer.ProduceAsync("order-placed", new Message<int, OrderPlacedEvent>
            {
                Key = request.OrderId,
                Value = new OrderPlacedEvent(request.OrderId, request.Amount)
            }, cancellationToken);
        }
    }
}

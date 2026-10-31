using Confluent.Kafka;
using Npgsql;
using StackExchange.Redis;

namespace FaultSweep.Redis.Tests;

public sealed class MixedHandler(RedisFixture fixture, IProducer<int, string> producer) : IMessageHandler<MixedRequest>
{
    public async ValueTask HandleAsync(MixedRequest request, CancellationToken token)
    {
        // This client is deliberately created inside the handler, independently of test DI registrations.
        await using ConnectionMultiplexer redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConfiguration);
        IDatabase database = redis.GetDatabase();
        string key = request.Prefix + "cache";
        if (!request.Correct && await database.KeyExistsAsync(key))
        {
            return;
        }

        await using var connection = new NpgsqlConnection(fixture.PostgresConfiguration);
        await connection.OpenAsync(token);
        if (request.Correct)
        {
            await using var command = new NpgsqlCommand($"INSERT INTO {request.Schema}.orders VALUES(1,10) ON CONFLICT(id) DO UPDATE SET amount = 10; INSERT INTO {request.Schema}.outbox VALUES(1,'placed') ON CONFLICT DO NOTHING", connection);
            await command.ExecuteNonQueryAsync(token);
            await database.StringSetAsync(key, 10);
        }
        else
        {
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token);
            await using var command = new NpgsqlCommand($"INSERT INTO {request.Schema}.orders VALUES(1,10)", connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            await database.StringSetAsync(key, 10);
            await transaction.CommitAsync(token);
            await producer.ProduceAsync("orders", new Message<int, string> { Key = 1, Value = "placed" }, token);
        }
    }
}

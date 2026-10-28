using FaultSweep.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FaultSweep.Postgres.Tests;

public sealed class MessageFlowEnvironment : IAsyncDisposable
{
    private readonly TestDatabase database;
    private readonly IHost host;

    public MessageFlowEnvironment(TestDatabase database)
    {
        this.database = database;
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton(database);
        builder.Services.AddFaultSweepPostgres();
        builder.Services.AddFaultSweepKafka<int, OrderPlacedEvent>();
        builder.Services.AddScoped<PostgresKafkaHandler>();
        host = builder.Build();
    }

    public Task InitializeAsync() => database.ExecuteAsync("""
        CREATE TABLE orders (id integer PRIMARY KEY, amount numeric NOT NULL);
        CREATE TABLE balances (id integer PRIMARY KEY, value numeric NOT NULL);
        CREATE TABLE inbox (message_id integer PRIMARY KEY);
        CREATE TABLE outbox (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            event_key integer NOT NULL,
            payload jsonb NOT NULL,
            created_at timestamptz NOT NULL DEFAULT clock_timestamp());
        """);

    public FaultSweepBuilder<PostgresKafkaHandler, MessageFlowRequest> Sweep(MessageFlow flow)
    {
        return FaultSweep.For<PostgresKafkaHandler>(host).WithMessage(new MessageFlowRequest(flow))
            .WithOptions(new FaultSweepOptions { FaultKinds = [FaultKind.CrashBefore, FaultKind.CrashAfter] })
            .ResetWith(async (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                await database.ExecuteAsync("TRUNCATE orders, balances, inbox, outbox RESTART IDENTITY; INSERT INTO balances VALUES (1, 0)");
            })
            .TrackPostgres(database.ConnectionString, tables: ["orders", "balances", "inbox"])
            // PostgreSQL sequences are not rolled back. Normalize only technical outbox fields.
            .TrackPostgres(database.ConnectionString, tables: ["outbox"], ignore: ["id", "created_at"], name: "outbox")
            .TrackProduced();
    }

    public async ValueTask DisposeAsync()
    {
        if (host is IAsyncDisposable asynchronous)
        {
            await asynchronous.DisposeAsync();
        }
        else
        {
            host.Dispose();
        }
    }
}

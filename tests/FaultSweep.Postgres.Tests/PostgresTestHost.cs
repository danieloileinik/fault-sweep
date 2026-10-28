using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace FaultSweep.Postgres.Tests;

public sealed class PostgresTestHost : IAsyncDisposable
{
    private readonly TestDatabase database;
    private readonly IHost host;
    private readonly List<NpgsqlConnection> createdConnections = [];

    public PostgresTestHost(TestDatabase database, AccessStyle style)
        : this(database, services =>
        {
            switch (style)
            {
                case AccessStyle.AdoDataSource:
                    services.AddScoped<IMessageHandler<PostgresMessage>, AdoDataSourceHandler>();
                    break;
                case AccessStyle.AdoFactory:
                    services.AddScoped<IMessageHandler<PostgresMessage>, AdoFactoryHandler>();
                    break;
                case AccessStyle.DapperDataSource:
                    services.AddScoped<IMessageHandler<PostgresMessage>, DapperDataSourceHandler>();
                    break;
                case AccessStyle.DapperFactory:
                    services.AddScoped<IMessageHandler<PostgresMessage>, DapperFactoryHandler>();
                    break;
                case AccessStyle.AdoDirect:
                    services.AddScoped<IMessageHandler<PostgresMessage>, DirectConnectionHandler>();
                    break;
                case AccessStyle.DapperDirect:
                    services.AddScoped<IMessageHandler<PostgresMessage>, DirectDapperHandler>();
                    break;
            }
        }, style is AccessStyle.AdoFactory or AccessStyle.DapperFactory)
    {
    }

    public PostgresTestHost(TestDatabase database, Action<IServiceCollection> configure, bool useFactory = false)
    {
        this.database = database;
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton(database);
        if (useFactory)
        {
            builder.Services.AddFaultSweepPostgresConnectionFactory(_ =>
            {
                var connection = new NpgsqlConnection(database.ConnectionString);
                createdConnections.Add(connection);
                return connection;
            });
        }
        else
        {
            builder.Services.AddFaultSweepPostgres(database.ConnectionString);
        }

        configure(builder.Services);
        host = builder.Build();
    }

    public IServiceProvider Services => host.Services;

    public FaultSweepBuilder<IMessageHandler<PostgresMessage>, PostgresMessage> Sweep(PostgresMessage message)
    {
        return FaultSweep.For<IMessageHandler<PostgresMessage>>(host).WithMessage(message)
            .ResetWith((_, token) => database.ResetAsync(token))
            .TrackState("postgres", (_, token) => database.CaptureAsync(token));
    }

    public async ValueTask DisposeAsync()
    {
        if (host is IAsyncDisposable asyncHost)
        {
            await asyncHost.DisposeAsync();
        }
        else
        {
            host.Dispose();
        }

        // Clear only this test's provider pools before dropping its isolated database.
        foreach (NpgsqlConnection connection in createdConnections)
        {
            NpgsqlConnection.ClearPool(connection);
        }
    }
}

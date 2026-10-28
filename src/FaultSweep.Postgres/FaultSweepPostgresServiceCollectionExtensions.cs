using System.Data.Common;
using FaultSweep.Effects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FaultSweep.Postgres;

public static class FaultSweepPostgresServiceCollectionExtensions
{
    /// <summary>Enables interception of Npgsql operations, including directly constructed connections.</summary>
    public static IServiceCollection AddFaultSweepPostgres(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddFaultSweep();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEffectInstrumentation, PostgresInstrumentation>());
        return services;
    }

    /// <summary>Enables interception and optionally registers an ordinary data source and connection factory.</summary>
    public static IServiceCollection AddFaultSweepPostgres(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return services.AddFaultSweepPostgres(_ => NpgsqlDataSource.Create(connectionString));
    }

    /// <summary>DI owns the data source returned by the factory. No connection decorators are installed.</summary>
    public static IServiceCollection AddFaultSweepPostgres(this IServiceCollection services,
        Func<IServiceProvider, DbDataSource> dataSourceFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSourceFactory);
        services.AddFaultSweepPostgres();
        services.Replace(ServiceDescriptor.Singleton<DbDataSource>(dataSourceFactory));
        services.Replace(ServiceDescriptor.Scoped<Func<DbConnection>>(provider =>
            provider.GetRequiredService<DbDataSource>().CreateConnection));
        return services;
    }

    /// <summary>Replaces a scoped factory whose callers own and dispose the returned connections.</summary>
    public static IServiceCollection AddFaultSweepPostgresConnectionFactory(this IServiceCollection services,
        Func<IServiceProvider, DbConnection> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        services.AddFaultSweepPostgres();
        services.Replace(ServiceDescriptor.Scoped<Func<DbConnection>>(provider =>
            () => connectionFactory(provider)));
        return services;
    }
}

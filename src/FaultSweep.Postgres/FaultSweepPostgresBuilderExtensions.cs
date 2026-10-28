using System.Data.Common;
using FaultSweep.Effects;
using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FaultSweep.Postgres;

public static class FaultSweepPostgresBuilderExtensions
{
    public static FaultSweepBuilder<THandler, TMessage> TrackPostgres<THandler, TMessage>(
        this FaultSweepBuilder<THandler, TMessage> builder, IReadOnlyCollection<string> tables,
        IReadOnlyCollection<string>? ignore = null, string schema = "public",
        SnapshotComparisonOptions? comparison = null, string name = "postgres") where THandler : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        PostgresSnapshotOptions options = CreateOptions(tables, ignore, schema);
        return ValidateInstrumentation(builder).TrackState(name, (services, token) =>
            new PostgresSnapshotProvider(() => CreateConnection(services), options).CaptureAsync(token), comparison);
    }

    /// <summary>Supports applications that create their own connections without a DI data source.</summary>
    public static FaultSweepBuilder<THandler, TMessage> TrackPostgres<THandler, TMessage>(
        this FaultSweepBuilder<THandler, TMessage> builder, string connectionString, IReadOnlyCollection<string> tables,
        IReadOnlyCollection<string>? ignore = null, string schema = "public",
        SnapshotComparisonOptions? comparison = null, string name = "postgres") where THandler : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        PostgresSnapshotOptions options = CreateOptions(tables, ignore, schema);
        var provider = new PostgresSnapshotProvider(() => new NpgsqlConnection(connectionString), options);
        return ValidateInstrumentation(builder).TrackState(name, (_, token) => provider.CaptureAsync(token), comparison);
    }

    private static FaultSweepBuilder<THandler, TMessage> ValidateInstrumentation<THandler, TMessage>(
        FaultSweepBuilder<THandler, TMessage> builder) where THandler : class
    {
        return builder.ValidateWith(services =>
        {
            if (!services.GetServices<IEffectInstrumentation>().OfType<PostgresInstrumentation>().Any())
            {
                throw new InvalidOperationException("TrackPostgres requires services.AddFaultSweepPostgres() in the test configuration.");
            }
        });
    }

    private static PostgresSnapshotOptions CreateOptions(IReadOnlyCollection<string> tables,
        IReadOnlyCollection<string>? ignore, string schema)
    {
        return new PostgresSnapshotOptions { Tables = tables, IgnoredColumns = ignore ?? [], Schema = schema }.ValidateAndCopy();
    }

    private static DbConnection CreateConnection(IServiceProvider services)
    {
        DbDataSource? source = services.GetService<DbDataSource>() ?? services.GetService<NpgsqlDataSource>();
        if (source is not null)
        {
            return source.CreateConnection();
        }

        Func<DbConnection>? factory = services.GetService<Func<DbConnection>>();
        return factory?.Invoke() ?? throw new InvalidOperationException(
            "TrackPostgres requires a DbDataSource, NpgsqlDataSource or Func<DbConnection> in DI, or an explicit snapshot connection string.");
    }
}

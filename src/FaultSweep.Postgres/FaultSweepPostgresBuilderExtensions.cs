using System.Data.Common;
using FaultSweep.Effects;
using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FaultSweep.Postgres;

public static class FaultSweepPostgresBuilderExtensions
{
    /// <summary>Discovers tables and preserves their seed data and sequence positions for an automatic sweep.</summary>
    public static FaultSweepBuilder<THandler> WithPostgres<THandler>(
        this FaultSweepBuilder<THandler> builder,
        string connectionString,
        IReadOnlyCollection<string>? ignore = null,
        string schema = "public"
    )
        where THandler : class =>
        WithPostgres(builder, connectionString, ignore, schema, "postgres");

    /// <summary>Tracks a named PostgreSQL store with its own restoration and comparison settings.</summary>
    public static FaultSweepBuilder<THandler> WithPostgres<THandler>(
        this FaultSweepBuilder<THandler> builder,
        string connectionString,
        IReadOnlyCollection<string>? ignore,
        string schema,
        string name,
        SnapshotComparisonOptions? comparison = null
    )
        where THandler : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        string[] ignored = ignore is null ? [] : [.. ignore];
        if (schema.Contains('\0') || ignored.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Schema and ignored columns must be valid PostgreSQL identifiers."
            );
        }

        return builder.WithEnvironment(
            name,
            new PostgresInstrumentation(),
            token => PostgresSweepEnvironment.CreateAsync(connectionString, schema, ignored, token),
            comparison
        );
    }

    public static FaultSweepBuilder<THandler, TMessage> TrackPostgres<THandler, TMessage>(
        this FaultSweepBuilder<THandler, TMessage> builder,
        IReadOnlyCollection<string> tables,
        IReadOnlyCollection<string>? ignore = null,
        string schema = "public",
        SnapshotComparisonOptions? comparison = null,
        string name = "postgres"
    )
        where THandler : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        PostgresSnapshotOptions options = CreateOptions(tables, ignore, schema);
        return ValidateInstrumentation(builder)
            .TrackState(
                name,
                (services, token) =>
                    new PostgresSnapshotProvider(
                        () => CreateConnection(services),
                        options
                    ).CaptureAsync(token),
                comparison
            );
    }

    /// <summary>Supports applications that create their own connections without a DI data source.</summary>
    public static FaultSweepBuilder<THandler, TMessage> TrackPostgres<THandler, TMessage>(
        this FaultSweepBuilder<THandler, TMessage> builder,
        string connectionString,
        IReadOnlyCollection<string> tables,
        IReadOnlyCollection<string>? ignore = null,
        string schema = "public",
        SnapshotComparisonOptions? comparison = null,
        string name = "postgres"
    )
        where THandler : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        PostgresSnapshotOptions options = CreateOptions(tables, ignore, schema);
        var provider = new PostgresSnapshotProvider(
            () => new NpgsqlConnection(connectionString),
            options
        );
        return ValidateInstrumentation(builder)
            .TrackState(name, (_, token) => provider.CaptureAsync(token), comparison);
    }

    private static FaultSweepBuilder<THandler, TMessage> ValidateInstrumentation<
        THandler,
        TMessage
    >(FaultSweepBuilder<THandler, TMessage> builder)
        where THandler : class
    {
        return builder.ValidateWith(services =>
        {
            if (
                !services
                    .GetServices<IEffectInstrumentation>()
                    .OfType<PostgresInstrumentation>()
                    .Any()
            )
            {
                throw new InvalidOperationException(
                    "TrackPostgres requires services.AddFaultSweepPostgres() in the test configuration."
                );
            }
        });
    }

    private static PostgresSnapshotOptions CreateOptions(
        IReadOnlyCollection<string> tables,
        IReadOnlyCollection<string>? ignore,
        string schema
    )
    {
        return new PostgresSnapshotOptions
        {
            Tables = tables,
            IgnoredColumns = ignore ?? [],
            Schema = schema,
        }.ValidateAndCopy();
    }

    private static DbConnection CreateConnection(IServiceProvider services)
    {
        DbDataSource? source =
            services.GetService<DbDataSource>() ?? services.GetService<NpgsqlDataSource>();
        if (source is not null)
        {
            return source.CreateConnection();
        }

        Func<DbConnection>? factory = services.GetService<Func<DbConnection>>();
        return factory?.Invoke()
            ?? throw new InvalidOperationException(
                "TrackPostgres requires a DbDataSource, NpgsqlDataSource or Func<DbConnection> in DI, or an explicit snapshot connection string."
            );
    }
}

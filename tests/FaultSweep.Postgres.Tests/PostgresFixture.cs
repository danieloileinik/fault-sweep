using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Images;
using Npgsql;
using Testcontainers.PostgreSql;

namespace FaultSweep.Postgres.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    // Verified against the official Docker Hub registry; latest resolves to this stable release.
    private const string PostgresImage =
        "postgres:18.6@sha256:979bccfde35445db618c65107d27d08c6c21339d8dc3ad4c830c6633947be0e4";
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder(PostgresImage)
        .WithDatabase("faultsweep")
        .WithUsername("faultsweep")
        .WithPassword("faultsweep-tests")
        .Build();
    private NpgsqlDataSource admin = null!;

    public async Task InitializeAsync()
    {
        TestcontainersSettings.ResourceReaperImage = new DockerImage(
            "testcontainers/ryuk:0.14.0@sha256:7c1a8a9a47c780ed0f983770a662f80deb115d95cce3e2daa3d12115b8cd28f0"
        );
        await container.StartAsync();
        admin = NpgsqlDataSource.Create(container.GetConnectionString());
    }

    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        string name = $"sweep_{Guid.NewGuid():N}";
        await using (NpgsqlCommand command = admin.CreateCommand($"CREATE DATABASE \"{name}\""))
        {
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = name,
            MaxPoolSize = 2,
            Timeout = 5,
            CommandTimeout = 5,
        };
        var database = new TestDatabase(
            connectionString.ConnectionString,
            async () =>
            {
                await using NpgsqlCommand command = admin.CreateCommand(
                    $"DROP DATABASE \"{name}\" WITH (FORCE)"
                );
                await command.ExecuteNonQueryAsync();
            }
        );
        await database.InitializeAsync();
        return database;
    }

    public async Task DisposeAsync()
    {
        if (admin is not null)
        {
            await admin.DisposeAsync();
        }

        await container.DisposeAsync();
    }
}

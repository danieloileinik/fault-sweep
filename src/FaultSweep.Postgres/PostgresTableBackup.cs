using System.Collections.Immutable;

namespace FaultSweep.Postgres;

internal sealed record PostgresTableBackup(
    string Name,
    string TemporaryName,
    ImmutableArray<string> Columns
);

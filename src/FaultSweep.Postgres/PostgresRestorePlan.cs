using System.Collections.Immutable;

namespace FaultSweep.Postgres;

internal sealed record PostgresRestorePlan(
    string Schema,
    ImmutableArray<PostgresTableBackup> Tables,
    ImmutableArray<PostgresSequenceBackup> Sequences,
    ImmutableArray<PostgresTriggerBackup> Triggers,
    ImmutableDictionary<string, ImmutableArray<string>> IgnoredColumns
);
